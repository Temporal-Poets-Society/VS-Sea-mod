using System;

namespace Sea
{
    /// <summary>
    /// Sea's additions to vanilla's water shaders (chunkliquid, chunkliquiddepth), added as the shaders load
    /// (Harmony postfix on ShaderRegistry.LoadShaderProgram). Only Sea edits these shaders, so ship mods ask Sea
    /// instead of patching them.
    ///
    ///  - Waves: vanilla's warp of animated water (random bumps) is replaced by <see cref="WaveField"/>'s travelling
    ///    waves, scaled down like vanilla's in sheltered water (lakes). Lava and weakly waving liquids keep vanilla's.
    ///  - Hulls: inside the waterline footprint of each nearby hull the water is held below the hull's waterline
    ///    (tilted with the hull when it rides the waves), and within a few blocks around it the waves settle to the
    ///    level the hull rides at, so no crest reaches into a ship between the 1-block water vertices.
    ///  - Dry hulls (<see cref="WaterClipBox.Dry"/>): no water at all is drawn inside the hull's own outline, cut per
    ///    pixel in the fragment shaders, so rooms below the waterline are dry and the waves run right up to the hull.
    ///    The cut leans with the hull's pitch and roll, and a hull can give its outline at several heights
    ///    (<see cref="WaterClipBox.DryProfile"/>), so it fits a hull that's much wider higher up (a transom stern), and
    ///    its stern end can be a raked plane (<see cref="WaterClipBox.DryRake"/>), so the cut ends exactly at a transom.
    ///    A dry hull with water in her (<see cref="WaterClipBox.Flooded"/>) isn't cut under that water's level.
    /// </summary>
    public static class WaterShader
    {
        public const int MaxClips = 8;
        public const int ProfileStations = 16;
        /// <summary>A detailed dry outline: half-widths at this many stations along the hull, at <see cref="DryLevels"/> heights.</summary>
        public const int DryStations = 32, DryLevels = 4;
        /// <summary>How many of the nearest dry hulls get their detailed outline (the others use the footprint's).</summary>
        public const int MaxDryShapes = 4;
        public const int MaxWaves = WavePlace.MaxWaves;
        /// <summary>Waves calm down over this many blocks outside a hull's footprint, unless the source says otherwise.</summary>
        public const float CalmZone = 4;
        public static readonly string[] Passes = { "chunkliquid", "chunkliquiddepth" };
        public const string Marker = "sea_Water";

        /// <summary>(0.6.0) The fragment stage's detailed dry-outline uniform: a program that has it takes the detailed cut.</summary>
        public const string DryOutlineUniform = "sea_DryE";

        /// <summary>
        /// (0.6.0) Every name Sea puts into a shader starts with <c>sea_</c>, so no other mod's code collides with it
        /// (Rough Seas declares its own <c>seaWaves</c>, <c>seaFoamCrest</c>; open contracts R8). Programs that declare
        /// Sea's uniforms themselves under the names before 0.6.0 (Wmap 0.5's far sea: <c>seaWave</c>,
        /// <c>seaNightFloor</c>…) still get them: <see cref="N"/> gives the name the program has.
        /// </summary>
        public static string Legacy(string name) => name.StartsWith("sea_", StringComparison.Ordinal) ? "sea" + name.Substring(4) : name;

        /// <summary>(0.6.0) Does the program declare this Sea uniform, under its name or its name before 0.6.0?</summary>
        public static bool Has(Vintagestory.API.Client.IShaderProgram p, string name) => p.HasUniform(name) || p.HasUniform(Legacy(name));

        /// <summary>(0.6.0) The name the program declares this Sea uniform under (its own name when neither is declared).</summary>
        public static string N(Vintagestory.API.Client.IShaderProgram p, string name) => p.HasUniform(name) || !p.HasUniform(Legacy(name)) ? name : Legacy(name);

