using System;
using System.Collections.Generic;

namespace StrmCompanion.Jobs
{
    public class JobInfo
    {
        public string JobId { get; set; }
        public string TaskId { get; set; }
        public string SeriesName { get; set; }
        public long SeriesInternalId { get; set; }
        public string SeasonName { get; set; }
        public long? SeasonInternalId { get; set; }
        public JobStatus Status { get; set; }
        public double ProgressPercent { get; set; }
        public string CurrentActivity { get; set; }
        public DateTime StartedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public string ErrorMessage { get; set; }

        /// <summary>Per-episode result messages, keyed by episode InternalId.</summary>
        public Dictionary<long, string> EpisodeResults { get; set; } = new Dictionary<long, string>();
    }
}
