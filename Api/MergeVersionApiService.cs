using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using MediaBrowser.Model.Services;
using StrmCompanion.Jobs;

namespace StrmCompanion.Api
{
    // ─── Request/Response DTOs ────────────────────────────────────────────────────

    [Route("/strmcompanion/mergeversion/libraries", "GET", Summary = "List Emby libraries for Merge Version")]
    [Authenticated]
    public class GetMergeVersionLibraries : IReturn<List<LibraryDto>> { }

    [Route("/strmcompanion/mergeversion/settings", "GET", Summary = "Get Merge Version settings")]
    [Authenticated]
    public class GetMergeVersionSettings : IReturn<MergeVersionSettingsDto> { }

    [Route("/strmcompanion/mergeversion/settings", "POST", Summary = "Save Merge Version settings")]
    [Authenticated]
    public class SaveMergeVersionSettings : IReturn<MergeVersionSettingsDto>
    {
        public string MergeVersionLibraryIds { get; set; }
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

    // ─── DTO ─────────────────────────────────────────────────────────────────────

    public class MergeVersionSettingsDto
    {
        public string MergeVersionLibraryIds { get; set; }
    }

    // ─── Service ─────────────────────────────────────────────────────────────────

    public class MergeVersionApiService : BaseApiService
    {
        private readonly ILibraryManager _libraryManager;
        private readonly ILogger _logger;

        public MergeVersionApiService(
            ILibraryManager libraryManager,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _logger = logManager.GetLogger(nameof(MergeVersionApiService));
        }

        public object Get(GetMergeVersionLibraries request)
        {
            var query = new InternalItemsQuery
            {
                IncludeItemTypes = new[] { "CollectionFolder" }
            };
            var result = _libraryManager.QueryItems(query);
            return result.Items.Select(f => new LibraryDto
            {
                Id = f.InternalId,
                Name = f.Name,
                CollectionType = (f as ICollectionFolder)?.CollectionType
            }).OrderBy(f => f.Name).ToList();
        }

        public object Get(GetMergeVersionSettings request)
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            return new MergeVersionSettingsDto { MergeVersionLibraryIds = cfg.MergeVersionLibraryIds };
        }

        public object Post(SaveMergeVersionSettings request)
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            cfg.MergeVersionLibraryIds = request.MergeVersionLibraryIds?.Trim() ?? string.Empty;
            Plugin.Instance?.SaveConfiguration();
            return new MergeVersionSettingsDto { MergeVersionLibraryIds = cfg.MergeVersionLibraryIds };
        }

        public object Post(StartMergeVersion request)
        {
            var svc = PluginEntryPoint.MergeVersionService;
            if (svc == null)
                throw new Exception("MergeVersionService is not initialized");

            var token = Request.Headers["X-MediaBrowser-Token"]
                     ?? Request.QueryString["api_key"]
                     ?? string.Empty;

            // Derive server base URL from the incoming request (e.g. "http://localhost:8096")
            var requestUri = new Uri(Request.AbsoluteUri);
            var serverBaseUrl = requestUri.GetLeftPart(UriPartial.Authority);

            var jobId = svc.StartMerge(serverBaseUrl, token, CancellationToken.None);
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
    }
}
