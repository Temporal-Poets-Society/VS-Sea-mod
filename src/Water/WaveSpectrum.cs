using System;
using System.Collections.Generic;

namespace Sea
{
    /// <summary>
    /// The sea's waves as a fixed library of travelling sine waves ("trains") that every side knows: each train has
    /// its own direction, wavelength and starting phase, made from the world key. The sea state and the wind only set
    /// how strong each train is, so when they change the waves grow and fade but never jump, and the surface at a
    /// place and time is the same on the server and every client (see <see cref="WavePlace"/> and <see cref="WaveClock"/>).
    ///
    ///  - Swell: 20 wavelengths, half an octave apart (4 … 2896 blocks), in 18 directions 20° apart. Breezes keep
    ///    the original shape; storms blend to PM log-wavelength energy bands around Lp = 30 * gameplay Hs.
    ///    Each splits between two directions; shorter waves spread up to ±40°.
    ///  - Chop: the original 6.5 and 4.3 blocks, plus 9.7 in storms, bounded within the game-height spectrum.
    ///  - Deep-water speeds: long waves run fast, short ones slow.
    ///  - On a Cylinder World ring every train fits a whole number of waves around it, so the sea is seamless.
    /// </summary>
    public sealed class WaveSpectrum
    {
        public const int DirBins = 18;
        public const int SwellBins = 20;
        public static readonly double[] ChopLengths = { 6.5, 4.3, 9.7 };
        const double DirStep = 2 * Math.PI / DirBins;
        const double G = 9.81;   // blocks/s² (1 block = 1 m)

        public readonly long Key;
        public readonly double Circumference;
        /// <summary>(0.3.7) The config's WaveScale for the chop (the swell gets it through the sea state's height).</summary>
        public readonly double ChopScale;
        public int Bins => SwellBins + ChopLengths.Length;
        public int TrainCount => Bins * DirBins;

        /// <summary>Per train: wave vector (radians per block), angular speed (radians per second), starting phase.</summary>
        public readonly double[] Kx, Kz, Omega, Phase;
        /// <summary>Per bin: how far its direction grid is turned (in steps), so bins don't all line up at once.</summary>
        readonly double[] grid;

        public WaveSpectrum(long key, double circumference, double chopScale = 1)
        {
            Key = key;
            Circumference = circumference;
            ChopScale = double.IsFinite(chopScale) ? Math.Max(0, chopScale) : 1;
            int n = TrainCount;
            Kx = new double[n]; Kz = new double[n]; Omega = new double[n]; Phase = new double[n];
            grid = new double[Bins];
            for (int b = 0; b < Bins; b++)
            {
                // Appending long swell bins must not re-seed the two existing breeze chop trains.
                int seedBin = b < SwellBins ? b : 12 + b - SwellBins;
                grid[b] = WorldShape.Mod(0.37 + seedBin * 0.6180339887, 1);
                double k = 2 * Math.PI / Length(b);
                for (int d = 0; d < DirBins; d++)
                {
                    int id = b * DirBins + d;
                    double dir = (d + grid[b]) * DirStep;
                    double kx = Math.Cos(dir) * k, kz = Math.Sin(dir) * k;
                    // a whole number of waves around the ring: no seam
                    if (circumference > 0) kx = Math.Round(kx * circumference / (2 * Math.PI)) * 2 * Math.PI / circumference;
                    Kx[id] = kx;
                    Kz[id] = kz;
                    Omega[id] = Math.Sqrt(G * Math.Sqrt(kx * kx + kz * kz));
                    Phase[id] = Noise.Unit(Noise.Hash(key, 0x5EA, seedBin, d)) * 2 * Math.PI;
                }
            }
        }

        /// <summary>Wavelength of a bin: swell 4·2^(b/2) blocks, then the chop.</summary>
        public static double Length(int bin) => bin < SwellBins ? 4 * Math.Pow(2, bin * 0.5) : ChopLengths[bin - SwellBins];

        /// <summary>Relative amplitude of a swell wavelength u octaves from the peak (u &gt; 0 longer).</summary>
        public static double SwellShape(double u)
        {
            if (u >= 0.6 || u <= -2.9) return 0;
            if (u >= 0)
            {
                double c = Math.Cos(Math.PI / 2 * u / 0.6);
                return c * c;
            }
            double s = Math.Pow(2, u);   // shorter waves, same steepness
            if (u < -2.4)
            {
                double c = Math.Cos(Math.PI / 2 * (-2.4 - u) / 0.5);
                s *= c * c;
            }
            return s;
        }

        /// <summary>A fixed side (-1..1) each swell bin is spread to, the shorter the wave the further.</summary>
        static double SpreadSide(int bin) => Math.Sin(bin * 2.39996 + 0.5);

        /// <summary>Leave calm/breeze geometry intact; change the spectrum smoothly from strength .4 to .8.</summary>
        public static double StormBlend(double strength)
        {
            double t = Math.Clamp((strength - 0.4) / 0.4, 0, 1);
            return t * t * (3 - 2 * t);
        }

