using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Logging;

namespace StrmCompanion.MediaInfo
{
    public class MediaInfoProbeService
    {
        private readonly ILogger _logger;
        private string _ffprobePath;

        public MediaInfoProbeService(ILogger logger)
        {
            _logger = logger;
        }

        public void SetFfprobePath(string path)
        {
            _ffprobePath = path;
        }

        /// <summary>
        /// Probes the stream URL inside a .strm file.
        /// Returns the parsed media streams on success, or null with a non-null error on failure.
        /// </summary>
        public List<MediaStream> Probe(string strmPath, string itemName, out string error, CancellationToken token)
        {
            error = null;

            string streamUrl;
            try
            {
                streamUrl = ReadStrmUrl(strmPath);
            }
            catch (Exception ex)
            {
                error = "Cannot read .strm: " + ex.Message;
                return null;
            }

            var args = $"-v quiet -print_format json -show_streams -timeout 30000000 -analyzeduration 15000000 -probesize 20000000 \"{streamUrl}\"";

            string jsonOutput;
            try
            {
                jsonOutput = RunProcess(_ffprobePath, args, timeoutMs: 60000, token: token);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _logger.Warn("StrmCompanion: ffprobe failed for '{0}': {1}", itemName, ex.Message);
                error = ex.Message;
                return null;
            }

            if (string.IsNullOrWhiteSpace(jsonOutput))
            {
                error = "ffprobe returned empty output";
                return null;
            }

            try
            {
                return ParseStreams(jsonOutput);
            }
            catch (Exception ex)
            {
                _logger.Warn("StrmCompanion: failed to parse ffprobe output for '{0}': {1}", itemName, ex.Message);
                error = "JSON parse failed: " + ex.Message;
                return null;
            }
        }

        private static List<MediaStream> ParseStreams(string json)
        {
            var streams = new List<MediaStream>();
            int index = 0;

            // Minimal JSON parser for the "streams" array from ffprobe output
            var streamsStart = json.IndexOf("\"streams\"", StringComparison.Ordinal);
            if (streamsStart < 0) return streams;

            // Extract codec_type / codec_name / width / height / r_frame_rate / channels / channel_layout / language per stream object
            int pos = json.IndexOf('[', streamsStart);
            if (pos < 0) return streams;

            int depth = 0;
            int objStart = -1;
            for (int i = pos; i < json.Length; i++)
            {
                char c = json[i];
                if (c == '{')
                {
                    if (depth == 0) objStart = i;
                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && objStart >= 0)
                    {
                        var obj = json.Substring(objStart, i - objStart + 1);
                        var stream = ParseStreamObject(obj, index++);
                        if (stream != null) streams.Add(stream);
                        objStart = -1;
                    }
                }
                else if (c == ']' && depth == 0)
                {
                    break;
                }
            }

            return streams;
        }

        private static MediaStream ParseStreamObject(string obj, int index)
        {
            var codecType = ExtractString(obj, "codec_type");
            if (string.IsNullOrEmpty(codecType)) return null;

            var codecName = ExtractString(obj, "codec_name");
            var lang = ExtractString(obj, "language");

            if (string.Equals(codecType, "video", StringComparison.OrdinalIgnoreCase))
            {
                var fps = ParseFrameRate(ExtractString(obj, "r_frame_rate") ?? ExtractString(obj, "avg_frame_rate"));
                return new MediaStream
                {
                    Type = MediaStreamType.Video,
                    Index = index,
                    Codec = codecName,
                    Width = ExtractInt(obj, "width"),
                    Height = ExtractInt(obj, "height"),
                    RealFrameRate = fps > 0 ? (float?)fps : null,
                    AverageFrameRate = fps > 0 ? (float?)fps : null,
                    Language = lang
                };
            }
            if (string.Equals(codecType, "audio", StringComparison.OrdinalIgnoreCase))
            {
                return new MediaStream
                {
                    Type = MediaStreamType.Audio,
                    Index = index,
                    Codec = codecName,
                    Channels = ExtractInt(obj, "channels"),
                    ChannelLayout = ExtractString(obj, "channel_layout"),
                    Language = lang
                };
            }
            if (string.Equals(codecType, "subtitle", StringComparison.OrdinalIgnoreCase))
            {
                return new MediaStream
                {
                    Type = MediaStreamType.Subtitle,
                    Index = index,
                    Codec = codecName,
                    Language = lang
                };
            }
            return null;
        }

        private static string ExtractString(string json, string key)
        {
            // Finds "key":"value" or nested "tags":{..."key":"value"...}
            var pattern = "\"" + key + "\"";
            int pos = json.IndexOf(pattern, StringComparison.Ordinal);
            if (pos < 0) return null;
            pos += pattern.Length;
            while (pos < json.Length && (json[pos] == ' ' || json[pos] == ':')) pos++;
            if (pos >= json.Length) return null;
            if (json[pos] == '"')
            {
                pos++;
                var end = json.IndexOf('"', pos);
                return end < 0 ? null : json.Substring(pos, end - pos);
            }
            return null;
        }

        private static int? ExtractInt(string json, string key)
        {
            var pattern = "\"" + key + "\"";
            int pos = json.IndexOf(pattern, StringComparison.Ordinal);
            if (pos < 0) return null;
            pos += pattern.Length;
            while (pos < json.Length && (json[pos] == ' ' || json[pos] == ':')) pos++;
            if (pos >= json.Length) return null;
            var end = pos;
            while (end < json.Length && (char.IsDigit(json[end]) || json[end] == '-')) end++;
            if (end == pos) return null;
            return int.TryParse(json.Substring(pos, end - pos), out var v) ? (int?)v : null;
        }

        private static double ParseFrameRate(string value)
        {
            if (string.IsNullOrEmpty(value)) return 0;
            var parts = value.Split('/');
            if (parts.Length == 2 &&
                double.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var num) &&
                double.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var den) &&
                den != 0)
                return num / den;
            return double.TryParse(value, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var fps) ? fps : 0;
        }

        private static string ReadStrmUrl(string strmPath)
        {
            foreach (var line in File.ReadAllLines(strmPath))
            {
                var trimmed = line.Trim();
                if (!string.IsNullOrEmpty(trimmed)) return trimmed;
            }
            throw new InvalidOperationException($".strm file is empty: {strmPath}");
        }

        private string RunProcess(string executable, string arguments, int timeoutMs, CancellationToken token)
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = arguments,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using (var process = Process.Start(psi))
            {
                if (process == null)
                    throw new InvalidOperationException("Failed to start ffprobe process");

                var output = new StringBuilder();
                process.OutputDataReceived += (_, e) => { if (e.Data != null) output.AppendLine(e.Data); };
                process.BeginOutputReadLine();

                var sw = Stopwatch.StartNew();
                while (!process.HasExited)
                {
                    if (token.IsCancellationRequested)
                    {
                        try { process.Kill(); } catch { }
                        token.ThrowIfCancellationRequested();
                    }
                    if (sw.ElapsedMilliseconds > timeoutMs)
                    {
                        try { process.Kill(); } catch { }
                        throw new TimeoutException($"ffprobe timed out after {timeoutMs / 1000}s");
                    }
                    Thread.Sleep(200);
                }

                process.WaitForExit();
                return output.ToString();
            }
        }
    }
}
