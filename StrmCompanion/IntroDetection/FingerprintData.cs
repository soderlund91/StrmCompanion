using System.Collections.Generic;

namespace StrmCompanion.IntroDetection
{
    public class FingerprintData
    {
        public long EpisodeInternalId { get; set; }
        public string EpisodeName { get; set; }
        public string EpisodePath { get; set; }

        /// <summary>Raw Chromaprint fingerprint values.</summary>
        public List<uint> Fingerprint { get; set; } = new List<uint>();

        /// <summary>Duration in seconds of the fingerprinted audio segment.</summary>
        public double Duration { get; set; }

        /// <summary>Samples per second computed from Fingerprint.Count / Duration.</summary>
        public double SamplesPerSecond => Duration > 0 ? Fingerprint.Count / Duration : 0;
    }
}
