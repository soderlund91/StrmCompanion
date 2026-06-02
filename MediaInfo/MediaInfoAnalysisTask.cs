using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using StrmCompanion.Analysis;
using StrmCompanion.Jobs;

namespace StrmCompanion.MediaInfo
{
    public class MediaInfoAnalysisTask : IAnalysisTask
    {
        public string TaskId => "media-info";
        public string DisplayName => "Media Info";
        public string Description => "Scans .strm files and writes media stream info (resolution, codec, audio, subtitles) to the Emby database.";

        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly JobManager _jobManager;
        private readonly ILogger _logger;

        public MediaInfoAnalysisTask(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            JobManager jobManager,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _itemRepository = itemRepository;
            _jobManager = jobManager;
            _logger = logManager.GetLogger(nameof(MediaInfoAnalysisTask));
        }

        // IAnalysisTask – delegates to StartScan (scope comes from configured library IDs)
        public string StartJob(long seriesId, long? seasonId, CancellationToken cancellationToken)
            => StartScan(cancellationToken);

        public string StartScan(CancellationToken cancellationToken)
        {
            var job = _jobManager.CreateJob(TaskId, 0, "Media Info Scan", null, null);
            var token = _jobManager.GetCancellationToken(job.JobId);
            Task.Run(() => RunAsync(job, token), token);
            return job.JobId;
        }

        public string StartSingleItemScan(long itemId, CancellationToken cancellationToken)
        {
            var item = _libraryManager.GetItemById(itemId);
            if (item == null) return null;

            var job = _jobManager.CreateJob(TaskId, 0, $"Auto-scan: {item.Name}", null, null);
            var token = _jobManager.GetCancellationToken(job.JobId);
            Task.Run(() => RunSingleAsync(job, item, token), token);
            return job.JobId;
        }

        private async Task RunAsync(JobInfo job, CancellationToken token)
        {
            try
            {
                var config = Plugin.Instance.Configuration;
                _jobManager.UpdateProgress(job.JobId, 0, "Querying library...");

                var items = QueryPendingItems(config);
                int total = items.Count;

                if (total == 0)
                {
                    _jobManager.UpdateProgress(job.JobId, 100, "No new items to scan");
                    _jobManager.CompleteJob(job.JobId);
                    return;
                }

                _jobManager.UpdateProgress(job.JobId, 0, $"Found {total} item(s) to scan");

                var probeService = CreateProbeService(config);
                int processed = 0;
                var semaphore = new SemaphoreSlim(Math.Max(1, config.MediaInfoConcurrency), Math.Max(1, config.MediaInfoConcurrency));

                var tasks = items.Select(item => Task.Run(() =>
                {
                    token.ThrowIfCancellationRequested();
                    semaphore.Wait(token);
                    try
                    {
                        token.ThrowIfCancellationRequested();
                        try
                        {
                            ScanItemCore(item, job, probeService, token);
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            _logger.Warn("StrmCompanion: scan failed for '{0}': {1}", item.Name, ex.Message);
                            _jobManager.AddEpisodeResult(job.JobId, item.InternalId, "Error: " + ex.Message);
                        }
                    }
                    finally
                    {
                        semaphore.Release();
                        int current = Interlocked.Increment(ref processed);
                        _jobManager.UpdateProgress(job.JobId, (double)current / total * 100.0, $"Scanned {current}/{total}");
                    }
                }, token)).ToList();

                await Task.WhenAll(tasks).ConfigureAwait(false);
                _jobManager.CompleteJob(job.JobId);
                _logger.Info("StrmCompanion MediaInfo: scan complete, {0} items processed", total);
            }
            catch (OperationCanceledException)
            {
                _logger.Info("StrmCompanion MediaInfo: job {0} cancelled", job.JobId);
                _jobManager.CancelJob(job.JobId);
            }
            catch (Exception ex)
            {
                _logger.Error("StrmCompanion MediaInfo: job {0} failed: {1}", job.JobId, ex.Message);
                _jobManager.FailJob(job.JobId, ex.Message);
            }
        }

        private async Task RunSingleAsync(JobInfo job, BaseItem item, CancellationToken token)
        {
            try
            {
                var config = Plugin.Instance.Configuration;
                var probeService = CreateProbeService(config);

                _jobManager.UpdateProgress(job.JobId, 0, $"Scanning {item.Name}...");
                ScanItemCore(item, job, probeService, token);
                _jobManager.CompleteJob(job.JobId);
            }
            catch (OperationCanceledException) { _jobManager.CancelJob(job.JobId); }
            catch (Exception ex) { _jobManager.FailJob(job.JobId, ex.Message); }

            await Task.CompletedTask;
        }

        private void ScanItemCore(BaseItem item, JobInfo job, MediaInfoProbeService probeService, CancellationToken token)
        {
            var streams = probeService.Probe(item.Path, item.Name, out var error, token);

            if (streams != null && streams.Count > 0)
            {
                try
                {
                    _itemRepository.SaveMediaStreams(item.InternalId, streams, CancellationToken.None);
                }
                catch (Exception ex)
                {
                    _logger.Warn("StrmCompanion: SaveMediaStreams failed for '{0}': {1}", item.Name, ex.Message);
                }

                var video = streams.FirstOrDefault(s => s.Type == MediaStreamType.Video);
                var summary = video != null
                    ? $"{video.Width}x{video.Height} {video.Codec}"
                    : "OK";
                _jobManager.AddEpisodeResult(job.JobId, item.InternalId, summary);
            }
            else
            {
                _jobManager.AddEpisodeResult(job.JobId, item.InternalId, error ?? "No streams found");
            }
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

            var result = _libraryManager.GetItemsResult(query);

            return result.Items
                .Where(i => i.Path != null && i.Path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                .Where(i => !HasMediaStreams(i.InternalId))
                .ToList();
        }

        internal bool HasMediaStreams(long itemId)
        {
            try
            {
                var streams = _itemRepository.GetMediaStreams(new MediaStreamQuery { ItemId = itemId }, System.Threading.CancellationToken.None);
                return streams != null && streams.Count > 0;
            }
            catch
            {
                return false;
            }
        }

        private MediaInfoProbeService CreateProbeService(PluginConfiguration config)
        {
            var service = new MediaInfoProbeService(_logger);
            var ffmpegPath = string.IsNullOrEmpty(config.FfmpegPathOverride)
                ? ResolveFfmpegPath()
                : config.FfmpegPathOverride;
            service.SetFfprobePath(DeriveFfprobePath(ffmpegPath));
            return service;
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

        private static string ResolveFfmpegPath()
        {
            var candidates = new[]
            {
                "/usr/bin/ffmpeg",
                "/usr/local/bin/ffmpeg",
                @"C:\Program Files\Emby Server\system\ffmpeg.exe",
                "ffmpeg"
            };
            foreach (var c in candidates)
                if (File.Exists(c)) return c;
            return "ffmpeg";
        }

        internal static string DeriveFfprobePath(string ffmpegPath)
        {
            if (string.IsNullOrEmpty(ffmpegPath)) return "ffprobe";
            var dir = Path.GetDirectoryName(ffmpegPath) ?? string.Empty;
            var ext = Path.GetExtension(ffmpegPath);
            var candidate = string.IsNullOrEmpty(dir)
                ? "ffprobe" + ext
                : Path.Combine(dir, "ffprobe" + ext);
            return File.Exists(candidate) ? candidate : "ffprobe";
        }
    }
}