        // sea_Wave[i] = (kx, kz, phase at the camera, amplitude)
        // sea_ClipA = (origin x, origin z, cos yaw, sin yaw) relative to the camera; sea_ClipB = (half length, the height of
        // the water flooding a dry hull over the water y (the vertex stage doesn't use it), water y at the origin, centre
        // offset along the keel)
        // sea_ClipC = (waterline rise per block toward the bow, … toward starboard, calm zone, calm level's rise per block toward starboard)
        // sea_ClipD = (dry outline's inset from the footprint, dry depth below the waterline, dry height above it, 1 if dry)
        // sea_ClipP: 16 half-widths per hull from stern to bow (4 vec4 each)
        // Shared verbatim with the offline GPU geometry checks. HeightAt inverts this map on the CPU.
        public const string Geometry = @"
uniform int sea_WaveCount = 0;
uniform vec4 sea_Wave[24];
uniform float sea_WaveShape = 0.0;
float sea_ShapeWeight(float k) { return 0.15+0.85/(1.0+k*k/0.09); }
// (0.5.3) How much of the sea's swell reaches water with this wave divisor: none on inland water (oceanity 0: rivers, lakes,
// rapids), all from oceanity 0.25 (the coast's bays and estuaries in between). Vanilla's divisor is 5 * max(0.2, 1 - oceanity).
float sea_OpenWater(float div) { return smoothstep(0.0, 0.25, 1.0 - div / 5.0); }
// (0.6.0) The coast (Sea's CoastMath, the same numbers as the CPU): per column depth, travel time from the shore line,
// and the direction away from it times its trust, bilinear between column centres. sea_CoastArea: column (0, 0)'s centre
// relative to the camera (xz), the map's size, 1 when there is a map. sea_WaveClock: per train its angular speed and its
// phase on the shore line now.
uniform vec2 sea_WaveClock[24];
uniform vec4 sea_CoastArea = vec4(0.0);
uniform sampler2D sea_CoastTex;
const float sea_MaxDepth = 32.0;
float sea_Surf = 0.0; // whitewater at the last sea_Differential (0.6.0)
vec4 sea_Coast(vec2 q) {
    vec4 open = vec4(sea_MaxDepth, 0.0, 0.0, 0.0);
    if (sea_CoastArea.w < 0.5) return open;
    vec2 f = q - sea_CoastArea.xy;
    float n = sea_CoastArea.z;
    if (f.x < 0.0 || f.y < 0.0 || f.x >= n - 1.0 || f.y >= n - 1.0) return open;
    vec2 i = floor(f), t = f - i;
    ivec2 c = ivec2(i);
    vec4 a = texelFetch(sea_CoastTex, c, 0), b = texelFetch(sea_CoastTex, c + ivec2(1, 0), 0);
    vec4 d = texelFetch(sea_CoastTex, c + ivec2(0, 1), 0), e = texelFetch(sea_CoastTex, c + ivec2(1, 1), 0);
    return (1.0 - t.y) * ((1.0 - t.x) * a + t.x * b) + t.y * ((1.0 - t.x) * d + t.x * e);
}
float sea_SurfShare(float k, vec4 c) {
    if (c.x >= sea_MaxDepth) return 0.0;
    return (1.0 - smoothstep(0.4, 1.6, k * c.x)) * (1.0 - smoothstep(0.6 * sea_MaxDepth, sea_MaxDepth, c.x)) * min(length(c.zw), 1.0);
}
float sea_Shoal(float omega, float h) { return clamp(sqrt(4.905 / max(omega, 0.000001) / sqrt(9.81 * max(h, 0.3))), 1.0, 2.5); }
// every train's amplitude cut (H_s <= 0.6 h) and how far the waves are breaking
vec2 sea_Breaking(vec4 c) {
    if (c.x >= sea_MaxDepth) return vec2(1.0, 0.0);
    float e = 0.0;
    for (int i=0; i<sea_WaveCount; i++) {
        vec4 w = sea_Wave[i];
        float s = sea_SurfShare(length(w.xy), c), ks = s > 0.0 ? sea_Shoal(sea_WaveClock[i].x, c.x) : 1.0;
        e += 0.5 * w.w * w.w * ((1.0 - s) * (1.0 - s) + s * s * ks * ks);
    }
    float ratio = 4.0 * sqrt(e) / max(0.6 * max(c.x, 0.0), 0.0001);
    return vec2(ratio > 1.0 ? 1.0 / ratio : 1.0, smoothstep(0.6, 1.0, ratio));
}
// a surf train's shape at phase theta when breaking b: (shape, its derivative, nearness to the crest)
vec3 sea_Breaker(float theta, float b) {
    float p = 1.0 + 2.0 * b, al = 0.6 * b;
    float psi = theta - al * sin(theta);
    float u = 0.5 + 0.5 * sin(psi);
    float mean = 0.5 + b * (-0.3517 + b * (0.2331 - 0.0985 * b));
    return vec3(2.0 * (pow(max(u, 0.0), p) - mean), p * pow(max(u, 0.000001), p - 1.0) * cos(psi) * (1.0 - al * cos(theta)), u);
}
vec3 sea_Surface(vec2 q, float factor) {
    vec3 p = vec3(q.x,0.0,q.y);
    vec4 c = sea_WaveCount > 0 ? sea_Coast(q) : vec4(sea_MaxDepth, 0.0, 0.0, 0.0);
    vec2 br = sea_Breaking(c);
    for (int i=0; i<sea_WaveCount; i++) {
        vec4 w=sea_Wave[i];
        float k=length(w.xy), phase=dot(w.xy,q)+w.z;
        float s=sea_SurfShare(k,c), a=w.w*br.x;
        p.y += a*(1.0-s)*sin(phase)*factor;
        if (s>0.0) { vec2 ck=sea_WaveClock[i]; p.y += a*s*sea_Shoal(ck.x,c.x)*sea_Breaker(ck.y-ck.x*c.y,br.y).x*factor; }
        if (k>0.0000001) p.xz += sea_WaveShape*sea_ShapeWeight(k)*a*(1.0-s)*w.xy/k*cos(phase);
    }
    return p;
}
// Parametric derivatives: Eulerian height gradient = transpose(inverse(J)) * dY/dq.
// Compression is 1 - the smallest horizontal eigenvalue: zero/negative in broad troughs.
void sea_Differential(vec2 q, float factor, float footprint, out vec2 slope, out float rough,
    out vec3 crest) {
    mat2 jac=mat2(1.0); vec2 gradient=vec2(0.0);
    float height=0.0, variance=0.0, fall=0.0, fallVariance=0.0;
    rough=0.0;
    vec4 c = sea_WaveCount > 0 ? sea_Coast(q) : vec4(sea_MaxDepth, 0.0, 0.0, 0.0);
    vec2 br = sea_Breaking(c);
    float away = length(c.zw);
    // a surf train's phase falls by omega per second of travel time: its wave vector is -omega grad T, |grad T| = 1/sqrt(g h)
    vec2 gradT = away > 0.0001 ? c.zw / away / sqrt(9.81 * max(c.x, 0.3)) : vec2(0.0);
    float surfSum = 0.0, surfCrest = 0.0;
    for(int i=0;i<sea_WaveCount;i++) {
        vec4 w=sea_Wave[i]; float k=length(w.xy);
        if(k<0.0000001) continue;
        float s=sea_SurfShare(k,c), a=w.w*br.x, ao=a*(1.0-s);
        float phase=dot(w.xy,q)+w.z, sn=sin(phase), cs=cos(phase);
        float keep=1.0-smoothstep(0.25,0.75,k*footprint);
        vec2 direction=w.xy/k;
        jac -= sea_WaveShape*sea_ShapeWeight(k)*ao*k*sn*keep*outerProduct(direction,direction);
        gradient += keep*ao*w.xy*cs*factor;
        rough += (1.0-keep*keep)*0.5*pow(ao*k*factor,2.0);
        float longWave=1.0-smoothstep(0.3,0.6,k);
        height += longWave*keep*ao*sn;
        variance += 0.5*pow(longWave*ao,2.0);
        float omega=sqrt(9.81*k);
        fall += longWave*keep*ao*omega*cs;
        fallVariance += 0.5*pow(longWave*ao*omega,2.0);
        if (s>0.0) {
            vec2 ck=sea_WaveClock[i];
            float as=a*s*sea_Shoal(ck.x,c.x);
            vec3 sh=sea_Breaker(ck.y-ck.x*c.y,br.y);
            float ks=ck.x*length(gradT), keepS=1.0-smoothstep(0.25,0.75,ks*footprint);
            gradient -= keepS*as*sh.y*ck.x*gradT*factor;
            rough += (1.0-keepS*keepS)*0.5*pow(as*ks*factor,2.0);
            surfSum += as; surfCrest += as*sh.z*sh.z*sh.z;
        }
    }
    sea_Surf = surfSum > 0.0001 ? br.y * (0.2 + 0.8 * smoothstep(0.35, 0.85, surfCrest / surfSum)) : 0.0;
    slope=inverse(jac)*gradient;
    float least=0.5*(jac[0][0]+jac[1][1]-length(vec2(jac[0][0]-jac[1][1],2.0*jac[0][1])));
    crest=vec3(height/max(sqrt(variance),0.0001),max(0.0,1.0-least)*factor,fall/max(sqrt(fallVariance),0.0001));
}
";

