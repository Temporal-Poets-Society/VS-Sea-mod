using System;

namespace Sea
{
    /// <summary>
    /// Hashing, pure and deterministic, so the server and every client get the same numbers from the same key (the
    /// wave trains' phases). Sea's noise wind went with its weather in 0.4.0.
    /// </summary>
    public static class Noise
    {
        /// <summary>splitmix64 finalizer.</summary>
        public static ulong Mix(ulong a)
        {
            a += 0x9E3779B97F4A7C15UL;
            a = (a ^ (a >> 30)) * 0xBF58476D1CE4E5B9UL;
            a = (a ^ (a >> 27)) * 0x94D049BB133111EBUL;
            return a ^ (a >> 31);
        }

        public static ulong Hash(long key, long a, long b, long c) => Mix((ulong)key ^ Mix((ulong)a ^ Mix((ulong)b ^ Mix((ulong)c))));

        /// <summary>0 ≤ u &lt; 1 from a hash.</summary>
        public static double Unit(ulong h) => (h >> 11) * (1.0 / (1UL << 53));
    }
}
