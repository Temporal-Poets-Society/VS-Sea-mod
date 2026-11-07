using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using ICoreAPI = Vintagestory.API.Common.ICoreAPI;
using ILogger = Vintagestory.API.Common.ILogger;

namespace Sea
{
    /// <summary>
    /// (0.6.0) Sea's side of the open sea contracts (docs/open-sea-contracts.md): what other mods can read and register
    /// without compiling against Sea.dll. Each role is one entry in <c>api.ObjectCache</c> under
    /// <c>seacontracts:&lt;role&gt;</c>, a <c>Dictionary&lt;string, object&gt;</c> of named members, on each side. Only
    /// .NET base types cross the boundary; Sea's typed API (<see cref="SeaSystem"/>) stays as it is.
    ///
    ///  - <c>seastate</c>, <c>surface</c>, <c>hulls</c>: published by Sea at priority 50 (a mod's default). A mod
    ///    already holding the role at the same or a higher priority keeps it, and Sea says so in the log.
    ///  - <c>weather</c>: read. When a world's weather owner (Earth) publishes it and no mod has called
    ///    <see cref="SeaSystem.SetWindProvider"/>, Sea's wind and sea follow it.
    ///
    /// Units: 1 block = 1 m, +X east, +Z south, vectors toward where things go, wind in m/s, game time in total game
    /// hours, wave time in <c>surface.seconds</c>.
    /// </summary>
    public sealed class SeaContracts : IDisposable
    {
        public const string Prefix = "seacontracts:";
        public const string Owner = "sea";
        public const int Version = 1;
        /// <summary>Priorities (R2): the world's owner, a mod's default, a fallback.</summary>
        public const int WorldOwner = 100, ModDefault = 50, Fallback = 0;
        /// <summary>Vanilla's wind unit in m/s (1.0 = 25 m/s), as Sea's <see cref="Beaufort.MetersPerSecondPerUnit"/>.</summary>
        const double Mps = Beaufort.MetersPerSecondPerUnit;


        readonly SeaSystem sys;
        readonly ICoreAPI api;
        readonly ILogger log;
        readonly ConcurrentDictionary<string, HullRecords> hulls = new();
        readonly ConcurrentDictionary<string, DryFunction> dries = new();
        readonly object trainLock = new();
        readonly Dictionary<(long, long), (WavePlace Place, long Used)> trainPlaces = new();
        long trainUses;
        long weatherListener = -1;
        string weatherOwner;
        bool weatherRefusedLogged;
        readonly List<string> published = new();

        public SeaContracts(SeaSystem sys, ICoreAPI api, ILogger log)
        {
            this.sys = sys;
            this.api = api;
            this.log = log;
        }

        /// <summary>The roles Sea publishes on this side (for <c>.sea contracts</c>).</summary>
        public IReadOnlyList<string> Published => published;

        /// <summary>The weather owner Sea follows through the contract, or null.</summary>
        public string WeatherOwner => weatherOwner;

        public void Publish()
        {
            Claim("seastate", SeaStateRole());
            Claim("surface", SurfaceRole());
            Claim("hulls", HullsRole());
            try { weatherListener = api.Event.RegisterGameTickListener(_ => { FollowWeather(); Reclaim(); }, 1000); }
            catch (Exception e) { log.Warning("[sea] contracts: can't watch for a weather owner: {0}", e.Message); }
        }

        public void Dispose()
        {
            if (weatherListener >= 0) { try { api.Event.UnregisterGameTickListener(weatherListener); } catch (Exception) { } }
            foreach (var role in published)
            {
                string key = Prefix + role;
                lock (api.ObjectCache)
                    if (api.ObjectCache.TryGetValue(key, out var o) && o is IDictionary<string, object> d && OwnerOf(d) == Owner)
                        api.ObjectCache.Remove(key);
            }
            published.Clear();
            foreach (var h in hulls.Values) sys.RemoveWaterClipSource(h);
            foreach (var d in dries.Values) sys.RemoveDrySpaces(d);
            hulls.Clear();
            dries.Clear();
        }

        // ------------------------------------------------------------------ registry

        /// <summary>Puts Sea's entry for a role in the cache unless another mod holds it at the same or a higher priority.</summary>
        bool Claim(string role, Dictionary<string, object> entry)
        {
            string key = Prefix + role;
            lock (api.ObjectCache)
            {
                if (api.ObjectCache.TryGetValue(key, out var o) && o is IDictionary<string, object> d && OwnerOf(d) != Owner
                    && PriorityOf(d) >= PriorityOf(entry))
                {
                    log.Notification("[sea] contracts: {0} is held by {1}; Sea doesn't publish its own", key, OwnerOf(d));
                    return false;
                }
                api.ObjectCache[key] = entry;
            }
            published.Add(role);
            return true;
        }