        const string Declarations = Geometry + @"
uniform int sea_ClipCount = 0;
uniform vec4 sea_ClipA[8];
uniform vec4 sea_ClipB[8];
uniform vec4 sea_ClipC[8];
uniform vec4 sea_ClipD[8];
uniform vec4 sea_ClipP[32];
out vec3 sea_FragPos;
float sea_WaveCalm = 1.0; // also suppress wind foam where a non-dry hull calms the drawn surface

vec4 sea_Waves(bool notLava, vec4 wp, float div) {
	float open = notLava && sea_WaveCount > 0 ? sea_OpenWater(div) : 0.0;
	if (open <= 0.0) return applyLiquidWarping(notLava, wp, div);
    vec3 p = sea_Surface(wp.xz, 1.0/div);
    vec4 sw = wp;
    sw.xz = p.xz;
    sw.y += p.y;
	sw.xyz = applyPerceptionWarping(sw.xyz);
	// between inland and open water, vanilla's ripples fade into the swell (a function of the corner, so faces stay joined)
	return open >= 1.0 ? sw : mix(applyLiquidWarping(notLava, wp, div), sw, open);
}

" + HullOutline + @"
vec4 sea_Water(vec4 wp, vec3 rest) {
	sea_WaveCalm = 1.0;
	for (int h = 0; h < sea_ClipCount; h++) {
		if (sea_ClipD[h].w > 0.5) continue;
		vec4 a = sea_ClipA[h];
		vec4 b = sea_ClipB[h];
		vec4 c = sea_ClipC[h];
		if (b.x <= 0.0) continue;
		vec2 d = wp.xz - a.xy;
		float fwd = d.x * a.w + d.y * a.z;
		float side = d.y * a.w - d.x * a.z;
		float along = fwd - b.w;
		float dAlong = max(0.0, abs(along) - b.x);
		float dAcross = max(0.0, abs(side) - sea_HalfWidth(h, along / b.x));
		float dist = length(vec2(dAlong, dAcross));
		float calm = max(c.z, 0.01);
		if (dist < calm && abs(rest.y - b.z) < 6.0) {
			float level = b.z + c.x * fwd + c.w * side;
			float damping = smoothstep(0.0, calm, dist);
			sea_WaveCalm *= damping;
			wp.y = level + (wp.y - level) * damping;
			if (dist <= 0.0) wp.y = min(wp.y, b.z + c.x * fwd + c.y * side);
		}
	}
	return wp;
}

";

