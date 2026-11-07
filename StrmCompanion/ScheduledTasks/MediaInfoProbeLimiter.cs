using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace StrmCompanion.ScheduledTasks
{
    // Caps media info probes across the full scan and auto-scan, using MediaInfoConcurrency as the limit.
    // Static because Emby creates its own MediaInfoScheduledTask instance alongside the one in PluginEntryPoint.
    // The limit is read on every acquire and release, so a changed setting also applies to queued scans.
    internal static class MediaInfoProbeLimiter
    {
        private static readonly object Sync = new object();
        private static readonly LinkedList<TaskCompletionSource<bool>> Waiters =
            new LinkedList<TaskCompletionSource<bool>>();
        private static int _active;

        private static int Limit => Math.Max(1, Plugin.Instance?.Configuration?.MediaInfoConcurrency ?? 1);

        // Waits in FIFO order for a free slot. Dispose the result to release the slot.
        public static async Task<IDisposable> AcquireAsync(CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();

            TaskCompletionSource<bool> waiter;
            LinkedListNode<TaskCompletionSource<bool>> node;
            lock (Sync)
            {
                if (Waiters.Count == 0 && _active < Limit)
                {
                    _active++;
                    return new Releaser();
                }

                waiter = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                node = Waiters.AddLast(waiter);
            }

            using (ct.Register(() =>
            {
                lock (Sync)
                {
                    // A detached node has already been given a slot, which the caller must release.
                    if (node.List == null) return;
                    Waiters.Remove(node);
                }
                waiter.TrySetCanceled();
            }))
            {
                await waiter.Task.ConfigureAwait(false);
            }

            return new Releaser();
        }

        private static void Release()
        {
            lock (Sync)
            {
                _active--;
                while (Waiters.Count > 0 && _active < Limit)
                {
                    var next = Waiters.First.Value;
                    Waiters.RemoveFirst();
                    _active++;
                    next.TrySetResult(true);
                }
            }
        }

        private sealed class Releaser : IDisposable
        {
            private int _released;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) == 0)
                    Release();
            }
        }
    }
}
