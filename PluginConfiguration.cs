using MediaBrowser.Model.Plugins;

namespace StrmCompanion
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>Minutes from episode start to fingerprint (default 5).</summary>
        public int FingerprintDurationMinutes { get; set; } = 5;

        /// <summary>Hamming distance threshold per 32-bit word. 8 is the proven value from IntroSkip.</summary>
        public int HammingDistanceThreshold { get; set; } = 8;

        /// <summary>Minimum detected intro length in seconds to be considered valid.</summary>
        public int MinimumIntroLengthSeconds { get; set; } = 10;

        /// <summary>Silence threshold for silencedetect filter (e.g. "-30dB").</summary>
        public string SilenceThresholdDb { get; set; } = "-30dB";

        /// <summary>Minimum silence duration in seconds for silencedetect filter.</summary>
        public double SilenceDurationSeconds { get; set; } = 0.5;

        /// <summary>Override path to ffmpeg executable. Empty = use Emby's bundled ffmpeg.</summary>
        public string FfmpegPathOverride { get; set; } = string.Empty;

        /// <summary>
        /// When false (default), episodes that already have IntroStart/IntroEnd markers are skipped.
        /// Set to true to overwrite existing markers with newly computed ones.
        /// </summary>
        public bool OverwriteExistingIntroMarkers { get; set; } = false;

        /// <summary>
        /// Custom path for storing fingerprint cache files.
        /// Empty = use Emby's default plugin data folder (DataFolderPath/fingerprints).
        /// Set this if you have many series and want to store data on a larger drive.
        /// </summary>
        public string FingerprintDataPath { get; set; } = string.Empty;

        // ── Media Info ──────────────────────────────────────────────────────────

        /// <summary>Comma-separated CollectionFolder InternalIds to include in media info scans. Empty = all libraries.</summary>
        public string MediaInfoLibraryIds { get; set; } = string.Empty;

        /// <summary>Maximum number of .strm files probed simultaneously. Keep low to avoid IPTV rate limiting.</summary>
        public int MediaInfoConcurrency { get; set; } = 2;

        /// <summary>When true, automatically probe new .strm items in configured libraries as they are added.</summary>
        public bool MediaInfoAutoScan { get; set; } = false;

        // ── Merge Version ────────────────────────────────────────────────────────────

        /// <summary>Comma-separated CollectionFolder InternalIds to scope merging. Empty = all libraries (Global).</summary>
        public string MergeVersionLibraryIds { get; set; } = string.Empty;
    }
}
