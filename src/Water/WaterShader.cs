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
        public const string Marker = "seaWater";

        // seaWave[i] = (kx, kz, phase at the camera, amplitude)
        // seaClipA = (origin x, origin z, cos yaw, sin yaw) relative to the camera; seaClipB = (half length, the height of
        // the water flooding a dry hull over the water y (the vertex stage doesn't use it), water y at the origin, centre
        // offset along the keel)
        // seaClipC = (waterline rise per block toward the bow, … toward starboard, calm zone, calm level's rise per block toward starboard)
        // seaClipD = (dry outline's inset from the footprint, dry depth below the waterline, dry height above it, 1 if dry)
        // seaClipP: 16 half-widths per hull from stern to bow (4 vec4 each)
        // Shared verbatim with the offline GPU geometry checks. HeightAt inverts this map on the CPU.
        public const string Geometry = @"
uniform int seaWaveCount = 0;
uniform vec4 seaWave[24];
uniform float seaWaveShape = 0.0;
float seaShapeWeight(float k) { return 0.15+0.85/(1.0+k*k/0.09); }
// (0.5.3) How much of the sea's swell reaches water with this wave divisor: none on inland water (oceanity 0: rivers, lakes,
// rapids), all from oceanity 0.25 (the coast's bays and estuaries in between). Vanilla's divisor is 5 * max(0.2, 1 - oceanity).
float seaOpenWater(float div) { return smoothstep(0.0, 0.25, 1.0 - div / 5.0); }
vec3 seaSurface(vec2 q, float factor) {
    vec3 p = vec3(q.x,0.0,q.y);
    for (int i=0; i<seaWaveCount; i++) {
        vec4 w=seaWave[i];
        float k=length(w.xy), phase=dot(w.xy,q)+w.z;
        p.y += w.w*sin(phase)*factor;
        if (k>0.0000001) p.xz += seaWaveShape*seaShapeWeight(k)*w.w*w.xy/k*cos(phase);
    }
    return p;
}
// Parametric derivatives: Eulerian height gradient = transpose(inverse(J)) * dY/dq.
// Compression is 1 - the smallest horizontal eigenvalue: zero/negative in broad troughs.
void seaDifferential(vec2 q, float factor, float footprint, out vec2 slope, out float rough,
    out vec3 crest) {
    mat2 jac=mat2(1.0); vec2 gradient=vec2(0.0);
    float height=0.0, variance=0.0, fall=0.0, fallVariance=0.0;
    rough=0.0;
    for(int i=0;i<seaWaveCount;i++) {
        vec4 w=seaWave[i]; float k=length(w.xy);
        if(k<0.0000001) continue;
        float phase=dot(w.xy,q)+w.z, sn=sin(phase), cs=cos(phase);
        float keep=1.0-smoothstep(0.25,0.75,k*footprint);
        vec2 direction=w.xy/k;
        jac -= seaWaveShape*seaShapeWeight(k)*w.w*k*sn*keep*outerProduct(direction,direction);
        gradient += keep*w.w*w.xy*cs*factor;
        rough += (1.0-keep*keep)*0.5*pow(w.w*k*factor,2.0);
        float longWave=1.0-smoothstep(0.3,0.6,k);
        height += longWave*keep*w.w*sn;
        variance += 0.5*pow(longWave*w.w,2.0);
        float omega=sqrt(9.81*k);
        fall += longWave*keep*w.w*omega*cs;
        fallVariance += 0.5*pow(longWave*w.w*omega,2.0);
    }
    slope=inverse(jac)*gradient;
    float least=0.5*(jac[0][0]+jac[1][1]-length(vec2(jac[0][0]-jac[1][1],2.0*jac[0][1])));
    crest=vec3(height/max(sqrt(variance),0.0001),max(0.0,1.0-least)*factor,fall/max(sqrt(fallVariance),0.0001));
}
";

        const string Declarations = Geometry + @"
uniform int seaClipCount = 0;
uniform vec4 seaClipA[8];
uniform vec4 seaClipB[8];
uniform vec4 seaClipC[8];
uniform vec4 seaClipD[8];
uniform vec4 seaClipP[32];
out vec3 seaFragPos;
float seaWaveCalm = 1.0; // also suppress wind foam where a non-dry hull calms the drawn surface

vec4 seaWaves(bool notLava, vec4 wp, float div) {
	float open = notLava && seaWaveCount > 0 ? seaOpenWater(div) : 0.0;
	if (open <= 0.0) return applyLiquidWarping(notLava, wp, div);
    vec3 p = seaSurface(wp.xz, 1.0/div);
    vec4 sw = wp;
    sw.xz = p.xz;
    sw.y += p.y;
	sw.xyz = applyPerceptionWarping(sw.xyz);
	// between inland and open water, vanilla's ripples fade into the swell (a function of the corner, so faces stay joined)
	return open >= 1.0 ? sw : mix(applyLiquidWarping(notLava, wp, div), sw, open);
}

