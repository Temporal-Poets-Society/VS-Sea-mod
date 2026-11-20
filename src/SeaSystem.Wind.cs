using System;
using System.Linq;
using System.Reflection;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.API.Util;

namespace Sea
{
    /// <summary>(SE-0046) The wind: where it comes from (a provider, a fixed wind, vanilla), the sea state, and the change protocol that keeps samples whole across a change.</summary>
    public partial class SeaSystem
    {
        /// <summary>Is Sea's wind (a provider's, or an admin's fixed one) deciding the wind at this time? Otherwise it's vanilla's.</summary>
        public bool Controls(double hours) => Model?.Decides(hours) ?? provider != null;

        /// <summary>
        /// The wind at a place now: an admin's fixed wind, else the provider's (gusts included), else vanilla's
        /// (blowing east, or the way a wind direction mod turns it; SE-0079). (SE-0015) An admin's change of the fixed
        /// wind counts from its wave sample on, as for the waves, so the server and every client switch at the same moment.
        /// </summary>
        public Wind WindAt(double x, double z) => WindAt(x, z, Hours, NowSample);

        /// <summary>The wind at a place and game time (vanilla's is only known now), fixed winds as known now.</summary>
        Wind WindAt(double x, double z, double hours) => WindAt(x, z, hours, long.MaxValue);

        /// <summary>The same, fixed winds as worked out for wave sample `sample` (<see cref="long.MaxValue"/>: as known now).</summary>
        Wind WindAt(double x, double z, double hours, long sample)
        {
            var m = Model;
            if (m != null && m.Decides(hours, sample)) return m.At(x, z, hours, true, sample);
            var p = provider;
            if (p != null) return p.At(x, z, hours, true);
            return VanillaWind(x, z);
        }

        /// <summary>(SE-0015) The wave sample now on this side: what "now" means for an admin's fixed wind.</summary>
        long NowSample
        {
            get
            {
                if (Model == null) return long.MaxValue;
                double t = WaveTime;
                return double.IsFinite(t) ? WavePlace.SampleIndex(t) : long.MaxValue;
            }
        }

        // ---------------------------------------------------------------- (SE-0017) vanilla's wind

        int mainThread = -1;
        readonly System.Collections.Generic.Dictionary<(int, int), Wind> vanillaWind = new();
        Wind lastVanillaWind;
        const int VanillaWindCells = 256;

        /// <summary>
        /// Vanilla's wind at a place, as the vector the game's wind lookup gives (+X east, +Z south). Vanilla's own weather
        /// sets only X, its strength, so it blows east; (SE-0079) a wind direction mod (Pelagus Winds, Wind Vanes, Simple
        /// Wind Direction and the like) turns that vector, and the direction is kept. Its weather is read through the block
        /// accessor, which belongs to the main thread: there it's read and kept per map region; other threads get the
        /// region's kept value (refreshed every second around the players), else the last one read.
        /// </summary>
        Wind VanillaWind(double x, double z)
        {
            int rs = Math.Max(1, RegionSize);
            var key = ((int)Math.Floor(x / rs), (int)Math.Floor(z / rs));
            if (Environment.CurrentManagedThreadId == mainThread)
            {
                Wind w;
                try { w = Vanilla(api.World.BlockAccessor.GetWindSpeedAt(new Vec3d(x, api.World.SeaLevel, z))); }
                catch (Exception) { return lastVanillaWind; }
                lock (vanillaWind)
                {
                    if (vanillaWind.Count >= VanillaWindCells && !vanillaWind.ContainsKey(key)) vanillaWind.Clear();
                    vanillaWind[key] = w;
                    lastVanillaWind = w;
                }
                return w;
            }
            lock (vanillaWind) return vanillaWind.TryGetValue(key, out var kept) ? kept : lastVanillaWind;
        }

        /// <summary>(SE-0079) The game's wind vector as Sea's wind: X and Z kept; calm when either isn't a number.</summary>
        internal static Wind Vanilla(Vec3d v) =>
            v != null && double.IsFinite(v.X) && double.IsFinite(v.Z) ? new Wind(v.X, v.Z) : default;

        /// <summary>Main thread, once a second: vanilla's wind where the players are, for other threads (only while it's in use).</summary>
        void RefreshVanillaWind()
        {
            // (tick listeners run on this side's main thread, whichever thread started the mod)
            mainThread = Environment.CurrentManagedThreadId;
            if (provider != null || api?.World == null) return;
            foreach (var p in api.World.AllOnlinePlayers)
                if (p?.Entity?.Pos is { } pos && pos.Dimension == 0) VanillaWind(pos.X, pos.Z);
        }

        /// <summary>The waves at a place now (SE-0015: an admin's change counts from its wave sample on).</summary>
        public SeaState SeaAt(double x, double z) => SeaAt(x, z, Hours, NowSample);

        SeaState SeaAt(double x, double z, double hours)
        {
            var m = Model;
            // (without a provider too: the sea follows the config's steady wind, the same on every side; WindModel.SeaWind)
            if (m != null) return m.SeaAt(x, z, hours);
            // no model yet: a sea fully built for the wind right away
            var w = WindAt(x, z, hours);
            double s = w.Strength;
            double referenceHeight = WindModel.BaseHeight(s, Config.WaveScale);
            double height = referenceHeight * (1 + (Config.StormHeightScale - 1) * WaveSpectrum.StormBlend(s));
            return new SeaState(height, s > 1e-6 ? w.X / s : 1, s > 1e-6 ? w.Z / s : 0,
                WindModel.PeriodForHeight(height, Config.WaveScale, referenceHeight), WindModel.StormForHeight(referenceHeight, Config.WaveScale));
        }

