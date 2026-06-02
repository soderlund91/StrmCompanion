using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using StrmCompanion.Jobs;
using StrmCompanion.MediaInfo;

namespace StrmCompanion.Api
{
    // ─── Request/Response DTOs ───────────────────────────────────────────────────

    [Route("/strmcompanion/mediainfo/libraries", "GET", Summary = "List all Emby libraries")]
    [Authenticated]
    public class GetMediaInfoLibraries : IReturn<List<LibraryDto>> { }

    [Route("/strmcompanion/mediainfo/settings", "GET", Summary = "Get Media Info settings")]
    [Authenticated]
    public class GetMediaInfoSettings : IReturn<MediaInfoSettingsDto> { }

    [Route("/strmcompanion/mediainfo/settings", "POST", Summary = "Save Media Info settings")]
    [Authenticated]
    public class SaveMediaInfoSettings : IReturn<MediaInfoSettingsDto>
    {
        public string MediaInfoLibraryIds { get; set; }
        public int MediaInfoConcurrency { get; set; }
        public bool MediaInfoAutoScan { get; set; }
    }

    [Route("/strmcompanion/mediainfo/scan", "POST", Summary = "Start a media info scan job")]
    [Authenticated]
    public class StartMediaInfoScan : IReturn<StartJobResponse> { }

    [Route("/strmcompanion/mediainfo/job/{JobId}", "GET", Summary = "Poll media info job status")]
    [Authenticated]
    public class GetMediaInfoJobStatus : IReturn<JobInfo>
    {
        public string JobId { get; set; }
    }

    [Route("/strmcompanion/mediainfo/job/{JobId}", "DELETE", Summary = "Cancel a media info job")]
    [Authenticated]
    public class CancelMediaInfoJob : IReturnVoid
    {
        public string JobId { get; set; }
    }

    [Route("/strmcompanion/mediainfo/jobs", "GET", Summary = "List all media info jobs")]
    [Authenticated]
    public class GetAllMediaInfoJobs : IReturn<List<JobInfo>> { }

    [Route("/strmcompanion/mediainfo/stats", "GET", Summary = "Get scan statistics for configured libraries")]
    [Authenticated]
    public class GetMediaInfoStats : IReturn<MediaInfoStatsDto> { }

    // ─── DTOs ────────────────────────────────────────────────────────────────────

    public class LibraryDto
    {
        public long Id { get; set; }
        public string Name { get; set; }
        public string CollectionType { get; set; }
    }

    public class MediaInfoSettingsDto
    {
        public string MediaInfoLibraryIds { get; set; }
        public int MediaInfoConcurrency { get; set; }
        public bool MediaInfoAutoScan { get; set; }
    }

    public class MediaInfoStatsDto
    {
        public int Total { get; set; }
        public int Scanned { get; set; }
        public int Pending { get; set; }
        public int TotalMovies { get; set; }
        public int ScannedMovies { get; set; }
        public int PendingMovies { get; set; }
        public int TotalEpisodes { get; set; }
        public int ScannedEpisodes { get; set; }
        public int PendingEpisodes { get; set; }
    }

    // ─── Service ─────────────────────────────────────────────────────────────────

    public class MediaInfoApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _logger;

        public MediaInfoApiService(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _logger = logManager.GetLogger(nameof(MediaInfoApiService));
        }

        public object Get(GetMediaInfoLibraries request)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "CollectionFolder" }
            };
            var result = _libraryManager.QueryItems(query);
            return result.Items.Select(f => new LibraryDto
            {
                Id = f.InternalId,
                Name = f.Name,
                CollectionType = (f as ICollectionFolder)?.CollectionType
            }).OrderBy(f => f.Name).ToList();
        }

        public object Get(GetMediaInfoSettings request)
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            return ToSettingsDto(cfg);
        }

        public object Post(SaveMediaInfoSettings request)
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            cfg.MediaInfoLibraryIds = request.MediaInfoLibraryIds?.Trim() ?? string.Empty;
            cfg.MediaInfoConcurrency = request.MediaInfoConcurrency >= 1 ? request.MediaInfoConcurrency : 2;
            cfg.MediaInfoAutoScan = request.MediaInfoAutoScan;
            Plugin.Instance?.SaveConfiguration();
            return ToSettingsDto(cfg);
        }

        public object Post(StartMediaInfoScan request)
        {
            var task = PluginEntryPoint.TaskRegistry?.GetById("media-info") as MediaInfoAnalysisTask;
            if (task == null)
                throw new Exception("Media info task is not registered");

            var jobId = task.StartScan(CancellationToken.None);
            return new StartJobResponse { JobId = jobId };
        }

        public object Get(GetMediaInfoJobStatus request)
        {
            var job = PluginEntryPoint.JobManager?.GetJob(request.JobId);
            if (job == null)
                throw new ArgumentException("Job not found: " + request.JobId);
            return job;
        }

        public void Delete(CancelMediaInfoJob request)
        {
            PluginEntryPoint.JobManager?.CancelJob(request.JobId);
        }

        public object Get(GetAllMediaInfoJobs request)
        {
            var jobs = PluginEntryPoint.JobManager?.GetAllJobs()
                .Where(j => j.TaskId == "media-info")
                .OrderByDescending(j => j.StartedAt)
                .ToList();
            return jobs ?? new List<JobInfo>();
        }

        public object Get(GetMediaInfoStats request)
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var libraryIds = MediaInfoAnalysisTask.ParseLibraryIds(cfg.MediaInfoLibraryIds);

            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie", "Episode" },
                IsVirtualItem = false
            };
            if (libraryIds.Length > 0)
                query.AncestorIds = libraryIds;

            var result = _libraryManager.GetItemsResult(query);
            var strmItems = result.Items
                .Where(i => i.Path != null && i.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                .ToList();

            int scannedMovies = 0, totalMovies = 0;
            int scannedEpisodes = 0, totalEpisodes = 0;

            foreach (var item in strmItems)
            {
                bool isMovie = item.GetType().Name == "Movie";
                bool hasStreams = false;
                try
                {
                    var streams = _itemRepository.GetMediaStreams(new MediaStreamQuery { ItemId = item.InternalId }, System.Threading.CancellationToken.None);
                    hasStreams = streams != null && streams.Count > 0;
                }
                catch { }

                if (isMovie) { totalMovies++; if (hasStreams) scannedMovies++; }
                else         { totalEpisodes++; if (hasStreams) scannedEpisodes++; }
            }

            int total   = totalMovies + totalEpisodes;
            int scanned = scannedMovies + scannedEpisodes;

            return new MediaInfoStatsDto
            {
                Total          = total,
                Scanned        = scanned,
                Pending        = total - scanned,
                TotalMovies    = totalMovies,
                ScannedMovies  = scannedMovies,
                PendingMovies  = totalMovies - scannedMovies,
                TotalEpisodes  = totalEpisodes,
                ScannedEpisodes = scannedEpisodes,
                PendingEpisodes = totalEpisodes - scannedEpisodes
            };
        }

        private static MediaInfoSettingsDto ToSettingsDto(PluginConfiguration cfg) =>
            new MediaInfoSettingsDto
            {
                MediaInfoLibraryIds = cfg.MediaInfoLibraryIds,
                MediaInfoConcurrency = cfg.MediaInfoConcurrency,
                MediaInfoAutoScan = cfg.MediaInfoAutoScan
            };
    }
}
