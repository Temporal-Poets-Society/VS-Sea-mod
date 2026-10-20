using System;
using System.Threading;

namespace Sea
{
    /// <summary>
    /// (0.4.0) A mod's wind (Earth's climate and weather), registered with <see cref="SeaSystem.SetWindProvider"/>.
    /// Its functions must be pure and thread-safe and give the same answer on the server and every client: Sea calls
    /// them from the server's tick, the client's render and particle threads and other mods' background threads, and
    /// for past hours too (the sea state looks 16 game hours back). Answers are checked here: a non-finite one, or one
    /// that throws, counts as calm; a stronger one is capped at <see cref="WindModel.MaxStrength"/>.
    /// </summary>
    public sealed class WindProvider
    {
        public readonly string Name;
        readonly Func<double, double, double, bool, ValueTuple<double, double>> windAt;
        readonly Func<double, double, double, ValueTuple<double, double>> prevailingAt;
        int failures;
        volatile string lastError;

        public WindProvider(string name, Func<double, double, double, bool, ValueTuple<double, double>> windAt, Func<double, double, double, ValueTuple<double, double>> prevailingAt)
        {
            Name = name;
            this.windAt = windAt ?? throw new ArgumentNullException(nameof(windAt));
            this.prevailingAt = prevailingAt;
        }

        /// <summary>Calls that threw or answered NaN or infinity so far, and the last such problem.</summary>
        public int Failures => Volatile.Read(ref failures);
        public string LastError => lastError;

        /// <summary>The wind at a place and game time; gusts = false is the steady wind the sea state follows.</summary>
        public Wind At(double x, double z, double hours, bool gusts)
        {
            try { return Checked(windAt(x, z, hours, gusts)); }
            catch (Exception e) { return Failed(e.Message); }
        }

        /// <summary>The month's prevailing wind (the provider's own, or its steady wind when it has none).</summary>
        public Wind Prevailing(double x, double z, double hours)
        {
            if (prevailingAt == null) return At(x, z, hours, false);
            try { return Checked(prevailingAt(x, z, hours)); }
            catch (Exception e) { return Failed(e.Message); }
        }

        Wind Checked(ValueTuple<double, double> w)
        {
            if (!double.IsFinite(w.Item1) || !double.IsFinite(w.Item2)) return Failed($"answered ({w.Item1}, {w.Item2})");
            var wind = new Wind(w.Item1, w.Item2);
            return wind.Strength > WindModel.MaxStrength ? wind.WithStrength(WindModel.MaxStrength) : wind;
        }

        Wind Failed(string why)
        {
            Interlocked.Increment(ref failures);
            lastError = why;
            return default;
        }
    }

    /// <summary>
    /// The wind the sea and ships follow, as a pure function of (provider, config, position, game time), the same on
    /// the server and every client. Since 0.4.0 Sea has no weather of its own (SEA-D30): the wind comes from a
    /// <see cref="WindProvider"/> (Earth), and an admin's fixed wind (<see cref="Override"/>) wins over it. Without a
    /// provider Sea leaves the wind to vanilla, and the sea follows a fixed steady wind from the config
    /// (<see cref="SeaConfig.SeaWindFrom"/>, <see cref="SeaConfig.SeaWindStrength"/>), the same on every side.
    /// The sea state follows the wind of the past hours at the same place.
    /// </summary>
    public sealed class WindModel
    {
        public readonly WorldShape Shape;
        public readonly SeaConfig Config;

        /// <summary>An admin's fixed wind, or null. Replaced as a whole (read from other threads).</summary>
        public volatile WindOverride Override;

        /// <summary>The mod deciding the wind (<see cref="SeaSystem.SetWindProvider"/>), or null: vanilla's wind.</summary>
        public volatile WindProvider Provider;

        public WindModel(WorldShape shape, SeaConfig config)
        {
            Shape = shape;
            Config = config;
        }

        /// <summary>"Provider" while a mod decides the wind, "Off" otherwise (vanilla's wind; Wmap reads this by name).</summary>
        public WindMode Mode => Provider != null ? WindMode.Provider : WindMode.Off;

        public const double MaxStrength = 1.5;

        /// <summary>Does Sea's wind (a provider's, or an admin's fixed one) decide the wind at this time?</summary>
        public bool Decides(double hours)
        {
            if (Provider != null) return true;
            var ov = Override;
            return ov != null && ov.Covers(hours);
        }

        /// <summary>The fixed steady wind the sea follows without a provider (from the config).</summary>
        public Wind SteadySeaWind => Wind.FromCompass(Config.SeaWindFrom, Math.Clamp(Config.SeaWindStrength, 0, MaxStrength));

        /// <summary>The prevailing wind of the month: the provider's, or calm without one.</summary>
        public Wind Prevailing(double x, double z, double hours) => Provider?.Prevailing(x, z, hours) ?? default;

        /// <summary>The wind at a place and game time (hours = calendar TotalHours).</summary>
        public Wind At(double x, double z, double hours) => At(x, z, hours, true);

        /// <summary>The wind at a place and game time: an admin's fixed wind, else the provider's, else calm (vanilla's then; see <see cref="SeaSystem.WindAt"/>).</summary>
        public Wind At(double x, double z, double hours, bool gusts)
        {
            var ov = Override;
            if (ov != null && ov.Covers(hours)) return Wind.FromCompass(ov.FromDegrees, ov.Strength);
            return Provider?.At(x, z, hours, gusts) ?? default;
        }

        /// <summary>
        /// The wind the sea follows (its sea state, its chop): an admin's fixed wind, else the provider's, else the
        /// config's steady wind. Vanilla's wind can't be the sea's: it changes from tick to tick and reaches each client
        /// at another moment, so ships and the drawn water would part.
        /// </summary>
        public Wind SeaWind(double x, double z, double hours, bool gusts = true)
        {
            var ov = Override;
            if (ov != null && ov.Covers(hours)) return Wind.FromCompass(ov.FromDegrees, ov.Strength);
            var p = Provider;
            return p != null ? p.At(x, z, hours, gusts) : SteadySeaWind;
        }

        /// <summary>Fully developed significant wave height (blocks) for a steady wind.</summary>
        public double DevelopedHeight(double strength) => (0.1 + 2.4 * strength * strength) * Config.WaveScale;

        /// <summary>
        /// Waves at a place: they build up over a few hours of wind and die away more slowly (swell), and run the way
        /// the wind has been blowing (<see cref="SeaWind"/> without gusts, the past 16 game hours).
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

        /// <summary>
        /// Sea no longer has wind belts (0.4.0): always null. Kept for mods that read it by reflection (Wmap), which
        /// leave the belt out when it's null.
        /// </summary>
        public string BeltName(double z, double hours) => null;
    }
}
