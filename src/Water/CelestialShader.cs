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
    seaSurfaceRough = 0.0;
    // (only the glints and the sky reflection use it: not worked out while both are off)
    if ((seaEffects.x > 0.0 || seaEffects.y > 0.0) && (waterFlagsIn & 1) == 1 && (waterFlagsIn & LiquidIsLavaBitMask) == 0) {
        float div = ((waterFlagsIn & LiquidWeakWaveBitMask) > 0 ? 90.0 : 5.0)
            * max(0.2, 1.0 - float((waterFlagsIn >> 2) & 255) / 255.0);
        // (0.4.7) Blocks per pixel here, as Wmap's far sea measures it (length of fwidth of the position): the distance
        // times the pixel's angle, stretched along the view by how grazing it is. 0 (no fading) until Sea sets seaPixel.
        vec3 seaFromEye = (xyz + origin) - seaEye;
        float seaDist = length(seaFromEye);
        float seaGraze = max(abs(seaFromEye.y) / max(seaDist, 0.001), 0.01);
        float seaFootprint = seaPixel * seaDist * sqrt(1.0 + 1.0 / (seaGraze * seaGraze));
        for (int i = 0; i < seaWaveCount; i++) {
            vec4 w = seaWave[i];
            // A train shorter than about four pixels fades out (Wmap's wmapWaveNormal, the same rule), so the near water
            // meets the far sea without a step; the slope it would have given is kept as roughness for the glints.
            float k = length(w.xy);
            float keep = 1.0 - smoothstep(0.25, 0.75, k * seaFootprint);
            float steep = w.w * k / div;
            seaSurfaceSlope += keep * w.w * w.xy * cos(dot(w.xy, (xyz + origin).xz) + w.z) / div;
            seaSurfaceRough += (1.0 - keep * keep) * 0.5 * steep * steep;
        }
    }");
            // (seaEffects and seaEye as the fragment stage declares them: one uniform of the program)
            return code.Insert(code.IndexOf("void main(", StringComparison.Ordinal),
                "out vec2 seaSurfaceSlope;\nout float seaSurfaceRough;\nuniform vec2 seaEffects = vec2(0.0);\nuniform vec3 seaEye;\nuniform float seaPixel = 0.0;\n");
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
        float fres = 0.02 + 0.98 * pow(1.0 - clamp(dot(-eye, n), 0.0, 1.0), 5.0);
        // (0.4.4) the sky the game draws, reflected by Fresnel: faint looking down, the sky's own colour at the horizon.
        // Smooth in direction, so waves shade it without speckles; it continues into Wmap's distant sea.
        if (seaEffects.y > 0.0) texColor = seaSkyReflect(texColor, ray, fres, rgbaFog.rgb);
        // Glints stop before Wmap's distant sea (from 0.75 of the view distance), which has none.
        float seaEdge = 1.0 - smoothstep(viewDistance * 0.50, viewDistance * 0.74, length(fWorldPos.xz));
        // (0.4.7) Only rays turned well down into the sea are cut; the lobe already needs the ray on the body, and a
        // setting sun's reflected ray is level or a little below (0.4.6 needed it 4.6 degrees up for full strength).
        float exposed = seaEdge * smoothstep(-0.06, -0.02, ray.y);
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
in float seaSurfaceRough; // (0.4.7) the slope variance of trains too short for the pixel, faded out of seaSurfaceSlope
uniform vec3 seaEye;
uniform vec4 seaSun;
uniform vec4 seaMoon;
uniform vec3 seaSunColor;
uniform vec4 seaWeather; // clear sky, night, horizon cloud (the game's horizonFog), roughness
uniform vec2 seaEffects = vec2(0.0); // glints, sky reflection
uniform vec4 seaSky; // the sky dome's daylight, sunset shift, fog minimum, fog density
uniform float seaAboveSea; // (0.4.7) the eye above sea level (the game's playerToSealevelOffset)
uniform vec4 seaSunset = vec4(0.0); // (0.4.7) AstraTerra's sunset glow (SunsetScatter.Shown): sun colour x strength, strength
uniform sampler2D seaSkyTex;  // the game's sky colours (sky.png)
uniform sampler2D seaGlowTex; // the game's sun glow (sunlight.png)
float seaCelestial(float p, float width) { return exp(-max(0.0, 1.0-p) / width); }
// (0.4.7) AstraTerra's sunset glow for a direction v and the sun (its SunsetScatter.Shape, the same numbers): a halo round
// the sun and a band along the horizon, strongest beneath it.
float seaSunsetShape(vec3 v, vec3 sun) {
    float c = max(dot(v, sun), 0.0);
    float halo = 0.8 * pow(c, 24.0) + 0.3 * pow(c, 4.0);
    vec2 vh = v.xz / max(length(v.xz), 1e-4);
    vec2 sh = sun.xz / max(length(sun.xz), 1e-4);
    float toward = 0.5 + 0.5 * dot(vh, sh);
    float band = exp(-max(v.y, 0.0) * 10.0) * (0.15 + 0.85 * toward * toward);
    return halo + 0.55 * band;
}
// (0.4.7) Full while the body's centre is up, gone once its top has set (2.3 degrees below). Up to 0.4.6 it faded out
// from 6 degrees altitude, so sunrise and sunset, when the glitter path is longest and brightest, had none; pre.3
// faded it from 2 degrees up, which a play-test found too soon.
float seaLobe(vec3 ray, vec3 body, float width) {
    return seaCelestial(dot(ray, body), width) * smoothstep(-0.04, 0.0, body.y) * seaWeather.x;
}
// Sun glitter and moon trail. glint: horizon/edge fade and Fresnel shading; shade: the sun's shadow-map brightness.
// (0.4.7) Far off, the trains too short for a pixel are faded out of the normal; their slope widens the lobe instead
// (a reflected ray turns twice the slope: 1 - cos = 2 x the variance) and spreads the same light over it, so the glitter
// path keeps its width toward the horizon instead of narrowing to a mirror's line.
vec4 seaGlints(vec4 c, vec3 ray, float glint, float shade) {
    float width = 0.0012 + seaWeather.w * 0.018;
    float spread = width + min(2.0 * seaSurfaceRough, 0.02);
    glint *= width / spread;
    float sun = clamp(seaEffects.x * glint * seaLobe(ray, seaSun.xyz, spread) * seaSun.w * shade, 0.0, 1.0);
    float moon = clamp(seaEffects.x * glint * seaLobe(ray, seaMoon.xyz, spread * 1.3) * seaMoon.w, 0.0, 1.0);
    c.rgb = mix(c.rgb, seaSunColor, sun);
    c.rgb = mix(c.rgb, vec3(0.62, 0.72, 0.90), moon);
    c.a = max(c.a, max(sun, moon));
    return c;
}
// The game's sky colour in a direction (skycolor.fsh getSkyColorAt, without its per-pixel dither): sky.png by the sun's
// height and the direction's, its sunlight.png glow, its fog, and the fog colour it fades to as the light goes. As the
// light goes the dome's alpha falls to its fog amount and it shows rgb * alpha over dark space, so that is returned.
// (0.4.7) With the game's flat fog and its horizon raised by the eye's height above the sea, as the dome and Wmap's far
// sea (which calls getSkyColorAt itself) have them: without them the near water reflected a clearer, darker sky than
// the far sea beyond it. And AstraTerra's sunset glow, mixed in after, as it is into the dome.
vec3 seaSkyColor(vec3 ray, vec3 fogColor) {
    vec3 d = normalize(vec3(ray.x, max(ray.y, 0.0), ray.z));
    vec3 skyPos = d * 250.0; // the length Wmap's far sea gives getSkyColorAt
    vec3 v2 = normalize(vec3(skyPos.x, skyPos.y + 0.25 * seaAboveSea, skyPos.z));
    float u = (seaSun.y + 1.0) / 2.0;
    float vl = 1.0 - clamp(distance(d, seaSun.xyz) / 6.0, 0.0, 1.0);
    vec4 glow = texture(seaGlowTex, vec2(clamp(u - seaSky.y * 2.0, 0.0, 1.0), 1.0 - vl));
    vec3 c = texture(seaSkyTex, vec2(u, pow(max(1.0 - v2.y, 0.0), 0.25))).rgb;
    c = c * (1.0 - glow.a) + glow.rgb * glow.a;
    float fogFrom = flatFogDensity < 0.0 ? flatFogStart : 0.0;
    float bias = flatFogDensity < 0.0 ? 0.55 * seaAboveSea : 0.0;
    float fog = max(seaSky.z + max(seaSky.w * 120.0 - 0.12, 0.0) + max(-flatFogDensity * fogFrom / 3.3, 0.0),
        1.0 - 1.0 / exp((skyPos.y - fogFrom - bias) * flatFogDensity));
    fog += max(0.0, ((1.0 - v2.y) / 2.0 + 0.3) * 0.7667 - 0.5) * max(min(1.0, 5.0 * fog), seaWeather.z);
    fog = clamp(fog, 0.0, 1.0);
    c = mix(c, fogColor, fog);
    float dark = 1.0 - clamp(seaSky.x, 0.0, 1.0);
    c = mix(c, fogColor, dark);
    if (seaSunset.w > 0.001) {
        float k = clamp(seaSunset.w * seaSunsetShape(d, seaSun.xyz) * (1.0 - 0.5 * fog), 0.0, 0.7);
        float near = pow(max(dot(d, seaSun.xyz), 0.0), 8.0);
        c = mix(c, seaSunset.rgb / seaSunset.w * (0.55 + 0.45 * near), k);
    }
    return c * mix(1.0, fog, dark);
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
