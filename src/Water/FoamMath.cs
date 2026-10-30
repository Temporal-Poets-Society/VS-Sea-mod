using System;

namespace Sea
{
    public enum FoamView { All, Wind, Wakes, Off }

    /// <summary>Cosmetic windrow onset, mirrored in seaWindStreaks. Independent of wave/ship sampling.</summary>
    public static class FoamMath
    {
        public static double StreakRamp(double strength)
        {
            double t=Math.Clamp((strength-0.68)/0.36,0,1);
            return t*t*(3-2*t);
        }
    }
}
