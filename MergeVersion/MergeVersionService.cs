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
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Querying;
using StrmCompanion.Jobs;
using StrmCompanion.ScheduledTasks;

namespace StrmCompanion.MergeVersion
{
    public class MergeVersionService
    {
        private static readonly string[] ProviderKeys = { "Tmdb", "Imdb", "Tvdb" };

        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IDirectoryService _directoryService;
        private readonly JobManager _jobManager;
        private readonly ILogger _logger;

        public MergeVersionService(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IDirectoryService directoryService,
            JobManager jobManager,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _directoryService = directoryService;
            _jobManager = jobManager;
            _logger = logManager.GetLogger(nameof(MergeVersionService));
        }

        public string StartMerge(CollectionFolder currentScanLibrary, CancellationToken externalCt)
        {
            var job = _jobManager.CreateJob("merge-version", 0, "Merge Version", null, null);
            var token = _jobManager.GetCancellationToken(job.JobId);
            Task.Run(() => RunAsync(job, currentScanLibrary, token), token);
            return job.JobId;
        }

        public string StartMergeForLibrary(CollectionFolder library, bool isEpisode, CancellationToken externalCt)
        {
            var job = _jobManager.CreateJob("merge-version", 0, "Merge Version (auto-detect)", null, null);
            var token = _jobManager.GetCancellationToken(job.JobId);
            Task.Run(() => RunLibraryMergeAsync(job, library, isEpisode, token), token);
            return job.JobId;
        }

        private Task RunLibraryMergeAsync(JobInfo job, CollectionFolder library, bool isEpisode, CancellationToken ct)
        {
            try
            {
                var parents = new[] { library.InternalId };
                if (isEpisode)
                {
                    _logger.Info("StrmCompanion MergeVersion auto-detect: merging episodes in '{0}'", library.Name);
                    ExecuteMergeEpisodes(parents, job, ct);
                }
                else
                {
                    _logger.Info("StrmCompanion MergeVersion auto-detect: merging movies in '{0}'", library.Name);
                    ExecuteMergeMovies(parents, job, ct);
                }
                _jobManager.CompleteJob(job.JobId);
            }
            catch (OperationCanceledException)
            {
                _jobManager.CancelJob(job.JobId);
            }
            catch (Exception ex)
            {
                _logger.Error("StrmCompanion MergeVersion auto-detect: job {0} failed: {1}", job.JobId, ex.Message);
                _jobManager.FailJob(job.JobId, ex.Message);
            }
            return Task.CompletedTask;
        }

