using System;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using StrmCompanion.ScheduledTasks;

namespace StrmCompanion.MergeVersion
{
    public class TriggerMergeVersionTask : ILibraryPostScanTask
    {
        private readonly ILogger _logger;

        public TriggerMergeVersionTask(ILogManager logManager)
        {
            _logger = logManager.GetLogger(nameof(TriggerMergeVersionTask));
        }

        public Task Run(IProgress<double> progress, CancellationToken cancellationToken)
        {
            try
            {
                var cfg = Plugin.Instance?.Configuration;
                if (cfg == null) return Task.CompletedTask;

                if (cfg.MergeMoviesScope == "Disabled" && cfg.MergeSeriesScope == "Disabled")
                    return Task.CompletedTask;

                var currentLibrary = MergeVersionScheduledTask.CurrentScanLibrary.Value;
                var svc = PluginEntryPoint.MergeVersionService;
                if (svc == null) return Task.CompletedTask;

                _logger.Info("StrmCompanion TriggerMergeVersionTask: triggering post-scan merge");
                svc.StartMerge(currentLibrary, cancellationToken);
                progress.Report(100);
            }
            catch (Exception ex)
            {
                _logger.Warn("StrmCompanion TriggerMergeVersionTask: failed: {0}", ex.Message);
            }

            return Task.CompletedTask;
        }
    }
}
