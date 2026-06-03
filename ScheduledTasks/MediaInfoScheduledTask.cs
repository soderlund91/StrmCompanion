using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Providers;
using MediaBrowser.Model.Tasks;
using StrmCompanion.Jobs;

namespace StrmCompanion.ScheduledTasks
{
    public class MediaInfoScheduledTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILibraryManager _libraryManager;
        private readonly IProviderManager _providerManager;
        private readonly IDirectoryService _directoryService;
        private readonly ILogger _logger;

        public static bool IsRunning { get; private set; }

        public MediaInfoScheduledTask(
            ILibraryManager libraryManager,
            IProviderManager providerManager,
            IDirectoryService directoryService,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _providerManager = providerManager;
            _directoryService = directoryService;
            _logger = logManager.GetLogger(nameof(MediaInfoScheduledTask));
        }

        public string Name => "Extract Media Info";
        public string Description => "Scans .strm files and writes media stream info (resolution, codec, audio, subtitles) to the Emby database.";
        public string Category => "StrmCompanion";
        public string Key => "StrmCompanionMediaInfoExtract";

        public bool IsHidden => false;
        public bool IsEnabled => true;
        public bool IsLogged => true;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = "DailyTrigger",
                TimeOfDayTicks = TimeSpan.FromHours(3).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            if (IsRunning)
            {
                _logger.Info("StrmCompanion MediaInfo: already running, skipping scheduled execution");
                progress.Report(100);
                return;
            }

            IsRunning = true;
            _logger.Info("StrmCompanion MediaInfo: scheduled task starting");

            var jobManager = PluginEntryPoint.JobManager;
            var job = jobManager?.CreateJob("media-info", 0, "Media Info Scan", null, null);

            try
            {
                var config = Plugin.Instance.Configuration;
                progress.Report(0);
                jobManager?.UpdateProgress(job?.JobId, 0, "Querying library...");

                var items = QueryPendingItems(config);
                int total = items.Count;

                if (total == 0)
                {
                    _logger.Info("StrmCompanion MediaInfo: no items to scan");
                    progress.Report(100);
                    jobManager?.UpdateProgress(job?.JobId, 100, "No new items to scan");
                    jobManager?.CompleteJob(job?.JobId);
                    return;
                }

                jobManager?.UpdateProgress(job?.JobId, 0, $"Found {total} item(s) to scan");
                _logger.Info("StrmCompanion MediaInfo: found {0} items to scan", total);

                int processed = 0;
                var semaphore = new SemaphoreSlim(
                    Math.Max(1, config.MediaInfoConcurrency),
                    Math.Max(1, config.MediaInfoConcurrency));

                var tasks = items.Select(item => Task.Run(async () =>
                {
                    try
                    {
                        await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        return;
                    }

                    try
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await ScanItemCoreAsync(item, job, cancellationToken).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warn("StrmCompanion: scan failed for '{0}': {1}", item.Name, ex.Message);
                        jobManager?.AddEpisodeResult(job?.JobId, item.InternalId, "Error: " + ex.Message);
                    }
                    finally
                    {
                        semaphore.Release();
                        int current = Interlocked.Increment(ref processed);
                        double pct = (double)current / total * 100.0;
                        progress.Report(pct);
                        jobManager?.UpdateProgress(job?.JobId, pct, $"Scanned {current}/{total}");
                        _logger.Info("StrmCompanion MediaInfo: progress {0}/{1} - {2}", current, total, item.Path);
                    }
                }, cancellationToken)).ToList();

                await Task.WhenAll(tasks).ConfigureAwait(false);

