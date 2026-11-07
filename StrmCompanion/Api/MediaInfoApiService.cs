using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using StrmCompanion.Jobs;
using StrmCompanion.ScheduledTasks;

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
        public bool MediaInfoAllLibraries { get; set; }
        public int MediaInfoConcurrency { get; set; }
        public bool MediaInfoAutoScan { get; set; }
        public int MediaInfoFirstRetryDays { get; set; }
        public int MediaInfoSecondRetryDays { get; set; }
        public int MediaInfoLaterRetryDays { get; set; }
    }

    [Route("/strmcompanion/mediainfo/scan", "POST", Summary = "Start a media info scan job")]
    [Authenticated]
    public class StartMediaInfoScan : IReturn<StartJobResponse> { }

    [Route("/strmcompanion/mediainfo/failed", "GET", Summary = "List items whose media info probe failed")]
    [Authenticated]
    public class GetMediaInfoFailed : IReturn<MediaInfoFailedListDto> { }

    [Route("/strmcompanion/mediainfo/failed/retry", "POST", Summary = "Make all failed items due and start a scan")]
    [Authenticated]
    public class RetryMediaInfoFailed : IReturn<StartJobResponse> { }

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
        public bool MediaInfoAllLibraries { get; set; }
        public int MediaInfoConcurrency { get; set; }
        public bool MediaInfoAutoScan { get; set; }
        public int MediaInfoFirstRetryDays { get; set; }
        public int MediaInfoSecondRetryDays { get; set; }
        public int MediaInfoLaterRetryDays { get; set; }
    }

    public class MediaInfoStatsDto
    {
        public MediaInfoStatsRowDto Movies { get; set; } = new MediaInfoStatsRowDto();
        public MediaInfoStatsRowDto Episodes { get; set; } = new MediaInfoStatsRowDto();
    }

    public class MediaInfoStatsRowDto
    {
        public int Total { get; set; }
        public int Scanned { get; set; }
        public int NotScanned { get; set; }
        public int Failed { get; set; }
        public int Queued { get; set; }
    }

    public class MediaInfoFailedItemDto
    {
        public long ItemId { get; set; }
        public string Name { get; set; }
        public string LastError { get; set; }
        public int FailureCount { get; set; }
        public string LastAttemptUtc { get; set; }
        public string NextRetryUtc { get; set; }
        public bool RetryNow { get; set; }
    }

    public class MediaInfoFailedListDto
    {
        public int TotalCount { get; set; }
        public List<MediaInfoFailedItemDto> Items { get; set; } = new List<MediaInfoFailedItemDto>();
    }

    // ─── Service ─────────────────────────────────────────────────────────────────

    public class MediaInfoApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public MediaInfoApiService(
            ILibraryManager libraryManager,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
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
            cfg.MediaInfoAllLibraries = request.MediaInfoAllLibraries;
            cfg.MediaInfoConcurrency = request.MediaInfoConcurrency >= 1 ? request.MediaInfoConcurrency : 2;
            cfg.MediaInfoAutoScan = request.MediaInfoAutoScan;
            cfg.MediaInfoFirstRetryDays = ClampRetryDays(request.MediaInfoFirstRetryDays);
            cfg.MediaInfoSecondRetryDays = ClampRetryDays(request.MediaInfoSecondRetryDays);
            cfg.MediaInfoLaterRetryDays = ClampRetryDays(request.MediaInfoLaterRetryDays);
            Plugin.Instance?.SaveConfiguration();
            return ToSettingsDto(cfg);
        }

        private static int ClampRetryDays(int days) =>
            Math.Min(Math.Max(days, MediaInfoFailureStore.MinRetryDays), MediaInfoFailureStore.MaxRetryDays);

        public object Post(StartMediaInfoScan request) => StartFullScan();

        public object Post(RetryMediaInfoFailed request)
        {
            PluginEntryPoint.MediaInfoFailures?.MakeAllDue();
            return StartFullScan();
        }

        private StartJobResponse StartFullScan()
        {
            var jobManager = PluginEntryPoint.JobManager;
            if (jobManager == null)
                throw new Exception("JobManager is not initialized");

            // Only a running full scan blocks a new one; auto-scan jobs must not.
            var running = jobManager.GetAllJobs()
                .FirstOrDefault(j => j.TaskId == "media-info" && j.Status == JobStatus.Running && !j.IsAutoScan);
            if (running != null)
                return new StartJobResponse { JobId = running.JobId };

            var job = jobManager.CreateJob("media-info", 0, "Media Info Scan", null, null);
            // Register the token now so a cancel before the task starts is not lost.
            jobManager.GetCancellationToken(job.JobId);
            MediaInfoScheduledTask.SetPendingJobId(job.JobId);
            PluginEntryPoint.TaskManager?.QueueScheduledTask<MediaInfoScheduledTask>();

            return new StartJobResponse { JobId = job.JobId };
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
            var strmItems = QueryStrmItems();
            var failures = PluginEntryPoint.MediaInfoFailures;
            var stats = new MediaInfoStatsDto();

            foreach (var item in strmItems)
            {
                var row = item.GetType().Name == "Movie" ? stats.Movies : stats.Episodes;
                row.Total++;

                // Every item lands in exactly one column, so the columns always add up to Total.
                if (HasMediaInfo(item)) row.Scanned++;
                else if (MediaInfoScheduledTask.IsInFlight(item.InternalId)) row.Queued++;
                else if (failures?.Get(item.InternalId) != null) row.Failed++;
                else row.NotScanned++;
            }

            _logger.Info("StrmCompanion stats: movies={0}/{1} scanned, episodes={2}/{3} scanned",
                stats.Movies.Scanned, stats.Movies.Total, stats.Episodes.Scanned, stats.Episodes.Total);

            return stats;
        }

        public object Get(GetMediaInfoFailed request)
        {
            const int maxItems = 500;

            var failures = PluginEntryPoint.MediaInfoFailures;
            var result = new MediaInfoFailedListDto();
            if (failures == null) return result;

            var task = PluginEntryPoint.MediaInfoTask;
            var failed = QueryStrmItems()
                .Where(i => !HasMediaInfo(i) && !MediaInfoScheduledTask.IsInFlight(i.InternalId))
                .Select(i => new { Item = i, Failure = failures.Get(i.InternalId) })
                .Where(x => x.Failure != null)
                .OrderByDescending(x => x.Failure.LastAttemptUtcTicks)
                .ToList();

            result.TotalCount = failed.Count;
            result.Items = failed
                .Take(maxItems)
                .Select(x => new MediaInfoFailedItemDto
                {
                    ItemId = x.Item.InternalId,
                    Name = task != null ? task.GetItemDisplayName(x.Item) : x.Item.Name,
                    LastError = x.Failure.LastError,
                    FailureCount = x.Failure.FailureCount,
                    LastAttemptUtc = x.Failure.GetLastAttemptUtc().ToString("o"),
                    NextRetryUtc = failures.GetNextRetryUtc(x.Failure).ToString("o"),
                    RetryNow = x.Failure.RetryNow
                })
                .ToList();

            return result;
        }

        private List<BaseItem> QueryStrmItems()
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie", "Episode" },
                IsVirtualItem = false
            };
            if (!MediaInfoScheduledTask.ApplyLibraryScope(query, cfg))
                return new List<BaseItem>();

            return _libraryManager.GetItemList(query)
                .Where(i => i.Path != null && i.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        private static bool HasMediaInfo(BaseItem item)
        {
            var task = PluginEntryPoint.MediaInfoTask;
            return task != null
                ? task.ItemHasMediaInfo(item)
                : item.GetMediaStreams().Any(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video);
        }

        private static MediaInfoSettingsDto ToSettingsDto(PluginConfiguration cfg) =>
            new MediaInfoSettingsDto
            {
                MediaInfoLibraryIds = cfg.MediaInfoLibraryIds,
                MediaInfoAllLibraries = cfg.MediaInfoAllLibraries,
                MediaInfoConcurrency = cfg.MediaInfoConcurrency,
                MediaInfoAutoScan = cfg.MediaInfoAutoScan,
                MediaInfoFirstRetryDays = cfg.MediaInfoFirstRetryDays,
                MediaInfoSecondRetryDays = cfg.MediaInfoSecondRetryDays,
                MediaInfoLaterRetryDays = cfg.MediaInfoLaterRetryDays
            };
    }
}
