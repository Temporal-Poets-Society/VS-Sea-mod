using System;

namespace Sea
{
    /// <summary>Presentation only. Never alters calendar light, block light or gameplay light levels.</summary>
    public static class NightSkyMath
    {
        public static float Smooth(float a, float b, float x)
        {
            float t = Math.Clamp((x - a) / (b - a), 0, 1);
            return t * t * (3 - 2 * t);
        }

        public static float Night(float sunY) => 1 - Smooth(-0.18f, 0.02f, sunY);
        public static float AboveHorizon(float y) => Smooth(0, 0.12f, y);
        public static float ClearSky(float cloud, float fog, float rain) =>
            Math.Clamp(1 - 0.92f * Math.Clamp(cloud, 0, 1), 0, 1)
            * (1 - 0.9f * Math.Clamp(rain, 0, 1)) * MathF.Exp(-Math.Max(0, fog - 0.001f) * 120);

        public static float Visibility(float sunY, float moonY, float illuminated, float clear, float scale) =>
            scale * Night(sunY) * clear * (0.075f + 0.15f * Math.Clamp(illuminated, 0, 1) * AboveHorizon(moonY));

        /// <summary>Illuminated fraction from the *displayed* sun and moon directions (new moon = conjunction).</summary>
        public static float Illuminated(float dotSunMoon) => Math.Clamp((1 - dotSunMoon) * 0.5f, 0, 1);

        /// <summary>Remove a known vanilla ambient moon contribution when AstraTerra displays a different local moon.
        /// Extra calendar lighting or eclipse changes are left alone. This only scales existing ambient down.</summary>
        public static float MoonAmbientScale(float sun, float oldMoon, float daylight, float moonY, float phase)
        {
            float oldBase = Math.Max(sun, oldMoon);
            if (oldBase <= 0.001f || Math.Abs(daylight - oldBase) > 0.01f) return 1;
            float local = 0.33f * Math.Clamp(phase, 0, 1) * Math.Clamp(0.66f + 0.66f * moonY, 0, 1)
                * Math.Clamp(moonY * 20, 0, 1);
            return Math.Clamp(Math.Max(sun, local) / oldBase, 0, 1);
        }
    }
}
