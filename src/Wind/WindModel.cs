using System;

namespace Sea
{
    /// <summary>
    /// A source of prevailing winds for a map (Earth Map could supply the real monthly averages). Must be a pure
    /// function that gives the same answer on the server and every client, and must be thread-safe.
    /// </summary>
    public interface IPrevailingWinds
    {
        /// <summary>Prevailing wind at a place and game time, in vanilla wind units (before PrevailingStrength).</summary>
        Wind At(double x, double z, double totalHours);
    }

    /// <summary>
    /// The wind and the sea, as a pure function of (key, world shape, config, position, game time). The server and
    /// every client build the same model from the same <see cref="SeaParamsPacket"/>, so they agree without sending
    /// any wind data. Four layers:
    ///   1. prevailing winds by latitude (trade winds, westerlies, polar easterlies), shifting with the seasons
    ///   2. weather drifting over days (smooth noise, stronger in the westerlies than in the trades)
    ///   3. travelling storms (<see cref="StormField"/>)
    ///   4. gusts over seconds
    /// The sea state follows the wind of the past hours at the same place.
    /// </summary>
    public sealed class WindModel
    {
        public readonly WorldShape Shape;
        public readonly SeaConfig Config;
        public readonly StormField Storms;
        public readonly WindMode Mode;
        readonly long key;

        /// <summary>An admin's fixed wind, or null. Replaced as a whole (read from other threads).</summary>
        public volatile WindOverride Override;

        /// <summary>Prevailing winds from a map mod (e.g. real monthly winds for Earth Map); null = Earth's belts by latitude.</summary>
        public volatile IPrevailingWinds PrevailingSource;

        public WindModel(WorldShape shape, SeaConfig config, long key)
        {
            Shape = shape;
            Config = config;
            Mode = config.Mode;
            this.key = key;
            Storms = new StormField(shape, config, key);
        }

        // ---------------------------------------------------------------- layers

        /// <summary>
        /// Prevailing wind for the northern hemisphere by |latitude| in degrees: (latitude, eastward, poleward), in
        /// vanilla wind units. Trades blow from the NE toward the equator, westerlies from the SW, polar easterlies
        /// from the NE again; calm belts in between (doldrums, horse latitudes, polar front).
        /// </summary>
        static readonly double[,] Belts =
        {
            { 0, -0.10, 0.00 },
            { 5, -0.20, -0.10 },
            { 10, -0.30, -0.15 },
            { 15, -0.33, -0.14 },
            { 22, -0.28, -0.10 },
            { 28, -0.12, -0.03 },
            { 32, 0.00, 0.00 },
            { 38, 0.20, 0.06 },
            { 45, 0.34, 0.10 },
            { 52, 0.36, 0.08 },
            { 58, 0.22, 0.04 },
            { 64, 0.00, 0.00 },
            { 70, -0.15, -0.05 },
            { 80, -0.20, -0.05 },
            { 90, -0.10, 0.00 },
        };

        /// <summary>Degrees the belts are shifted north at this time of year (the ITCZ follows the sun, lagging a month).</summary>
        public double BeltShift(double hours) => Config.SeasonShift * Math.Sin(2 * Math.PI * (Shape.YearRel(hours) - 0.3));

        /// <summary>Latitude (degrees) as the belts see it, after the seasonal shift.</summary>
        public double BeltLatitude(double z, double hours) => Shape.LatitudeDeg(z) - BeltShift(hours);

        /// <summary>The prevailing wind: Earth's belts by latitude, or what a map mod supplies (<see cref="PrevailingSource"/>).</summary>
        public Wind Prevailing(double x, double z, double hours)
        {
            var src = PrevailingSource;
            return (src != null ? src.At(x, z, hours) : Belt(z, hours)) * Config.PrevailingStrength;
        }

        /// <summary>The prevailing wind of Earth's belts at this latitude and time of year.</summary>
        public Wind Belt(double z, double hours)
        {
            double lat = BeltLatitude(z, hours), a = Math.Min(90, Math.Abs(lat)), hs = lat >= 0 ? 1 : -1;
            int i = 0;
            while (i < Belts.GetLength(0) - 2 && a > Belts[i + 1, 0]) i++;
            double t = Noise.SmoothStep(Belts[i, 0], Belts[i + 1, 0], a);
            double east = Noise.Lerp(Belts[i, 1], Belts[i + 1, 1], t);
            double pole = Noise.Lerp(Belts[i, 2], Belts[i + 1, 2], t);
            if (hs < 0)
            {
                double forties = 1 + 0.25 * Noise.Band(a, 36, 42, 55, 62);   // stronger southern westerlies
                east *= forties;
                pole *= forties;
            }
            double north = pole * hs;
            return new Wind(east, -north);
        }

