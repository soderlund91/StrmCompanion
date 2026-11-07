using System;
using System.Threading;
using StrmCompanion.Jobs;

namespace StrmCompanion.MergeVersion
{
    // Serializes merge and split jobs. MergeItems/SplitItems rewrite PresentationUniqueKey one item at a
    // time with a fresh key per call, so two jobs touching the same items concurrently can leave a version
    // group with mixed keys, which Emby shows as split.
    internal static class VersionJobLock
    {
        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);

        // Blocks until no other merge/split job is running. Dispose the result to release the lock.
        public static IDisposable Acquire(JobManager jobManager, string jobId, CancellationToken ct)
        {
            if (!Gate.Wait(0))
            {
                jobManager.SetActivity(jobId, "Waiting for another merge/split job to finish...");
                Gate.Wait(ct);
            }
            return new Releaser();
        }

        private sealed class Releaser : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    Gate.Release();
            }
        }
    }
}