                progress.Report(100);
                jobManager?.CompleteJob(job?.JobId);
                _logger.Info("StrmCompanion MediaInfo: scheduled task complete, {0} item(s) processed", total);
            }
            catch (OperationCanceledException)
            {
                _logger.Info("StrmCompanion MediaInfo: scheduled task cancelled");
                jobManager?.CancelJob(job?.JobId);
                progress.Report(100);
            }
            catch (Exception ex)
            {
                _logger.Error("StrmCompanion MediaInfo: scheduled task failed: {0}", ex.Message);
                jobManager?.FailJob(job?.JobId, ex.Message);
                progress.Report(100);
            }
            finally
            {
                IsRunning = false;
            }
        }

        public string StartScan(CancellationToken cancellationToken)
        {
            var jobManager = PluginEntryPoint.JobManager;
            var job = jobManager.CreateJob("media-info", 0, "Media Info Scan", null, null);
            var token = jobManager.GetCancellationToken(job.JobId);
            Task.Run(() => RunAsync(job, token), token);
            return job.JobId;
        }

        public string StartSingleItemScan(long itemId, CancellationToken cancellationToken)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item == null) return null;

            var jobManager = PluginEntryPoint.JobManager;
            var job = jobManager.CreateJob("media-info", 0, $"Auto-scan: {item.Name}", null, null);
            var token = jobManager.GetCancellationToken(job.JobId);
            Task.Run(() => RunSingleAsync(job, item, token), token);
            return job.JobId;
        }

        private async Task RunAsync(JobInfo job, CancellationToken token)
        {
            var jobManager = PluginEntryPoint.JobManager;
            try
            {
                var config = Plugin.Instance.Configuration;
                jobManager.UpdateProgress(job.JobId, 0, "Querying library...");

                var items = QueryPendingItems(config);
                int total = items.Count;

                if (total == 0)
                {
                    jobManager.UpdateProgress(job.JobId, 100, "No new items to scan");
                    jobManager.CompleteJob(job.JobId);
                    return;
                }

                jobManager.UpdateProgress(job.JobId, 0, $"Found {total} item(s) to scan");

                int processed = 0;
                var semaphore = new SemaphoreSlim(
                    Math.Max(1, config.MediaInfoConcurrency),
                    Math.Max(1, config.MediaInfoConcurrency));

                var tasks = items.Select(item => Task.Run(async () =>
                {
                    token.ThrowIfCancellationRequested();
                    await semaphore.WaitAsync(token).ConfigureAwait(false);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        await ScanItemCoreAsync(item, job, token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.Warn("StrmCompanion: scan failed for '{0}': {1}", item.Name, ex.Message);
                        jobManager.AddEpisodeResult(job.JobId, item.InternalId, "Error: " + ex.Message);
                    }
                    finally
                    {
                        semaphore.Release();
                        int current = Interlocked.Increment(ref processed);
                        jobManager.UpdateProgress(job.JobId, (double)current / total * 100.0, $"Scanned {current}/{total}");
                    }
                }, token)).ToList();

                await Task.WhenAll(tasks).ConfigureAwait(false);
                jobManager.CompleteJob(job.JobId);
                _logger.Info("StrmCompanion MediaInfo: scan complete, {0} item(s) processed", total);
            }
            catch (OperationCanceledException)
            {
                _logger.Info("StrmCompanion MediaInfo: job {0} cancelled", job.JobId);
                jobManager.CancelJob(job.JobId);
            }
            catch (Exception ex)
            {
                _logger.Error("StrmCompanion MediaInfo: job {0} failed: {1}", job.JobId, ex.Message);
                jobManager.FailJob(job.JobId, ex.Message);
            }
        }

        private async Task RunSingleAsync(JobInfo job, BaseItem item, CancellationToken token)
        {
            var jobManager = PluginEntryPoint.JobManager;
            try
            {
                jobManager.UpdateProgress(job.JobId, 0, $"Scanning {item.Name}...");
                await ScanItemCoreAsync(item, job, token).ConfigureAwait(false);
                jobManager.CompleteJob(job.JobId);
            }
            catch (OperationCanceledException) { jobManager.CancelJob(job.JobId); }
            catch (Exception ex) { jobManager.FailJob(job.JobId, ex.Message); }
        }

        private async Task ScanItemCoreAsync(BaseItem item, JobInfo job, CancellationToken token)
        {
            var libraryOptions = _libraryManager.GetLibraryOptions(item);
            var refreshOptions = new MetadataRefreshOptions(_directoryService)
            {
                MetadataRefreshMode = MetadataRefreshMode.FullRefresh,
                ImageRefreshMode    = MetadataRefreshMode.ValidationOnly,
                ReplaceAllImages    = false
            };
            await _providerManager
                .RefreshSingleItem(item, refreshOptions, new BaseItem[0], libraryOptions, token)
                .ConfigureAwait(false);

            var refreshed = _libraryManager.GetItemById(item.Id);
            string summary;
            if (refreshed?.Width > 0)
                summary = $"{refreshed.Width}x{refreshed.Height}";
            else if (refreshed?.RunTimeTicks > 0)
                summary = "OK (audio only)";
            else
                summary = "No media info found";

            var displayName = GetItemDisplayName(item);
            _logger.Info("StrmCompanion: scanned '{0}' — {1}", displayName, summary);
            if (job != null)
            {
                var jobMgr = PluginEntryPoint.JobManager;
                jobMgr?.AddEpisodeResult(job.JobId, item.InternalId, summary);
                jobMgr?.SetItemTitle(job.JobId, item.InternalId, displayName);
            }
        }

        private string GetItemDisplayName(BaseItem item)
        {
            if (item.GetType().Name == "Episode" && item.IndexNumber.HasValue)
            {
                var season = item.ParentId != 0 ? _libraryManager.GetItemById(item.ParentId) : null;
                var series = season != null && season.ParentId != 0
                    ? _libraryManager.GetItemById(season.ParentId)
                    : null;
                var s = item.ParentIndexNumber.HasValue ? item.ParentIndexNumber.Value.ToString("D2") : "??";
                var e = item.IndexNumber.Value.ToString("D2");
                return $"{series?.Name ?? "?"} S{s}E{e} - {item.Name}";
            }
            return item.Name;
        }

        internal bool ItemHasMediaInfo(BaseItem item)
        {
            if (item.Width > 0) return true;
            if (item.RunTimeTicks.HasValue && item.RunTimeTicks > 0) return true;
            return false;
        }

        private List<BaseItem> QueryPendingItems(PluginConfiguration config)
        {
            var libraryIds = ParseLibraryIds(config.MediaInfoLibraryIds);
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "Movie", "Episode" },
                IsVirtualItem = false
            };
            if (libraryIds.Length > 0)
                query.AncestorIds = libraryIds;

            return _libraryManager.GetItemList(query)
                .Where(i => i.Path != null && i.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                .Where(i => !ItemHasMediaInfo(i))
                .ToList();
        }

        public static long[] ParseLibraryIds(string ids)
        {
            if (string.IsNullOrWhiteSpace(ids)) return new long[0];
            return ids.Split(',')
                .Select(s => s.Trim())
                .Where(s => !string.IsNullOrEmpty(s))
                .Select(s => long.TryParse(s, out var id) ? id : 0L)
                .Where(id => id > 0)
                .ToArray();
        }
    }
}