        /// <summary>Day-to-day drift: a smooth random wind over ~5000 blocks and ~30 game hours.</summary>
        public Wind Drift(double x, double z, double hours)
        {
            if (Config.Variability <= 0) return default;
            const double scale = 5000, period = 30;
            long cells = Shape.Cells(scale);
            double lx = Shape.LatticeX(x, scale, cells), lz = z / scale, lt = hours / period;
            double a = Math.Abs(BeltLatitude(z, hours));
            double amp = 0.45 * Config.Variability * Noise.Lerp(0.55, 1.1, Noise.SmoothStep(20, 35, a));
            return new Wind(Noise.Fbm(key + 11, lx, lz, lt, cells) * amp, Noise.Fbm(key + 12, lx, lz, lt, cells) * amp);
        }

        /// <summary>Gusts: ±~20% strength and ±~12° direction, changing over tens of seconds.</summary>
        public Wind Gust(Wind w, double x, double z, double hours)
        {
            if (Config.Gusts <= 0) return w;
            const double scale = 300, period = 0.12;
            long cells = Shape.Cells(scale);
            double lx = Shape.LatticeX(x, scale, cells), lz = z / scale, lt = hours / period;
            double gs = Noise.Fbm(key + 21, lx, lz, lt, cells), ga = Noise.Fbm(key + 22, lx, lz, lt, cells);
            double s = w.Strength * (1 + 0.45 * gs * Config.Gusts);
            return w.WithStrength(Math.Max(0, s)).Rotated(0.35 * ga * Config.Gusts);
        }

        // ---------------------------------------------------------------- totals

        public const double MaxStrength = 1.5;

        /// <summary>The wind at a place and game time (hours = calendar TotalHours).</summary>
        public Wind At(double x, double z, double hours) => At(x, z, hours, true);

        public Wind At(double x, double z, double hours, bool gusts)
        {
            var ov = Override;
            if (ov != null && ov.Covers(hours)) return Wind.FromCompass(ov.FromDegrees, ov.Strength);
            if (Mode == WindMode.Off) return default;
            return Layers(x, z, hours, gusts, Mode);
        }

        /// <summary>
        /// (0.3.6) The wind the sea follows (its sea state, its chop): Sea's wind, or with WindModel "Off" its steady
        /// prevailing winds and gusts. Vanilla's wind can't be the sea's: it changes from tick to tick and reaches each
        /// client at another moment, so ships and the drawn water would part.
        /// </summary>
        public Wind SeaWind(double x, double z, double hours, bool gusts = true)
        {
            var ov = Override;
            if (ov != null && ov.Covers(hours)) return Wind.FromCompass(ov.FromDegrees, ov.Strength);
            return Layers(x, z, hours, gusts, Mode == WindMode.Off ? WindMode.Steady : Mode);
        }

        Wind Layers(double x, double z, double hours, bool gusts, WindMode mode)
        {
            Wind w = Prevailing(x, z, hours);
            if (mode == WindMode.Earthlike)
            {
                w += Drift(x, z, hours);
                var s = Storms.At(x, z, hours);
                // a storm's own circulation takes over from the background wind near it
                double take = Math.Min(1, s.Strength / 0.6) * 0.6;
                w = w * (1 - take) + s;
            }
            if (gusts) w = Gust(w, x, z, hours);
            double l = w.Strength;
            return l > MaxStrength ? w.WithStrength(MaxStrength) : w;
        }

        /// <summary>Fully developed significant wave height (blocks) for a steady wind.</summary>
        public double DevelopedHeight(double strength) => (0.1 + 2.4 * strength * strength) * Config.WaveScale;

        /// <summary>
        /// Waves at a place: they build up over a few hours of wind and die away more slowly (swell), and run the way
        /// the wind has been blowing (<see cref="SeaWind"/>).
        /// </summary>
        public SeaState SeaAt(double x, double z, double hours)
        {
            double fast = 0, fastW = 0, slow = 0, slowW = 0, dx = 0, dz = 0;
            for (int k = 0; k < 16; k++)
            {
                var w = SeaWind(x, z, hours - k, false);
                double s = w.Strength, h = DevelopedHeight(s);
                double wf = Math.Exp(-k / 3.0), ws = Math.Exp(-k / 10.0);
                fast += wf * h; fastW += wf;
                slow += ws * h; slowW += ws;
                if (s > 1e-6) { dx += wf * h * w.X / s; dz += wf * h * w.Z / s; }
            }
            double height = Math.Max(fast / fastW, 0.75 * slow / slowW);
            double dl = Math.Sqrt(dx * dx + dz * dz);
            if (dl < 1e-9) { dx = 1; dz = 0; dl = 1; }
            double period = 2.5 + 3.2 * Math.Sqrt(height / Math.Max(0.1, Config.WaveScale));
            return new SeaState(height, dx / dl, dz / dl, period);
        }

        /// <summary>A name for the wind belt at a place, for players.</summary>
        public string BeltName(double z, double hours)
        {
            double a = Math.Abs(BeltLatitude(z, hours));
            if (a < 5) return "the doldrums";
            if (a < 29) return "the trade winds";
            if (a < 35) return "the horse latitudes";
            if (a < 62) return "the westerlies";
            return "the polar easterlies";
        }
    }
}
