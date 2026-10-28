using System;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Services;
using StrmCompanion.Jobs;

namespace StrmCompanion.Api
{
    // ─── Request/Response DTOs ────────────────────────────────────────────────────

    [Route("/strmcompanion/splitversion/run", "POST", Summary = "Start a split version job")]
    [Authenticated]
    public class StartSplitVersion : IReturn<StartJobResponse>
    {
        public long SeriesId { get; set; }
        public long? SeasonId { get; set; }
    }

    [Route("/strmcompanion/splitversion/runmovie", "POST", Summary = "Start a split version job for a movie")]
    [Authenticated]
    public class StartSplitMovieVersion : IReturn<StartJobResponse>
    {
        public long MovieId { get; set; }
    }

    [Route("/strmcompanion/splitversion/runall", "POST", Summary = "Start a library-wide split version job")]
    [Authenticated]
    public class StartSplitAllVersions : IReturn<StartJobResponse>
    {
        // "all", "movies" or "series"
        public string Scope { get; set; }
    }

    [Route("/strmcompanion/splitversion/job/{JobId}", "GET", Summary = "Poll split version job status")]
    [Authenticated]
    public class GetSplitVersionJobStatus : IReturn<JobInfo>
    {
        public string JobId { get; set; }
    }

    [Route("/strmcompanion/splitversion/job/{JobId}", "DELETE", Summary = "Cancel a split version job")]
    [Authenticated]
    public class CancelSplitVersionJob : IReturnVoid
    {
        public string JobId { get; set; }
    }

    // ─── Service ─────────────────────────────────────────────────────────────────

    public class SplitVersionApiService : BaseApiService
    {
        private readonly ILogger _logger;

        public SplitVersionApiService(ILogManager logManager)
        {
            _logger = logManager.GetLogger(nameof(SplitVersionApiService));
        }

        public object Post(StartSplitVersion request)
        {
            var svc = PluginEntryPoint.SplitVersionService;
            if (svc == null)
                throw new Exception("SplitVersionService is not initialized");

            var jobId = svc.StartSplit(request.SeriesId, request.SeasonId);
            return new StartJobResponse { JobId = jobId };
        }

        public object Post(StartSplitMovieVersion request)
        {
            var svc = PluginEntryPoint.SplitVersionService;
            if (svc == null)
                throw new Exception("SplitVersionService is not initialized");

            var jobId = svc.StartSplitMovie(request.MovieId);
            return new StartJobResponse { JobId = jobId };
        }

        public object Post(StartSplitAllVersions request)
        {
            var svc = PluginEntryPoint.SplitVersionService;
            if (svc == null)
                throw new Exception("SplitVersionService is not initialized");

            var jobId = svc.StartSplitAll(request.Scope);
            return new StartJobResponse { JobId = jobId };
        }

        public object Get(GetSplitVersionJobStatus request)
        {
            var job = PluginEntryPoint.JobManager?.GetJob(request.JobId);
            if (job == null)
                throw new ArgumentException("Job not found: " + request.JobId);
            return job;
        }

        public void Delete(CancelSplitVersionJob request)
        {
            PluginEntryPoint.JobManager?.CancelJob(request.JobId);
        }
    }
}
