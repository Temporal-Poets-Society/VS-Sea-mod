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

            // (7 November) the full detail: calm level and ring, inset, rake and the dry outline by levels, both ways
            var full = new WaterClipBox
            {
                X = 10, Z = -20, WaterY = 110.875, Yaw = 0.3, HalfLength = 2.2f, HalfWidth = 0.7f, CentreOffset = 0.1f,
                SlopeAlong = 0.02f, SlopeAcross = -0.04f, CalmSlopeAcross = -0.01f, CalmZone = 3, Dry = true, DryInset = 0.15f,
                DryDepth = 0.5f, DryHeight = 0.6f, DryRake = -0.3f, DryLevel0 = -0.5f, DryLevelStep = 0.4f, Flooded = true, FloodY = 110.5,
                Profile = new float[WaterShader.ProfileStations], DryProfile = new float[WaterShader.DryStations * WaterShader.DryLevels],
            };
            for (int i = 0; i < full.Profile.Length; i++) full.Profile[i] = 0.2f + 0.03f * i;
            for (int i = 0; i < full.DryProfile.Length; i++) full.DryProfile[i] = 0.1f + 0.004f * i;
            var fr = SeaContractMath.ToRecord(full);
            bool backOk = SeaContractMath.ToClip(fr, out var back);
            check(backOk && fr.Length == SeaContractMath.HullRecordWithDryProfile
                && back.X == full.X && back.Z == full.Z && back.WaterY == full.WaterY && back.Yaw == full.Yaw && back.HalfLength == full.HalfLength
                && back.HalfWidth == full.HalfWidth && back.CentreOffset == full.CentreOffset && back.SlopeAlong == full.SlopeAlong
                && back.SlopeAcross == full.SlopeAcross && back.CalmSlopeAcross == full.CalmSlopeAcross && back.CalmZone == full.CalmZone
                && back.Dry && back.DryInset == full.DryInset && back.DryDepth == full.DryDepth && back.DryHeight == full.DryHeight
                && back.DryRake == full.DryRake && back.DryLevel0 == full.DryLevel0 && back.DryLevelStep == full.DryLevelStep
                && back.Flooded && back.FloodY == full.FloodY && back.HasDryProfile
                && System.Linq.Enumerable.SequenceEqual(back.Profile, full.Profile) && System.Linq.Enumerable.SequenceEqual(back.DryProfile, full.DryProfile),
                "hulls: a detailed record carries every field of Sea's clip box (calm level and ring, inset, rake, dry outline by levels)");
            int cuts = 0, same = 0;
            for (int i = 0; i < 4000; i++)
            {
                double px = 10 + (i % 80 - 40) * 0.07, pz = -20 + (i / 80 - 25) * 0.07, py = 110.875 + ((i * 7) % 11 - 5) * 0.12;
                bool a = full.InDryHull(px, py, pz), b = back.InDryHull(px, py, pz);
                if (a) cuts++;
                if (a == b) same++;
            }
            check(cuts > 200 && same == 4000, $"hulls: the detailed record's dry hull cuts exactly as Sea's own box ({cuts} of 4000 points inside)");
            var noProfiles = (double[])fr.Clone();
            noProfiles[SeaContractMath.HProfile] = double.NaN; noProfiles[SeaContractMath.HDryProfile] = double.NaN; noProfiles[SeaContractMath.HCalmSlopeAcross] = double.NaN;
            check(SeaContractMath.ToClip(noProfiles, out var plain) && plain.Profile == null && !plain.HasDryProfile && plain.CalmSlopeAcross == plain.SlopeAcross && plain.DryInset == full.DryInset,
                "hulls: NaN marks the footprint, the dry outline or the calm level as not given");
            var badStep = (double[])fr.Clone();
            badStep[SeaContractMath.HDryLevelStep] = 0;
            check(!SeaContractMath.ToClip(badStep, out _), "hulls: a dry outline with no step between its levels is left out");

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