        private async Task RunAsync(JobInfo job, CollectionFolder currentScanLibrary, CancellationToken ct)
        {
            try
            {
                var cfg = Plugin.Instance.Configuration;

                // Phase 1: Series (if enabled)
                if (cfg.MergeSeriesScope != "Disabled")
                {
                    _jobManager.UpdateProgress(job.JobId, 0, "Processing series...");
                    _logger.Info("StrmCompanion MergeVersion: series scope = {0}", cfg.MergeSeriesScope);

                    var tvLibraries = PrepareMergeSeries();
                    foreach (var lib in tvLibraries)
                    {
                        ct.ThrowIfCancellationRequested();
                        var parents = new[] { lib.InternalId };

                        var duplicates = FindDuplicateSeries(parents);
                        _logger.Info("StrmCompanion MergeVersion: found {0} duplicate series in '{1}'",
                            duplicates.Count, lib.Name);
                        foreach (var series in duplicates)
                        {
                            ct.ThrowIfCancellationRequested();
                            await RefreshSeriesAsync(series, ct).ConfigureAwait(false);
                        }

                        var inconsistent = FindInconsistentSeries(parents);
                        _logger.Info("StrmCompanion MergeVersion: found {0} inconsistent series in '{1}'",
                            inconsistent.Count, lib.Name);
                        foreach (var series in inconsistent)
                        {
                            ct.ThrowIfCancellationRequested();
                            await RefreshSeriesAsync(series, ct).ConfigureAwait(false);
                        }
                    }

                    // Merge episode alternate versions in all TV libraries
                    var allTvLibs = GetAllTvLibraries();
                    _logger.Info("StrmCompanion MergeVersion: merging episode versions across {0} TV library(s)", allTvLibs.Count);
                    foreach (var lib in allTvLibs)
                    {
                        ct.ThrowIfCancellationRequested();
                        ExecuteMergeEpisodes(new[] { lib.InternalId }, job, ct);
                    }
                }

                // Phase 2: Movies
                _jobManager.UpdateProgress(job.JobId, 5, "Processing movies...");
                _logger.Info("StrmCompanion MergeVersion: movies scope = {0}", Plugin.Instance.Configuration.MergeMoviesScope);

                var movieGroups = PrepareMergeMovies(currentScanLibrary);
                int total = movieGroups.Length;
                int processed = 0;

                if (total == 0)
                {
                    _jobManager.UpdateProgress(job.JobId, 100, "No movie libraries found");
                    _jobManager.CompleteJob(job.JobId);
                    return;
                }

                foreach (var parents in movieGroups)
                {
                    ct.ThrowIfCancellationRequested();
                    ExecuteMergeMovies(parents, job, ct);
                    processed++;
                    _jobManager.UpdateProgress(job.JobId,
                        5 + (double)processed / total * 95.0,
                        $"Processed {processed}/{total} library group(s)");
                }

                _jobManager.CompleteJob(job.JobId);
                _logger.Info("StrmCompanion MergeVersion: complete");
            }
            catch (OperationCanceledException)
            {
                _logger.Info("StrmCompanion MergeVersion: job {0} cancelled", job.JobId);
                _jobManager.CancelJob(job.JobId);
            }
            catch (Exception ex)
            {
                _logger.Error("StrmCompanion MergeVersion: job {0} failed: {1}", job.JobId, ex.Message);
                _jobManager.FailJob(job.JobId, ex.Message);
            }

            await Task.CompletedTask.ConfigureAwait(false);
        }

        private List<CollectionFolder> PrepareMergeSeries()
        {
            var result = _libraryManager.QueryItems(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "CollectionFolder" }
            });

            var tvLibraries = new List<CollectionFolder>();
            foreach (var item in result.Items)
            {
                var cf = item as CollectionFolder;
                if (cf == null) continue;
                var icf = item as ICollectionFolder;
                if (icf == null || icf.CollectionType != "tvshows") continue;

                try
                {
                    var opts = _libraryManager.GetLibraryOptions(cf);
                    if (opts?.EnableAutomaticSeriesGrouping == true)
                        tvLibraries.Add(cf);
                }
                catch (Exception ex)
                {
                    _logger.Warn("StrmCompanion MergeVersion: could not read library options for '{0}': {1}",
                        cf.Name, ex.Message);
                }
            }

