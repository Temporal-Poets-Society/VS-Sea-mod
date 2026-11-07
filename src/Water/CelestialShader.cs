using System;

namespace Sea
{
    public static class CelestialShader
    {
        public const string Marker = "sea_Celestial";
        // Wmap's far-terrain light term: up to 0.4.2 the sun colour, from 0.4.3 the game's ambient colour.
        static readonly string[] FarLight = { "max(vec3(0.05), sun)", "clamp(wmapAmbient, 0.0, 1.0)" };

        /// <summary>Sea's night floor and sky reflection text-patched into Wmap's far terrain shader (Wmap 0.4.x). Wmap 0.5
        /// declares sea_NightFloor, sea_SkyReflect and the wave uniforms itself and does that shading in all its far programs
        /// (its far-water contract); Sea then only sets the uniforms, and a shader that declares sea_NightFloor is left as it is.</summary>
        public static string FarFragment(string code)
        {
            if (code == null || code.Contains("sea_NightFloor") || code.Contains("seaNightFloor") || !code.Contains("void main()")) return null;
            string light = Array.Find(FarLight, l => code.Contains("albedo * " + l));
            if (light == null) return null;
            if (!code.Contains("haze = min(haze, clamp(dayLight"))
            {
                const string mix = "color.rgb = mix(color.rgb, sky.rgb, haze);";
                if (!code.Contains(mix)) return null;
                code = code.Replace(mix, "haze = mix(haze, min(haze, clamp(dayLight, 0.0, 1.0)), sea_NightEnabled);\n    " + mix);
            }
            code = code.Replace("albedo * " + light, "albedo * max(" + light + ", sea_NightFloor)");
            // (0.4.4) the distant sea reflects the sky it already looks up (Fresnel at its grazing angle), as Sea's near
            // water does, so the sea meets the sky at the horizon without a step where Wmap takes over
            const string skyMix = "color.rgb = mix(color.rgb, sky.rgb, haze);";
            if (code.Contains("bool water") && code.Contains("in vec3 wmapPos;") && code.Contains(skyMix))
                code = code.Replace(skyMix, skyMix + FarSkyReflection);
            return code.Replace("void main()", "uniform vec3 sea_NightFloor = vec3(0.0);\nuniform float sea_NightEnabled = 0.0;\nuniform float sea_SkyReflect = 0.0;\nvoid main()");
        }

