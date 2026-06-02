using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Serialization;
using StrmCompanion.Analysis;
using StrmCompanion.Jobs;

namespace StrmCompanion.IntroDetection
{
    public class IntroDetectionAnalysisTask : IAnalysisTask
    {
        public string TaskId => "intro-detection";
        public string DisplayName => "Intro Detection";
        public string Description => "Identifies intro sequences in TV series using Chromaprint fingerprinting and updates Emby with precise timestamps.";

        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly JobManager _jobManager;
        private readonly ILogger _logger;
        private readonly ILogManager _logManager;

        public IntroDetectionAnalysisTask(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IJsonSerializer jsonSerializer,
            JobManager jobManager,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _jsonSerializer = jsonSerializer;
            _jobManager = jobManager;
            _logManager = logManager;
            _logger = logManager.GetLogger(nameof(IntroDetectionAnalysisTask));
        }

        public string StartJob(long seriesId, long? seasonId, CancellationToken cancellationToken)
        {
            var series = _libraryManager.GetItemById(seriesId);
            if (series == null)
                throw new ArgumentException($"Series {seriesId} not found");

            string seasonName = null;
            if (seasonId.HasValue)
            {
                var season = _libraryManager.GetItemById(seasonId.Value);
                seasonName = season?.Name;
            }

            var job = _jobManager.CreateJob(TaskId, seriesId, series.Name, seasonId, seasonName);
            var token = _jobManager.GetCancellationToken(job.JobId);

            Task.Run(() => RunAsync(job, seasonId, token), token);

            return job.JobId;
        }

        private async Task RunAsync(JobInfo job, long? seasonId, CancellationToken token)
        {
            try
            {
                var config = Plugin.Instance.Configuration;
                var dataPath = Plugin.GetFingerprintBasePath();

                var ffmpegPath = string.IsNullOrEmpty(config.FfmpegPathOverride)
                    ? ResolveFfmpegPath()
                    : config.FfmpegPathOverride;

                var fingerprintService = new FingerprintService(_logger);
                fingerprintService.SetFfmpegPath(ffmpegPath);

                var silenceService = new SilenceDetectionService(_logger);
                silenceService.SetFfmpegPath(ffmpegPath);

                var fingerprintStore = new FingerprintStore(dataPath, _jsonSerializer, _logger);
                var matcher = new FingerprintMatcher(_logger, config.HammingDistanceThreshold, config.MinimumIntroLengthSeconds);
                var markerWriter = new MarkerWriterService(_libraryManager, _itemRepository, _logger);

                // Determine which seasons to process
                List<long> seasonIds;
                if (seasonId.HasValue)
                {
                    seasonIds = new List<long> { seasonId.Value };
                }
                else
                {
                    seasonIds = GetSeasonIds(job.SeriesInternalId);
                    _logger.Info("StrmCompanion: processing {0} season(s) for '{1}'",
                        seasonIds.Count, job.SeriesName);
                }

                int totalSeasons = seasonIds.Count;
                for (int si = 0; si < totalSeasons; si++)
                {
                    token.ThrowIfCancellationRequested();

                    var currentSeasonId = seasonIds[si];
                    double seasonProgressBase = (double)si / totalSeasons * 100;
                    double seasonProgressShare = 100.0 / totalSeasons;

                    var seasonItem = _libraryManager.GetItemById(currentSeasonId) as Season;
                    var seasonLabel = seasonItem?.Name ?? $"Season {si + 1}";

                    _logger.Info("StrmCompanion: processing {0}", seasonLabel);
                    await ProcessSeasonAsync(
                        job, currentSeasonId, seasonLabel,
                        seasonProgressBase, seasonProgressShare,
                        fingerprintService, silenceService, fingerprintStore, matcher, markerWriter,
                        config, token);
                }

                _jobManager.CompleteJob(job.JobId);
            }
            catch (OperationCanceledException)
            {
                _logger.Info("StrmCompanion: job {0} cancelled", job.JobId);
                job.Status = JobStatus.Cancelled;
                job.CompletedAt = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                _logger.Error("StrmCompanion: job {0} failed: {1}", job.JobId, ex.Message);
                _jobManager.FailJob(job.JobId, ex.Message);
            }
        }

