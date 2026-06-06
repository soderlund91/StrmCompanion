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
using StrmCompanion.IntroDetection;
using StrmCompanion.Jobs;

namespace StrmCompanion.Api
{
    [Route("/strmcompanion/intro/run", "POST", Summary = "Start an intro detection job")]
    [Authenticated]
    public class StartIntroDetection : IReturn<StartJobResponse>
    {
        public long SeriesId { get; set; }
        public long? SeasonId { get; set; }
    }

    [Route("/strmcompanion/intro/job/{JobId}", "GET", Summary = "Poll job status")]
    [Authenticated]
    public class GetJobStatus : IReturn<JobInfo>
    {
        public string JobId { get; set; }
    }

    [Route("/strmcompanion/intro/job/{JobId}", "DELETE", Summary = "Cancel a running job")]
    [Authenticated]
    public class CancelJob : IReturnVoid
    {
        public string JobId { get; set; }
    }

    [Route("/strmcompanion/intro/jobs", "GET", Summary = "List all jobs")]
    [Authenticated]
    public class GetAllJobs : IReturn<List<JobInfo>> { }

    [Route("/strmcompanion/intro/markers/season/{SeasonId}", "GET", Summary = "Get current intro markers for a season")]
    [Authenticated]
    public class GetSeasonMarkers : IReturn<List<EpisodeMarkerDto>>
    {
        public long SeasonId { get; set; }
    }

    [Route("/strmcompanion/intro/markers/series/{SeriesId}", "GET", Summary = "Get current intro markers for all episodes in a series")]
    [Authenticated]
    public class GetSeriesMarkers : IReturn<List<EpisodeMarkerDto>>
    {
        public long SeriesId { get; set; }
    }

    [Route("/strmcompanion/intro/markers/episode/{EpisodeId}", "DELETE", Summary = "Delete intro markers for one episode")]
    [Authenticated]
    public class DeleteEpisodeMarkers : IReturnVoid
    {
        public long EpisodeId { get; set; }
    }

    [Route("/strmcompanion/intro/markers/season/{SeasonId}", "DELETE", Summary = "Delete intro markers for all episodes in a season")]
    [Authenticated]
    public class DeleteSeasonMarkers : IReturnVoid
    {
        public long SeasonId { get; set; }
    }

    [Route("/strmcompanion/intro/markers/series/{SeriesId}", "DELETE", Summary = "Delete intro markers for all episodes in a series")]
    [Authenticated]
    public class DeleteSeriesMarkers : IReturnVoid
    {
        public long SeriesId { get; set; }
    }

    public class StartJobResponse
    {
        public string JobId { get; set; }
    }

    public class EpisodeMarkerDto
    {
        public long EpisodeId { get; set; }
        public string EpisodeName { get; set; }
        public int? EpisodeIndex { get; set; }
        public string SeasonName { get; set; }
        public int? SeasonNumber { get; set; }
        public bool HasMarkers { get; set; }
        public double? IntroStartSeconds { get; set; }
        public double? IntroEndSeconds { get; set; }
    }

    public class IntroDetectionService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly ILogger _logger;

        public IntroDetectionService(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _logger = logManager.GetLogger(nameof(IntroDetectionService));
        }

        public object Post(StartIntroDetection request)
        {
            var task = PluginEntryPoint.TaskRegistry?.GetById("intro-detection");
            if (task == null)
                throw new InvalidOperationException("Intro detection task is not registered");

            var jobId = task.StartJob(request.SeriesId, request.SeasonId, CancellationToken.None);
            return new StartJobResponse { JobId = jobId };
        }

        public object Get(GetJobStatus request)
        {
            var job = PluginEntryPoint.JobManager?.GetJob(request.JobId);
            if (job == null)
                throw new InvalidOperationException("Job not found: " + request.JobId);
            return job;
        }

        public object Get(GetAllJobs request)
        {
            var jobs = PluginEntryPoint.JobManager?.GetAllJobs()
                ?? Enumerable.Empty<JobInfo>();
            return new List<JobInfo>(jobs);
        }

        public void Delete(CancelJob request)
        {
            PluginEntryPoint.JobManager?.CancelJob(request.JobId);
        }

        // ------------------------------------------------------------------ delete markers

        public void Delete(DeleteEpisodeMarkers request)
        {
            var writer = new MarkerWriterService(_libraryManager, _itemRepository, _logger);
            writer.DeleteIntroMarkers(request.EpisodeId);
        }