" + HullOutline + @"
vec4 seaWater(vec4 wp, vec3 rest) {
	seaWaveCalm = 1.0;
	for (int h = 0; h < seaClipCount; h++) {
		if (seaClipD[h].w > 0.5) continue;
		vec4 a = seaClipA[h];
		vec4 b = seaClipB[h];
		vec4 c = seaClipC[h];
		if (b.x <= 0.0) continue;
		vec2 d = wp.xz - a.xy;
		float fwd = d.x * a.w + d.y * a.z;
		float side = d.y * a.w - d.x * a.z;
		float along = fwd - b.w;
		float dAlong = max(0.0, abs(along) - b.x);
		float dAcross = max(0.0, abs(side) - seaHalfWidth(h, along / b.x));
		float dist = length(vec2(dAlong, dAcross));
		float calm = max(c.z, 0.01);
		if (dist < calm && abs(rest.y - b.z) < 6.0) {
			float level = b.z + c.x * fwd + c.w * side;
			float damping = smoothstep(0.0, calm, dist);
			seaWaveCalm *= damping;
			wp.y = level + (wp.y - level) * damping;
			if (dist <= 0.0) wp.y = min(wp.y, b.z + c.x * fwd + c.y * side);
		}
	}
	return wp;
}

";

        // the footprint's half-width along the keel (u: -1 stern .. 1 bow), the same in both shader stages
        const string HullOutline = @"
float seaPick(int h, int i) {
	vec4 v = seaClipP[h * 4 + i / 4];
	int c = i - (i / 4) * 4;
	return c == 0 ? v.x : (c == 1 ? v.y : (c == 2 ? v.z : v.w));
}

float seaHalfWidth(int h, float u) {
	float s = clamp((u + 1.0) * 7.5, 0.0, 15.0);
	int i = int(floor(s));
	int j = min(i + 1, 15);
	return mix(seaPick(h, i), seaPick(h, j), s - float(i));
}
";

        public const string DryMarker = "seaDry";

        // fragment stage: is this water pixel (camera-relative, as drawn) inside a dry hull's outline?
        // seaClipB.y: a flooded hull's water, level in the world, this far over the water y (NoFlood far below: none);
        // under it the sea isn't cut away, it's in her too
        // seaClipD.w: 0 not dry, 1 dry with the footprint's outline, 2 + k dry with detailed outline k:
        // seaDryE[k] = (height of its lowest row above the waterline, height between rows, half length, how far its stern
        // end moves along the keel per block up: a raked transom),
        // seaDryP: 32 half-widths per row, 4 rows (32 vec4) per outline, stern to bow, lowest row first
        const string FragmentDeclarations = @"
uniform int seaClipCount = 0;
uniform vec4 seaClipA[8];
uniform vec4 seaClipB[8];
uniform vec4 seaClipC[8];
uniform vec4 seaClipD[8];
uniform vec4 seaClipP[32];
uniform vec4 seaDryE[4];
uniform vec4 seaDryP[128];
in vec3 seaFragPos;
" + HullOutline + @"
float seaDryPick(int k, int i) {
	vec4 v = seaDryP[k * 32 + i / 4];
	int c = i - (i / 4) * 4;
	return c == 0 ? v.x : (c == 1 ? v.y : (c == 2 ? v.z : v.w));
}

float seaDryHalfWidth(int k, float u, float hl) {
	vec4 e = seaDryE[k];
	float s = clamp((u + 1.0) * 15.5, 0.0, 31.0);
	float t = clamp((hl - e.x) / e.y, 0.0, 3.0);
	int i = int(floor(s));
	int j = int(floor(t));
	int i2 = min(i + 1, 31);
	int j2 = min(j + 1, 3);
	float fs = s - float(i);
	float lo = mix(seaDryPick(k, j * 32 + i), seaDryPick(k, j * 32 + i2), fs);
	float hi = mix(seaDryPick(k, j2 * 32 + i), seaDryPick(k, j2 * 32 + i2), fs);
	return mix(lo, hi, t - float(j));
}

bool seaDry(vec3 p) {
	for (int h = 0; h < seaClipCount; h++) {
		vec4 d = seaClipD[h];
		if (d.w < 0.5) continue;
		vec4 a = seaClipA[h];
		vec4 b = seaClipB[h];
		vec4 c = seaClipC[h];
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
			float len = seaDryE[k].z;
			if (len <= 0.0 || seaDryE[k].y <= 0.0) continue;
			if (along > len || along < seaDryE[k].w * hl - len || across > seaDryHalfWidth(k, along / len, hl)) continue;
			return true;
		}
		if (abs(along) > b.x - d.x || across > seaHalfWidth(h, along / b.x) - d.x) continue;
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
            code = code.Insert(c, "worldPos = " + Marker + "(worldPos, xyz + origin);\n\t" + (dry ? "seaFragPos = worldPos.xyz;\n\t" : ""));
            int w = code.IndexOf(WarpCall, m, StringComparison.Ordinal);
            if (w >= 0)
            {
                code = code.Remove(w, "applyLiquidWarping".Length).Insert(w, "seaWaves");
                waves = true;
            }
            return code.Insert(m, dry ? Declarations : Declarations.Replace("out vec3 seaFragPos;", ""));
        }

        /// <summary>
        /// The water fragment shader with Sea's dry-hull cut (a discard first thing in main), or null if it's already in
        /// or main wasn't found. Goes with <see cref="Inject"/> on the same program (that passes seaFragPos).
        /// </summary>
        public static string InjectFragment(string code)
        {
            if (code == null || code.Contains(DryMarker)) return null;
            int m = code.IndexOf(MainAnchor, StringComparison.Ordinal);
            if (m < 0) return null;
            int brace = code.IndexOf('{', m);
            if (brace < 0) return null;
            code = code.Insert(brace + 1, "\n\tif (" + DryMarker + "(seaFragPos)) discard;");
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

        /// <summary>What the shader gets for the flood (seaClipB.y): its height over <see cref="WaterY"/>, or far below when there's none.</summary>
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
