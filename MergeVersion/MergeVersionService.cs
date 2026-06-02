using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Querying;
using StrmCompanion.Jobs;
using StrmCompanion.MediaInfo;

namespace StrmCompanion.MergeVersion
{
    public class MergeVersionService
    {
        private static readonly HttpClient HttpClient = new HttpClient();

        private readonly ILibraryManager _libraryManager;
        private readonly JobManager _jobManager;
        private readonly ILogger _logger;

        public MergeVersionService(
            ILibraryManager libraryManager,
            JobManager jobManager,
            ILogManager logManager)
        {
            _libraryManager = libraryManager;
            _jobManager = jobManager;
            _logger = logManager.GetLogger(nameof(MergeVersionService));
        }

        // serverBaseUrl: e.g. "http://localhost:8096" — derived from the incoming request
        public string StartMerge(string serverBaseUrl, string adminToken, CancellationToken externalCt)
        {
            var job = _jobManager.CreateJob("merge-version", 0, "Merge Version", null, null);
            var token = _jobManager.GetCancellationToken(job.JobId);
            Task.Run(() => RunAsync(job, serverBaseUrl, adminToken, token), token);
            return job.JobId;
        }

        private async Task RunAsync(JobInfo job, string serverBaseUrl, string adminToken, CancellationToken ct)
        {
            try
            {
                _jobManager.UpdateProgress(job.JobId, 0, "Querying libraries...");

                var cfg = Plugin.Instance.Configuration;
                var libraryIds = MediaInfoAnalysisTask.ParseLibraryIds(cfg.MergeVersionLibraryIds);

                var movieQuery = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Movie" },
                    IsVirtualItem = false
                };
                if (libraryIds.Length > 0)
                    movieQuery.AncestorIds = libraryIds;

                var movies = _libraryManager.GetItemsResult(movieQuery).Items
                    .OfType<Movie>()
                    .Where(m => !m.IsSecondaryMergedItemInSameFolder)
                    .ToList();

                var epQuery = new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { "Episode" },
                    IsVirtualItem = false
                };
                if (libraryIds.Length > 0)
                    epQuery.AncestorIds = libraryIds;

                var episodes = _libraryManager.GetItemsResult(epQuery).Items
                    .OfType<Episode>()
                    .Where(e => !e.IsSecondaryMergedItemInSameFolder)
                    .ToList();

                var movieGroups = movies
                    .GroupBy(m => m.Name.Trim().ToLowerInvariant() + "|" + (m.ProductionYear ?? 0))
                    .Where(g => g.Count() >= 2)
                    .ToList();

                var epGroups = episodes
                    .GroupBy(e => e.SeriesId.ToString("N")
                                  + "|" + (e.ParentIndexNumber ?? -1)
                                  + "|" + (e.IndexNumber ?? -1))
                    .Where(g => g.Count() >= 2)
                    .ToList();

                int totalGroups = movieGroups.Count + epGroups.Count;
                int processed = 0;

                if (totalGroups == 0)
                {
                    _jobManager.UpdateProgress(job.JobId, 100, "No duplicate groups found");
                    _jobManager.CompleteJob(job.JobId);
                    return;
                }

                _jobManager.UpdateProgress(job.JobId, 0,
                    string.Format("Found {0} movie group(s) + {1} episode group(s) to merge",
                        movieGroups.Count, epGroups.Count));

                foreach (var group in movieGroups)
                {
                    ct.ThrowIfCancellationRequested();
                    var ids = group.Select(m => m.InternalId).ToList();
                    var primary = ids.First();
                    var result = await MergeGroup(ids, serverBaseUrl, adminToken, ct).ConfigureAwait(false);
                    _jobManager.AddEpisodeResult(job.JobId, primary, result);
                    processed++;
                    _jobManager.UpdateProgress(job.JobId,
                        (double)processed / totalGroups * 100.0,
                        string.Format("Merged {0}/{1} groups", processed, totalGroups));
                }

                foreach (var group in epGroups)
                {
                    ct.ThrowIfCancellationRequested();
                    var ids = group.Select(e => e.InternalId).ToList();
                    var primary = ids.First();
                    var result = await MergeGroup(ids, serverBaseUrl, adminToken, ct).ConfigureAwait(false);
                    _jobManager.AddEpisodeResult(job.JobId, primary, result);
                    processed++;
                    _jobManager.UpdateProgress(job.JobId,
                        (double)processed / totalGroups * 100.0,
                        string.Format("Merged {0}/{1} groups", processed, totalGroups));
                }

                _jobManager.CompleteJob(job.JobId);
                _logger.Info("StrmCompanion MergeVersion: complete, {0} group(s) processed", totalGroups);
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
        }

        private async Task<string> MergeGroup(List<long> ids, string serverBaseUrl, string adminToken, CancellationToken ct)
        {
            try
            {
                var url = string.Format("{0}/Videos/MergeVersions?Ids={1}",
                    serverBaseUrl.TrimEnd('/'), string.Join(",", ids));

                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.Add("X-MediaBrowser-Token", adminToken);
                    var response = await HttpClient.SendAsync(req, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                }
                return string.Format("Merged {0} version(s)", ids.Count - 1);
            }
            catch (Exception ex)
            {
                _logger.Warn("StrmCompanion MergeVersion: merge failed for ids [{0}]: {1}",
                    string.Join(",", ids), ex.Message);
                return "Error: " + ex.Message;
            }
        }
    }
}