        public void Delete(DeleteSeasonMarkers request)
        {
            var writer = new MarkerWriterService(_libraryManager, _itemRepository, _logger);
            foreach (var ep in GetEpisodesForSeason(request.SeasonId))
                writer.DeleteIntroMarkers(ep.InternalId);
        }

        public void Delete(DeleteSeriesMarkers request)
        {
            var writer = new MarkerWriterService(_libraryManager, _itemRepository, _logger);
            foreach (var seasonId in GetSeasonInternalIds(request.SeriesId))
                foreach (var ep in GetEpisodesForSeason(seasonId))
                    writer.DeleteIntroMarkers(ep.InternalId);
        }

        // ------------------------------------------------------------------ get markers

        public object Get(GetSeasonMarkers request)
        {
            var season = _libraryManager.GetItemById(request.SeasonId);
            if (season == null) return new List<EpisodeMarkerDto>();
            return BuildMarkerDtos(season.InternalId, season.Name, season.IndexNumber);
        }

        public object Get(GetSeriesMarkers request)
        {
            var series = _libraryManager.GetItemById(request.SeriesId);
            if (series == null) return new List<EpisodeMarkerDto>();

            var seasonQuery = new InternalItemsQuery
            {
                AncestorIds = new[] { series.InternalId },
                IncludeItemTypes = new[] { "Season" },
                IsVirtualItem = false,
                OrderBy = new[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
            };
            var seasons = _libraryManager.GetItemsResult(seasonQuery).Items;

            var result = new List<EpisodeMarkerDto>();
            foreach (var season in seasons)
                result.AddRange(BuildMarkerDtos(season.InternalId, season.Name, season.IndexNumber));
            return result;
        }

        private List<EpisodeMarkerDto> BuildMarkerDtos(long seasonInternalId, string seasonName, int? seasonNumber)
        {
            var query = new InternalItemsQuery
            {
                AncestorIds = new[] { seasonInternalId },
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = false,
                OrderBy = new[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
            };

            var result = new List<EpisodeMarkerDto>();
            foreach (var ep in _libraryManager.GetItemsResult(query).Items)
            {
                var dto = new EpisodeMarkerDto
                {
                    EpisodeId    = ep.InternalId,
                    EpisodeName  = ep.Name,
                    EpisodeIndex = ep.IndexNumber,
                    SeasonName   = seasonName,
                    SeasonNumber = seasonNumber,
                    HasMarkers   = false
                };

                try
                {
                    var chapters   = _itemRepository.GetChapters(ep);
                    var introStart = chapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroStart);
                    var introEnd   = chapters.FirstOrDefault(c => c.MarkerType == MarkerType.IntroEnd);
                    if (introStart != null && introEnd != null)
                    {
                        dto.HasMarkers        = true;
                        dto.IntroStartSeconds = TimeSpan.FromTicks(introStart.StartPositionTicks).TotalSeconds;
                        dto.IntroEndSeconds   = TimeSpan.FromTicks(introEnd.StartPositionTicks).TotalSeconds;
                    }
                }
                catch (Exception ex)
                {
                    _logger.Warn("StrmCompanion: could not read chapters for {0}: {1}", ep.InternalId, ex.Message);
                }

                result.Add(dto);
            }
            return result;
        }

        // ------------------------------------------------------------------ helpers

        private IEnumerable<MediaBrowser.Controller.Entities.BaseItem> GetEpisodesForSeason(long seasonInternalId)
        {
            var season = _libraryManager.GetItemById(seasonInternalId);
            if (season == null) return Enumerable.Empty<MediaBrowser.Controller.Entities.BaseItem>();

            var query = new InternalItemsQuery
            {
                AncestorIds = new[] { season.InternalId },
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = false
            };
            return _libraryManager.GetItemsResult(query).Items;
        }

        private IEnumerable<long> GetSeasonInternalIds(long seriesInternalId)
        {
            var series = _libraryManager.GetItemById(seriesInternalId);
            if (series == null) return Enumerable.Empty<long>();

            var query = new InternalItemsQuery
            {
                AncestorIds = new[] { series.InternalId },
                IncludeItemTypes = new[] { "Season" },
                IsVirtualItem = false
            };
            return _libraryManager.GetItemsResult(query).Items.Select(s => s.InternalId);
        }
    }
}
