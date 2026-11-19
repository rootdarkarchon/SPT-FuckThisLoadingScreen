using System.Collections.Generic;

namespace FuckThisLoadingScreen
{
    /// <summary>
    /// Lets at most one message per key through per interval and counts the messages it suppressed in between.
    /// Thread-safe: log callbacks can fire from background threads.
    /// </summary>
    internal sealed class LogRateLimiter
    {
        private sealed class Slot
        {
            internal long NextAllowedMs;
            internal int Suppressed;
        }

        private readonly long _intervalMs;
        private readonly Dictionary<string, Slot> _slots = new Dictionary<string, Slot>();

        internal LogRateLimiter(long intervalMs)
        {
            _intervalMs = intervalMs;
        }

        /// <summary>True when the message for <paramref name="key"/> may be logged now; <paramref name="suppressed"/> is the number dropped since the last allowed one.</summary>
        internal bool TryAcquire(string key, long nowMs, out int suppressed)
        {
            lock (_slots)
            {
                if (!_slots.TryGetValue(key, out var slot))
                {
                    slot = new Slot();
                    _slots.Add(key, slot);
                }
                else if (nowMs < slot.NextAllowedMs)
                {
                    slot.Suppressed++;
                    suppressed = 0;
                    return false;
                }

                suppressed = slot.Suppressed;
                slot.Suppressed = 0;
                slot.NextAllowedMs = nowMs + _intervalMs;
                return true;
            }
        }
    }
}
