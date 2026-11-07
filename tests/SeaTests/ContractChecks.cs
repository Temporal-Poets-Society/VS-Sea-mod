using System;
using Sea;

namespace SeaTests
{
    /// <summary>(0.6.0) The open sea contracts' pure parts: the pose (Rough Seas' PoseOf meaning) and hull records.</summary>
    static class ContractChecks
    {
        public static void Run(Action<bool, string> check)
        {
            // a plane rising 0.1 per block east and 0.05 per block south, 0.7 above still water at (100, 200)
            Func<double, double, double> plane = (x, z) => 0.7 + 0.1 * (x - 100) + 0.05 * (z - 200);

            // yaw −90°: the renderer's turn of yaw + 90° is 0, so the shape's +X (her length) points east and +Z south
            var p = SeaContractMath.PoseOn(plane, 1, 100, 200, -Math.PI / 2, 8, 3);
            check(Math.Abs(p.Lift - 0.7) < 1e-12, "pose: lift is the surface's mean height under the hull");
            check(Math.Abs(p.Pitch - Math.Atan(0.1)) < 1e-12, "pose: pitch is the rise along the shape's X axis (bow up +, as Rough Seas' zangle)");
            check(Math.Abs(p.Roll + Math.Atan(0.05)) < 1e-12, "pose: roll is minus the rise along the shape's Z axis (as Rough Seas' xangle)");
            check(p.Hold == 1, "pose: hold passes through");

            // turned a quarter: her length now points along -Z (north) in Rough Seas' convention
            var q = SeaContractMath.PoseOn(plane, 0.5, 100, 200, 0, 8, 3);
            check(Math.Abs(q.Pitch - Math.Atan(-0.05)) < 1e-12 && Math.Abs(q.Roll + Math.Atan(0.1)) < 1e-12,
                "pose: a quarter turn swaps which slope she pitches and rolls with");
            var flat = SeaContractMath.PoseOn((x, z) => 0, 1, 5, 5, 1.3, 20, 6);
            check(flat.Lift == 0 && flat.Roll == 0 && flat.Pitch == 0, "pose: calm water gives no invented rocking");

            // hull records → Sea's clip boxes
            var r = new double[SeaContractMath.HullRecordLength];
            r[SeaContractMath.HX] = 10; r[SeaContractMath.HZ] = -20; r[SeaContractMath.HWaterY] = 110.875; r[SeaContractMath.HYaw] = 0.3;
            r[SeaContractMath.HHalfLength] = 2.2; r[SeaContractMath.HHalfBeam] = 0.7; r[SeaContractMath.HCentreOffset] = 0.1;
            r[SeaContractMath.HSlopeAlong] = 0.02; r[SeaContractMath.HSlopeAcross] = -0.04; r[SeaContractMath.HDry] = 1;
            r[SeaContractMath.HDryDepth] = 0.5; r[SeaContractMath.HDryHeight] = 0.6; r[SeaContractMath.HFloodY] = double.NaN;
            check(SeaContractMath.ToClip(r, out var c) && c.Dry && !c.Flooded && c.Profile == null && c.HalfWidth == 0.7f
                && c.WaterY == 110.875 && c.SlopeAcross == -0.04f && c.CalmSlopeAcross == c.SlopeAcross,
                "hulls: a simple record becomes a dry clip box without a flood or profile");
            check(c.InDryHull(10, 110.875, -20) && !c.InDryHull(10, 110.875 + 2, -20), "hulls: the record's dry hull cuts at her waterline, not far above it");

            r[SeaContractMath.HFloodY] = 110.6;
            check(SeaContractMath.ToClip(r, out c) && c.Flooded && c.FloodY == 110.6, "hulls: a finite flood height floods her");

            var withProfile = new double[SeaContractMath.HullRecordWithProfile];
            Array.Copy(r, withProfile, SeaContractMath.HullRecordLength);
            for (int i = 0; i < WaterShader.ProfileStations; i++) withProfile[SeaContractMath.HProfile + i] = 0.2 + 0.03 * i;
            check(SeaContractMath.ToClip(withProfile, out c) && c.Profile?.Length == WaterShader.ProfileStations && Math.Abs(c.Profile[15] - 0.65f) < 1e-6,
                "hulls: a record with 16 half-widths gives her outline");

            var bad = (double[])r.Clone();
            bad[SeaContractMath.HYaw] = double.NaN;
            check(!SeaContractMath.ToClip(bad, out _), "hulls: a record with a non-finite field is left out");
            bad = (double[])r.Clone();
            bad[SeaContractMath.HHalfLength] = 0;
            check(!SeaContractMath.ToClip(bad, out _) && !SeaContractMath.ToClip(new double[3], out _) && !SeaContractMath.ToClip(null, out _),
                "hulls: a record with no length, too short a record, or none, is left out");
        }
    }
}
