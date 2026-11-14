using System;

namespace Sea
{
    // (SE-0071 R5) the public types ship mods use to cut water from hulls and keep places dry, out of the shader file
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
        /// (0.6.0) The detailed outline's tops, or null: <see cref="WaterShader.DryTopStations"/> heights above the waterline
        /// (in the hull's frame) from stern to bow over the detailed outline's stations, interpolated between them. With
        /// them the cut reaches the rail at each station (a raised bow or stern castle), not one height all along; without
        /// them it reaches <see cref="DryHeight"/> everywhere. Used with the detailed outline only: the far hulls cut by the
        /// footprint's outline keep <see cref="DryHeight"/>.
        /// </summary>
        public float[] DryTops;

        /// <summary>Has usable tops (all finite) for its detailed outline.</summary>
        public bool HasDryTops
        {
            get
            {
                if (DryTops == null || DryTops.Length != WaterShader.DryTopStations) return false;
                foreach (float t in DryTops) if (!float.IsFinite(t)) return false;
                return true;
            }
        }

        /// <summary>The cut's top at u along the detailed outline (-1 stern .. 1 bow), as the shader interpolates it.</summary>
        public double DryTopAt(double u)
        {
            if (!HasDryTops) return DryHeight;
            double s = Math.Clamp((u + 1) * 7.5, 0, 15);
            int i = (int)Math.Floor(s), j = Math.Min(i + 1, 15);
            return DryTops[i] + (DryTops[j] - DryTops[i]) * (s - i);
        }

        /// <summary>What the shader gets for its tops (sea_DryT): the tops, or <see cref="DryHeight"/> at every station.</summary>
        /// <summary>(SE-0047) <see cref="DryTopsUniform"/> straight into the uniform's array, without a new one per hull and frame.</summary>
        public void CopyDryTops(float[] into, int at)
        {
            for (int i = 0; i < WaterShader.DryTopStations; i++) into[at + i] = HasDryTops ? DryTops[i] : DryHeight;
        }

        public float[] DryTopsUniform()
        {
            var t = new float[WaterShader.DryTopStations];
            for (int i = 0; i < t.Length; i++) t[i] = HasDryTops ? DryTops[i] : DryHeight;
            return t;
        }

        /// <summary>The highest the cut reaches (sea_ClipD.z): the highest top with the detailed outline, else <see cref="DryHeight"/>.</summary>
        public float DryCeiling(bool detailed)
        {
            if (!detailed || !HasDryProfile || !HasDryTops) return DryHeight;
            float m = float.MinValue;
            foreach (float t in DryTops) m = Math.Max(m, t);
            return m;
        }
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
            if (hl < -DryDepth || hl > DryCeiling(detailed)) return false;
            // (no outline left: nothing is dry; dividing by it below would give NaN, and an index from that throws)
            if (HalfLength <= 0 || HalfLength - DryInset <= 0) return false;
            if (detailed && HasDryProfile)
            {
                double len = HalfLength - DryInset;
                // (0.6.0) up to the rail at this station (the shader's sea_DryTop)
                if (HasDryTops && hl > DryTopAt(along / len)) return false;
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