        /// <summary>
        /// Each second: a role Sea gave way to (another mod's entry at the same or a higher priority) that has since been
        /// withdrawn, say by a world's owner on joining a world it doesn't run, is Sea's again.
        /// </summary>
        void Reclaim()
        {
            foreach (var role in new[] { "seastate", "surface", "hulls" })
            {
                lock (api.ObjectCache) if (api.ObjectCache.ContainsKey(Prefix + role)) continue;
                published.Remove(role);
                if (Claim(role, role == "seastate" ? SeaStateRole() : role == "surface" ? SurfaceRole() : HullsRole()))
                    log.Notification("[sea] contracts: {0} was withdrawn; Sea publishes its own again", Prefix + role);
            }
        }

        /// <summary>A role's entry on this side, or null.</summary>
        public static IDictionary<string, object> Find(ICoreAPI api, string role)
        {
            lock (api.ObjectCache)
                return api.ObjectCache.TryGetValue(Prefix + role, out var o) ? o as IDictionary<string, object> : null;
        }

        static string OwnerOf(IDictionary<string, object> d) => d.TryGetValue("owner", out var o) ? o as string : null;
        static int PriorityOf(IDictionary<string, object> d) => d.TryGetValue("priority", out var o) && o is int p ? p : Fallback;

        Dictionary<string, object> Entry() => new()
        {
            ["owner"] = Owner,
            ["version"] = Version,
            ["priority"] = ModDefault,
            ["revision"] = new Func<long>(() => sys.ContractRevision),
        };

        // ------------------------------------------------------------------ seastate (section 6)

        Dictionary<string, object> SeaStateRole()
        {
            var e = Entry();
            e["at"] = new Func<double, double, double, (double Height, double Period, double DirX, double DirZ, double WindX, double WindZ, double Storm)>(SeaStateAt);
            e["shelter"] = new Func<double, double, double>(Shelter);
            e["current"] = new Func<double, double, (double X, double Z)>(Current);
            e["surf"] = new Func<double, double, (double Depth, double Breaking, double Foam)>(Surf);
            e["lee"] = new Func<double, double, double>(Lee);
            return e;
        }

        /// <summary>
        /// (0.6.0) The water's own drift at a place now (m/s, +X east, +Z south): the longshore current breaking waves
        /// drive along a beach (Komar 1979) and a rip's flow back out to sea; 0 off the coast. Things afloat there move with it.
        /// </summary>
        public (double X, double Z) Current(double x, double z) =>
            AtPlace(x, z, p => sys.CurrentAt(p, x, z), (0.0, 0.0));

        /// <summary>
        /// (0.6.0) The surf at a place now: depth to the sea floor (blocks; 32 or more off the coast), how far the waves
        /// break there (0–1) and the whitewater drawn (0–1).
        /// </summary>
        public (double Depth, double Breaking, double Foam) Surf(double x, double z) =>
            AtPlace(x, z, p => { var s = p.SurfAt(x, z); return (s.Depth, s.Breaking, s.Foam); }, (CoastMath.MaxDepth, 0.0, 0.0));

        /// <summary>
        /// (0.6.0) The share of the sea's height that gets past the land to a place now (1 on open water; less in the lee
        /// of an island or a headland, deep water too). surface.height already has it.
        /// </summary>
        public double Lee(double x, double z) => AtPlace(x, z, p => sys.LeeAt(p, x, z), 1.0);

        // a place sampled at (x, z) now, from the same small cache as Trains
        T AtPlace<T>(double x, double z, Func<WavePlace, T> read, T none)
        {
            if (sys.Spectrum == null || !double.IsFinite(x) || !double.IsFinite(z)) return none;
            try
            {
                lock (trainLock)
                {
                    var p = TrainPlace(x, z);
                    sys.SampleWaves(p, x, z, sys.WaveTime);
                    return read(p);
                }
            }
            catch (Exception) { return none; }
        }

        /// <summary>The sea at a place and game hour: significant height (m), peak period (s), travel direction, the sea's wind (m/s), storm 0–1.</summary>
        public (double Height, double Period, double DirX, double DirZ, double WindX, double WindZ, double Storm) SeaStateAt(double x, double z, double hours)
        {
            var s = sys.SeaAtHours(x, z, hours);
            var m = sys.Model;
            var w = m != null ? m.SeaWind(x, z, hours, true) : sys.WindAtHours(x, z, hours);
            double storm = s.SpectrumBlend >= 0 ? s.SpectrumBlend : WindModel.StormForHeight(s.Height, sys.Config.WaveScale);
            return (s.Height, s.Period, s.DirX, s.DirZ, w.X * Mps, w.Z * Mps, storm);
        }