        // ---------------------------------------------------------------- wind provider

        /// <summary>
        /// (0.4.0) Let a mod decide the wind (Earth's climate and weather). Call it on each side, the server and every
        /// client, with the same functions, any time (before or after the world is ready); Sea's wind, sea state, ships
        /// (<see cref="WindAt"/>) and Wmap then follow it. An admin's fixed wind (/sea setwind) still wins over it.
        /// <para><paramref name="windAt"/>(x, z, totalHours, gusts) is the wind toward (X, Z) in vanilla wind units
        /// (+X east, +Z south; the length is the strength, 0..1.5), like <see cref="Wind"/>. gusts = false is the steady
        /// wind the sea state follows (16 game hours back); true includes the provider's gusts.
        /// <paramref name="prevailingAt"/>(x, z, totalHours) is the month's prevailing wind (Wmap's prevailing layer),
        /// or null: windAt without gusts then.</para>
        /// <para>Both must be pure, thread-safe and the same on every side: the sea state and the waves ships ride are
        /// worked out from them on each side separately. One provider at a time: true when accepted; false when the
        /// arguments are missing or another provider (by name) holds it (clear it first).</para>
        /// Earth binds this by reflection, so the signature uses BCL types only.
        /// </summary>
        public bool SetWindProvider(string name, System.Func<double, double, double, bool, ValueTuple<double, double>> windAt, System.Func<double, double, double, ValueTuple<double, double>> prevailingAt)
        {
            if (string.IsNullOrWhiteSpace(name) || windAt == null) return false;
            string other = null;
            lock (providerSync)
            {
                if (provider != null && provider.Name != name) other = provider.Name;
                else
                    Change(() =>
                    {
                        provider = new WindProvider(name, windAt, prevailingAt)
                        {
                            FirstFailure = (who, why) => api?.Logger.Warning("[sea] The wind provider {0} failed ({1}); Sea takes calm for such answers and counts them (.sea)", who, why)
                        };
                        var m = Model;
                        if (m != null) m.Provider = provider;
                    });
            }
            if (other != null)
            {
                Mod?.Logger.Warning("{0} asked to decide the wind, but {1} does already; ignored", name, other);
                return false;
            }
            Mod?.Logger.Notification("The wind now comes from {0} ({1} side)", name, api?.Side.ToString() ?? "unknown");
            return true;
        }

        /// <summary>(0.4.0) Stop taking the wind from this provider (by name; another provider is left alone): vanilla's wind again.</summary>
        public void ClearWindProvider(string name)
        {
            lock (providerSync)
            {
                if (provider == null || provider.Name != name) return;
                Change(() =>
                {
                    provider = null;
                    var m = Model;
                    if (m != null) m.Provider = null;
                });
            }
            Mod?.Logger.Notification("The wind no longer comes from {0}: vanilla's wind; the sea follows a steady wind", name);
        }

        /// <summary>(0.4.0) The name of the mod deciding the wind on this side, or null (vanilla's wind).</summary>
        public string WindProviderName => provider?.Name;

        /// <summary>(0.4.0) The provider on this side, or null (its name, and how many of its answers were refused).</summary>
        public WindProvider WindProvider => provider;

        volatile WindProvider provider;
        readonly object providerSync = new();

        /// <summary>Make this the model, with the provider as it is now (no provider change can slip in between).</summary>
        void UseModel(WindModel model)
        {
            Change(() =>
            {
                model.Provider = provider;
                Model = model;
            });
            // (SE-0053) the coast map wraps on the world's ring (outside Change: it takes the map's own lock)
            coast?.SetRing(model.Shape.RingStart, model.Shape.Circumference);
        }

        /// <summary>
        /// (0.4.2) The sea's past may read differently after this (a provider, a fixed wind, a model): wave places
        /// resample. (SE-0014) One change at a time; the revision is odd while it's being made, so a sample worked out
        /// across one (from another thread) is seen to be and worked out again at once (<see cref="WavePlace.Sample"/>),
        /// and one starting meanwhile waits the moment until it's made (<see cref="StableRevision"/>). The change must
        /// only set fields: nothing in it may sample the waves or wait for another thread.
        /// </summary>
        void Change(Action apply)
        {
            lock (providerSync)
            {
                System.Threading.Interlocked.Increment(ref revision);
                try { apply(); }
                finally { System.Threading.Interlocked.Increment(ref revision); }
            }
        }

        /// <summary>(SE-0014) The revision once no change is being made (the thread making one gets it as it is).</summary>
        long StableRevision()
        {
            var spin = new System.Threading.SpinWait();
            while (true)
            {
                long r = System.Threading.Interlocked.Read(ref revision);
                if ((r & 1) == 0 || System.Threading.Monitor.IsEntered(providerSync)) return r;
                spin.SpinOnce();
            }
        }

        long revision;
    }
}