        /// <summary>Monahan & O'Muircheartaigh (1980), bounded appearance target, not a hurricane forecast.</summary>
        public static double WhitecapCoverage(double strength)
        {
            double u = Math.Clamp(strength, 0, WindModel.MaxStrength) * Beaufort.MetersPerSecondPerUnit;
            double onset = Math.Clamp((u - 5.5) / 5.3, 0, 1);
            onset *= onset * (3 - 2 * onset);
            return Math.Min(0.95, 3.84e-6 * Math.Pow(u, 3.41)) * onset;
        }

        // PM S(f) df on logarithmic wavelength bins: amplitude proportional to L exp(-5/8 (L/Lp)²).
        // The very short tail is represented by bounded chop; taper it to fit the existing 24-train GPU budget.
        static double DevelopedShape(double u)
        {
            if (u <= -3 || u >= 1.5) return 0;
            double r = Math.Pow(2, u), w = r * Math.Exp(-0.625 * r * r);
            if (u < -2.5) w *= Math.Pow(Math.Cos(Math.PI * (-2.5 - u)), 2);
            if (u > 1) w *= Math.Pow(Math.Cos(Math.PI * (u - 1)), 2);
            return w;
        }

        /// <summary>
        /// How strong every train is for a sea state and the wind (dense, one entry per train, mostly zero). The swell's
        /// significant height (4σ) is the sea state's height.
        /// </summary>
        public void Amplitudes(in SeaState sea, in Wind wind, double[] a)
        {
            Array.Clear(a, 0, a.Length);
            double lp = Math.Clamp(sea.Wavelength, 10, 1600);
            double storm = sea.SpectrumBlend >= 0 ? sea.SpectrumBlend : WindModel.StormForHeight(sea.Height, ChopScale);
            double theta0 = Math.Atan2(sea.DirZ, sea.DirX);
            Span<double> w = stackalloc double[SwellBins];
            double sum = 0;
            for (int m = 0; m < SwellBins; m++)
            {
                double u = Math.Log2(Length(m) / lp);
                w[m] = (1 - storm) * SwellShape(u) + storm * DevelopedShape(u);
                sum += w[m] * w[m];
            }
            if (sum > 0 && sea.Height > 0)
            {
                // significant height = 4σ, σ² = Σ a²/2  →  a_m = w_m · H / sqrt(8 Σ w²)
                double scale = sea.Height / Math.Sqrt(8 * sum);
                for (int m = 0; m < SwellBins; m++)
                {
                    if (w[m] <= 0) continue;
                    double len = Length(m), u = Math.Log2(len / lp);
                    double spread = 40 * Math.PI / 180 * Math.Clamp(-u / 2.4, 0, 1) * SpreadSide(m);
                    Split(m, theta0 + spread, Math.Min(w[m] * scale, 0.05 * len), a);
                }
            }
            // chop from the wind right here
            double ws = wind.Strength;
            // Keep the storm's equilibrium texture near the breeze slope, independent of peak length/height.
            double chopStorm = StormBlend(ws);
            double chop = ((1 - chopStorm) * (0.03 + 0.12 * Math.Min(1.5, ws))
                + chopStorm * 0.066) * ChopScale;
            double tc = ws > 1e-6 ? Math.Atan2(wind.Z, wind.X) : theta0;
            Split(SwellBins, tc, chop, a);
            Split(SwellBins + 1, tc - 35 * Math.PI / 180, chop * 0.6, a);
            Split(SwellBins + 2, tc + 23 * Math.PI / 180, chop * 0.45 * chopStorm, a);
            // Include short swell bins in the saturation budget, rather than allowing them to grow on top of
            // the chop. This leaves breeze geometry exact and keeps the storm short-wave RMS slope ~0.065.
            double shortVariance = 0;
            const double shortK = 2 * Math.PI / 16;
            for (int i = 0; i < TrainCount; i++)
            {
                double k2 = Kx[i] * Kx[i] + Kz[i] * Kz[i];
                if (k2 > shortK * shortK) shortVariance += 0.5 * a[i] * a[i] * k2;
            }
            double limit = 0.065 * ChopScale;
            double saturation = Math.Max(storm, chopStorm);
            if (shortVariance > limit * limit)
            {
                double keep = 1 + saturation * (limit / Math.Sqrt(shortVariance) - 1);
                for (int i = 0; i < TrainCount; i++)
                    if (Kx[i] * Kx[i] + Kz[i] * Kz[i] > shortK * shortK) a[i] *= keep;
            }
        }

        /// <summary>A wave running toward theta, shared between the bin's two nearest directions (keeping its energy).</summary>
        void Split(int bin, double theta, double amp, double[] a)
        {
            double p = theta / DirStep - grid[bin];
            double d0 = Math.Floor(p), f = p - d0;
            int i0 = (int)WorldShape.Mod(d0, DirBins), i1 = (i0 + 1) % DirBins;
            a[bin * DirBins + i0] += amp * Math.Cos(f * Math.PI / 2);
            a[bin * DirBins + i1] += amp * Math.Sin(f * Math.PI / 2);
        }
    }
}
