using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.IO;
using MediaBrowser.Model.Logging;
using StrmCompanion.Jobs;

namespace StrmCompanion.MergeVersion
{
    public class SplitVersionService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IFileSystem _fileSystem;
        private readonly JobManager _jobManager;
        private readonly ILogger _logger;

        public SplitVersionService(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IFileSystem fileSystem,
            JobManager jobManager,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _fileSystem = fileSystem;
            _jobManager = jobManager;
            _logger = logManager.GetLogger(nameof(SplitVersionService));
        }

        public string StartSplit(long seriesId, long? seasonId)
        {
            var series = _libraryManager.GetItemById(seriesId) as Series;
            if (series == null)
                throw new ArgumentException("Series not found: " + seriesId);

            string seasonName = null;
            if (seasonId.HasValue)
            {
                var season = _libraryManager.GetItemById(seasonId.Value) as Season;
                if (season == null)
                    throw new ArgumentException("Season not found: " + seasonId.Value);
                seasonName = season.Name;
            }

            var job = _jobManager.CreateJob("split-version", seriesId, series.Name, seasonId, seasonName);
            var token = _jobManager.GetCancellationToken(job.JobId);
            // Token is deliberately not passed to Task.Run: an early cancel must still reach RunJob's handler.
            Task.Run(() => RunSeries(job, series, seasonId, token));
            return job.JobId;
        }

        public string StartSplitMovie(long movieId)
        {
            var movie = _libraryManager.GetItemById(movieId) as Movie;
            if (movie == null)
                throw new ArgumentException("Movie not found: " + movieId);

            var job = _jobManager.CreateJob("split-version", movieId, movie.Name);
            var token = _jobManager.GetCancellationToken(job.JobId);
            Task.Run(() => RunMovie(job, movie, token));
            return job.JobId;
        }

        private void RunSeries(JobInfo job, Series series, long? seasonId, CancellationToken ct)
        {
            RunJob(job, ct, () =>
            {
                _jobManager.UpdateProgress(job.JobId, 0, "Finding episodes...");

                var episodes = _libraryManager.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    IsVirtualItem = false,
                    AncestorIds = new[] { seasonId ?? series.InternalId }
                }).OfType<Episode>()
                  .Where(e => !e.IsSecondaryMergedItemInSameFolder)
                  .ToList();

                _logger.Info("StrmCompanion SplitVersion: found {0} episode(s) to check in series {1} (season {2})",
                    episodes.Count, series.InternalId, seasonId);

                var processed = new HashSet<long>();
                int total = episodes.Count;
                int done = 0;

                foreach (var ep in episodes)
                {
                    ct.ThrowIfCancellationRequested();
                    done++;
                    _jobManager.UpdateProgress(job.JobId, (double)done / Math.Max(1, total) * 100.0,
                        $"Processing {done}/{total}...");

                    if (processed.Contains(ep.InternalId)) continue;

                    var altIds = SplitOne(ep, series.Name + " — " + ep.Name, job);
                    processed.Add(ep.InternalId);
                    foreach (var altId in altIds) processed.Add(altId);
                }
            });
        }

        private void RunMovie(JobInfo job, Movie movie, CancellationToken ct)
        {
            RunJob(job, ct, () =>
            {
                _jobManager.UpdateProgress(job.JobId, 0, "Checking movie...");
                ct.ThrowIfCancellationRequested();
                SplitOne(movie, movie.Name, job);
            });
        }

        private void RunJob(JobInfo job, CancellationToken ct, Action body)
        {
            try
            {
                ct.ThrowIfCancellationRequested();
                body();
                _jobManager.CompleteJob(job.JobId);
                _logger.Info("StrmCompanion SplitVersion: complete");
            }
            catch (OperationCanceledException)
            {
                _logger.Info("StrmCompanion SplitVersion: job {0} cancelled", job.JobId);
                _jobManager.CancelJob(job.JobId);
            }
            catch (Exception ex)
            {
                _logger.Error("StrmCompanion SplitVersion: job {0} failed: {1}", job.JobId, ex.Message);
                _jobManager.FailJob(job.JobId, ex.Message);
            }
        }

        // Splits one merged item, records the result, and queues refreshes for all resulting items.
        // Returns the alternate version IDs that were part of the item (empty if nothing to split).
        private List<long> SplitOne(Video item, string title, JobInfo job)
        {
            var altIds = item.GetAlternateVersionIds();
            if (altIds.Count == 0) return altIds;

            try
            {
                _libraryManager.SplitItems(item);
            }
            catch (Exception ex)
            {
                _logger.Warn("StrmCompanion SplitVersion: SplitItems failed for '{0}': {1}", title, ex.Message);
                _jobManager.AddEpisodeResult(job.JobId, item.InternalId, "Error: " + ex.Message);
                _jobManager.SetItemTitle(job.JobId, item.InternalId, title);
                return altIds;
            }

            var label = $"Split {altIds.Count + 1} versions";
            _logger.Info("StrmCompanion SplitVersion: {0} — {1}", title, label);
            _jobManager.AddEpisodeResult(job.JobId, item.InternalId, label);
            _jobManager.SetItemTitle(job.JobId, item.InternalId, title);

            // Queued rather than awaited so a slow or cancelled refresh cannot block the job or
            // mask a split that already happened.
            QueueRefresh(item.InternalId);
            foreach (var altId in altIds) QueueRefresh(altId);

            return altIds;
        }

        private void QueueRefresh(long itemId)
        {
            try
            {
                _providerManager.QueueRefresh(itemId,
                    RefreshOptionsFactory.FullMetadataRefresh(_logger, _fileSystem), RefreshPriority.Normal);
            }
            catch (Exception ex)
            {
                _logger.Warn("StrmCompanion SplitVersion: could not queue refresh for item {0}: {1}", itemId, ex.Message);
            }
        }
    }
}
