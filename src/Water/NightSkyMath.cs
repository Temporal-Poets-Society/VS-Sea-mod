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

        /// <summary>(0.4.7-night) How much of the night floor is in: none with the sun on the horizon, all by the end of civil
        /// twilight (6° down). Starlight is there all through twilight; the floor only shows once the twilight (now real,
        /// TwilightSunY) has fallen under it, so the dusk darkens steadily into the night instead of dipping. Was −10° to +1°.</summary>
        public static float Night(float sunY) => 1 - Smooth(-0.1045f, 0, sunY);

        /// <summary>(0.4.7-night) How much sky light gets through clouds, rain and fog. Cloud thickness counts more than
        /// linearly (thickening cloud before rain already darkens the night), and thick fog halves it (density 0.004 to
        /// 0.03: vanilla's clear sky is 0.001, its haze 0.007–0.02). Was clouds × (1 − 0.92 c) and no fog (0.4.4).</summary>
        public static float ClearSky(float cloud, float rain, float fog = 0) =>
            Math.Clamp(1 - 0.95f * MathF.Pow(Math.Clamp(cloud, 0, 1), 0.75f), 0, 1) * (1 - 0.9f * Math.Clamp(rain, 0, 1))
            * (1 - 0.5f * Smooth(0.004f, 0.03f, fog));

        /// <summary>(0.4.7-night) Outdoor ambient floor at strength 1 (blue channel): 0.10 under a clear moonless sky, 0.40
        /// under a high full moon. 0.4.4–0.4.7-pre had 0.15 / 0.45 (the user's pick of 21 October); on 25 October the user
        /// found moonless nights too light, so the moonless level is two thirds of it (`.sea night 1.5` = the old one).</summary>
        public const float Moonless = 0.10f, FullMoonExtra = 0.30f;

        /// <summary>(0.4.7-night) The moon's share of the floor: its lit fraction to the power 1.5 (a half moon gives about a
        /// third of the full moon's light) times its height: 0.35 just over the horizon (the air dims it and it lights the
        /// ground at a slant) to 1 from 35° up.</summary>
        public static float MoonLight(float illuminated, float moonY) =>
            MathF.Pow(Math.Clamp(illuminated, 0, 1), 1.5f) * MoonHeight(moonY);

        public static float MoonHeight(float moonY) =>
            Smooth(-0.01f, 0.06f, moonY) * Math.Clamp(0.35f + 0.65f * moonY / 0.574f, 0, 1);

        public static float Visibility(float sunY, float moonY, float illuminated, float clear, float scale) =>
            scale * Night(sunY) * clear * (Moonless + FullMoonExtra * MoonLight(illuminated, moonY));

        /// <summary>(0.4.7-night) Real twilight for the rendered day's light: below the horizon the game's sun term is read
        /// as if the sun stood 2.5 times as far down (−18° reads as −45°, where vanilla's sunlight.png reaches its night
        /// value), so dusk keeps the game's colours but ends when the real sky does. Above the horizon nothing changes.</summary>
        public const float TwilightStretch = 2.5f;

        public static float TwilightSunY(float sunY)
        {
            if (!(sunY < 0)) return sunY;
            float altitude = MathF.Asin(Math.Clamp(sunY, -1, 1)) * TwilightStretch;
            return MathF.Sin(Math.Max(altitude, -MathF.PI / 2));
        }

        /// <summary>Where the game reads its sunlight.png row for a sun this high (ClientGameCalendar.Update).</summary>
        public static float SunlightTextureX(float sunY) => (Math.Clamp(sunY * 1.4f + 0.2f, -1, 1) + 1) / 2;

        /// <summary>(0.4.7-pre.7) How much the reflect colour (the ambient's and the fog's tint) is darkened for a sun this far
        /// down (the twilight's sine). The game's own step (ClientGameCalendar.Update) subtracts −5y − 0.3 once the sun is
        /// 0.1° down, which is negative until 3.4° down: the colour jumped 0.29 brighter at that instant and the fog with it.
        /// Here it only darkens, from nothing.</summary>
        public static float ReflectDarkening(float sunY) => Math.Max(0, -5 * sunY - 0.3f);

        /// <summary>(0.4.7-pre.7) How much of the game's shadows to keep. The game casts them from the moon when its light term
        /// is the larger, else from the sun (SystemRenderShadowMap), with no margin, and keeps them on whenever the moon is up:
        /// with real twilight the two terms cross soon after sunset, so for a while the shadows came from a sun at or below the
        /// horizon and flipped to the moon at once. Gone while the casting light is under 3° (the game drops a sun-only shadow
        /// under 2.75° itself) and while the two terms are within 0.04 of each other.</summary>
        public static float ShadowKeep(float castingY, float moonMinusSun) =>
            Smooth(0, 0.05f, castingY) * Smooth(0, 0.04f, Math.Abs(moonMinusSun));

        /// <summary>(0.4.7-pre.7) How far the shading direction has gone from the sun to the moon (the game: all within a 0.02
        /// difference, 50 × (moon − sun)); here over ±0.04, while ShadowKeep has the shadows off.</summary>
        public static float LightTowardMoon(float moonMinusSun) => Smooth(-0.04f, 0.04f, moonMinusSun);

        /// <summary>(0.4.7-night) The rendered day's light once the sun's term is replaced: vanilla takes the larger of the
        /// sun's and the moon's terms, so only the part of it that came from them changes and anything else the game added
        /// (height above the sea) stays. Never brighter than the game's.</summary>
        public static float Daylight(float vanillaDaylight, float vanillaSun, float vanillaMoon, float sun, float moon) =>
            Math.Clamp(vanillaDaylight - (Math.Max(vanillaSun, vanillaMoon) - Math.Max(sun, moon)), 0, vanillaDaylight);

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
        public static float MoonAmbientScale(float sun, float oldMoon, float daylight, float moonY, float phase, float clear = 1)
        {
            float oldBase = Math.Max(sun, oldMoon);
            if (oldBase <= 0.001f || Math.Abs(daylight - oldBase) > 0.01f) return 1;
            float local = 0.33f * Math.Clamp(phase, 0, 1) * Math.Clamp(0.66f + 0.66f * moonY, 0, 1)
                * Math.Clamp(moonY * 20, 0, 1) * Math.Clamp(clear, 0, 1);
            return Math.Clamp(Math.Max(sun, local) / oldBase, 0, 1);
        }
    }
}
