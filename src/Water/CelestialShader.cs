using System;

namespace Sea
{
    public static class CelestialShader
    {
        public const string Marker = "seaCelestial";
        // Wmap's far-terrain light term: up to 0.4.2 the sun colour, from 0.4.3 the game's ambient colour.
        static readonly string[] FarLight = { "max(vec3(0.05), sun)", "clamp(wmapAmbient, 0.0, 1.0)" };

        /// <summary>Sea's night floor and sky reflection text-patched into Wmap's far terrain shader (Wmap 0.4.x). Wmap 0.5
        /// declares seaNightFloor, seaSkyReflect and the wave uniforms itself and does that shading in all its far programs
        /// (its far-water contract); Sea then only sets the uniforms, and a shader that declares seaNightFloor is left as it is.</summary>
        public static string FarFragment(string code)
        {
            if (code == null || code.Contains("seaNightFloor") || !code.Contains("void main()")) return null;
            string light = Array.Find(FarLight, l => code.Contains("albedo * " + l));
            if (light == null) return null;
            if (!code.Contains("haze = min(haze, clamp(dayLight"))
            {
                const string mix = "color.rgb = mix(color.rgb, sky.rgb, haze);";
                if (!code.Contains(mix)) return null;
                code = code.Replace(mix, "haze = mix(haze, min(haze, clamp(dayLight, 0.0, 1.0)), seaNightEnabled);\n    " + mix);
            }
            code = code.Replace("albedo * " + light, "albedo * max(" + light + ", seaNightFloor)");
            // (0.4.4) the distant sea reflects the sky it already looks up (Fresnel at its grazing angle), as Sea's near
            // water does, so the sea meets the sky at the horizon without a step where Wmap takes over
            const string skyMix = "color.rgb = mix(color.rgb, sky.rgb, haze);";
            if (code.Contains("bool water") && code.Contains("in vec3 wmapPos;") && code.Contains(skyMix))
                code = code.Replace(skyMix, skyMix + FarSkyReflection);
            return code.Replace("void main()", "uniform vec3 seaNightFloor = vec3(0.0);\nuniform float seaNightEnabled = 0.0;\nuniform float seaSkyReflect = 0.0;\nvoid main()");
        }

