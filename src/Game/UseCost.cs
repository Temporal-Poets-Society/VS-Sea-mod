using System.Diagnostics;

namespace Sea
{
    /// <summary>
    /// (SE-0023) <c>.sea cost</c>: what Sea's postfixes on <c>ShaderProgramBase.Use</c> (the water uniforms and the
    /// celestial uniforms) cost the render thread per frame. Measured only between two <c>.sea cost</c>; otherwise each
    /// postfix pays one bool check.
    /// </summary>
    public static class UseCost
    {
        static bool on;
        static long ticks, calls;
        static int frames;

        /// <summary>A timestamp when measuring, else 0.</summary>
        public static long Begin() => on ? Stopwatch.GetTimestamp() : 0;

        public static void End(long start)
        {
            if (start == 0) return;
            ticks += Stopwatch.GetTimestamp() - start;
            calls++;
        }

        /// <summary>Client, first thing each frame (<see cref="SeaSystem"/>'s frame start).</summary>
        public static void Frame() { if (on) frames++; }

        /// <summary>Start measuring, or stop and say what it cost.</summary>
        public static string Toggle()
        {
            if (!on)
            {
                ticks = calls = 0;
                frames = 0;
                on = true;
                return "Measuring what Sea's shader-use postfixes cost; run .sea cost again to see it.";
            }
            on = false;
            double us = ticks * 1e6 / Stopwatch.Frequency;
            return frames == 0 ? "No frames measured." : string.Format(System.Globalization.CultureInfo.InvariantCulture,
                "Sea's shader-use postfixes: {0:0.0} µs a frame ({1:0} calls a frame, {2:0} ns a call) over {3} frames.",
                us / frames, (double)calls / frames, calls > 0 ? us * 1000 / calls : 0, frames);
        }
    }
}