        /// <summary>The share of the open sea's waves that reaches here, 0–1 (0 on inland water).</summary>
        public double Shelter(double x, double z) => sys.Spectrum == null ? 0 : Math.Clamp(sys.WaveFactorAt(x, z), 0, 1);

        // ------------------------------------------------------------------ surface (section 7)

        Dictionary<string, object> SurfaceRole()
        {
            var e = Entry();
            e["seconds"] = new Func<double>(Seconds);
            e["clock"] = "elapsed";
            e["trains"] = new Func<double, double, double, double[], double[], double[], double[], double[], int>(Trains);
            e["height"] = new Func<double, double, double>(Height);
            e["pose"] = new Func<double, double, double, double, double, (double Hold, double Lift, double Roll, double Pitch)>(Pose);
            return e;
        }

        /// <summary>The waves' clock: the server's running time, which clients follow (it runs while the calendar stands).</summary>
        public double Seconds()
        {
            try { return sys.WaveTime; }
            catch (Exception) { return 0; }
        }

        // (caller holds trainLock) the cached place for this grid cell
        WavePlace TrainPlace(double x, double z)
        {
            var key = ((long)Math.Floor(x / WavePlace.GridCell), (long)Math.Floor(z / WavePlace.GridCell));
            if (!trainPlaces.TryGetValue(key, out var slot))
            {
                if (trainPlaces.Count >= 32)
                {
                    var oldest = key; long used = long.MaxValue;
                    foreach (var kv in trainPlaces) if (kv.Value.Used < used) { used = kv.Value.Used; oldest = kv.Key; }
                    trainPlaces.Remove(oldest);
                }
                slot = (new WavePlace(), 0);
            }
            trainPlaces[key] = (slot.Place, ++trainUses);
            return slot.Place;
        }

        /// <summary>
        /// The trains at (x, z) at wave time `seconds`: each the height <c>amp · sin(kx·x + kz·z − omega·t + phase)</c> in m,
        /// before shelter (multiply by <see cref="Shelter"/>). (0.6.0) These are the open sea's trains: near a beach the
        /// drawn surface (<see cref="Height"/>, <see cref="Pose"/>) also follows the bottom, shoals and breaks. The same on every side. Fills the arrays (at most their
        /// length) and returns how many; 0 before the waves are ready.
        /// </summary>
        public int Trains(double x, double z, double seconds, double[] kx, double[] kz, double[] omega, double[] phase, double[] amp)
        {
            if (sys.Spectrum == null || kx == null || kz == null || omega == null || phase == null || amp == null) return 0;
            if (!double.IsFinite(x) || !double.IsFinite(z) || !double.IsFinite(seconds)) return 0;
            lock (trainLock)
            {
                var p = TrainPlace(x, z);
                sys.SampleWaves(p, x, z, seconds);
                int n = Math.Min(p.Count, Math.Min(Math.Min(kx.Length, kz.Length), Math.Min(Math.Min(omega.Length, phase.Length), amp.Length)));
                for (int i = 0; i < n; i++)
                {
                    kx[i] = p.Kx[i]; kz[i] = p.Kz[i]; omega[i] = p.Omega[i]; phase[i] = p.Phase[i]; amp[i] = p.Amp[i];
                }
                return n;
            }
        }

        /// <summary>
        /// The drawn surface's height above still water now (m), shelter and crest movement included. On a client it is
        /// what the water shader draws there; on the server the place's own waves.
        /// </summary>
        public double Height(double x, double z) => sys.Spectrum == null ? 0 : sys.WaveHeightAt(x, z);

        /// <summary>
        /// How a hull of `length` by `beam` m centred at (x, z) lies on the water now, with Rough Seas' meaning of
        /// <c>IDrawnSea.PoseOf</c>, so a vessel written for his drawn sea rides Sea's: `yaw` is the heading as the
        /// renderer turns the shape (<c>Pos.Yaw + rotateY · DEG2RAD</c>), the hull's length along the shape's X axis.
        /// Hold 0–1 (the share of the open sea's waves here; 0: none drawn), Lift m, Roll rad about the length (the
        /// renderer's xangle), Pitch rad about the beam (zangle). The water's pose, without the hull's inertia.
        /// </summary>
        public (double Hold, double Lift, double Roll, double Pitch) Pose(double x, double z, double yaw, double length, double beam)
        {
            if (sys.Spectrum == null || !(length > 0) || !(beam > 0)) return default;
            double hold = Shelter(x, z);
            if (hold <= 0) return default;
            return SeaContractMath.PoseOn(Height, hold, x, z, yaw, length, beam);
        }

        // ------------------------------------------------------------------ hulls (section 8)

