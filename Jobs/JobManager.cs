using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using MediaBrowser.Model.Logging;

namespace StrmCompanion.Jobs
{
    public class JobManager
    {
        private readonly ConcurrentDictionary<string, JobInfo> _jobs =
            new ConcurrentDictionary<string, JobInfo>();

        private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations =
            new ConcurrentDictionary<string, CancellationTokenSource>();

        private readonly ILogger _logger;

        public JobManager(ILogger logger)
        {
            _logger = logger;
        }

        public JobInfo CreateJob(string taskId, long seriesId, string seriesName, long? seasonId = null, string seasonName = null)
        {
            var job = new JobInfo
            {
                JobId = Guid.NewGuid().ToString("N"),
                TaskId = taskId,
                SeriesInternalId = seriesId,
                SeriesName = seriesName,
                SeasonInternalId = seasonId,
                SeasonName = seasonName,
                Status = JobStatus.Queued,
                StartedAt = DateTime.UtcNow
            };
            _jobs[job.JobId] = job;
            _logger.Info("StrmCompanion: job {0} created for task '{1}', series '{2}'",
                job.JobId, taskId, seriesName);
            return job;
        }

        public CancellationToken GetCancellationToken(string jobId)
        {
            var cts = new CancellationTokenSource();
            _cancellations[jobId] = cts;
            return cts.Token;
        }

        public void UpdateProgress(string jobId, double percent, string activity)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                job.Status = JobStatus.Running;
                job.ProgressPercent = Math.Min(100, Math.Max(0, percent));
                job.CurrentActivity = activity;
            }
        }

        public void AddEpisodeResult(string jobId, long episodeId, string message)
        {
            if (_jobs.TryGetValue(jobId, out var job))
                job.EpisodeResults[episodeId] = message;
        }

        public void CompleteJob(string jobId)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                job.Status = JobStatus.Completed;
                job.ProgressPercent = 100;
                job.CurrentActivity = "Done";
                job.CompletedAt = DateTime.UtcNow;
                _logger.Info("StrmCompanion: job {0} completed", jobId);
            }
            _cancellations.TryRemove(jobId, out _);
        }

        public void FailJob(string jobId, string errorMessage)
        {
            if (_jobs.TryGetValue(jobId, out var job))
            {
                job.Status = JobStatus.Failed;
                job.ErrorMessage = errorMessage;
                job.CompletedAt = DateTime.UtcNow;
                _logger.Error("StrmCompanion: job {0} failed: {1}", jobId, errorMessage);
            }
            _cancellations.TryRemove(jobId, out _);
        }

        public void CancelJob(string jobId)
        {
            if (_cancellations.TryGetValue(jobId, out var cts))
            {
                cts.Cancel();
                _logger.Info("StrmCompanion: job {0} cancelled", jobId);
            }
            if (_jobs.TryGetValue(jobId, out var job))
            {
                job.Status = JobStatus.Cancelled;
                job.CompletedAt = DateTime.UtcNow;
            }
        }

        public void CancelAll()
        {
            foreach (var cts in _cancellations.Values)
            {
                try { cts.Cancel(); } catch { }
            }
        }

        public JobInfo GetJob(string jobId) =>
            _jobs.TryGetValue(jobId, out var job) ? job : null;

        public IEnumerable<JobInfo> GetAllJobs() => _jobs.Values;
    }
}