        // (0.4.6) The dome as it reaches the screen: at night the sky colour function returns the fog colour with alpha
        // fogAmount, and the dome shows rgb * alpha over dark space. Reflecting the rgb alone drew a fog-brown horizon.
        public const string FarSkyReflection = @"
    if (water && sea_SkyReflect > 0.0) {
        float sea_Cos = clamp(-wmapPos.y / max(length(wmapPos), 0.001), 0.0, 1.0);
        color.rgb = mix(color.rgb, sky.rgb * clamp(sky.a, 0.0, 1.0), clamp(sea_SkyReflect * (0.02 + 0.98 * pow(1.0 - sea_Cos, 5.0)), 0.0, 1.0));
    }";
        // The wave gradient is evaluated at vertices, then interpolated. No 24-wave loop per water pixel.
        public static string Vertex(string code)
        {
            const string anchor = "fragNormal = unpackNormal(renderFlags);";
            if (code == null || code.Contains("out vec2 sea_SurfaceSlope;") || !code.Contains(anchor)) return null;
            code = code.Replace(anchor, anchor + @"
    sea_SurfaceSlope = vec2(0.0);
    sea_SurfaceRough = 0.0;
    sea_FoamCrest = vec3(0.0);
    if ((sea_Effects.x > 0.0 || sea_Effects.y > 0.0 || sea_Storm.z > 0.0) && (waterFlagsIn & 1) == 1 && (waterFlagsIn & LiquidIsLavaBitMask) == 0) {
        float div = ((waterFlagsIn & LiquidWeakWaveBitMask) > 0 ? 90.0 : 5.0)
            * max(0.2, 1.0 - float((waterFlagsIn >> 2) & 255) / 255.0);
        // (0.4.7) Blocks per pixel here, as Wmap's far sea measures it (length of fwidth of the position): the distance
        // times the pixel's angle, stretched along the view by how grazing it is. 0 (no fading) until Sea sets sea_Pixel.
        vec3 sea_FromEye = (xyz + origin) - sea_Eye;
        float sea_Dist = length(sea_FromEye);
        float sea_Graze = max(abs(sea_FromEye.y) / max(sea_Dist, 0.001), 0.01);
        float sea_Footprint = sea_Pixel * sea_Dist * sqrt(1.0 + 1.0 / (sea_Graze * sea_Graze));
        sea_Differential((xyz+origin).xz, sea_OpenWater(div)/div, sea_Footprint, sea_SurfaceSlope, sea_SurfaceRough, sea_FoamCrest);
        sea_SurfaceSlope *= sea_WaveCalm;
        sea_FoamCrest.y *= sea_WaveCalm;
    }");
            // (sea_Effects and sea_Eye as the fragment stage declares them: one uniform of the program)
            return code.Insert(code.IndexOf("void main(", StringComparison.Ordinal),
                "out vec2 sea_SurfaceSlope;\nout float sea_SurfaceRough;\nout vec3 sea_FoamCrest;\nuniform vec4 sea_Storm = vec4(0.0);\nuniform vec2 sea_Effects = vec2(0.0);\nuniform vec3 sea_Eye;\nuniform float sea_Pixel = 0.0;\n");
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
            code = code.Remove(s, e - s).Insert(s, "if (sea_Effects.x <= 0.0) {\n" + old + "}\n");
            code = code.Replace(finish, @"
    if (!isLava && fragNormal.y > 0.9 && skyExposed > 0 && cameraUnderwater < 0.01 && (sea_Effects.x > 0.0 || sea_Effects.y > 0.0)) {
        vec3 eyeDelta = fWorldPos - sea_Eye;
        vec3 eye = eyeDelta / max(length(eyeDelta), 0.001);
        vec3 n = normalize(vec3(-sea_SurfaceSlope.x, 1.0, -sea_SurfaceSlope.y));
        vec3 ray = reflect(eye, n);
        float fres = 0.02 + 0.98 * pow(1.0 - clamp(dot(-eye, n), 0.0, 1.0), 5.0);
        // (0.4.4) the sky the game draws, reflected by Fresnel: faint looking down, the sky's own colour at the horizon.
        // Smooth in direction, so waves shade it without speckles; it continues into Wmap's distant sea.
        if (sea_Effects.y > 0.0) texColor = sea_SkyReflect(texColor, ray, fres, rgbaFog.rgb);
        // (0.4.7-pre.7) Glints go on until the game's water has faded (0.76 to 0.8 of the view distance); Wmap 0.5.0-pre.8's
        // far sea, from 0.75, carries the same glitter on to the horizon (sea_Glint). Up to pre.6 they stopped from half the
        // view distance, leaving a calm band short of the horizon (issue 14).
        float sea_Edge = 1.0 - smoothstep(viewDistance * 0.76, viewDistance * 0.80, length(fWorldPos.xz));
        // (0.4.7) Only rays turned well down into the sea are cut; the lobe already needs the ray on the body, and a
        // setting sun's reflected ray is level or a little below (0.4.6 needed it 4.6 degrees up for full strength).
        float exposed = sea_Edge * smoothstep(-0.06, -0.02, ray.y);
        // The OIT pass premultiplies by alpha, so a glint must raise alpha to show (as vanilla's does). The sun and
        // moon are far brighter than the sky, so the glint saturates in a low-dynamic-range frame even where Fresnel
        // reflectance is small; Fresnel only shades it.
        if (sea_Effects.x > 0.0) texColor = sea_Glints(texColor, ray, exposed * (0.55 + 0.45 * fres), clamp(pow(shadowBright, 4.0), 0.0, 1.0));
    }
    // Sea owns breaking-wave foam; vanilla's depth/contact foam above is preserved. This sits outside the
    // reflection switch and FOAMEFFECT, as the shared waves do outside WAVINGSTUFF. Lit by the water's own light.
    if (!isLava && fragNormal.y > 0.9 && cameraUnderwater < 0.01 && sea_Storm.z > 0.0) {
        vec2 foamPos = sea_FoamCoordinates(fWorldPos.xz, sea_Storm.xy, sea_FoamOrigin.xy);
        float coverage = skyExposed > 0 ? sea_Storm.z : 0.0;
        float foam = coverage > 0.0 ? sea_WindFoam(foamPos, sea_FoamCrest, coverage) : 0.0;
        float edge = 1.0 - smoothstep(viewDistance * 0.76, viewDistance * 0.80, length(fWorldPos.xz));
        foam *= edge;
        texColor.rgb = mix(texColor.rgb, vec3(0.88, 0.94, 0.92) * rgba.rgb * shadowBright, foam);
        texColor.a = max(texColor.a, foam * 0.96);
    }
    " + finish);
            return code.Insert(code.IndexOf("void main(", StringComparison.Ordinal), Declarations);
        }

        // p.x is DOWNWIND, p.y ACROSS WIND. The same projection and morphology drive the diagnostic renders.
        public const string FoamDeclarations = @"
vec2 sea_FoamCoordinates(vec2 worldRelative, vec2 toward, vec2 anchor) {
    vec2 p = vec2(dot(worldRelative, toward), dot(worldRelative, vec2(-toward.y, toward.x))) + anchor;
    return mod(p + 32768.0,65536.0) - 32768.0;
}
float sea_FoamHash(vec2 p) {
    p = mod(p, 4096.0);
    return fract(sin(dot(p, vec2(12.71, 31.17))) * 43758.5453);
}
float sea_FoamNoise(vec2 p) {
    vec2 i = floor(p), f = fract(p);
    f = f * f * (3.0 - 2.0 * f);
    return mix(mix(sea_FoamHash(i), sea_FoamHash(i + vec2(1,0)), f.x),
        mix(sea_FoamHash(i + vec2(0,1)), sea_FoamHash(i + vec2(1,1)), f.x), f.y);
}
// Jittered cellular holes in a foam film, about 12.5 cm apart. Older films have larger holes (lace).
// Fade unresolved cells to their mean, rather than a moving pixel pattern or opaque white sheet.
float sea_FoamCells(vec2 p, float lace) {
    vec2 q = p * 8.0;
    float footprint = length(fwidth(q));
    float mean = mix(0.82, 0.48, lace);
    if (footprint > 1.2) return mean;
    q += 0.65 * vec2(sea_FoamNoise(p*2.0),sea_FoamNoise(p*2.0+vec2(31,67)));
    vec2 cell = floor(q), f = fract(q);
    float d = 2.0, randomRadius = 0.5;
    for (int y=-1; y<=1; y++) for (int x=-1; x<=1; x++) {
        vec2 o = vec2(x,y), id = cell + o;
        vec2 centre = vec2(sea_FoamHash(id), sea_FoamHash(id + vec2(73,119)));
        float distance = length(o + 0.1 + 0.8 * centre - f);
        if (distance < d) { d = distance; randomRadius = sea_FoamHash(id+vec2(151,43)); }
    }
    float radius = mix(0.10+0.22*randomRadius,0.30+0.18*randomRadius,lace);
    float aa = max(0.02, footprint * 0.4);
    float film = smoothstep(radius-aa, radius+aa, d);
    return mix(film, mean, smoothstep(0.3, 1.2, footprint));
}
float sea_CrestFoam(vec2 p, vec3 crest, float coverage) {
    // Two independent masks break a crest into short islands; neither becomes an all-white threshold at high wind.
    float islands = sea_FoamNoise(p * 0.5 + vec2(13,71));
    float holes = sea_FoamNoise(p * 1.0 + vec2(91,27));
    // More of the eligible breaking tops whiten as local wind rises; coverage never bypasses the crest gate.
    float density = sqrt(clamp(coverage,0.0,1.0));
    float patches = smoothstep(mix(0.72,0.40,density),mix(0.88,0.70,density),islands) * smoothstep(0.22,0.5,holes);
    // Compression measures actual crest sharpening, scaled by the local liquid height factor. Sheltered
    // water (factor .2) cannot reach .22 even at the global no-loop bound (.65*.2=.13).
    float breaking = smoothstep(0.22,0.40,crest.y) * smoothstep(0.8,1.65,crest.x);
    // Only a short shoulder behind a passing crest remains; this is a phase-age proxy, not saved foam.
    float tail = 1.0-smoothstep(0.35,1.8,max(crest.z,0.0));
    return breaking * tail * patches * min(1.0,24.0*coverage);
}
float sea_WindFoam(vec2 p, vec3 crest, float coverage) {
    if (coverage <= 0.0) return 0.0;
    float breaking = sea_CrestFoam(p,crest,coverage);
    if (breaking < 0.0001) return 0.0;
    // Actual holes, not pre.9's grain multiplier with an opaque 0.72 floor.
    // A receding crest's film opens into lace. The independent downwind and surface-spume layers are removed.
    float lace = smoothstep(-0.3,0.9,crest.z);
    return clamp(breaking * sea_FoamCells(p,lace) * (0.55+0.45*sea_FoamNoise(p*2.0)),0.0,1.0);
}
";

        public const string Declarations = @"
in vec2 sea_SurfaceSlope;
in float sea_SurfaceRough; // (0.4.7) the slope variance of trains too short for the pixel, faded out of sea_SurfaceSlope
in vec3 sea_FoamCrest; // normalised long height, local crest compression, normalised falling velocity
uniform vec4 sea_Storm = vec4(0.0); // wind TOWARD direction xz, bounded whitecap input, reserved
uniform vec3 sea_FoamOrigin; // camera along/across wind minus small drift (periodic, CPU doubles); z reserved
uniform vec3 sea_Eye;
uniform vec4 sea_Sun;
uniform vec4 sea_Moon;
uniform vec3 sea_SunColor;
uniform vec4 sea_Weather; // clear sky, night, horizon cloud (the game's horizonFog), roughness
uniform vec2 sea_Effects = vec2(0.0); // glints, sky reflection
uniform vec4 sea_Sky; // the sky dome's daylight, sunset shift, fog minimum, fog density
uniform float sea_AboveSea; // (0.4.7) the eye above sea level (the game's playerToSealevelOffset)
uniform vec4 sea_Sunset = vec4(0.0); // (0.4.7) AstraTerra's sunset glow (SunsetScatter.Shown): sun colour x strength, strength
uniform sampler2D sea_SkyTex;  // the game's sky colours (sky.png)
uniform sampler2D sea_GlowTex; // the game's sun glow (sunlight.png)
float sea_Celestial(float p, float width) { return exp(-max(0.0, 1.0-p) / width); }
" + FoamDeclarations + @"
// (0.4.7) AstraTerra's sunset glow for a direction v and the sun (its SunsetScatter.Shape, the same numbers): a halo round
// the sun and a band along the horizon, strongest beneath it.
float sea_SunsetShape(vec3 v, vec3 sun) {
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
float sea_Lobe(vec3 ray, vec3 body, float width) {
    return sea_Celestial(dot(ray, body), width) * smoothstep(-0.04, 0.0, body.y) * sea_Weather.x;
}
// Sun glitter and moon trail. glint: horizon/edge fade and Fresnel shading; shade: the sun's shadow-map brightness.
// (0.4.7) Far off, the trains too short for a pixel are faded out of the normal; their slope widens the lobe instead
// (a reflected ray turns twice the slope: 1 - cos = 2 x the variance) and spreads the same light over it, so the glitter
// path keeps its width toward the horizon instead of narrowing to a mirror's line.
vec4 sea_Glints(vec4 c, vec3 ray, float glint, float shade) {
    float width = 0.0012 + sea_Weather.w * 0.018;
    float spread = width + min(2.0 * sea_SurfaceRough, 0.02);
    glint *= width / spread;
    float sun = clamp(sea_Effects.x * glint * sea_Lobe(ray, sea_Sun.xyz, spread) * sea_Sun.w * shade, 0.0, 1.0);
    float moon = clamp(sea_Effects.x * glint * sea_Lobe(ray, sea_Moon.xyz, spread * 1.3) * sea_Moon.w, 0.0, 1.0);
    c.rgb = mix(c.rgb, sea_SunColor, sun);
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
vec3 sea_SkyColor(vec3 ray, vec3 fogColor) {
    vec3 d = normalize(vec3(ray.x, max(ray.y, 0.0), ray.z));
    vec3 skyPos = d * 250.0; // the length Wmap's far sea gives getSkyColorAt
    vec3 v2 = normalize(vec3(skyPos.x, skyPos.y + 0.25 * sea_AboveSea, skyPos.z));
    float u = (sea_Sun.y + 1.0) / 2.0;
    float vl = 1.0 - clamp(distance(d, sea_Sun.xyz) / 6.0, 0.0, 1.0);
    vec4 glow = texture(sea_GlowTex, vec2(clamp(u - sea_Sky.y * 2.0, 0.0, 1.0), 1.0 - vl));
    vec3 c = texture(sea_SkyTex, vec2(u, pow(max(1.0 - v2.y, 0.0), 0.25))).rgb;
    c = c * (1.0 - glow.a) + glow.rgb * glow.a;
    float fogFrom = flatFogDensity < 0.0 ? flatFogStart : 0.0;
    float bias = flatFogDensity < 0.0 ? 0.55 * sea_AboveSea : 0.0;
    float fog = max(sea_Sky.z + max(sea_Sky.w * 120.0 - 0.12, 0.0) + max(-flatFogDensity * fogFrom / 3.3, 0.0),
        1.0 - 1.0 / exp((skyPos.y - fogFrom - bias) * flatFogDensity));
    fog += max(0.0, ((1.0 - v2.y) / 2.0 + 0.3) * 0.7667 - 0.5) * max(min(1.0, 5.0 * fog), sea_Weather.z);
    fog = clamp(fog, 0.0, 1.0);
    c = mix(c, fogColor, fog);
    float dark = 1.0 - clamp(sea_Sky.x, 0.0, 1.0);
    c = mix(c, fogColor, dark);
    if (sea_Sunset.w > 0.001) {
        float k = clamp(sea_Sunset.w * sea_SunsetShape(d, sea_Sun.xyz) * (1.0 - 0.5 * fog), 0.0, 0.7);
        float near = pow(max(dot(d, sea_Sun.xyz), 0.0), 8.0);
        c = mix(c, sea_Sunset.rgb / sea_Sunset.w * (0.55 + 0.45 * near), k);
    }
    return c * mix(1.0, fog, dark);
}
// Water reflects the sky by Fresnel; reflected light covers what is seen through the water, so alpha rises with it.
vec4 sea_SkyReflect(vec4 c, vec3 ray, float fres, vec3 fogColor) {
    float r = clamp(sea_Effects.y * fres, 0.0, 1.0);
    c.rgb = mix(c.rgb, sea_SkyColor(ray, fogColor), r);
    c.a = max(c.a, r);
    return c;
}
";
    }
}
