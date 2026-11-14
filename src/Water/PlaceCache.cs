using System.Collections.Generic;

namespace Sea
{
    /// <summary>
    /// (SE-0071 R3) A small cache of wave places, one per <see cref="WavePlace.GridCell"/> cell, the least recently used
    /// dropped beyond <see cref="Capacity"/>: the server's probes and the contracts' places both keep one. Not thread-safe:
    /// its owner holds a lock around it and the sampling.
    /// </summary>
    public sealed class PlaceCache
    {
        public const int Capacity = 32;

        sealed class Slot { public readonly WavePlace Place = new(); public long Used; }
        readonly Dictionary<(long, long), Slot> slots = new();
        long uses;

        /// <summary>The place kept for the cell of (x, z) (made if new; not sampled here).</summary>
        public WavePlace For(double x, double z)
        {
            var key = ((long)System.Math.Floor(x / WavePlace.GridCell), (long)System.Math.Floor(z / WavePlace.GridCell));
            if (!slots.TryGetValue(key, out var slot))
            {
                if (slots.Count >= Capacity)
                {
                    var oldest = key; long used = long.MaxValue;
                    foreach (var kv in slots) if (kv.Value.Used < used) { used = kv.Value.Used; oldest = kv.Key; }
                    slots.Remove(oldest);
                }
                slots[key] = slot = new Slot();
            }
            slot.Used = ++uses;
            return slot.Place;
        }
    }
}