        // (0.4.6) The dome as it reaches the screen: at night the sky colour function returns the fog colour with alpha
        // fogAmount, and the dome shows rgb * alpha over dark space. Reflecting the rgb alone drew a fog-brown horizon.
        public const string FarSkyReflection = @"
    if (water && seaSkyReflect > 0.0) {
        float seaCos = clamp(-wmapPos.y / max(length(wmapPos), 0.001), 0.0, 1.0);
        color.rgb = mix(color.rgb, sky.rgb * clamp(sky.a, 0.0, 1.0), clamp(seaSkyReflect * (0.02 + 0.98 * pow(1.0 - seaCos, 5.0)), 0.0, 1.0));
    }";
        // The wave gradient is evaluated at vertices, then interpolated. No 24-wave loop per water pixel.
        public static string Vertex(string code)
        {
            const string anchor = "fragNormal = unpackNormal(renderFlags);";
            if (code == null || code.Contains("out vec2 seaSurfaceSlope;") || !code.Contains(anchor)) return null;
            code = code.Replace(anchor, anchor + @"
    seaSurfaceSlope = vec2(0.0);
    if ((waterFlagsIn & 1) == 1 && (waterFlagsIn & LiquidIsLavaBitMask) == 0) {
        float div = ((waterFlagsIn & LiquidWeakWaveBitMask) > 0 ? 90.0 : 5.0)
            * max(0.2, 1.0 - float((waterFlagsIn >> 2) & 255) / 255.0);
        for (int i = 0; i < seaWaveCount; i++) {
            vec4 w = seaWave[i];
            seaSurfaceSlope += w.w * w.xy * cos(dot(w.xy, (xyz + origin).xz) + w.z) / div;
        }
    }");
            return code.Insert(code.IndexOf("void main(", StringComparison.Ordinal), "out vec2 seaSurfaceSlope;\n");
        }

        public static string Fragment(string code)
        {
            const string start = "// Specular reflection";
            const string end = "texColor.rgb *= 1 + f;";
            const string finish = "texColor = applyFog(texColor, fogAmount);";
            if (code == null || code.Contains(Marker)) return null;
            int s = code.IndexOf(start, StringComparison.Ordinal), e = code.IndexOf(end, StringComparison.Ordinal);
            if (s < 0 || e <= s || !code.Contains(finish)) return null;
            // Preserve vanilla verbatim behind a runtime switch; never layer two sun highlights.
            string old = code.Substring(s, e - s);
            code = code.Remove(s, e - s).Insert(s, "if (seaEffects.x <= 0.0) {\n" + old + "}\n");
            code = code.Replace(finish, @"
    if (!isLava && fragNormal.y > 0.9 && skyExposed > 0 && cameraUnderwater < 0.01 && (seaEffects.x > 0.0 || seaEffects.y > 0.0)) {
        vec3 eyeDelta = fWorldPos - seaEye;
        vec3 eye = eyeDelta / max(length(eyeDelta), 0.001);
        vec3 n = normalize(vec3(-seaSurfaceSlope.x, 1.0, -seaSurfaceSlope.y));
        vec3 ray = reflect(eye, n);
        float fres = 0.02 + 0.98 * pow(1.0 - max(0.0, dot(-eye, n)), 5.0);
        // (0.4.4) the sky the game draws, reflected by Fresnel: faint looking down, the sky's own colour at the horizon.
        // Smooth in direction, so waves shade it without speckles; it continues into Wmap's distant sea.
        if (seaEffects.y > 0.0) texColor = seaSkyReflect(texColor, ray, fres, rgbaFog.rgb);
        // Glints stop before Wmap's distant sea (from 0.75 of the view distance), which has none.
        float seaEdge = 1.0 - smoothstep(viewDistance * 0.50, viewDistance * 0.74, length(fWorldPos.xz));
        float exposed = seaEdge * smoothstep(0.0, 0.08, ray.y);
        // The OIT pass premultiplies by alpha, so a glint must raise alpha to show (as vanilla's does). The sun and
        // moon are far brighter than the sky, so the glint saturates in a low-dynamic-range frame even where Fresnel
        // reflectance is small; Fresnel only shades it.
        if (seaEffects.x > 0.0) texColor = seaGlints(texColor, ray, exposed * (0.55 + 0.45 * fres), clamp(pow(shadowBright, 4.0), 0.0, 1.0));
    }
    " + finish);
            return code.Insert(code.IndexOf("void main(", StringComparison.Ordinal), Declarations);
        }

        public const string Declarations = @"
in vec2 seaSurfaceSlope;
uniform vec3 seaEye;
uniform vec4 seaSun;
uniform vec4 seaMoon;
uniform vec3 seaSunColor;
uniform vec4 seaWeather; // clear sky, night, horizon cloud (the game's horizonFog), roughness
uniform vec2 seaEffects = vec2(0.0); // glints, sky reflection
uniform vec4 seaSky; // the sky dome's daylight, sunset shift, fog minimum, fog density
uniform sampler2D seaSkyTex;  // the game's sky colours (sky.png)
uniform sampler2D seaGlowTex; // the game's sun glow (sunlight.png)
float seaCelestial(float p, float width) { return exp(-max(0.0, 1.0-p) / width); }
float seaLobe(vec3 ray, vec3 body, float width) {
    return seaCelestial(dot(ray, body), width) * smoothstep(0.0, 0.10, body.y) * seaWeather.x;
}
// Sun glitter and moon trail. glint: horizon/edge fade and Fresnel shading; shade: the sun's shadow-map brightness.
vec4 seaGlints(vec4 c, vec3 ray, float glint, float shade) {
    float width = 0.0012 + seaWeather.w * 0.018;
    float sun = clamp(seaEffects.x * glint * seaLobe(ray, seaSun.xyz, width) * seaSun.w * shade, 0.0, 1.0);
    float moon = clamp(seaEffects.x * glint * seaLobe(ray, seaMoon.xyz, width * 1.3) * seaMoon.w, 0.0, 1.0);
    c.rgb = mix(c.rgb, seaSunColor, sun);
    c.rgb = mix(c.rgb, vec3(0.62, 0.72, 0.90), moon);
    c.a = max(c.a, max(sun, moon));
    return c;
}
// The game's sky colour in a direction (skycolor.fsh getSkyColorAt, without its per-pixel dither): sky.png by the sun's
// height and the direction's, its sunlight.png glow, its fog, and the fog colour it fades to as the light goes. As the
// light goes the dome's alpha falls to its fog amount and it shows rgb * alpha over dark space, so that is returned.
vec3 seaSkyColor(vec3 ray, vec3 fogColor) {
    vec3 d = normalize(vec3(ray.x, max(ray.y, 0.0), ray.z));
    float u = (seaSun.y + 1.0) / 2.0;
    float vl = 1.0 - clamp(distance(d, seaSun.xyz) / 6.0, 0.0, 1.0);
    vec4 glow = texture(seaGlowTex, vec2(clamp(u - seaSky.y * 2.0, 0.0, 1.0), 1.0 - vl));
    vec3 c = texture(seaSkyTex, vec2(u, pow(1.0 - d.y, 0.25))).rgb;
    c = c * (1.0 - glow.a) + glow.rgb * glow.a;
    float fog = clamp(seaSky.z + max(seaSky.w * 120.0 - 0.12, 0.0), 0.0, 1.0);
    fog += max(0.0, ((1.0 - d.y) / 2.0 + 0.3) * 0.7667 - 0.5) * max(min(1.0, 5.0 * fog), seaWeather.z);
    fog = clamp(fog, 0.0, 1.0);
    c = mix(c, fogColor, fog);
    float dark = 1.0 - clamp(seaSky.x, 0.0, 1.0);
    return mix(c, fogColor, dark) * mix(1.0, fog, dark);
}
// Water reflects the sky by Fresnel; reflected light covers what is seen through the water, so alpha rises with it.
vec4 seaSkyReflect(vec4 c, vec3 ray, float fres, vec3 fogColor) {
    float r = clamp(seaEffects.y * fres, 0.0, 1.0);
    c.rgb = mix(c.rgb, seaSkyColor(ray, fogColor), r);
    c.a = max(c.a, r);
    return c;
}
";
    }
}
