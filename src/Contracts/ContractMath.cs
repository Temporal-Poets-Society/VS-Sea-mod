using System;

namespace Sea
{
    /// <summary>(0.6.0) The pure parts of the open sea contracts (docs/open-sea-contracts.md): the pose and hull records.</summary>
    public static class SeaContractMath
    {
        // Hull records (section 8): indices into each double[]
        public const int HX = 0, HZ = 1, HWaterY = 2, HYaw = 3, HHalfLength = 4, HHalfBeam = 5, HCentreOffset = 6,
            HSlopeAlong = 7, HSlopeAcross = 8, HDry = 9, HDryDepth = 10, HDryHeight = 11, HFloodY = 12, HProfile = 13,
            HullRecordLength = 13, HullRecordWithProfile = HProfile + WaterShader.ProfileStations,
            // (7 November) the detail Sea's typed clip boxes have, so a hull registered by record is cut as finely: the
            // calm level beside her, the calm ring, the footprint's margin, the stern's rake and the dry outline by levels
            HCalmSlopeAcross = HullRecordWithProfile, HCalmZone = HCalmSlopeAcross + 1, HDryInset = HCalmZone + 1,
            HDryRake = HDryInset + 1, HDryLevel0 = HDryRake + 1, HDryLevelStep = HDryLevel0 + 1, HDryProfile = HDryLevelStep + 1,
            HullRecordDetailed = HDryProfile, HullRecordWithDryProfile = HDryProfile + WaterShader.DryStations * WaterShader.DryLevels;

        /// <summary>The pose on any surface: heights at the hull's ends and sides, turned into how she lies on them.</summary>
        public static (double Hold, double Lift, double Roll, double Pitch) PoseOn(Func<double, double, double> height, double hold,
            double x, double z, double yaw, double length, double beam)
        {
            // the shape's +X axis after the renderer's turn by yaw + 90° (Rough Seas' convention), and its +Z axis
            var (sin, cos) = Math.SinCos(yaw + Math.PI / 2);
            double At(double alongX, double alongZ) => height(x + cos * alongX + sin * alongZ, z - sin * alongX + cos * alongZ);
            double plusX = At(length / 2, 0), minusX = At(-length / 2, 0), plusZ = At(0, beam / 2), minusZ = At(0, -beam / 2);
            return (hold, (plusX + minusX + plusZ + minusZ) / 4, -Math.Atan((plusZ - minusZ) / beam), Math.Atan((plusX - minusX) / length));
        }

        /// <summary>A hull record as Sea's clip box; false when it is too short or not finite (the shader leaves those out too).</summary>
        public static bool ToClip(double[] r, out WaterClipBox c)
        {
            c = default;
            if (r == null || r.Length < HullRecordLength) return false;
            for (int i = 0; i < HullRecordLength; i++) if (i != HFloodY && !double.IsFinite(r[i])) return false;
            c.X = r[HX];
            c.Z = r[HZ];
            c.WaterY = r[HWaterY];
            c.Yaw = r[HYaw];
            c.HalfLength = (float)r[HHalfLength];
            c.HalfWidth = (float)r[HHalfBeam];
            c.CentreOffset = (float)r[HCentreOffset];
            c.SlopeAlong = (float)r[HSlopeAlong];
            c.SlopeAcross = (float)r[HSlopeAcross];
            c.CalmSlopeAcross = c.SlopeAcross;
            c.Dry = r[HDry] > 0.5;
            c.DryDepth = (float)r[HDryDepth];
            c.DryHeight = (float)r[HDryHeight];
            c.Flooded = double.IsFinite(r[HFloodY]);
            c.FloodY = c.Flooded ? r[HFloodY] : 0;
            // the footprint's 16 half-widths; NaN at the first: none (a record that only wants the detail below)
            if (r.Length >= HullRecordWithProfile && !double.IsNaN(r[HProfile]))
            {
                if (!Floats(r, HProfile, WaterShader.ProfileStations, out c.Profile)) return false;
            }
            if (r.Length >= HullRecordDetailed)
            {
                // NaN calm slope: the hull's own roll, as without the detail
                if (double.IsFinite(r[HCalmSlopeAcross])) c.CalmSlopeAcross = (float)r[HCalmSlopeAcross];
                else if (!double.IsNaN(r[HCalmSlopeAcross])) return false;
                for (int i = HCalmZone; i <= HDryLevelStep; i++) if (!double.IsFinite(r[i])) return false;
                c.CalmZone = (float)Math.Max(0, r[HCalmZone]);
                c.DryInset = (float)Math.Max(0, r[HDryInset]);
                c.DryRake = (float)r[HDryRake];
                c.DryLevel0 = (float)r[HDryLevel0];
                c.DryLevelStep = (float)r[HDryLevelStep];
                // the dry outline by levels; NaN at the first: none (the footprint less the inset)
                if (r.Length >= HullRecordWithDryProfile && !double.IsNaN(r[HDryProfile]))
                {
                    if (!(c.DryLevelStep > 0) || !Floats(r, HDryProfile, WaterShader.DryStations * WaterShader.DryLevels, out c.DryProfile)) return false;
                }
            }
            return c.HalfLength > 0 && c.HalfWidth > 0;
        }

        static bool Floats(double[] r, int from, int count, out float[] into)
        {
            into = null;
            var f = new float[count];
            for (int i = 0; i < count; i++)
            {
                double v = r[from + i];
                if (!double.IsFinite(v)) return false;
                f[i] = (float)v;
            }
            into = f;
            return true;
        }

        /// <summary>
        /// A clip box as a hull record with all its detail (the inverse of <see cref="ToClip"/>), for tests and for a mod
        /// that has Sea's types at hand.
        /// </summary>
        public static double[] ToRecord(in WaterClipBox c)
        {
            var r = new double[c.HasDryProfile ? HullRecordWithDryProfile : HullRecordDetailed];
            r[HX] = c.X; r[HZ] = c.Z; r[HWaterY] = c.WaterY; r[HYaw] = c.Yaw;
            r[HHalfLength] = c.HalfLength; r[HHalfBeam] = c.HalfWidth; r[HCentreOffset] = c.CentreOffset;
            r[HSlopeAlong] = c.SlopeAlong; r[HSlopeAcross] = c.SlopeAcross; r[HDry] = c.Dry ? 1 : 0;
            r[HDryDepth] = c.DryDepth; r[HDryHeight] = c.DryHeight; r[HFloodY] = c.Flooded ? c.FloodY : double.NaN;
            if (c.Profile != null && c.Profile.Length == WaterShader.ProfileStations)
                for (int i = 0; i < c.Profile.Length; i++) r[HProfile + i] = c.Profile[i];
            else r[HProfile] = double.NaN;
            r[HCalmSlopeAcross] = c.CalmSlopeAcross; r[HCalmZone] = c.CalmZone; r[HDryInset] = c.DryInset;
            r[HDryRake] = c.DryRake; r[HDryLevel0] = c.DryLevel0; r[HDryLevelStep] = c.DryLevelStep;
            if (c.HasDryProfile) for (int i = 0; i < c.DryProfile.Length; i++) r[HDryProfile + i] = c.DryProfile[i];
            return r;
        }

    }
}
