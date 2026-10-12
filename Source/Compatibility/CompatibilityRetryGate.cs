using System.Threading;

namespace MoreNetworkCapacity.Compatibility
{
    // Lifecycle requests invalidate a completed pass without scanning from
    // the simulation thread on every interval. Requests may arrive from the
    // assembly loader, so completion must not overwrite a newer request.
    internal sealed class CompatibilityRetryGate
    {
        private int _requested = 1;
        private int _completed;
        private int _countdown;
        private readonly int _interval;

        internal CompatibilityRetryGate(int interval) { _interval = interval; }

        internal bool Pending
        {
            get { return Read(ref _requested) != Read(ref _completed); }
        }

        internal void Request()
        {
            Interlocked.Increment(ref _requested);
            Interlocked.Exchange(ref _countdown, 0);
        }

        internal bool TryBegin(out int generation)
        {
            generation = Read(ref _requested);
            if (generation == Read(ref _completed)) return false;
            if (Read(ref _countdown) > 0)
            {
                Interlocked.Decrement(ref _countdown);
                return false;
            }
            Interlocked.Exchange(ref _countdown, _interval);
            return true;
        }

        internal void Complete(int generation)
        {
            // A concurrent Request leaves requested != completed.
            Interlocked.Exchange(ref _completed, generation);
        }

        internal void Reset()
        {
            Interlocked.Exchange(ref _completed, 0);
            Interlocked.Exchange(ref _requested, 1);
            Interlocked.Exchange(ref _countdown, 0);
        }

        internal int Generation { get { return Read(ref _requested); } }

        private static int Read(ref int value)
        {
            return Interlocked.CompareExchange(ref value, 0, 0);
        }
    }
}
