using System;

namespace Sea
{
    /// <summary>A small boat's presentation above its ordinary, still-water physics position.</summary>
    public readonly struct BoatWavePose
    {
        public readonly double Heave, Pitch, Roll;
        public BoatWavePose(double heave, double pitch, double roll)
        { Heave = heave; Pitch = pitch; Roll = roll; }
    }

    public readonly struct BoatWaterplane
    {
        public readonly double HalfLength, HalfBeam, CentreAlong, CentreAcross;
        public BoatWaterplane(double halfLength, double halfBeam, double centreAlong = 0, double centreAcross = 0)
        { HalfLength = halfLength; HalfBeam = halfBeam; CentreAlong = centreAlong; CentreAcross = centreAcross; }
    }

    /// <summary>Fit a rigid waterplane to the actual displaced wave surface, rather than a second rocking clock.</summary>
    public static class BoatWaveMath
    {
        public static BoatWavePose Sample(Func<double, double, double> height, double x, double z, double yaw,
            BoatWaterplane hull)
        {
            double sn = Math.Sin(yaw), cs = Math.Cos(yaw);
            double length = Math.Max(0.25, hull.HalfLength * 0.7), beam = Math.Max(0.2, hull.HalfBeam * 0.7);
            double cx = x + sn * hull.CentreAlong + cs * hull.CentreAcross;
            double cz = z + cs * hull.CentreAlong - sn * hull.CentreAcross;
            double mean = 0, along = 0, across = 0;
            for (int a = -1; a <= 1; a++)
                for (int b = -1; b <= 1; b++)
                {
                    double h = height(cx + sn * a * length + cs * b * beam, cz + cs * a * length - sn * b * beam);
                    if (!double.IsFinite(h)) return default;
                    mean += h; along += a * h; across += b * h;
                }
            along /= 6 * length; across /= 6 * beam;
            // The native model's long axis is -X after yaw + pi/2. Its Z axis points across the hull.
            // xangle is roll and zangle is pitch in EntityShapeRenderer/EntityRideableSeat.
            double roll = Math.Clamp(-Math.Atan(across), -Math.PI / 6, Math.PI / 6);
            double pitch = Math.Clamp(-Math.Atan(along * Math.Cos(roll)), -Math.PI / 6, Math.PI / 6);
            double heave = mean / 9 - along * hull.CentreAlong - across * hull.CentreAcross;
            return new BoatWavePose(heave, pitch, roll);
        }

        /// <summary>Gameplay polar: a 40-degree no-go cone, useful close-hauled drive, best on a beam reach.</summary>
        public static double SailDrive(double windX, double windZ, double yaw)
        {
            if (!double.IsFinite(windX + windZ + yaw)) return 0;
            double strength = Math.Sqrt(windX * windX + windZ * windZ);
            if (strength < 1e-6) return 0;
            double downwind = Math.Clamp((windX * Math.Sin(yaw) + windZ * Math.Cos(yaw)) / strength, -1, 1);
            double angleFromSource = Math.Acos(-downwind) * 180 / Math.PI;
            if (angleFromSource <= 40) return 0;
            double reach = Math.Clamp((angleFromSource - 40) / 20, 0, 1);
            reach = Math.Sqrt(reach * reach * (3 - 2 * reach)) * (0.65 + 0.35 * Math.Clamp((angleFromSource - 60) / 30, 0, 1));
            double running = 1 - 0.25 * Math.Clamp((angleFromSource - 120) / 60, 0, 1);
            return Math.Min(2, strength / 0.3) * reach * running;
        }

        /// <summary>Brace the square yard about the mast: athwart on a run, forward on the windward side upwind.</summary>
        public static double SailTrim(double windX, double windZ, double yaw, double previous)
        {
            double strength = Math.Sqrt(windX * windX + windZ * windZ);
            if (!double.IsFinite(strength + yaw) || strength < 1e-6) return 0;
            double side = windX * Math.Cos(yaw) - windZ * Math.Sin(yaw);
            double along = windX * Math.Sin(yaw) + windZ * Math.Cos(yaw);
            double sign = Math.Abs(side) < strength * 0.015 ? (previous < 0 ? -1 : 1) : Math.Sign(side);
            return sign * Math.Min(65, Math.Acos(Math.Clamp(along / strength, -1, 1)) * 90 / Math.PI);
        }

        /// <summary>Fraction of reserve buoyancy lost to shipped water; no intake without overtopping.</summary>
        public static double FloodStep(double fill, double overtopping, double seconds)
            => Math.Clamp(fill + 0.04 * Math.Pow(Math.Clamp(overtopping, 0, 4), 1.5) * Math.Clamp(seconds, 0, 0.5), 0, 1);

        public static double FloodDensity(double fill) => 100 + 1050 * Math.Pow(Math.Clamp(fill, 0, 1), 4);
        public static double FloodDraft(double fill) => 0.8 * Math.Clamp(fill, 0, 1);
    }
}