        // the footprint's half-width along the keel (u: -1 stern .. 1 bow), the same in both shader stages
        const string HullOutline = @"
float sea_Pick(int h, int i) {
	vec4 v = sea_ClipP[h * 4 + i / 4];
	int c = i - (i / 4) * 4;
	return c == 0 ? v.x : (c == 1 ? v.y : (c == 2 ? v.z : v.w));
}

float sea_HalfWidth(int h, float u) {
	float s = clamp((u + 1.0) * 7.5, 0.0, 15.0);
	int i = int(floor(s));
	int j = min(i + 1, 15);
	return mix(sea_Pick(h, i), sea_Pick(h, j), s - float(i));
}
";

        public const string DryMarker = "sea_Dry";

        // fragment stage: is this water pixel (camera-relative, as drawn) inside a dry hull's outline?
        // sea_ClipB.y: a flooded hull's water, level in the world, this far over the water y (NoFlood far below: none);
        // under it the sea isn't cut away, it's in her too
        // sea_ClipD.w: 0 not dry, 1 dry with the footprint's outline, 2 + k dry with detailed outline k:
        // sea_DryE[k] = (height of its lowest row above the waterline, height between rows, half length, how far its stern
        // end moves along the keel per block up: a raked transom),
        // sea_DryP: 32 half-widths per row, 4 rows (32 vec4) per outline, stern to bow, lowest row first
        const string FragmentDeclarations = @"
uniform int sea_ClipCount = 0;
uniform vec4 sea_ClipA[8];
uniform vec4 sea_ClipB[8];
uniform vec4 sea_ClipC[8];
uniform vec4 sea_ClipD[8];
uniform vec4 sea_ClipP[32];
uniform vec4 sea_DryE[4];
uniform vec4 sea_DryP[128];
in vec3 sea_FragPos;
" + HullOutline + @"
float sea_DryPick(int k, int i) {
	vec4 v = sea_DryP[k * 32 + i / 4];
	int c = i - (i / 4) * 4;
	return c == 0 ? v.x : (c == 1 ? v.y : (c == 2 ? v.z : v.w));
}

float sea_DryHalfWidth(int k, float u, float hl) {
	vec4 e = sea_DryE[k];
	float s = clamp((u + 1.0) * 15.5, 0.0, 31.0);
	float t = clamp((hl - e.x) / e.y, 0.0, 3.0);
	int i = int(floor(s));
	int j = int(floor(t));
	int i2 = min(i + 1, 31);
	int j2 = min(j + 1, 3);
	float fs = s - float(i);
	float lo = mix(sea_DryPick(k, j * 32 + i), sea_DryPick(k, j * 32 + i2), fs);
	float hi = mix(sea_DryPick(k, j2 * 32 + i), sea_DryPick(k, j2 * 32 + i2), fs);
	return mix(lo, hi, t - float(j));
}

bool sea_Dry(vec3 p) {
	for (int h = 0; h < sea_ClipCount; h++) {
		vec4 d = sea_ClipD[h];
		if (d.w < 0.5) continue;
		vec4 a = sea_ClipA[h];
		vec4 b = sea_ClipB[h];
		vec4 c = sea_ClipC[h];
		vec2 q = p.xz - a.xy;
		float fwd = q.x * a.w + q.y * a.z;
		float side = q.y * a.w - q.x * a.z;
		float dy = p.y - b.z;
		// under the water flooding her the sea shows (level in the world, like the water drawn in her hold)
		if (dy < b.y) continue;
		// in the hull's own frame: undo her tilt exactly (roll about the keel, then pitch, both read from the slopes of
		// her waterline), measured from her waterline above the origin
		float cp = inversesqrt(1.0 + c.x * c.x), sp = c.x * cp;
		float tr = -c.y * cp, cr = inversesqrt(1.0 + tr * tr), sr = tr * cr;
		float up = dy * cp - fwd * sp;
		float hl = up * cr + side * sr;
		float along = fwd * cp + dy * sp - b.w;
		float across = abs(side * cr - up * sr);
		if (hl < -d.y || hl > d.z) continue;
		// (no outline left: nothing is dry, as InDryHull has it; dividing by it would give NaN indices)
		if (b.x <= 0.0 || b.x - d.x <= 0.0) continue;
		if (d.w > 1.5) {
			int k = int(d.w - 1.5);
			float len = sea_DryE[k].z;
			if (len <= 0.0 || sea_DryE[k].y <= 0.0) continue;
			if (along > len || along < sea_DryE[k].w * hl - len || across > sea_DryHalfWidth(k, along / len, hl)) continue;
			return true;
		}
		if (abs(along) > b.x - d.x || across > sea_HalfWidth(h, along / b.x) - d.x) continue;
		return true;
	}
	return false;
}

";

