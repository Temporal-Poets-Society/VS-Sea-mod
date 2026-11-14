using System;
using System.Collections.Generic;

namespace Sea
{
    /// <summary>(0.4.2) What <see cref="WavePlace.Sample"/> asks for: the same answers on every side (<see cref="SeaSystem"/>'s).</summary>
    public interface IWaveSource
    {
        /// <summary>The game hour of wave sample n (wave time t is now: for a guess until the server's anchors arrive).</summary>
        double HoursOf(long n, double t);
        /// <summary>The sea state at game hour `hours`, as worked out for wave sample n (an admin's change of the fixed wind counts from its sample).</summary>
        SeaState SeaAt(double x, double z, double hours, long n);
        /// <summary>The wind the chop follows, likewise.</summary>
        Wind WindAt(double x, double z, double hours, long n);
        /// <summary>
        /// Changes whenever the answers may have changed (a new wind provider, a fixed wind, a new model, the samples'
        /// hours): places sampled before then sample again.
        /// </summary>
        long Revision { get; }
        /// <summary>
        /// (SE-0025) Changes whenever the answers for the same place, hour and sample may change (a provider, a fixed wind,
        /// a model), but not when only the samples' hours do: an end a place already worked out for the same hour is
        /// then kept. By default <see cref="Revision"/> (every change starts the place over).
        /// </summary>
        long Inputs => Revision;
    }

    /// <summary>
    /// The grid corners' sea state and wind, worked out once for every place that asks (the camera and the ships in a
    /// cell on a client, the ships and wave probes on the server): a corner's answers depend only on where it is, the
    /// game hour and sample asked for, and what the wind comes from, so each is kept for that and handed to every place
    /// alike. Thread-safe; only the last few samples are kept.
    /// </summary>
    internal sealed class SharedCorners : IWaveSource
    {
        readonly IWaveSource source;
        readonly System.Func<long> inputs;
        readonly object sync = new();
        readonly Dictionary<Key, (SeaState Sea, Wind Wind)> kept = new();
        readonly List<Key> old = new();
        long newest = long.MinValue;
        /// <summary>At most this many corners are kept (they're pruned by sample long before).</summary>
        public const int Cap = 1024;

        readonly record struct Key(double X, double Z, double Hours, long N, long Inputs);

        /// <param name="inputs">changes whenever the source may answer differently for the same place, hour and sample
        /// (a provider, a fixed wind, a model; not the samples' hours, which are part of the question); long.MinValue
        /// when its answers aren't fixed by those (no model yet: vanilla's wind), and nothing is kept</param>
        public SharedCorners(IWaveSource source, System.Func<long> inputs)
        {
            this.source = source;
            this.inputs = inputs;
        }

        public double HoursOf(long n, double t) => source.HoursOf(n, t);
        public long Revision => source.Revision;
        public long Inputs => source.Inputs;
        public SeaState SeaAt(double x, double z, double hours, long n)
        {
            long now = inputs();
            return now == long.MinValue ? source.SeaAt(x, z, hours, n) : Corner(x, z, hours, n, now).Sea;
        }

        public Wind WindAt(double x, double z, double hours, long n)
        {
            long now = inputs();
            return now == long.MinValue ? source.WindAt(x, z, hours, n) : Corner(x, z, hours, n, now).Wind;
        }

        (SeaState Sea, Wind Wind) Corner(double x, double z, double hours, long n, long before)
        {
            var key = new Key(x, z, hours, n, before);
            lock (sync)
                if (kept.TryGetValue(key, out var found)) return found;
            var v = (source.SeaAt(x, z, hours, n), source.WindAt(x, z, hours, n));
            // (something changed while this was worked out, from another thread: not kept)
            if (inputs() != before) return v;
            lock (sync)
            {
                if (n > newest)
                {
                    newest = n;
                    old.Clear();
                    foreach (var k in kept.Keys) if (k.N < n - 2) old.Add(k);
                    foreach (var k in old) kept.Remove(k);
                }
                if (kept.Count >= Cap) kept.Clear();
                kept[key] = v;
            }
            return v;
        }
    }
}
