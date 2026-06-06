using System.Threading;

namespace StrmCompanion.Analysis
{
    public interface IAnalysisTask
    {
        /// <summary>Unique machine-readable identifier, e.g. "intro-detection".</summary>
        string TaskId { get; }

        /// <summary>Human-readable name shown in dashboard.</summary>
        string DisplayName { get; }

        /// <summary>Short description shown in dashboard.</summary>
        string Description { get; }

        /// <summary>
        /// Starts an analysis job. Pass seasonId = null to process all seasons of the series.
        /// Returns the jobId that callers can poll for status.
        /// </summary>
        string StartJob(long seriesId, long? seasonId, CancellationToken cancellationToken);
    }
}