        const string MainAnchor = "void main(";
        const string CallAnchor = "vec4 cameraPos = modelViewMatrix * worldPos;";
        const string WarpCall = "applyLiquidWarping((waterFlagsIn & LiquidIsLavaBitMask) == 0, worldPos, div)";

        /// <summary>Returns the shader code with Sea's water code added, or null if it's already there or the anchors weren't found.</summary>
        public static string Inject(string code) => Inject(code, out _);

        /// <param name="waves">whether vanilla's wave warp was found and replaced (the hull clip works either way)</param>
        /// <param name="dry">also pass each vertex's position on for the fragment stage's dry-hull cut
        /// (<see cref="InjectFragment"/>); off when that failed to compile on this machine</param>
        public static string Inject(string code, out bool waves, bool dry = true)
        {
            waves = false;
            if (code == null || code.Contains(Marker)) return null;
            int m = code.IndexOf(MainAnchor, StringComparison.Ordinal);
            int c = code.IndexOf(CallAnchor, StringComparison.Ordinal);
            if (m < 0 || c < m) return null;
            code = code.Insert(c, "worldPos = " + Marker + "(worldPos, xyz + origin);\n\t" + (dry ? "sea_FragPos = worldPos.xyz;\n\t" : ""));
            int w = code.IndexOf(WarpCall, m, StringComparison.Ordinal);
            if (w >= 0)
            {
                code = code.Remove(w, "applyLiquidWarping".Length).Insert(w, "sea_Waves");
                waves = true;
            }
            return code.Insert(m, dry ? Declarations : Declarations.Replace("out vec3 sea_FragPos;", ""));
        }

