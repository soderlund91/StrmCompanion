using System.Collections.Generic;
using System.Linq;

namespace StrmCompanion.Analysis
{
    public class AnalysisTaskRegistry
    {
        private readonly List<IAnalysisTask> _tasks = new List<IAnalysisTask>();

        public void Register(IAnalysisTask task)
        {
            _tasks.Add(task);
        }

        public IReadOnlyList<IAnalysisTask> GetAll() => _tasks.AsReadOnly();

        public IAnalysisTask GetById(string taskId) =>
            _tasks.FirstOrDefault(t => t.TaskId == taskId);
    }
}
