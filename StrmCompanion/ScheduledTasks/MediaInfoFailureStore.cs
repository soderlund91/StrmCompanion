using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using MediaBrowser.Model.Logging;
using MediaBrowser.Model.Serialization;

namespace StrmCompanion.ScheduledTasks
{
    public class MediaInfoFailure
    {
        public long ItemId { get; set; }

        // Stored as ticks so the value does not depend on how the serializer handles DateTime kinds.
        public long LastAttemptUtcTicks { get; set; }
        public int FailureCount { get; set; }
        public string LastError { get; set; }

        // Set by a manual retry: the item is due regardless of the backoff.
        public bool RetryNow { get; set; }

        // A method, not a property, so it is left out of the saved file.
        public DateTime GetLastAttemptUtc() => new DateTime(LastAttemptUtcTicks, DateTimeKind.Utc);
    }

    // Remembers which items failed their media info probe, so dead streams are not probed again every night.
    // Items that were never probed or that probed fine have no entry; successful results live in Emby itself.
    public sealed class MediaInfoFailureStore : IDisposable
    {
        private const int MaxErrorLength = 500;
        private const int SaveDelayMs = 5000;

        public const int MinRetryDays = 1;
        public const int MaxRetryDays = 365;

        // A scan that reaches an item slightly later than the night before must not make it wait another full day.
        private static readonly TimeSpan RetryTolerance = TimeSpan.FromHours(6);

        private readonly object _sync = new object();
        private readonly object _saveSync = new object();
        private readonly Dictionary<long, MediaInfoFailure> _failures = new Dictionary<long, MediaInfoFailure>();
        private readonly string _path;
        private readonly IJsonSerializer _json;
        private readonly ILogger _logger;
        private readonly Func<int[]> _getRetryDays;
        private readonly Timer _saveTimer;
        private bool _dirty;
        private bool _disposed;

        // getRetryDays returns the delay after the 1st, 2nd, ... failure; the last value repeats for later failures.
        // It is read on every check, so a changed setting also applies to items that have already failed.
        public MediaInfoFailureStore(string path, IJsonSerializer json, ILogger logger, Func<int[]> getRetryDays)
        {
            _path = path;
            _json = json;
            _logger = logger;
            _getRetryDays = getRetryDays;
            _saveTimer = new Timer(_ => Flush(), null, Timeout.Infinite, Timeout.Infinite);
            Load();
        }

        public void RecordFailure(long itemId, string error, DateTime nowUtc)
        {
            if (error != null && error.Length > MaxErrorLength)
                error = error.Substring(0, MaxErrorLength);

            lock (_sync)
            {
                if (!_failures.TryGetValue(itemId, out var failure))
                {
                    failure = new MediaInfoFailure { ItemId = itemId };
                    _failures[itemId] = failure;
                }
                failure.FailureCount++;
                failure.LastAttemptUtcTicks = nowUtc.Ticks;
                failure.LastError = error;
                failure.RetryNow = false;
                MarkDirty();
            }
        }

        public void Clear(long itemId)
        {
            lock (_sync)
            {
                if (_failures.Remove(itemId))
                    MarkDirty();
            }
        }

        // Returns a copy, so the caller never reads an entry that is being updated.
        public MediaInfoFailure Get(long itemId)
        {
            lock (_sync)
            {
                return _failures.TryGetValue(itemId, out var failure) ? Copy(failure) : null;
            }
        }

        public bool IsRetryDue(long itemId, DateTime nowUtc)
        {
            lock (_sync)
            {
                if (!_failures.TryGetValue(itemId, out var failure)) return true;
                return failure.RetryNow || nowUtc >= GetNextRetryUtc(failure);
            }
        }

        public DateTime GetNextRetryUtc(MediaInfoFailure failure)
        {
            var retryDays = _getRetryDays();
            var index = Math.Min(Math.Max(failure.FailureCount, 1), retryDays.Length) - 1;

            // Clamped here too, since a hand-edited config could otherwise overflow the date calculation.
            var days = Math.Min(Math.Max(retryDays[index], MinRetryDays), MaxRetryDays);
            return failure.GetLastAttemptUtc() + TimeSpan.FromDays(days) - RetryTolerance;
        }

        // Makes every failed item due on the next scan. Failure counts are kept, so the backoff still grows.
        public void MakeAllDue()
        {
            lock (_sync)
            {
                foreach (var failure in _failures.Values)
                    failure.RetryNow = true;
                if (_failures.Count > 0)
                    MarkDirty();
            }
        }

        public void RemoveWhere(Func<long, bool> shouldRemove)
        {
            List<long> ids;
            lock (_sync)
            {
                ids = _failures.Keys.ToList();
            }

            // The predicate may hit the library, so it runs outside the lock.
            var toRemove = ids.Where(shouldRemove).ToList();

            lock (_sync)
            {
                foreach (var id in toRemove)
                {
                    if (_failures.Remove(id))
                        MarkDirty();
                }
            }
        }

        public void Flush()
        {
            lock (_saveSync)
            {
                List<MediaInfoFailure> snapshot;
                lock (_sync)
                {
                    if (!_dirty) return;
                    _dirty = false;
                    snapshot = _failures.Values.Select(Copy).ToList();
                }

                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(_path));

                    // Written to a temp file first, so a crash mid-write cannot corrupt the saved results.
                    var tempPath = _path + ".tmp";
                    File.WriteAllText(tempPath, _json.SerializeToString(snapshot));
                    if (File.Exists(_path))
                        File.Replace(tempPath, _path, null);
                    else
                        File.Move(tempPath, _path);
                }
                catch (Exception ex)
                {
                    _logger.Warn("StrmCompanion MediaInfo: could not save failed items: {0}", ex.Message);
                    lock (_sync)
                    {
                        // Schedule another attempt, since nothing else would until the data changes again.
                        MarkDirty();
                    }
                }
            }
        }

        public void Dispose()
        {
            lock (_sync)
            {
                _disposed = true;
                _saveTimer.Dispose();
            }
            Flush();
        }

        private void Load()
        {
            try
            {
                if (!File.Exists(_path)) return;

                var saved = _json.DeserializeFromString<List<MediaInfoFailure>>(File.ReadAllText(_path));
                if (saved == null) return;

                foreach (var failure in saved)
                    _failures[failure.ItemId] = failure;
            }
            catch (Exception ex)
            {
                _logger.Warn("StrmCompanion MediaInfo: could not read failed items, starting empty: {0}", ex.Message);
            }
        }

        // Must be called while holding _sync.
        private void MarkDirty()
        {
            if (_dirty) return;
            _dirty = true;

            // Scans still winding down during shutdown can record after Dispose; a later Flush still saves them.
            if (!_disposed)
                _saveTimer.Change(SaveDelayMs, Timeout.Infinite);
        }

        private static MediaInfoFailure Copy(MediaInfoFailure failure) =>
            new MediaInfoFailure
            {
                ItemId = failure.ItemId,
                LastAttemptUtcTicks = failure.LastAttemptUtcTicks,
                FailureCount = failure.FailureCount,
                LastError = failure.LastError,
                RetryNow = failure.RetryNow
            };
    }
}