        /// <summary>
        /// The water fragment shader with Sea's dry-hull cut (a discard first thing in main), or null if it's already in
        /// or main wasn't found. Goes with <see cref="Inject"/> on the same program (that passes sea_FragPos).
        /// </summary>
        public static string InjectFragment(string code)
        {
            if (code == null || code.Contains(DryMarker)) return null;
            int m = code.IndexOf(MainAnchor, StringComparison.Ordinal);
            if (m < 0) return null;
            int brace = code.IndexOf('{', m);
            if (brace < 0) return null;
            code = code.Insert(brace + 1, "\n\tif (" + DryMarker + "(sea_FragPos)) discard;");
            return code.Insert(m, FragmentDeclarations);
        }
    }

    /// <summary>
    /// A hull footprint where water must stay below the waterline, in world coordinates. The keel runs along
    /// (sin Yaw, cos Yaw) (an entity's yaw); the footprint is centred CentreOffset blocks along the keel from (X, Z).
    /// A hull riding the waves tilts its waterline (SlopeAlong, SlopeAcross), and the water next to it settles to
    /// the level it rides at instead of the still-water level.
    /// </summary>
    public struct WaterClipBox
    {
        public double X, Z;
        /// <summary>Highest the water may be drawn inside the footprint at (X, Z) (world y).</summary>
        public double WaterY;
        public double Yaw;
        public float HalfLength, CentreOffset;
        /// <summary>Half-widths at 16 evenly spaced stations from stern (-HalfLength) to bow (+HalfLength); null = HalfWidth everywhere.</summary>
        public float[] Profile;
        public float HalfWidth;
        /// <summary>How much the waterline rises per block toward the bow and toward starboard (the hull's pitch and roll).</summary>
        public float SlopeAlong, SlopeAcross;
        /// <summary>
        /// The rise per block toward starboard of the level the waves next to the hull settle to: the roll the waves
        /// give the hull, without its lean in the wind (the sea around a heeling ship stays level).
        /// </summary>
        public float CalmSlopeAcross;
        /// <summary>Blocks around the footprint over which the waves die down to that level (0 = <see cref="WaterShader.CalmZone"/>).</summary>
        public float CalmZone;
        /// <summary>
        /// A dry hull: no water is drawn inside the hull's own outline at all (cut per pixel), from
        /// <see cref="DryDepth"/> below <see cref="WaterY"/> to <see cref="DryHeight"/> above it, so rooms below the
        /// waterline stay dry. The waves run right up to the hull: nothing is held down or calmed around it. The outline
        /// is the footprint less <see cref="DryInset"/> (the margin the footprint has around the hull), or the detailed
        /// <see cref="DryProfile"/>. The outline and the heights are in the hull's own frame: they lean with its pitch
        /// and roll (<see cref="SlopeAlong"/>, <see cref="SlopeAcross"/>), an exact rotation (<see cref="HullFrame"/>)
        /// about the waterline's point above (X, Z). A hull whose origin is on its waterline gets its own frame exactly;
        /// one whose origin is h below it is off by about h·tan(tilt) along and across.
        /// </summary>
        public bool Dry;
        public float DryInset, DryDepth, DryHeight;
        /// <summary>
        /// The dry outline in detail, or null: <see cref="WaterShader.DryLevels"/> rows of
        /// <see cref="WaterShader.DryStations"/> half-widths, the lowest row first. Row j is the outline at
        /// <see cref="DryLevel0"/> + j * <see cref="DryLevelStep"/> above the waterline (in the hull's frame; above the top
        /// row and below the bottom one the nearest row holds), its stations evenly spaced from the stern
        /// (-(HalfLength - DryInset) along the keel from the centre) to the bow. Between them the shader interpolates
        /// linearly, so give values that cover the hull there too.
        /// </summary>
        public float[] DryProfile;
        public float DryLevel0, DryLevelStep;
        /// <summary>
        /// The detailed outline's stern end is a plane across the hull, raked: at the waterline it's the stern station
        /// (-(HalfLength - DryInset) along the keel), and it moves this far along the keel per block up in the hull's frame
        /// (negative: aft, a transom leaning out over the water). Aft of its first station the outline holds the first
        /// station's widths, so give those as the transom's: the plane then cuts the water off square at the transom,
        /// where interpolating from the last station would leave a wedge of water cut out behind it. 0 = upright.
        /// </summary>
        public float DryRake;
        /// <summary>
        /// A dry hull that has water in her (a leak, a hole): the sea isn't cut away under <see cref="FloodY"/> (world y,
        /// a level plane, like the water the ship mod draws in her hold), so where the waves inside her outline dip below it
        /// they show, and nothing of the sea is hidden under water that's in her anyway.
        /// </summary>
        public bool Flooded;
        public double FloodY;

