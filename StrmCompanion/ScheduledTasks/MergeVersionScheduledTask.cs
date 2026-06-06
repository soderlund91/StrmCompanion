using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Tasks;
using StrmCompanion.Jobs;

namespace StrmCompanion.ScheduledTasks
{
    public class MergeVersionScheduledTask : IScheduledTask, IConfigurableScheduledTask
    {
        private readonly ILogger _logger;

        public static readonly AsyncLocal<CollectionFolder> CurrentScanLibrary = new AsyncLocal<CollectionFolder>();
        private static string _pendingJobId;
        public static void SetPendingJobId(string jobId) => _pendingJobId = jobId;

        public MergeVersionScheduledTask(ILogManager logManager)
        {
            _logger = logManager.GetLogger(nameof(MergeVersionScheduledTask));
        }

        public string Name => "Merge Versions";
        public string Description => "Merges duplicate movies and episodes that share the same IMDb, TMDB or TVDb ID as Emby alternate versions.";
        public string Category => "StrmCompanion";
        public string Key => "StrmCompanionMergeVersions";

        public bool IsHidden => false;
        public bool IsEnabled => true;
        public bool IsLogged => true;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
        {
            yield return new TaskTriggerInfo
            {
                Type = "DailyTrigger",
                TimeOfDayTicks = TimeSpan.FromHours(4).Ticks
            };
        }

        public async Task Execute(CancellationToken cancellationToken, IProgress<double> progress)
        {
            var svc = PluginEntryPoint.MergeVersionService;
            var jobManager = PluginEntryPoint.JobManager;
            if (svc == null || jobManager == null)
            {
                _logger.Warn("StrmCompanion MergeVersion scheduled task: services not initialized");
                progress.Report(100);
                return;
            }

            CurrentScanLibrary.Value = null;
            var pendingId = System.Threading.Interlocked.Exchange(ref _pendingJobId, null);
            var jobId = svc.StartMerge(null, cancellationToken, pendingId);
            await WaitForJob(jobManager, jobId, progress, cancellationToken).ConfigureAwait(false);
        }

        private static async Task WaitForJob(JobManager jobManager, string jobId, IProgress<double> progress, CancellationToken cancellationToken)
        {
            try
            {
                while (!cancellationToken.IsCancellationRequested)
                {
                    var job = jobManager.GetJob(jobId);
                    if (job == null) break;

                    progress.Report(job.ProgressPercent);

                    if (job.Status == JobStatus.Completed || job.Status == JobStatus.Failed || job.Status == JobStatus.Cancelled)
                        break;

                    await Task.Delay(1000, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                jobManager.CancelJob(jobId);
            }
            finally
            {
                progress.Report(100);
            }
        }
    }
}