            _logger.Info("StrmCompanion MergeVersion: TV libraries with auto-grouping: {0}",
                string.Join(", ", tvLibraries.Select(l => l.Name)));
            return tvLibraries;
        }

        private List<Series> FindDuplicateSeries(long[] parents)
        {
            var allSeries = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Series" },
                IsVirtualItem = false,
                AncestorIds = parents
            }).OfType<Series>().ToList();

            var parentMap = allSeries.ToDictionary(s => s.InternalId, s => s.InternalId);
            var keyToIds = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);

            foreach (var s in allSeries)
            {
                if (s.ProviderIds == null) continue;
                foreach (var pk in ProviderKeys)
                {
                    string val;
                    if (!s.ProviderIds.TryGetValue(pk, out val) || string.IsNullOrEmpty(val)) continue;
                    var composite = pk + ":" + val.Trim();
                    List<long> list;
                    if (!keyToIds.TryGetValue(composite, out list))
                        keyToIds[composite] = list = new List<long>();
                    list.Add(s.InternalId);
                }
            }

            foreach (var list in keyToIds.Values)
            {
                if (list.Count < 2) continue;
                var root = list[0];
                for (int i = 1; i < list.Count; i++)
                    Union(root, list[i], parentMap);
            }

            var idToSeries = allSeries.ToDictionary(s => s.InternalId);
            return parentMap.Keys
                .GroupBy(id => Find(id, parentMap))
                .Where(g => g.Count() >= 2)
                .Select(g => idToSeries[g.Key])
                .ToList();
        }

        private List<Series> FindInconsistentSeries(long[] parents)
        {
            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = false,
                AncestorIds = parents
            }).OfType<Episode>().ToList();

            var inconsistentIds = new HashSet<long>();
            foreach (var ep in episodes)
            {
                if (string.IsNullOrEmpty(ep.SeriesPresentationUniqueKey)) continue;
                var series = _libraryManager.GetItemById(ep.SeriesId) as Series;
                if (series == null) continue;
                if (!string.Equals(ep.SeriesPresentationUniqueKey, series.PresentationUniqueKey,
                    StringComparison.Ordinal))
                {
                    inconsistentIds.Add(series.InternalId);
                }
            }

            return inconsistentIds
                .Select(id => _libraryManager.GetItemById(id) as Series)
                .Where(s => s != null)
                .ToList();
        }

        private List<CollectionFolder> GetAllTvLibraries()
        {
            return _libraryManager.QueryItems(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "CollectionFolder" }
            }).Items
                .OfType<CollectionFolder>()
                .Where(cf => (cf as ICollectionFolder)?.CollectionType == "tvshows")
                .ToList();
        }

        private void ExecuteMergeEpisodes(long[] parents, JobInfo job, CancellationToken ct)
        {
            var episodes = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Episode" },
                IsVirtualItem = false,
                AncestorIds = parents
            }).OfType<Episode>()
              .Where(e => !e.IsSecondaryMergedItemInSameFolder)
              .ToList();

            var groups = episodes
                .Where(e => e.ParentIndexNumber.HasValue && e.IndexNumber.HasValue)
                .GroupBy(e => new
                {
                    SeriesKey = !string.IsNullOrEmpty(e.SeriesPresentationUniqueKey)
                        ? e.SeriesPresentationUniqueKey
                        : e.SeriesId.ToString(),
                    Season  = e.ParentIndexNumber.Value,
                    Episode = e.IndexNumber.Value
                })
                .Where(g => g.Count() >= 2)
                .Select(g => g.Cast<BaseItem>().ToList())
                .Where(group => !AlreadyMerged(group))
                .ToList();

            _logger.Info("StrmCompanion MergeVersion: found {0} episode group(s) to merge in this batch", groups.Count);

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                MergeGroup(group, job);
            }
        }

        private long[][] PrepareMergeMovies(CollectionFolder currentScanLibrary)
        {
            var cfg = Plugin.Instance.Configuration;

            if (currentScanLibrary != null)
            {
                _logger.Info("StrmCompanion MergeVersion: post-scan trigger for library '{0}'", currentScanLibrary.Name);
                return new[] { new[] { currentScanLibrary.InternalId } };
            }

            var allMovieLibs = _libraryManager.QueryItems(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "CollectionFolder" }
            }).Items
                .Where(f => (f as ICollectionFolder)?.CollectionType == "movies")
                .ToList();

            if (allMovieLibs.Count == 0)
                return new long[0][];

            _logger.Info("StrmCompanion MergeVersion: movie libraries: {0}",
                string.Join(", ", allMovieLibs.Select(f => f.Name)));

            if (cfg.MergeMoviesScope == "LibraryScope")
                return allMovieLibs.Select(f => new[] { f.InternalId }).ToArray();

            // GlobalScope (default): all libraries merged together
            return new[] { allMovieLibs.Select(f => f.InternalId).ToArray() };
        }

        private void ExecuteMergeMovies(long[] parents, JobInfo job, CancellationToken ct)
        {
            var movies = _libraryManager.GetItemList(new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie" },
                IsVirtualItem = false,
                AncestorIds = parents
            }).OfType<Movie>()
              .Where(m => !m.IsSecondaryMergedItemInSameFolder)
              .ToList();

            var groups = FindMergeGroups(movies.Cast<BaseItem>().ToList());
            _logger.Info("StrmCompanion MergeVersion: found {0} movie group(s) to merge in this batch", groups.Count);

            foreach (var group in groups)
            {
                ct.ThrowIfCancellationRequested();
                MergeGroup(group, job);
            }
        }

        private async Task RefreshSeriesAsync(Series series, CancellationToken ct)
        {
            try
            {
                var libraryOptions = _libraryManager.GetLibraryOptions(series);
                var refreshOptions = new MetadataRefreshOptions(_directoryService)
                {
                    MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                    ImageRefreshMode    = MetadataRefreshMode.ValidationOnly,
                    ReplaceAllImages    = false
                };
                await _providerManager
                    .RefreshSingleItem(series, refreshOptions, new BaseItem[0], libraryOptions, ct)
                    .ConfigureAwait(false);
                _logger.Info("StrmCompanion MergeVersion: refreshed series '{0}'", series.Name);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warn("StrmCompanion MergeVersion: refresh failed for series '{0}': {1}",
                    series.Name, ex.Message);
            }
        }

        // Groups items that share any provider ID using union-find so transitively linked items are merged.
        private List<List<BaseItem>> FindMergeGroups(List<BaseItem> items)
        {
            var parentMap = new Dictionary<long, long>(items.Count);
            foreach (var item in items)
                parentMap[item.InternalId] = item.InternalId;

            var keyToIds = new Dictionary<string, List<long>>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                if (item.ProviderIds == null) continue;
                foreach (var pk in ProviderKeys)
                {
                    string val;
                    if (!item.ProviderIds.TryGetValue(pk, out val) || string.IsNullOrEmpty(val)) continue;
                    var composite = pk + ":" + val.Trim();
                    List<long> list;
                    if (!keyToIds.TryGetValue(composite, out list))
                        keyToIds[composite] = list = new List<long>();
                    list.Add(item.InternalId);
                }
            }

            foreach (var list in keyToIds.Values)
            {
                if (list.Count < 2) continue;
                var root = list[0];
                for (int i = 1; i < list.Count; i++)
                    Union(root, list[i], parentMap);
            }

            var idToItem = items.ToDictionary(i => i.InternalId);

            return parentMap.Keys
                .GroupBy(id => Find(id, parentMap))
                .Where(g => g.Count() >= 2)
                .Select(g => g.Select(id => idToItem[id]).ToList())
                .Where(group => !AlreadyMerged(group))
                .ToList();
        }

        private static bool AlreadyMerged(List<BaseItem> group)
        {
            var video = group[0] as Video;
            if (video == null) return false;
            var altIds = new HashSet<long>(video.GetAlternateVersionIds());
            return group.Skip(1).All(item => altIds.Contains(item.InternalId));
        }

        private void MergeGroup(List<BaseItem> group, JobInfo job)
        {
            var primary = group[0];
            var title   = GetDisplayTitle(primary);
            try
            {
                _libraryManager.MergeItems(group.ToArray());
                var label = $"Merged {group.Count} versions";
                _logger.Info("StrmCompanion MergeVersion: {0} — {1}", title, label);
                _jobManager.AddEpisodeResult(job.JobId, primary.InternalId, label);
                _jobManager.SetItemTitle(job.JobId, primary.InternalId, title);
            }
            catch (Exception ex)
            {
                _logger.Warn("StrmCompanion MergeVersion: MergeItems failed for '{0}': {1}",
                    title, ex.Message);
                _jobManager.AddEpisodeResult(job.JobId, primary.InternalId, "Error: " + ex.Message);
                _jobManager.SetItemTitle(job.JobId, primary.InternalId, title);
            }
        }

        private string GetDisplayTitle(BaseItem item)
        {
            if (item is Episode ep)
            {
                var series = _libraryManager.GetItemById(ep.SeriesId);
                return (series?.Name ?? ep.SeriesName ?? "Unknown series") + " — " + ep.Name;
            }
            return item.Name ?? item.InternalId.ToString();
        }

        private static long Find(long x, Dictionary<long, long> parent)
        {
            if (parent[x] == x) return x;
            return parent[x] = Find(parent[x], parent);
        }

        private static void Union(long x, long y, Dictionary<long, long> parent)
        {
            var rx = Find(x, parent);
            var ry = Find(y, parent);
            if (rx != ry) parent[rx] = ry;
        }
    }
}