        /// <summary>What the shader gets for the flood (sea_ClipB.y): its height over <see cref="WaterY"/>, or far below when there's none.</summary>
        public float FloodUniform => Flooded ? (float)(FloodY - WaterY) : NoFlood;

        /// <summary>The flood's height when there's none (blocks under the waterline).</summary>
        public const float NoFlood = -10000f;

        /// <summary>The footprint's half-width at u along it (-1 stern .. 1 bow), as the shader interpolates it.</summary>
        public double HalfWidthAt(double u)
        {
            if (Profile == null || Profile.Length != WaterShader.ProfileStations) return HalfWidth;
            double s = Math.Clamp((u + 1) * 7.5, 0, 15);
            int i = (int)Math.Floor(s), j = Math.Min(i + 1, 15);
            return Profile[i] + (Profile[j] - Profile[i]) * (s - i);
        }

        /// <summary>Has a usable detailed dry outline.</summary>
        public bool HasDryProfile => DryProfile != null && DryProfile.Length == WaterShader.DryStations * WaterShader.DryLevels && DryLevelStep > 0;

        /// <summary>The detailed dry outline's half-width at u along it (-1 stern .. 1 bow) and h above the waterline, as the shader interpolates it.</summary>
        public double DryHalfWidthAt(double u, double h)
        {
            const int S = WaterShader.DryStations, L = WaterShader.DryLevels;
            double s = Math.Clamp((u + 1) * (S - 1) / 2.0, 0, S - 1), t = Math.Clamp((h - DryLevel0) / DryLevelStep, 0, L - 1);
            int i = (int)Math.Floor(s), j = (int)Math.Floor(t), i2 = Math.Min(i + 1, S - 1), j2 = Math.Min(j + 1, L - 1);
            double fs = s - i, lo = DryProfile[j * S + i] + (DryProfile[j * S + i2] - DryProfile[j * S + i]) * fs;
            double hi = DryProfile[j2 * S + i] + (DryProfile[j2 * S + i2] - DryProfile[j2 * S + i]) * fs;
            return lo + (hi - lo) * (t - j);
        }

