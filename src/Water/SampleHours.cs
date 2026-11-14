using System;
using System.Collections.Generic;

namespace Sea
{
    /// <summary>
    /// (0.3.6) The game hour of each wave sample (whole seconds of <see cref="WaveClock"/> time), the same for every
    /// caller and on every side. The sea state and wind are functions of game hours, but the calendar only moves in steps
    /// (every 200 ms on the server, faster while players sleep) and each client's is a little off, so "now" can't be
    /// used. The server instead publishes anchors: from a sample a few seconds ahead, hours advance at the calendar's
    /// current rate. Every side picks, for each sample, the latest anchor in force there. Until a client hears of one,
    /// it makes its own from its calendar, and drops it for the server's.
    /// </summary>
    public sealed class SampleHours
    {
        /// <summary>At most this many are kept, whatever else (they're pruned by use long before).</summary>
        public const int Cap = 64;
        /// <summary>Anchors no blend from this many samples back on can use are dropped.</summary>
        public const int Behind = 2;
        readonly object sync = new();
        // in the order the server made them (Seq); local: one made here before any from the server
        HoursAnchor[] anchors = Array.Empty<HoursAnchor>();
        bool local;
        long droppedBelow = long.MinValue;
        long version;

        /// <summary>(0.4.2) Counts up whenever the anchors change (a sample's hour may read differently from then on).</summary>
        public long Version { get { lock (sync) return version; } }

        /// <summary>Has an anchor from the server (or, on the server, its own).</summary>
        public bool Anchored { get { lock (sync) return anchors.Length > 0 && !local; } }

        /// <summary>
        /// Take the server's anchor (made there, or received), which replaces one made locally. now: this side's
        /// current sample; anchors that no blend from <see cref="Behind"/> samples back on can use are dropped. One
        /// already had, or older than one dropped, is ignored, so receiving the server's whole list again changes nothing.
        /// </summary>
        public void Add(HoursAnchor a, long now)
        {
            if (a == null || !(a.HoursPerSample >= 0) || !double.IsFinite(a.HoursPerSample) || !double.IsFinite(a.Hours)) return;
            lock (sync)
            {
                if (local) { anchors = Array.Empty<HoursAnchor>(); local = false; version++; }
                if (a.Seq < droppedBelow) return;
                foreach (var b in anchors) if (b.Seq == a.Seq) return;
                var list = new List<HoursAnchor>(anchors) { a };
                list.Sort((p, q) => p.Seq.CompareTo(q.Seq));
                // the one in force Behind samples back: nothing made before it is used from there on
                int keepFrom = 0;
                for (int i = 0; i < list.Count; i++) if (list[i].FromSample <= now - Behind) keepFrom = i;
                keepFrom = Math.Max(keepFrom, list.Count - Cap);
                if (keepFrom > 0)
                {
                    droppedBelow = Math.Max(droppedBelow, list[keepFrom].Seq);
                    list.RemoveRange(0, keepFrom);
                }
                anchors = list.ToArray();
                version++;
            }
        }

        /// <summary>
        /// The server's anchor at wave time t, number seq (counting up): from `ahead` samples on, hours run on from where
        /// the calendar reads hoursNow, at its present speed.
        /// </summary>
        public static HoursAnchor Make(double t, double hoursNow, double hoursPerSecond, int ahead, long seq)
        {
            long n = WavePlace.SampleIndex(t) + ahead;
            double rate = Math.Max(0, hoursPerSecond);
            return new HoursAnchor { Seq = seq, FromSample = n, Hours = hoursNow + (n * WavePlace.SampleSeconds - t) * rate, HoursPerSample = rate * WavePlace.SampleSeconds };
        }

        /// <summary>How far (game hours) the calendar may run from the newest anchor before the server makes another.</summary>
        public const double MaxDrift = 0.05;

        /// <summary>
        /// Server: has the calendar left the newest anchor's line (an admin's /time set, players starting or stopping to
        /// sleep)? Then a new anchor is due now rather than at the next clock packet.
        /// </summary>
        public static bool Drifted(HoursAnchor newest, double t, double hoursNow)
        {
            if (newest == null) return true;
            double predicted = newest.Hours + (t / WavePlace.SampleSeconds - newest.FromSample) * newest.HoursPerSample;
            return !(Math.Abs(predicted - hoursNow) <= MaxDrift);
        }

        /// <summary>The newest anchor kept (the server's), or null.</summary>
        public HoursAnchor Newest
        {
            get { lock (sync) return anchors.Length == 0 || local ? null : anchors[anchors.Length - 1]; }
        }

        /// <summary>Forget every anchor (a new world, a new clock).</summary>
        public void Clear()
        {
            lock (sync) { anchors = Array.Empty<HoursAnchor>(); local = false; droppedBelow = long.MinValue; version++; }
        }

        /// <summary>
        /// The game hour of a sample. With no anchor yet, one is made here from this estimate (the calendar now, worked
        /// back to the sample) and used until the server's arrive.
        /// </summary>
        public double At(long sample, double estimate, double hoursPerSample)
        {
            lock (sync)
            {
                if (anchors.Length == 0)
                {
                    anchors = new[] { new HoursAnchor { Seq = long.MinValue, FromSample = sample, Hours = estimate, HoursPerSample = Math.Max(0, hoursPerSample) } };
                    local = true;
                    version++;
                }
                return Pick(sample).At(sample);
            }
        }

        // the anchor in force at a sample: the last made that starts at or before it, else the earliest-starting
        HoursAnchor Pick(long sample)
        {
            HoursAnchor best = null, first = anchors[0];
            foreach (var a in anchors)
            {
                if (a.FromSample <= sample) best = a;
                if (a.FromSample < first.FromSample) first = a;
            }
            return best ?? first;
        }

        /// <summary>The anchor in force at a sample, or null.</summary>
        public HoursAnchor InForce(long sample)
        {
            lock (sync) return anchors.Length == 0 ? null : Pick(sample);
        }

        /// <summary>Every anchor kept, in the order they were made (for the clock packet; none made locally).</summary>
        public HoursAnchor[] All
        {
            get { lock (sync) return local ? Array.Empty<HoursAnchor>() : anchors; }
        }
    }
}
