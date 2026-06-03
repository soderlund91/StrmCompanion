using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using StrmCompanion.Jobs;

namespace StrmCompanion.Api
{
    // ─── Request/Response DTOs ────────────────────────────────────────────────────

    [Route("/strmcompanion/mergeversion/settings", "GET", Summary = "Get Merge Version settings")]
    [Authenticated]
    public class GetMergeVersionSettings : IReturn<MergeVersionSettingsDto> { }

    [Route("/strmcompanion/mergeversion/settings", "POST", Summary = "Save Merge Version settings")]
    [Authenticated]
    public class SaveMergeVersionSettings : IReturn<MergeVersionSettingsDto>
    {
        public string MergeMoviesScope  { get; set; }
        public string MergeSeriesScope  { get; set; }
        public bool   MergeAutoDetect   { get; set; }
    }

    [Route("/strmcompanion/mergeversion/run", "POST", Summary = "Start a merge version job")]
    [Authenticated]
    public class StartMergeVersion : IReturn<StartJobResponse> { }

    [Route("/strmcompanion/mergeversion/job/{JobId}", "GET", Summary = "Poll merge version job status")]
    [Authenticated]
    public class GetMergeVersionJobStatus : IReturn<JobInfo>
    {
        public string JobId { get; set; }
    }

    [Route("/strmcompanion/mergeversion/job/{JobId}", "DELETE", Summary = "Cancel a merge version job")]
    [Authenticated]
    public class CancelMergeVersionJob : IReturnVoid
    {
        public string JobId { get; set; }
    }

    [Route("/strmcompanion/mergeversion/jobs", "GET", Summary = "List all merge version jobs")]
    [Authenticated]
    public class GetMergeVersionJobs : IReturn<List<JobInfo>> { }

    // ─── DTO ─────────────────────────────────────────────────────────────────────

    public class MergeVersionSettingsDto
    {
        public string MergeMoviesScope { get; set; }
        public string MergeSeriesScope { get; set; }
        public bool   MergeAutoDetect  { get; set; }
    }

    // ─── Service ─────────────────────────────────────────────────────────────────

    public class MergeVersionApiService : BaseApiService
    {
        private readonly ILogger _logger;

        public MergeVersionApiService(ILogManager logManager)
        {
            _logger = logManager.GetLogger(nameof(MergeVersionApiService));
        }

        public object Get(GetMergeVersionSettings request)
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            return ToSettingsDto(cfg);
        }

        public object Post(SaveMergeVersionSettings request)
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            cfg.MergeMoviesScope = request.MergeMoviesScope ?? "GlobalScope";
            cfg.MergeSeriesScope = request.MergeSeriesScope ?? "Disabled";
            cfg.MergeAutoDetect  = request.MergeAutoDetect;
            Plugin.Instance?.SaveConfiguration();
            return ToSettingsDto(cfg);
        }

        public object Post(StartMergeVersion request)
        {
            var svc = PluginEntryPoint.MergeVersionService;
            if (svc == null)
                throw new Exception("MergeVersionService is not initialized");

            var jobId = svc.StartMerge(null, CancellationToken.None);
            return new StartJobResponse { JobId = jobId };
        }

        public object Get(GetMergeVersionJobStatus request)
        {
            var job = PluginEntryPoint.JobManager?.GetJob(request.JobId);
            if (job == null)
                throw new ArgumentException("Job not found: " + request.JobId);
            return job;
        }

        public void Delete(CancelMergeVersionJob request)
        {
            PluginEntryPoint.JobManager?.CancelJob(request.JobId);
        }

        public object Get(GetMergeVersionJobs request)
        {
            var jobs = PluginEntryPoint.JobManager?.GetAllJobs()
                .Where(j => j.TaskId == "merge-version")
                .OrderByDescending(j => j.StartedAt)
                .ToList();
            return jobs ?? new List<JobInfo>();
        }

        private static MergeVersionSettingsDto ToSettingsDto(PluginConfiguration cfg) =>
            new MergeVersionSettingsDto
            {
                MergeMoviesScope = cfg.MergeMoviesScope,
                MergeSeriesScope = cfg.MergeSeriesScope,
                MergeAutoDetect  = cfg.MergeAutoDetect
            };
    }
}