        /// <summary>
        /// A point given level from the hull's waterline above its origin (fwd along her heading, dy up, side toward
        /// starboard) in her own tilted frame: the exact inverse of her tilt, the same as the shader's. Her waterline's
        /// slopes fix the tilt: pitch = atan(slopeAlong), and tan(roll) = -slopeAcross·cos(pitch), the roll about her
        /// keel applied before the pitch (so her keel stays in the vertical plane of her heading).
        /// </summary>
        public static void HullFrame(double slopeAlong, double slopeAcross, double fwd, double dy, double side, out double along, out double up, out double across)
        {
            double cp = 1 / Math.Sqrt(1 + slopeAlong * slopeAlong), sp = slopeAlong * cp;
            double tr = -slopeAcross * cp, cr = 1 / Math.Sqrt(1 + tr * tr), sr = tr * cr;
            double pitchedUp = dy * cp - fwd * sp;
            along = fwd * cp + dy * sp;
            up = pitchedUp * cr + side * sr;
            across = side * cr - pitchedUp * sr;
        }

        /// <summary>Is a world point inside the dry outline? The same test the water shader makes for every pixel.</summary>
        public bool InDryHull(double x, double y, double z) => InDryHull(x, y, z, HasDryProfile);

        /// <param name="detailed">use the detailed outline (the shader does for the nearest few dry hulls that have one)</param>
        public bool InDryHull(double x, double y, double z, bool detailed)
        {
            if (!Dry) return false;
            double sn = Math.Sin(Yaw), cs = Math.Cos(Yaw), dx = x - X, dz = z - Z, dy = y - WaterY;
            // under the water flooding her the sea isn't cut away (compared as the shader does, in single precision)
            if ((float)dy < FloodUniform) return false;
            double fwd = dx * sn + dz * cs, side = dz * sn - dx * cs;
            HullFrame(SlopeAlong, SlopeAcross, fwd, dy, side, out double lx, out double hl, out double lz);
            double along = lx - CentreOffset, across = Math.Abs(lz);
            if (hl < -DryDepth || hl > DryHeight) return false;
            // (no outline left: nothing is dry; dividing by it below would give NaN, and an index from that throws)
            if (HalfLength <= 0 || HalfLength - DryInset <= 0) return false;
            if (detailed && HasDryProfile)
            {
                double len = HalfLength - DryInset;
                return along <= len && along >= DryRake * hl - len && across <= DryHalfWidthAt(along / len, hl);
            }
            return Math.Abs(along) <= HalfLength - DryInset && across <= HalfWidthAt(along / HalfLength) - DryInset;
        }
    }

    /// <summary>
    /// Something with dry places where the world has water (a ship mod's hulls). Sea asks before it treats a spot as
    /// under water: the camera's underwater fog, tint and muffled sound (client), and whether a creature there can
    /// breathe (server). Only asked about the world itself (dimension 0, where ships sail; 0.3.7): a creature or camera
    /// in another dimension is never in a dry place. Register it on both sides (<see cref="SeaSystem.AddDrySpaces"/>). It's asked every tick for
    /// the camera and for each creature in water, so answer quickly; it may be asked from the sound thread.
    /// </summary>
    public interface IDrySpaces
    {
        bool IsDry(double x, double y, double z);

        /// <summary>
        /// (0.3.5) Will this world place be dry at any moment in the next <paramref name="seconds"/>, for something that
        /// stays put in the world there (a particle in the water): a hull sailing, rolling and heaving moves through the
        /// world's water, so a speck spawned just outside her is inside her hold a moment later. Asked from the client's
        /// particle thread for each spawn in or on water near the player; answer quickly. Unless the source says
        /// otherwise, it's <see cref="IsDry"/> now.
        /// </summary>
        bool IsDryWithin(double x, double y, double z, double seconds) => IsDry(x, y, z);
    }

    /// <summary>
    /// Something that has hulls in the water (a ship mod). Sea asks every frame, just before drawing water. (0.4.2) A
    /// hull with no length, a dry one inset by all of it, or one with numbers that aren't finite is left out.
    /// </summary>
    public interface IWaterClipSource
    {
        void AddWaterClips(System.Collections.Generic.List<WaterClipBox> clips);
    }
}
