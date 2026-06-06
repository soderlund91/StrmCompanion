using System;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using MediaBrowser.Model.Logging;

namespace StrmCompanion.IntroDetection
{
    /// <summary>
    /// Uses FFmpeg's silencedetect filter to find the precise point where audio resumes
    /// after the intro (i.e., refines the consensus intro end timestamp per episode).
    /// </summary>
    public class SilenceDetectionService
    {
        private readonly ILogger _logger;
        private string _ffmpegPath;

        // Matches: "silence_end: 142.530000 | silence_duration: 1.230000"
        private static readonly Regex SilenceEndRegex =
            new Regex(@"silence_end:\s*(\d+\.?\d*)", RegexOptions.Compiled);

        public SilenceDetectionService(ILogger logger)
        {
            _logger = logger;
        }

        public void SetFfmpegPath(string path)
        {
            _ffmpegPath = path;
        }

        /// <summary>
        /// Runs silence detection in a narrow window around the expected intro end.
        /// Returns a refined timestamp, or the original consensusEnd if no silence is found.
        /// </summary>
        public double RefineIntroEnd(
            string mediaPath,
            double consensusEndSeconds,
            string noiseDb,
            double minSilenceDuration,
            CancellationToken cancellationToken)
        {
            string resolvedPath;
            try
            {
                resolvedPath = ResolveStrmPath(mediaPath);
            }
            catch
            {
                return consensusEndSeconds;
            }

            // Search in a ±8 second window around the consensus end
            double windowStart = Math.Max(0, consensusEndSeconds - 8);
            double windowLen = 16;

            var args = $"-ss {windowStart:F3} -t {windowLen:F3} -i \"{resolvedPath}\" " +
                       $"-af silencedetect=noise={noiseDb}:d={minSilenceDuration:F2} " +
                       $"-f null -";

            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var output = RunProcess(_ffmpegPath, args, timeoutMs: 30000);

                // Find the silence_end closest to consensusEndSeconds
                double bestTime = consensusEndSeconds;
                double bestDiff = double.MaxValue;

                foreach (Match m in SilenceEndRegex.Matches(output))
                {
                    if (!double.TryParse(m.Groups[1].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double silenceEnd))
                        continue;

                    // silenceEnd is relative to window start; convert to absolute
                    double absolute = windowStart + silenceEnd;
                    double diff = Math.Abs(absolute - consensusEndSeconds);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        bestTime = absolute;
                    }
                }

                if (bestDiff < double.MaxValue)
                {
                    _logger.Debug("StrmCompanion: silence-refined intro end {0:F2}s → {1:F2}s",
                        consensusEndSeconds, bestTime);
                }

                return bestTime;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Warn("StrmCompanion: silence detection failed: {0}", ex.Message);
                return consensusEndSeconds;
            }
        }

        private string ResolveStrmPath(string mediaPath)
        {
            if (!mediaPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                return mediaPath;
            var lines = System.IO.File.ReadAllLines(mediaPath);
            foreach (var line in lines)
            {
                var t = line.Trim();
                if (!string.IsNullOrEmpty(t)) return t;
            }
            throw new InvalidOperationException("Empty .strm: " + mediaPath);
        }

        private string RunProcess(string executable, string arguments, int timeoutMs)
        {
            var psi = new ProcessStartInfo(executable, arguments)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            var output = new StringBuilder();
            using (var process = new Process { StartInfo = psi })
            {
                process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.ErrorDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(timeoutMs))
                {
                    try { process.Kill(); } catch { }
                }
            }
            return output.ToString();
        }
    }
}
