using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using MediaBrowser.Model.Logging;
using StrmCompanion.Jobs;

namespace StrmCompanion.IntroDetection
{
    /// <summary>
    /// Generates Chromaprint audio fingerprints by invoking FFmpeg with the chromaprint muxer.
    /// Handles .strm files by reading the stream URL from the file contents.
    /// </summary>
    public class FingerprintService
    {
        private readonly ILogger _logger;
        private string _ffmpegPath;

        public FingerprintService(ILogger logger)
        {
            _logger = logger;
        }

        public void SetFfmpegPath(string path)
        {
            _ffmpegPath = path;
        }

        /// <summary>
        /// Checks if the bundled FFmpeg supports the chromaprint muxer.
        /// Call at startup to detect missing capability.
        /// </summary>
        public bool HasChromaprintSupport()
        {
            try
            {
                var output = RunProcess(_ffmpegPath, "-formats -hide_banner", timeoutMs: 10000);
                return output.Contains("chromaprint");
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Generates a Chromaprint fingerprint for the given media path.
        /// Returns null on failure.
        /// </summary>
        public FingerprintData GenerateFingerprint(
            long seriesId,
            long episodeId,
            string episodeName,
            string mediaPath,
            int durationSeconds,
            CancellationToken cancellationToken)
        {
            string resolvedInput;
            try
            {
                resolvedInput = ResolveInputPath(mediaPath);
            }
            catch (Exception ex)
            {
                _logger.Error("StrmCompanion: cannot resolve input for '{0}': {1}", mediaPath, ex.Message);
                return null;
            }

            var tempOutput = Path.Combine(Path.GetTempPath(), $"strmcompanion_{episodeId}.bin");
            try
            {
                cancellationToken.ThrowIfCancellationRequested();

                // -ss before -i = input seek (efficient for HTTP streams – stops after N seconds)
                var args = $"-ss 0 -t {durationSeconds} -i \"{resolvedInput}\" " +
                           $"-ac 1 -acodec pcm_s16le -ar 16000 -vn " +
                           $"-f chromaprint -fp_format raw \"{tempOutput}\"";

                _logger.Debug("StrmCompanion: fingerprinting '{0}' with: ffmpeg {1}", episodeName, args);

                var timeoutMs = durationSeconds * 3 * 1000;
                var ffmpegOutput = RunProcess(_ffmpegPath, args, timeoutMs);

                if (!File.Exists(tempOutput) || new FileInfo(tempOutput).Length == 0)
                {
                    _logger.Warn("StrmCompanion: fingerprint output empty for '{0}'. FFmpeg output: {1}",
                        episodeName, ffmpegOutput);
                    return null;
                }

                var fingerprint = ReadBinaryFingerprint(tempOutput);
                if (fingerprint.Count == 0)
                {
                    _logger.Warn("StrmCompanion: empty fingerprint for '{0}'", episodeName);
                    return null;
                }

                return new FingerprintData
                {
                    EpisodeInternalId = episodeId,
                    EpisodeName = episodeName,
                    EpisodePath = mediaPath,
                    Fingerprint = fingerprint,
                    Duration = durationSeconds
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.Error("StrmCompanion: fingerprint failed for '{0}': {1}", episodeName, ex.Message);
                return null;
            }
            finally
            {
                try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { }
            }
        }

        /// <summary>
        /// If path is a .strm file, reads the stream URL from its contents.
        /// Otherwise returns the path unchanged.
        /// </summary>
        public string ResolveInputPath(string mediaPath)
        {
            if (!mediaPath.EndsWith(".strm", StringComparison.OrdinalIgnoreCase))
                return mediaPath;

            var lines = File.ReadAllLines(mediaPath);
            foreach (var line in lines)
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed))
                    return trimmed;
            }
            throw new InvalidOperationException($"Empty .strm file: {mediaPath}");
        }

        private List<uint> ReadBinaryFingerprint(string path)
        {
            var result = new List<uint>();
            using (var fs = File.OpenRead(path))
            using (var reader = new BinaryReader(fs))
            {
                while (fs.Position + 4 <= fs.Length)
                    result.Add(reader.ReadUInt32());
            }
            return result;
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
                    throw new TimeoutException($"FFmpeg process timed out after {timeoutMs}ms");
                }
            }
            return output.ToString();
        }
    }
}
