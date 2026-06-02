using System.Collections.Generic;
using System.Linq;
using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;
using StrmCompanion.Jobs;

namespace StrmCompanion.Api
{
    [Route("/strmcompanion/tasks", "GET", Summary = "List all registered StrmCompanion analysis tasks")]
    [Authenticated]
    public class GetTaskList : IReturn<List<TaskInfoDto>> { }

    public class TaskInfoDto
    {
        public string TaskId { get; set; }
        public string DisplayName { get; set; }
        public string Description { get; set; }
        public int ActiveJobCount { get; set; }
    }

    public class DashboardService : BaseApiService
    {
        public object Get(GetTaskList request)
        {
            var registry = PluginEntryPoint.TaskRegistry;
            var jobManager = PluginEntryPoint.JobManager;

            if (registry == null)
                return new List<TaskInfoDto>();

            return registry.GetAll().Select(t => new TaskInfoDto
            {
                TaskId = t.TaskId,
                DisplayName = t.DisplayName,
                Description = t.Description,
                ActiveJobCount = jobManager?.GetAllJobs()
                    .Count(j => j.TaskId == t.TaskId && j.Status == JobStatus.Running) ?? 0
            }).ToList();
        }
    }
}
