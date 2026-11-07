using System;

namespace Sea
{
    /// <summary>(0.6.0) The pure parts of the open sea contracts (docs/open-sea-contracts.md): the pose and hull records.</summary>
    public static class SeaContractMath
    {
        // Hull records (section 8): indices into each double[]
        public const int HX = 0, HZ = 1, HWaterY = 2, HYaw = 3, HHalfLength = 4, HHalfBeam = 5, HCentreOffset = 6,
            HSlopeAlong = 7, HSlopeAcross = 8, HDry = 9, HDryDepth = 10, HDryHeight = 11, HFloodY = 12, HProfile = 13,
            HullRecordLength = 13, HullRecordWithProfile = HProfile + WaterShader.ProfileStations;

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
            if (r.Length >= HullRecordWithProfile)
            {
                var p = new float[WaterShader.ProfileStations];
                for (int i = 0; i < p.Length; i++)
                {
                    double v = r[HProfile + i];
                    if (!double.IsFinite(v)) return false;
                    p[i] = (float)v;
                }
                c.Profile = p;
            }
            return c.HalfLength > 0 && c.HalfWidth > 0;
        }

    }
}