        Dictionary<string, object> HullsRole()
        {
            var e = Entry();
            e["register"] = new Action<string, Func<double[][]>>(RegisterHulls);
            e["unregister"] = new Action<string>(UnregisterHulls);
            e["registerDry"] = new Action<string, Func<double, double, double, bool>>(RegisterDry);
            e["unregisterDry"] = new Action<string>(UnregisterDry);
            e["isDry"] = new Func<double, double, double, bool>((x, y, z) => sys.IsDry(x, y, z));
            return e;
        }

        /// <summary>Client: hulls by record (see the H… indices); Sea asks every frame before drawing water.</summary>
        public void RegisterHulls(string id, Func<double[][]> records)
        {
            if (string.IsNullOrEmpty(id) || records == null) return;
            var h = new HullRecords(records, log, id);
            if (hulls.TryRemove(id, out var old)) sys.RemoveWaterClipSource(old);
            hulls[id] = h;
            sys.AddWaterClipSource(h);
        }

        public void UnregisterHulls(string id)
        {
            if (id != null && hulls.TryRemove(id, out var old)) sys.RemoveWaterClipSource(old);
        }

        /// <summary>Both sides: a mod's dry places where the world has water.</summary>
        public void RegisterDry(string id, Func<double, double, double, bool> isDry)
        {
            if (string.IsNullOrEmpty(id) || isDry == null) return;
            var d = new DryFunction(isDry);
            if (dries.TryRemove(id, out var old)) sys.RemoveDrySpaces(old);
            dries[id] = d;
            sys.AddDrySpaces(d);
        }

        public void UnregisterDry(string id)
        {
            if (id != null && dries.TryRemove(id, out var old)) sys.RemoveDrySpaces(old);
        }

        sealed class HullRecords : IWaterClipSource
        {
            readonly Func<double[][]> records;
            readonly ILogger log;
            readonly string id;
            bool failed;

            public HullRecords(Func<double[][]> records, ILogger log, string id) { this.records = records; this.log = log; this.id = id; }

            public void AddWaterClips(List<WaterClipBox> clips)
            {
                double[][] list;
                try { list = records(); }
                catch (Exception e)
                {
                    if (!failed) { failed = true; log.Warning("[sea] contracts: hulls of {0} failed: {1}", id, e.Message); }
                    return;
                }
                if (list == null) return;
                foreach (var r in list) if (SeaContractMath.ToClip(r, out var c)) clips.Add(c);
            }
        }

        sealed class DryFunction : IDrySpaces
        {
            readonly Func<double, double, double, bool> isDry;
            public DryFunction(Func<double, double, double, bool> isDry) { this.isDry = isDry; }
            public bool IsDry(double x, double y, double z) => isDry(x, y, z);
        }

        // ------------------------------------------------------------------ weather (section 4), read

        /// <summary>
        /// Follows a world's weather owner published as <c>seacontracts:weather</c>: its wind (m/s) becomes Sea's wind
        /// provider, unless a mod already decides the wind through <see cref="SeaSystem.SetWindProvider"/> (Earth's
        /// bridge before the contract), which keeps it.
        /// </summary>
        void FollowWeather()
        {
            var d = Find(api, "weather");
            string owner = d == null ? null : OwnerOf(d);
            if (owner == null || owner == Owner)
            {
                if (weatherOwner != null) { sys.ClearWindProvider(weatherOwner); weatherOwner = null; }
                return;
            }
            if (owner == weatherOwner) return;
            string current = sys.WindProviderName;
            // (the same mod already gives Sea its wind directly, as Earth does through SetWindProvider: nothing to change)
            if (current == owner) return;
            if (current != null && current != weatherOwner)
            {
                if (!weatherRefusedLogged) { weatherRefusedLogged = true; log.Notification("[sea] contracts: {0} publishes the weather, but {1} already decides Sea's wind; kept", owner, current); }
                return;
            }
            if (!(d.TryGetValue("wind", out var w) && w is Func<double, double, double, bool, (double X, double Z)> wind))
            {
                if (!weatherRefusedLogged) { weatherRefusedLogged = true; log.Warning("[sea] contracts: {0}'s weather has no wind of the contract's type", owner); }
                return;
            }
            Func<double, double, double, ValueTuple<double, double>> prevailingAt = null;
            if (d.TryGetValue("prevailing", out var p) && p is Func<double, double, double, (double X, double Z)> prevailing)
                prevailingAt = (x, z, h) => { var v = prevailing(x, z, h); return (v.X / Mps, v.Z / Mps); };
            if (weatherOwner != null) sys.ClearWindProvider(weatherOwner);
            if (sys.SetWindProvider(owner, (x, z, h, g) => { var v = wind(x, z, h, g); return (v.X / Mps, v.Z / Mps); }, prevailingAt))
                weatherOwner = owner;
        }
    }
}
