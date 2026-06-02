using MediaBrowser.Controller.Api;
using MediaBrowser.Controller.Net;
using MediaBrowser.Model.Services;

namespace StrmCompanion.Api
{
    [Route("/strmcompanion/version", "GET", Summary = "Get StrmCompanion plugin version")]
    [Authenticated]
    public class GetPluginVersion : IReturn<PluginVersionDto> { }

    public class PluginVersionDto
    {
        public string Version { get; set; }
    }

    [Route("/strmcompanion/settings", "GET", Summary = "Get StrmCompanion plugin configuration")]
    [Authenticated]
    public class GetSettings : IReturn<SettingsDto> { }

    [Route("/strmcompanion/settings", "POST", Summary = "Save StrmCompanion plugin configuration")]
    [Authenticated]
    public class SaveSettings : IReturn<SettingsDto>
    {
        public string FingerprintDataPath { get; set; }
        public int FingerprintDurationMinutes { get; set; }
        public int HammingDistanceThreshold { get; set; }
        public int MinimumIntroLengthSeconds { get; set; }
        public string SilenceThresholdDb { get; set; }
        public double SilenceDurationSeconds { get; set; }
        public string FfmpegPathOverride { get; set; }
        public bool OverwriteExistingIntroMarkers { get; set; }
    }

    public class SettingsDto
    {
        public string FingerprintDataPath { get; set; }
        public string EffectiveFingerprintPath { get; set; }
        public int FingerprintDurationMinutes { get; set; }
        public int HammingDistanceThreshold { get; set; }
        public int MinimumIntroLengthSeconds { get; set; }
        public string SilenceThresholdDb { get; set; }
        public double SilenceDurationSeconds { get; set; }
        public string FfmpegPathOverride { get; set; }
        public bool OverwriteExistingIntroMarkers { get; set; }
    }

    public class SettingsService : BaseApiService
    {
        public object Get(GetPluginVersion request)
        {
            var version = Plugin.Instance?.Version?.ToString() ?? string.Empty;
            return new PluginVersionDto { Version = version };
        }

        public object Get(GetSettings request)
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();
            return ToDto(cfg);
        }

        public object Post(SaveSettings request)
        {
            var cfg = Plugin.Instance?.Configuration ?? new PluginConfiguration();

            cfg.FingerprintDataPath           = request.FingerprintDataPath?.Trim() ?? string.Empty;
            cfg.FingerprintDurationMinutes     = request.FingerprintDurationMinutes > 0 ? request.FingerprintDurationMinutes : 5;
            cfg.HammingDistanceThreshold       = request.HammingDistanceThreshold > 0  ? request.HammingDistanceThreshold : 8;
            cfg.MinimumIntroLengthSeconds      = request.MinimumIntroLengthSeconds > 0  ? request.MinimumIntroLengthSeconds : 10;
            cfg.SilenceThresholdDb             = string.IsNullOrWhiteSpace(request.SilenceThresholdDb) ? "-30dB" : request.SilenceThresholdDb;
            cfg.SilenceDurationSeconds         = request.SilenceDurationSeconds > 0 ? request.SilenceDurationSeconds : 0.5;
            cfg.FfmpegPathOverride             = request.FfmpegPathOverride?.Trim() ?? string.Empty;
            cfg.OverwriteExistingIntroMarkers  = request.OverwriteExistingIntroMarkers;

            Plugin.Instance?.SaveConfiguration();

            return ToDto(cfg);
        }

        private static SettingsDto ToDto(PluginConfiguration cfg) => new SettingsDto
        {
            FingerprintDataPath           = cfg.FingerprintDataPath,
            EffectiveFingerprintPath      = Plugin.GetFingerprintBasePath(),
            FingerprintDurationMinutes    = cfg.FingerprintDurationMinutes,
            HammingDistanceThreshold      = cfg.HammingDistanceThreshold,
            MinimumIntroLengthSeconds     = cfg.MinimumIntroLengthSeconds,
            SilenceThresholdDb            = cfg.SilenceThresholdDb,
            SilenceDurationSeconds        = cfg.SilenceDurationSeconds,
            FfmpegPathOverride            = cfg.FfmpegPathOverride,
            OverwriteExistingIntroMarkers = cfg.OverwriteExistingIntroMarkers
        };
    }
}
