using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using StrmCompanion.IntroDetection;

namespace StrmCompanion.Api
{
    // ------------------------------------------------------------------ requests

    [Route("/strmcompanion/fingerprints/season/{SeasonId}", "DELETE",
        Summary = "Delete cached fingerprints for all episodes in a season")]
    [Authenticated]
    public class DeleteSeasonFingerprints : IReturnVoid
    {
        public long SeasonId { get; set; }
    }

    [Route("/strmcompanion/fingerprints/series/{SeriesId}", "DELETE",
        Summary = "Delete cached fingerprints for an entire series")]
    [Authenticated]
    public class DeleteSeriesFingerprints : IReturnVoid
    {
        public long SeriesId { get; set; }
    }

    // Full delete (markers + fingerprints)
    [Route("/strmcompanion/intro/all/season/{SeasonId}", "DELETE",
        Summary = "Delete intro markers AND fingerprint cache for a season")]
    [Authenticated]
    public class DeleteAllSeasonData : IReturnVoid
    {
        public long SeasonId { get; set; }
    }

    [Route("/strmcompanion/intro/all/series/{SeriesId}", "DELETE",
        Summary = "Delete intro markers AND fingerprint cache for a series")]
    [Authenticated]
    public class DeleteAllSeriesData : IReturnVoid
    {
        public long SeriesId { get; set; }
    }

    // ------------------------------------------------------------------ service

    public class FingerprintCacheService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public FingerprintCacheService(ILibraryManager libraryManager, ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger(nameof(FingerprintCacheService));
        }

        public void Delete(DeleteSeasonFingerprints request)
        {
            var store = GetStore();
            var seriesId = GetSeriesIdForSeason(request.SeasonId);
            if (seriesId == null) return;

            foreach (var ep in GetEpisodes(request.SeasonId))
                store.Delete(seriesId.Value, ep.InternalId);

            _logger.Info("StrmCompanion: cleared fingerprint cache for season {0}", request.SeasonId);
        }

        public void Delete(DeleteSeriesFingerprints request)
        {
            var store = GetStore();
            store.DeleteAll(request.SeriesId);
            _logger.Info("StrmCompanion: cleared fingerprint cache for series {0}", request.SeriesId);
        }

        public void Delete(DeleteAllSeasonData request)
        {
            // Delete markers
            var markerWriter = GetMarkerWriter();
            foreach (var ep in GetEpisodes(request.SeasonId))
                markerWriter.DeleteIntroMarkers(ep.InternalId);

            // Delete fingerprint cache
            var store = GetStore();
            var seriesId = GetSeriesIdForSeason(request.SeasonId);
            if (seriesId != null)
                foreach (var ep in GetEpisodes(request.SeasonId))
                    store.Delete(seriesId.Value, ep.InternalId);

            _logger.Info("StrmCompanion: deleted all data for season {0}", request.SeasonId);
        }

        public void Delete(DeleteAllSeriesData request)
        {
            // Delete markers for all episodes in all seasons
            var markerWriter = GetMarkerWriter();
            foreach (var seasonId in GetSeasonIds(request.SeriesId))
                foreach (var ep in GetEpisodes(seasonId))
                    markerWriter.DeleteIntroMarkers(ep.InternalId);

            // Delete entire fingerprint cache folder for this series
            var store = GetStore();
            store.DeleteAll(request.SeriesId);

            _logger.Info("StrmCompanion: deleted all data for series {0}", request.SeriesId);
        }

        // ------------------------------------------------------------------ helpers

        private FingerprintStore GetStore()
        {
            // jsonSerializer only needed for Load/Save; Delete works without it
            return new FingerprintStore(Plugin.GetFingerprintBasePath(), null, _logger);
        }

        private MediaBrowser.Controller.Persistence.IItemRepository GetItemRepository()
        {
            return PluginEntryPoint.ItemRepository;
        }

        private MarkerWriterService GetMarkerWriter()
        {
            return new MarkerWriterService(_libraryManager, GetItemRepository(), _logger);
        }

        private long? GetSeriesIdForSeason(long seasonInternalId)
        {
            var season = _libraryManager.GetItemById(seasonInternalId);
            if (season == null) return null;
            // Walk up to find the series InternalId
            var parent = _libraryManager.GetItemById(season.ParentId);
            return parent?.InternalId;
        }

        private IEnumerable<BaseItem> GetEpisodes(long seasonInternalId)
        {
            var season = _libraryManager.GetItemById(seasonInternalId);
            if (season == null) return System.Linq.Enumerable.Empty<BaseItem>();
            var query = new InternalItemsQuery
            {
                AncestorIds = new[] { season.InternalId },
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = false
            };
            return _libraryManager.GetItemsResult(query).Items;
        }

        private IEnumerable<long> GetSeasonIds(long seriesInternalId)
        {
            var series = _libraryManager.GetItemById(seriesInternalId);
            if (series == null) return System.Linq.Enumerable.Empty<long>();
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
