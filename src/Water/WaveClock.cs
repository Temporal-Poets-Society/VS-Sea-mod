using System;
using System.Collections.Generic;

namespace Sea
{
    /// <summary>
    /// Seconds on a clock the server and every client agree on, so they draw the same waves: the server's own
    /// running time, sent to each client every few seconds (plus half its ping). A client runs its own in-game clock
    /// and eases toward the server's by at most 2% so the waves never jump; it snaps only when it's more than a
    /// second off (joining, a long pause).
    /// </summary>
    public sealed class WaveClock
    {
        public const double SnapSeconds = 1;
        public const double MaxSlew = 0.02;

        double offset, target;
        public bool Synced { get; private set; }
        /// <summary>How far this clock still is from the server's last word (seconds, + = behind).</summary>
        public double Error => target - offset;

        /// <summary>The shared time for this side's local clock reading.</summary>
        public double Now(double local) => local + offset;

        /// <summary>The server said its clock reads serverSeconds when ours reads local.</summary>
        public void OnServerTime(double serverSeconds, double local)
        {
            // (0.4.2) a NaN or infinite time is refused: once in the offset, no later packet could snap it back
            if (!double.IsFinite(serverSeconds) || !double.IsFinite(local)) return;
            target = serverSeconds - local;
            if (!Synced || !double.IsFinite(offset) || Math.Abs(target - offset) > SnapSeconds)
            {
                offset = target;
                Synced = true;
            }
        }

        /// <summary>Ease toward the server's clock; call every frame with the local time that passed.</summary>
        public void Advance(double dt)
        {
            if (!double.IsFinite(dt)) return;
            double max = MaxSlew * Math.Max(0, dt);
            offset += Math.Clamp(target - offset, -max, max);
        }
    }
}
