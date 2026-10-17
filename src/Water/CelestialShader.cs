using System;

namespace Sea
{
    public static class CelestialShader
    {
        public const string Marker = "seaCelestial";
        public static string FarFragment(string code)
        {
            const string anchor = "albedo * max(vec3(0.05), sun)";
            if (code == null || !code.Contains(anchor) || code.Contains("seaNightFloor") || !code.Contains("void main()")) return null;
            if (!code.Contains("haze = min(haze, clamp(dayLight"))
            {
                const string mix = "color.rgb = mix(color.rgb, sky.rgb, haze);";
                if (!code.Contains(mix)) return null;
                code = code.Replace(mix, "haze = mix(haze, min(haze, clamp(dayLight, 0.0, 1.0)), seaNightEnabled);\n    " + mix);
            }
            return code.Replace(anchor, "albedo * max(max(vec3(0.05), sun), seaNightFloor)")
                .Replace("void main()", "uniform vec3 seaNightFloor = vec3(0.0);\nuniform float seaNightEnabled = 0.0;\nvoid main()");
        }
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
        float seaEdge = 1.0 - smoothstep(viewDistance * 0.50, viewDistance * 0.74, length(fWorldPos.xz));
        vec3 eyeDelta = fWorldPos - seaEye;
        vec3 eye = eyeDelta / max(length(eyeDelta), 0.001);
        vec3 n = normalize(vec3(-seaSurfaceSlope.x, 1.0, -seaSurfaceSlope.y));
        vec3 ray = reflect(eye, n);
        float fres = 0.02 + 0.98 * pow(1.0 - max(0.0, dot(-eye, n)), 5.0);
        float exposed = seaEdge * smoothstep(0.0, 0.08, ray.y);
        // The OIT pass premultiplies by alpha, so a glint must raise alpha to show (as vanilla's does). The sun and
        // moon are far brighter than the sky, so the glint saturates in a low-dynamic-range frame even where Fresnel
        // reflectance is small; Fresnel only shades it.
        if (seaEffects.x > 0.0) texColor = seaGlints(texColor, ray, exposed * (0.55 + 0.45 * fres), clamp(pow(shadowBright, 4.0), 0.0, 1.0));
        vec3 added = vec3(0.0);
        if (seaEffects.y > 0.0) {
            vec3 stars = vec3(0.0);
            if (seaStarMode.x > 0.5) {
                vec3 st = (transpose(seaStarMatrix) * vec4(ray, 0.0)).xyz;
                stars += max(vec3(0.0), textureLod(seaVanillaStars, st, 0.5 + seaWeather.w * 3.0).rgb - 0.001) * 2.0 * seaWeather.y;
            }
            if (seaStarMode.y > 0.5) {
                vec2 st = vec2(atan(ray.z, ray.x) / 6.28318530718 + 0.5, asin(clamp(ray.y, -1.0, 1.0)) / 3.14159265359 + 0.5);
                stars += vec3(textureLod(seaCustomStars, st, seaWeather.w * 3.0).r);
            }
            added += seaEffects.y * seaWeather.x * (stars * 0.045 + vec3(0.008, 0.013, 0.023) * seaWeather.y);
        }
        // Faint sky light keeps the water's transparency; dividing by alpha keeps it visible after the premultiply.
        texColor.rgb += added * exposed * (0.12 + 0.88 * fres) / max(texColor.a, 0.25);
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
uniform vec4 seaWeather; // clear sky, night, unused, roughness
uniform vec2 seaEffects = vec2(0.0);
uniform vec2 seaStarMode;
uniform samplerCube seaVanillaStars;
uniform sampler2D seaCustomStars;
uniform mat4 seaStarMatrix;
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
";
    }
}