        private async Task ProcessSeasonAsync(
            JobInfo job,
            long seasonId,
            string seasonLabel,
            double progressBase,
            double progressShare,
            FingerprintService fingerprintService,
            SilenceDetectionService silenceService,
            FingerprintStore fingerprintStore,
            FingerprintMatcher matcher,
            MarkerWriterService markerWriter,
            PluginConfiguration config,
            CancellationToken token)
        {
            var episodes = GetEpisodes(seasonId);
            if (episodes.Count < 2)
            {
                _logger.Info("StrmCompanion: {0} has {1} episode(s) – skipping (need ≥2)",
                    seasonLabel, episodes.Count);
                return;
            }

            int total = episodes.Count;
            int durationSec = config.FingerprintDurationMinutes * 60;

            // Phase 1: Fingerprinting (0→50% of this season's share)
            var fingerprints = new List<FingerprintData>();
            for (int i = 0; i < total; i++)
            {
                token.ThrowIfCancellationRequested();

                var ep = episodes[i];
                double pct = progressBase + progressShare * ((double)i / total * 0.5);
                _jobManager.UpdateProgress(job.JobId, pct,
                    $"{seasonLabel}: fingerprinting {ep.Name} ({i + 1}/{total})");

                FingerprintData fp;
                if (fingerprintStore.Exists(job.SeriesInternalId, ep.InternalId))
                {
                    fp = fingerprintStore.Load(job.SeriesInternalId, ep.InternalId);
                    _logger.Debug("StrmCompanion: loaded cached fingerprint for '{0}'", ep.Name);
                }
                else
                {
                    fp = fingerprintService.GenerateFingerprint(
                        job.SeriesInternalId, ep.InternalId, ep.Name,
                        ep.Path, durationSec, token);

                    if (fp != null)
                        fingerprintStore.Save(job.SeriesInternalId, fp);
                }

                if (fp != null)
                    fingerprints.Add(fp);
                else
                    _jobManager.AddEpisodeResult(job.JobId, ep.InternalId, "Fingerprinting failed");

                // Small pause between episodes to respect rate limits on .strm sources
                await Task.Delay(500, token);
            }

            if (fingerprints.Count < 2)
            {
                _jobManager.UpdateProgress(job.JobId,
                    progressBase + progressShare,
                    $"{seasonLabel}: too few successful fingerprints");
                return;
            }

            // Phase 2: Fingerprint comparison
            _jobManager.UpdateProgress(job.JobId,
                progressBase + progressShare * 0.55,
                $"{seasonLabel}: comparing fingerprints...");

            var introMap = matcher.FindConsensusIntros(fingerprints);

            // Phase 3: Write markers (50→100% of this season's share).
            // End time comes from fingerprint consensus length, not silence detection.
            // Silence detection caused wildly varying results because it found silences
            // WITHIN the intro music rather than at the true boundary.
            for (int i = 0; i < fingerprints.Count; i++)
            {
                token.ThrowIfCancellationRequested();

                var fp = fingerprints[i];
                double pct = progressBase + progressShare * (0.55 + (double)i / fingerprints.Count * 0.45);
                _jobManager.UpdateProgress(job.JobId, pct,
                    $"{seasonLabel}: writing markers for {fp.EpisodeName} ({i + 1}/{fingerprints.Count})");

                if (!introMap.TryGetValue(fp.EpisodeInternalId, out var introTs) || introTs == null)
                {
                    _jobManager.AddEpisodeResult(job.JobId, fp.EpisodeInternalId, "No intro found");
                    continue;
                }

                if (!config.OverwriteExistingIntroMarkers && markerWriter.HasIntroMarkers(fp.EpisodeInternalId))
                {
                    _jobManager.AddEpisodeResult(job.JobId, fp.EpisodeInternalId, "Skipped (existing markers kept)");
                    _logger.Debug("StrmCompanion: skipping '{0}' – already has intro markers", fp.EpisodeName);
                    continue;
                }

                markerWriter.WriteIntroMarkers(fp.EpisodeInternalId, introTs.StartSeconds, introTs.EndSeconds);
                _jobManager.AddEpisodeResult(job.JobId, fp.EpisodeInternalId,
                    $"Intro: {introTs.StartSeconds:F1}s – {introTs.EndSeconds:F1}s ({introTs.LengthSeconds:F1}s)");

                await Task.Delay(200, token);
            }
        }

        private List<MediaBrowser.Controller.Entities.BaseItem> GetEpisodes(long seasonId)
        {
            var season = _libraryManager.GetItemById(seasonId);
            if (season == null) return new List<MediaBrowser.Controller.Entities.BaseItem>();

            var query = new InternalItemsQuery
            {
                AncestorIds = new[] { season.InternalId },
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = false,
                OrderBy = new[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
            };

            return _libraryManager.GetItemsResult(query).Items.ToList();
        }

        private List<long> GetSeasonIds(long seriesId)
        {
            var series = _libraryManager.GetItemById(seriesId);
            if (series == null) return new List<long>();

            var query = new InternalItemsQuery
            {
                AncestorIds = new[] { series.InternalId },
                IncludeItemTypes = new[] { "Season" },
                IsVirtualItem = false,
                OrderBy = new[] { (ItemSortBy.IndexNumber, SortOrder.Ascending) }
            };

            return _libraryManager.GetItemsResult(query).Items
                .Select(s => s.InternalId)
                .ToList();
        }

        private string ResolveFfmpegPath()
        {
            // Emby exposes its bundled ffmpeg through IMediaEncoder, but we can also
            // check standard locations as fallback.
            var candidates = new[]
            {
                "/usr/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                @"C:\Program Files\Emby Server\system\ffmpeg.exe",
                "ffmpeg"  // rely on PATH as last resort
            };

            foreach (var c in candidates)
            {
                if (System.IO.File.Exists(c)) return c;
            }
            return "ffmpeg";
        }
    }
}
