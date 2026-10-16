using System;

namespace Sea
{
    /// <summary>
    /// Wind at a place: the air moves toward (X, Z) in world axes (+X east, +Z south). The length is the strength in
    /// vanilla's wind units: 0 still, 0.15 light breeze, 0.3 medium, 0.6 strong breeze, 1 storm (1.5 at most).
    /// </summary>
    public readonly struct Wind
    {
        public readonly double X, Z;

        public Wind(double x, double z) { X = x; Z = z; }

        public double Strength => Math.Sqrt(X * X + Z * Z);

        /// <summary>Compass bearing the wind blows from: 0 north, 90 east, 180 south, 270 west.</summary>
        public double FromDegrees => Compass.Bearing(-X, -Z);

        /// <summary>Compass bearing the wind blows toward.</summary>
        public double ToDegrees => Compass.Bearing(X, Z);

        public static Wind FromCompass(double fromDegrees, double strength)
        {
            double to = (fromDegrees + 180) * Math.PI / 180;
            return new Wind(Math.Sin(to) * strength, -Math.Cos(to) * strength);
        }

        public Wind WithStrength(double s)
        {
            double l = Strength;
            return l < 1e-9 ? new Wind(s, 0) : new Wind(X / l * s, Z / l * s);
        }

        public Wind Rotated(double radians)
        {
            // positive = clockwise seen from above (compass direction increases)
            double c = Math.Cos(radians), s = Math.Sin(radians);
            return new Wind(X * c - Z * s, X * s + Z * c);
        }

        public static Wind operator +(Wind a, Wind b) => new Wind(a.X + b.X, a.Z + b.Z);
        public static Wind operator *(Wind a, double k) => new Wind(a.X * k, a.Z * k);

        public override string ToString() => $"{Strength:0.00} from {Compass.Name(FromDegrees)} ({FromDegrees:0}°)";
    }

    /// <summary>Wave conditions at a place, following the wind of the past hours.</summary>
    public readonly struct SeaState
    {
        /// <summary>Significant wave height (crest to trough of the bigger waves), in blocks.</summary>
        public readonly double Height;
        /// <summary>Unit direction the waves travel toward (+X east, +Z south).</summary>
        public readonly double DirX, DirZ;
        /// <summary>Wave period in seconds.</summary>
        public readonly double Period;

        public SeaState(double height, double dirX, double dirZ, double period)
        {
            Height = height; DirX = dirX; DirZ = dirZ; Period = period;
        }

        /// <summary>Deep-water wavelength in blocks (1 block = 1 m).</summary>
        public double Wavelength => 9.81 * Period * Period / (2 * Math.PI);

        public double ToDegrees => Compass.Bearing(DirX, DirZ);
    }

    public static class Compass
    {
        static readonly string[] Points = { "N", "NNE", "NE", "ENE", "E", "ESE", "SE", "SSE", "S", "SSW", "SW", "WSW", "W", "WNW", "NW", "NNW" };

        /// <summary>Compass bearing of a direction in world axes (north = -Z).</summary>
        public static double Bearing(double x, double z)
        {
            if (x * x + z * z < 1e-18) return 0;
            double deg = Math.Atan2(x, -z) * 180 / Math.PI;
            return deg < 0 ? deg + 360 : deg;
        }

        public static string Name(double bearing) => Points[(int)Math.Round(WorldShape.Mod(bearing, 360) / 22.5) % 16];

        /// <summary>"N", "ne", "sse", "270" … → bearing, or null.</summary>
        public static double? Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            if (double.TryParse(s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double d)) return WorldShape.Mod(d, 360);
            int i = Array.IndexOf(Points, s.Trim().ToUpperInvariant());
            return i < 0 ? null : i * 22.5;
        }
    }

    public static class Beaufort
    {
        /// <summary>Metres per second for one vanilla wind unit (vanilla's "storm" ≈ Beaufort 10).</summary>
        public const double MetersPerSecondPerUnit = 25;

        static readonly double[] Upper = { 0.5, 1.6, 3.4, 5.5, 8.0, 10.8, 13.9, 17.2, 20.8, 24.5, 28.5, 32.7 };
        static readonly string[] Names =
        {
            "calm", "light air", "light breeze", "gentle breeze", "moderate breeze", "fresh breeze", "strong breeze",
            "near gale", "gale", "strong gale", "storm", "violent storm", "hurricane"
        };

        public static int Force(double strength)
        {
            double ms = strength * MetersPerSecondPerUnit;
            int f = 0;
            while (f < Upper.Length && ms >= Upper[f]) f++;
            return f;
        }

        public static string Name(double strength) => Names[Force(strength)];
    }
}
