using System.Collections.Generic;
using System.IO;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace StrmCompanion.IntroDetection
{
    /// <summary>
    /// Persists Chromaprint fingerprints as JSON files under PluginDataPath/fingerprints/{seriesId}/{episodeId}.json.
    /// Avoids re-fingerprinting on subsequent runs.
    /// </summary>
    public class FingerprintStore
    {
        private readonly string _basePath;
        private readonly IJsonSerializer _jsonSerializer;
        private readonly ILogger _logger;

        /// <param name="fingerprintBasePath">Root directory for fingerprint files (series sub-folders go here).</param>
        public FingerprintStore(string fingerprintBasePath, IJsonSerializer jsonSerializer, ILogger logger)
        {
            _basePath = fingerprintBasePath;
            _jsonSerializer = jsonSerializer;
            _logger = logger;
        }

        private string GetPath(long seriesId, long episodeId)
        {
            var dir = Path.Combine(_basePath, seriesId.ToString());
            return Path.Combine(dir, episodeId + ".json");
        }

        public bool Exists(long seriesId, long episodeId) =>
            File.Exists(GetPath(seriesId, episodeId));

        public FingerprintData Load(long seriesId, long episodeId)
        {
            var path = GetPath(seriesId, episodeId);
            if (!File.Exists(path)) return null;
            try
            {
                var json = File.ReadAllText(path);
                return _jsonSerializer.DeserializeFromString<FingerprintData>(json);
            }
            catch
            {
                _logger.Warn("StrmCompanion: failed to load fingerprint cache for episode {0}", episodeId);
                return null;
            }
        }

        public void Save(long seriesId, FingerprintData data)
        {
            var path = GetPath(seriesId, data.EpisodeInternalId);
            var dir = Path.GetDirectoryName(path);
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            try
            {
                var json = _jsonSerializer.SerializeToString(data);
                File.WriteAllText(path, json);
            }
            catch
            {
                _logger.Warn("StrmCompanion: failed to save fingerprint cache for episode {0}",
                    data.EpisodeInternalId);
            }
        }

        public void Delete(long seriesId, long episodeId)
        {
            var path = GetPath(seriesId, episodeId);
            if (File.Exists(path))
                File.Delete(path);
        }

        /// <summary>Removes all cached fingerprints for a series.</summary>
        public void DeleteAll(long seriesId)
        {
            var dir = Path.Combine(_basePath, seriesId.ToString());
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }
}
