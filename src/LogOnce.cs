using System;
using System.Collections.Concurrent;
using Vintagestory.API.Common;

namespace Sea
{
    /// <summary>
    /// (SE-0045) An error that Sea answers safely (calm water, a shelter of 0.2, not dry) is still logged, once per place
    /// in the code and kind of error, so a bug does not pass as plausible water and a failing source does not fill the log
    /// sixty lines a second. Forgotten when a world starts (<see cref="Reset"/>).
    /// </summary>
    public static class LogOnce
    {
        static readonly ConcurrentDictionary<string, byte> seen = new();

        /// <param name="site">where, and for a foreign source which one (its type name)</param>
        public static void Error(ILogger log, string site, Exception e)
        {
            if (log == null || e == null) return;
            if (seen.Count > 256 || !seen.TryAdd(site + "|" + e.GetType().FullName, 0)) return;
            log.Error("[sea] {0} failed; Sea answers safely from now on and logs this only once: {1}", site, e);
        }

        public static void Reset() => seen.Clear();
    }
}
