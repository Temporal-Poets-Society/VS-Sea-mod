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

        /// <summary>How much sky light gets through clouds and rain. (0.4.4) Fog no longer counts: the game fogs the water
        /// and the land itself, and fog scatters the sky's light rather than stopping it.</summary>
        public static float ClearSky(float cloud, float rain) =>
            Math.Clamp(1 - 0.92f * Math.Clamp(cloud, 0, 1), 0, 1) * (1 - 0.9f * Math.Clamp(rain, 0, 1));

        /// <summary>(0.4.4) Outdoor ambient floor at strength 1: 0.15 under a clear moonless sky, 0.45 under a high full moon
        /// (blue channel). The user chose the moonless level in game on 21 October (it was strength 2 of 0.4.3's scale);
        /// the moon's share keeps its ratio to it.</summary>
        public const float Moonless = 0.15f, FullMoonExtra = 0.30f;

        public static float Visibility(float sunY, float moonY, float illuminated, float clear, float scale) =>
            scale * Night(sunY) * clear * (Moonless + FullMoonExtra * Math.Clamp(illuminated, 0, 1) * AboveHorizon(moonY));

        /// <summary>(0.4.7) The sun's colour through the air by the sine of its altitude, high to low: white above 25 degrees,
        /// orange on the horizon. The same table as AstraTerra's drawn sun (Twilight.SunColour), so the glitter path
        /// matches the disc; the game's reflect colour stays pale yellow down to the horizon.</summary>
        static readonly float[,] SunColours =
        {
            { 25, 1.00f, 1.00f, 1.00f }, { 12, 1.00f, 0.95f, 0.84f }, { 6, 1.00f, 0.87f, 0.64f }, { 3, 1.00f, 0.76f, 0.46f },
            { 1, 1.00f, 0.63f, 0.32f }, { -1, 1.00f, 0.50f, 0.24f }, { -3, 0.95f, 0.40f, 0.20f },
        };

        public static (float R, float G, float B) SunThroughAir(float sunY)
        {
            float altitude = (float)(Math.Asin(Math.Clamp(sunY, -1, 1)) * 180 / Math.PI);
            if (altitude >= SunColours[0, 0]) return (1, 1, 1);
            int last = SunColours.GetLength(0) - 1;
            for (int i = 1; i <= last; i++)
            {
                if (altitude < SunColours[i, 0]) continue;
                float t = (altitude - SunColours[i, 0]) / (SunColours[i - 1, 0] - SunColours[i, 0]);
                return (Lerp(i, 1, t), Lerp(i, 2, t), Lerp(i, 3, t));
            }
            return (SunColours[last, 1], SunColours[last, 2], SunColours[last, 3]);
        }

        static float Lerp(int row, int channel, float t) => SunColours[row, channel] + (SunColours[row - 1, channel] - SunColours[row, channel]) * t;

        /// <summary>Illuminated fraction from the *displayed* sun and moon directions (new moon = conjunction).</summary>
        public static float Illuminated(float dotSunMoon) => Math.Clamp((1 - dotSunMoon) * 0.5f, 0, 1);

        /// <summary>The sky dome's own daylight (SystemRenderSkyColor; Wmap uses the same): how far the game's sky colour
        /// is from the fog colour it shows at night.</summary>
        public static float SkyDaylight(float dayLight, float moonLight, double aboveSea)
        {
            float daylight = 1.25f * Math.Max(dayLight - moonLight / 2, 0.05f);
            float high = (float)Math.Clamp((aboveSea - 1000) / 30000, 0, 1);
            return Math.Max(0, daylight * (1 - high));
        }

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
