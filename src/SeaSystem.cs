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
    /// <summary>
    /// Sea's mod system, on both sides. The server builds the wind model from its config and the world's shape, and
    /// sends clients the same inputs so they compute the same sea. (0.4.0) Sea has no weather of its own: the wind
    /// comes from a provider mod (<see cref="SetWindProvider"/>, Earth), which also drives vanilla's weather; without
    /// one it's vanilla's wind, and the sea follows the config's steady wind (SEA-D30).
    ///
    /// The waves are the same on every side too: a fixed library of wave trains (<see cref="WaveSpectrum"/>) whose
    /// strengths follow the sea state, run on a clock the server shares (<see cref="WaveClock"/>), so a ship riding
    /// them moves the same for everyone.
    ///
    /// Other mods: <c>api.ModLoader.GetModSystem&lt;SeaSystem&gt;()</c>, then <see cref="WindAt"/>,
    /// <see cref="SeaAt"/>, <see cref="SampleWaves"/>, <see cref="SetWindProvider"/>, and on clients
    /// <see cref="AddWaterClipSource"/>.
    /// </summary>
    public class SeaSystem : ModSystem
    {
        public const string ChannelName = "sea";
        const string ConfigFile = "sea.json";
        const string OverrideKey = "sea-override";
        const string HistoryKey = "sea-override-history";
        const string WindKeyKey = "sea-windkey";

        static SeaSystem serverInstance, clientInstance;

        /// <summary>This process's Sea system for a side (singleplayer runs both).</summary>
        public static SeaSystem For(EnumAppSide side) => side == EnumAppSide.Server ? serverInstance : clientInstance;

        ICoreAPI api;
        ICoreServerAPI sapi;
        ICoreClientAPI capi;
        IServerNetworkChannel serverChannel;
        SeaParamsPacket sentParams;
        WaterRenderer waterRenderer;
        CelestialRenderer celestialRenderer;
        public string CelestialStatus => celestialRenderer?.Status ?? "Celestial presentation unavailable.";
        /// <summary>The client's sky and night presentation (null on the server, or before the world is ready).</summary>
        public CelestialRenderer Celestial => celestialRenderer;

        public SeaConfig Config = new();
        /// <summary>The wind model; null until the world is ready (server) or the server's parameters arrived (client).</summary>
        public WindModel Model { get; private set; }
        public int RegionSize { get; private set; } = 512;

        public double Hours => api.World.Calendar.TotalHours;

        /// <summary>The waves: the same trains on every side (null until the model is ready).</summary>
        public WaveSpectrum Spectrum { get; private set; }
        readonly WaveClock clock = new();
        double clientWaveTime;
        /// <summary>Client: the waves at the camera, what the water shader draws.</summary>
        public readonly WavePlace CameraWaves = new();
        const int ClockPacketMs = 5000;

        public SeaSystem() { waveSource = new WaveSource(this); }

        public override double ExecuteOrder() => 0.15;

        public override void Start(ICoreAPI api)
        {
            this.api = api;
            api.Network.RegisterChannel(ChannelName).RegisterMessageType<SeaParamsPacket>().RegisterMessageType<SeaClockPacket>();
            // the client's patches only by the client (or a dedicated server, which has none): see Hooks
            clientHooks = api.Side == EnumAppSide.Client || (api as ICoreServerAPI)?.Server.IsDedicated == true;
            Hooks.Apply(Mod.Logger, clientHooks);
        }

        bool clientHooks;

        public override void Dispose()
        {
            if (api == null) return;
            if (api.Side == EnumAppSide.Server && serverInstance == this) serverInstance = null;
            if (api.Side == EnumAppSide.Client && clientInstance == this) clientInstance = null;
            if (capi != null)
            {
                if (waterRenderer != null)
                {
                    capi.Event.UnregisterRenderer(waterRenderer, EnumRenderStage.Before);
                    waterRenderer.Dispose();
                }
                if (celestialRenderer != null)
                {
                    capi.Event.UnregisterRenderer(celestialRenderer, EnumRenderStage.Opaque);
                    celestialRenderer.Dispose();
                }
                if (frameStart != null) capi.Event.UnregisterRenderer(frameStart, EnumRenderStage.Before);
                // (0.4.2) the game keeps its compiled shaders from world to world: leave Sea's code in them switched off,
                // or a world without Sea joined next would draw this one's last waves, hull cut-outs and glints
                WaterRenderer.SwitchOff(capi);
                Hooks.RequestShaderReload = null;
            }
            Hooks.Remove(clientHooks);
        }

        // ---------------------------------------------------------------- API

        /// <summary>Is Sea's wind (a provider's, or an admin's fixed one) deciding the wind at this time? Otherwise it's vanilla's.</summary>
        public bool Controls(double hours) => Model?.Decides(hours) ?? provider != null;

        /// <summary>
        /// The wind at a place now: an admin's fixed wind, else the provider's (gusts included), else vanilla's
        /// strength, blowing east like vanilla's.
        /// </summary>
        public Wind WindAt(double x, double z) => WindAt(x, z, Hours);

        /// <summary>The wind at a place and game time (vanilla's is only known now).</summary>
        Wind WindAt(double x, double z, double hours)
        {
            var m = Model;
            if (m != null && m.Decides(hours)) return m.At(x, z, hours);
            var p = provider;
            if (p != null) return p.At(x, z, hours, true);
            return new Wind(api.World.BlockAccessor.GetWindSpeedAt(new Vec3d(x, api.World.SeaLevel, z)).X, 0);
        }

        /// <summary>The waves at a place now.</summary>
        public SeaState SeaAt(double x, double z) => SeaAt(x, z, Hours);

        SeaState SeaAt(double x, double z, double hours)
        {
            var m = Model;
            // (without a provider too: the sea follows the config's steady wind, the same on every side; WindModel.SeaWind)
            if (m != null) return m.SeaAt(x, z, hours);
            // no model yet: a sea fully built for the wind right away
            var w = WindAt(x, z, hours);
            double s = w.Strength, hgt = (0.1 + 2.4 * s * s) * Config.WaveScale;
            return new SeaState(hgt, s > 1e-6 ? w.X / s : 1, s > 1e-6 ? w.Z / s : 0, 2.5 + 3.2 * Math.Sqrt(hgt));
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
                {
                    Changed();
                    provider = new WindProvider(name, windAt, prevailingAt);
                    var m = Model;
                    if (m != null) m.Provider = provider;
                    Changed();
                }
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
                Changed();
                provider = null;
                var m = Model;
                if (m != null) m.Provider = null;
                Changed();
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
            lock (providerSync)
            {
                Changed();
                model.Provider = provider;
                Model = model;
                Changed();
            }
        }

        /// <summary>
        /// (0.4.2) The sea's past may read differently now (a provider, a fixed wind, a model): wave places resample.
        /// Called before and after each change, so a sample worked out across one (from another thread) is seen to be
        /// and worked out again at its place's next call (<see cref="WavePlace.Sample"/>).
        /// </summary>
        void Changed() => System.Threading.Interlocked.Increment(ref revision);
        long revision;

        // ---------------------------------------------------------------- waves

        /// <summary>
        /// Seconds on the clock the waves run by, the same on the server and every client (the server's running
        /// time; clients follow it smoothly). On clients it's read once a frame, before anything is drawn.
        /// </summary>
        public double WaveTime => api.Side == EnumAppSide.Server ? sapi.World.ElapsedMilliseconds / 1000.0 : clientWaveTime;

        /// <summary>Client: how far the local wave clock still is from the server's (seconds), and whether it heard from it yet.</summary>
        public double WaveClockError => clock.Error;
        public bool WaveClockSynced => clock.Synced;

        /// <summary>
        /// Bring a place (a ship, the camera) to the current wave time, with the sea state and wind at (x, z). Keep
        /// one <see cref="WavePlace"/> per thing that rides or looks at the waves and call this once a tick or frame.
        /// </summary>
        public void SampleWaves(WavePlace place, double x, double z) => SampleWaves(place, x, z, WaveTime);

        /// <summary>
        /// The same at wave time t. Both ends of each second's blend are taken at their own sample's game hour (the
        /// server's anchors), not whenever this caller got here, and (0.4.2) at fixed grid points around the place
        /// (<see cref="WavePlace.Sample"/>), not wherever it was: a ship new to the place, one that missed a second or
        /// sailed in from elsewhere, the server's tick and a client's frame all get the same surface there.
        /// </summary>
        internal void SampleWaves(WavePlace place, double x, double z, double t) =>
            place.Sample(Spectrum, t, x, z, Model?.Shape.RingStart ?? 0, waveSource);

        /// <summary>The wind the waves' chop follows (<see cref="WindModel.SeaWind(double, double, double, bool, long)"/>), as worked out for wave sample n.</summary>
        Wind SeaWindAt(double x, double z, double hours, long n) => Model?.SeaWind(x, z, hours, true, n) ?? WindAt(x, z, hours);

        /// <summary>The sea state as worked out for wave sample n (an admin's change of the fixed wind counts from its sample on).</summary>
        SeaState SeaAt(double x, double z, double hours, long n)
        {
            var m = Model;
            return m != null ? m.SeaAt(x, z, hours, n) : SeaAt(x, z, hours);
        }

        /// <summary>(0.4.2) What wave places sample: the sea state and wind, at the sample hours every side agrees on.</summary>
        sealed class WaveSource : IWaveSource
        {
            readonly SeaSystem sys;
            public WaveSource(SeaSystem sys) { this.sys = sys; }

            public double HoursOf(long n, double t)
            {
                double rate = sys.GameHoursPerWaveSecond, s = WavePlace.SampleSeconds;
                // the estimate (the calendar now, worked back to the sample) only counts until the server's anchors arrive
                return sys.sampleHours.At(n, sys.Hours - (t - n * s) * rate, rate * s);
            }

            public SeaState SeaAt(double x, double z, double hours, long n) => sys.SeaAt(x, z, hours, n);
            public Wind WindAt(double x, double z, double hours, long n) => sys.SeaWindAt(x, z, hours, n);
            // (the model's changes and the anchors' together: either may change what a sample reads)
            public long Revision => System.Threading.Interlocked.Read(ref sys.revision) + sys.sampleHours.Version;
        }

        readonly WaveSource waveSource;

        /// <summary>The game hour of each wave sample, the same on every side (the server's anchors).</summary>
        readonly SampleHours sampleHours = new();
        /// <summary>Anchors are made this many samples ahead, so clients have them before they're in force.</summary>
        const int AnchorLead = 3;

        /// <summary>Server: from `ahead` samples on, the sample hours follow the calendar as it is now (where and how fast).</summary>
        void PublishAnchor(int ahead)
        {
            double t = WaveTime;
            sampleHours.Add(SampleHours.Make(t, Hours, GameHoursPerWaveSecond, ahead, ++anchorSeq), WavePlace.SampleIndex(t));
        }

        long anchorSeq;

        /// <summary>Game hours per second of wave time (the calendar's speed; it runs faster while everyone sleeps).</summary>
        double GameHoursPerWaveSecond
        {
            get
            {
                var cal = api.World.Calendar;
                return Math.Max(0, cal.SpeedOfTime * cal.CalendarSpeedMul) / 3600.0;
            }
        }

        /// <summary>
        /// How much of the open-sea waves reaches this place: 1 on the open ocean, down to a fifth in sheltered water
        /// (lakes, inland), the same divisor the water shader applies there (from the world's ocean map).
        /// </summary>
        public double WaveFactorAt(double x, double z)
        {
            try
            {
                var ba = api.World.BlockAccessor;
                int rs = ba.RegionSize;
                int cx = (int)Math.Floor(x / 32), cz = (int)Math.Floor(z / 32);
                var om = ba.GetMapRegion((int)Math.Floor(cx * 32.0 / rs), (int)Math.Floor(cz * 32.0 / rs))?.OceanMap;
                if (om == null || om.InnerSize <= 0) return 0.2;
                // like the chunk tesselator: the map at the chunk's corners, blended across the chunk
                float mx = (float)GameMath.Mod(cx * 32, rs) / rs * om.InnerSize, mz = (float)GameMath.Mod(cz * 32, rs) / rs * om.InnerSize;
                float tl = om.GetIntLerpedCorrectly(mx, mz), tr = om.GetIntLerpedCorrectly(mx + 1, mz);
                float bl = om.GetIntLerpedCorrectly(mx, mz + 1), br = om.GetIntLerpedCorrectly(mx + 1, mz + 1);
                double oceanity = (byte)GameMath.BiLerp(tl, tr, bl, br, (float)((x - cx * 32) / 32), (float)((z - cz * 32) / 32)) / 255.0;
                return 1 / (5 * Math.Max(0.2, 1 - oceanity));
            }
            catch (Exception)
            {
                return 0.2;
            }
        }

        /// <summary>
        /// Height of the open-sea surface above its rest level at a world position, now (sheltered water scaled
        /// like the shader does). On a client it's the drawn surface near the camera.
        /// </summary>
        public double WaveHeightAt(double x, double z)
        {
            double h;
            if (api.Side == EnumAppSide.Client) h = CameraWaves.HeightAt(x, z);
            else
            {
                // (0.4.2) a probe per grid cell asked about lately, so ships far apart don't make one probe start over at
                // every call; and one caller at a time (ships may ask from other threads)
                var key = ((long)Math.Floor(x / WavePlace.GridCell), (long)Math.Floor(z / WavePlace.GridCell));
                lock (probes)
                {
                    if (!probes.TryGetValue(key, out var probe))
                    {
                        if (probes.Count >= MaxProbes)
                        {
                            var oldest = key;
                            long used = long.MaxValue;
                            foreach (var kv in probes) if (kv.Value.Used < used) { used = kv.Value.Used; oldest = kv.Key; }
                            probes.Remove(oldest);
                        }
                        probes[key] = probe = new Probe();
                    }
                    probe.Used = ++probeUses;
                    SampleWaves(probe.Place, x, z);
                    h = probe.Place.HeightAt(x, z);
                }
            }
            return h * WaveFactorAt(x, z);
        }

        sealed class Probe { public readonly WavePlace Place = new(); public long Used; }
        readonly System.Collections.Generic.Dictionary<(long, long), Probe> probes = new();
        long probeUses;
        const int MaxProbes = 32;

        void BuildSpectrum(long key, double circumference, double waveScale)
        {
            if (Spectrum == null || Spectrum.Key != key || Spectrum.Circumference != circumference || Spectrum.ChopScale != waveScale)
                Spectrum = new WaveSpectrum(key, circumference, waveScale);
        }

        /// <summary>Client: register something with hulls in the water; Sea keeps the waves out of them. Any time (from any thread), even before Sea started.</summary>
        public void AddWaterClipSource(IWaterClipSource source)
        {
            if (source == null) return;
            lock (drySync) { if (Array.IndexOf(waterSources, source) < 0) waterSources = waterSources.Append(source).ToArray(); }
        }

        public void RemoveWaterClipSource(IWaterClipSource source)
        {
            lock (drySync) { waterSources = waterSources.Where(s => s != source).ToArray(); }
        }

        /// <summary>(0.4.2) The registered hull sources, as of now (a copy is made on every change, so drawing never sees one change).</summary>
        internal IWaterClipSource[] WaterSources => waterSources;
        volatile IWaterClipSource[] waterSources = Array.Empty<IWaterClipSource>();

        /// <summary>
        /// Both sides: register something with dry places where the world has water (a ship mod's hulls, rooms below
        /// the waterline). There the camera isn't under water (no fog, tint or muffled sound) and creatures can breathe.
        /// Any time, even before Sea started. For the water not to be drawn there, also mark its clips
        /// <see cref="WaterClipBox.Dry"/>.
        /// </summary>
        public void AddDrySpaces(IDrySpaces source)
        {
            lock (drySync) { if (Array.IndexOf(drySpaces, source) < 0) drySpaces = drySpaces.Append(source).ToArray(); }
        }

        public void RemoveDrySpaces(IDrySpaces source)
        {
            lock (drySync) { drySpaces = drySpaces.Where(s => s != source).ToArray(); }
        }

        /// <summary>Is this world position dry though the world has water there (inside a hull)? Safe from any thread.</summary>
        public bool IsDry(double x, double y, double z)
        {
            var list = drySpaces;
            for (int i = 0; i < list.Length; i++)
            {
                try { if (list[i].IsDry(x, y, z)) return true; }
                catch (Exception) { }
            }
            return false;
        }

        /// <summary>(0.3.5) Will this world position be dry at any moment in the next seconds (<see cref="IDrySpaces.IsDryWithin"/>)? Safe from any thread.</summary>
        public bool IsDryWithin(double x, double y, double z, double seconds)
        {
            var list = drySpaces;
            for (int i = 0; i < list.Length; i++)
            {
                try { if (list[i].IsDryWithin(x, y, z, seconds)) return true; }
                catch (Exception) { }
            }
            return false;
        }

        public int DrySpaceSources => drySpaces.Length;

        volatile IDrySpaces[] drySpaces = Array.Empty<IDrySpaces>();
        readonly object drySync = new();

        WorldShape MakeShape(double ringStart, double circumference, double daysPerYear, double hoursPerDay)
        {
            var world = api.World;
            return new WorldShape
            {
                RingStart = ringStart,
                Circumference = circumference,
                Latitude = z => world.Calendar.OnGetLatitude(z),
                DaysPerYear = daysPerYear,
                HoursPerDay = hoursPerDay
            };
        }

        // ---------------------------------------------------------------- server

        public override void StartServerSide(ICoreServerAPI api)
        {
            sapi = api;
            serverInstance = this;
            try { Config = (api.LoadModConfig<SeaConfig>(ConfigFile) ?? new SeaConfig()).Checked(); }
            catch (Exception e) { Mod.Logger.Error("Bad ModConfig/{0}, using defaults: {1}", ConfigFile, e.Message); Config = new SeaConfig(); }
            api.StoreModConfig(Config, ConfigFile);

            serverChannel = api.Network.GetChannel(ChannelName);
            // Cylinder World sets up its ring at WorldReady
            api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, BuildServerModel);
            api.Event.PlayerJoin += p =>
            {
                if (sentParams != null) serverChannel.SendPacket(sentParams, p);
                SendClock(p);
            };
            api.Event.RegisterGameTickListener(_ =>
            {
                // the calendar as it is now (its speed changes while players sleep), in force a few seconds from now
                if (Model != null) PublishAnchor(AnchorLead);
                foreach (IServerPlayer p in api.World.AllOnlinePlayers)
                    if (p.ConnectionState == EnumClientState.Playing) SendClock(p);
            }, ClockPacketMs);
            api.Event.GameWorldSave += SaveOverride;
            // the calendar jumped (/time set) or changed speed (sleeping): a new anchor now, sent at once
            api.Event.RegisterGameTickListener(_ =>
            {
                if (Model == null || !SampleHours.Drifted(sampleHours.Newest, WaveTime, Hours)) return;
                PublishAnchor(AnchorLead);
                foreach (IServerPlayer p in api.World.AllOnlinePlayers)
                    if (p.ConnectionState == EnumClientState.Playing) SendClock(p);
            }, 1000);

            SeaCommands.Register(api, this);
        }

        void BuildServerModel()
        {
            RegionSize = sapi.World.BlockAccessor.RegionSize;
            var cal = sapi.World.Calendar;
            ReadRing(out double ringStart, out double circumference);
            long key = WindKey();
            var shape = MakeShape(ringStart, circumference, cal.DaysPerYear, cal.HoursPerDay);
            var model = new WindModel(shape, Config);

            model.Override = LoadOverride();
            model.History = LoadHistory();
            UseModel(model);
            BuildSpectrum(key, circumference, Config.WaveScale);
            sampleHours.Clear();
            PublishAnchor(0);
            sentParams = new SeaParamsPacket
            {
                Key = key, Config = Config, RingStart = ringStart, Circumference = circumference,
                DaysPerYear = cal.DaysPerYear, HoursPerDay = cal.HoursPerDay, Override = model.Override, History = model.History
            };
            serverChannel.BroadcastPacket(sentParams);

            Mod.Logger.Notification("Sea ready{0}; wind from {1}", circumference > 0 ? $", waves wrapping every {circumference:0} blocks from x={ringStart:0}" : "",
                WindProviderName ?? $"vanilla (no wind provider yet; the sea follows a steady {model.SteadySeaWind})");
        }

        /// <summary>
        /// (0.3.6) The world's wind key (since 0.4.0 it only picks the wave trains' phases): random, made once and kept in the save. It used to be a mix of the seed, which
        /// a client could undo (the mix is invertible) to learn the seed; the key is sent to every client.
        /// </summary>
        long WindKey()
        {
            var save = sapi.WorldManager.SaveGame;
            byte[] data = save.GetData(WindKeyKey);
            if (data != null && data.Length == 8) return BitConverter.ToInt64(data, 0);
            long key = BitConverter.ToInt64(System.Security.Cryptography.RandomNumberGenerator.GetBytes(8), 0);
            save.StoreData(WindKeyKey, BitConverter.GetBytes(key));
            return key;
        }

        /// <summary>The wave clock as it will read when the packet arrives (half the ping later).</summary>
        void SendClock(IServerPlayer p)
        {
            double t = WaveTime, ping = p.Ping;   // (NaN while the player has no connection)
            serverChannel.SendPacket(new SeaClockPacket
            {
                Seconds = t + (double.IsFinite(ping) ? Math.Clamp(ping, 0, 2) / 2 : 0),
                Anchors = sampleHours.All
            }, p);
        }

        /// <summary>Cylinder World's ring (read by reflection, so Sea doesn't depend on it).</summary>
        void ReadRing(out double ringStart, out double circumference)
        {
            ringStart = circumference = 0;
            try
            {
                var core = sapi.ModLoader.Systems.FirstOrDefault(s => s.GetType().FullName == "CylinderWorld.CylinderWorldCore");
                var p = core?.GetType().GetProperty("Params", BindingFlags.Public | BindingFlags.Instance)?.GetValue(core);
                if (p == null) return;
                var t = p.GetType();
                circumference = Convert.ToDouble(t.GetProperty("Circumference")?.GetValue(p) ?? 0.0);
                ringStart = Convert.ToDouble(t.GetField("JumpInset")?.GetValue(p) ?? 0);
                if (circumference <= 0) ringStart = circumference = 0;
            }
            catch (Exception e)
            {
                Mod.Logger.Warning("Cylinder World is installed but its ring couldn't be read ({0}); the wind won't line up at the seam", e.Message);
                ringStart = circumference = 0;
            }
        }

        /// <summary>
        /// Fix the wind everywhere (null = back to the provider's or vanilla's), tell every client, remember it in the
        /// save. It decides Sea's wind and sea (ships, waves); vanilla's weather is the provider's (Earth's) to drive.
        /// It takes over on this side at once, and the sea's past is rewritten: no trace of the fixed winds before it is
        /// kept (for tests and mods that want a known sea now; an admin's change is <see cref="ChangeOverride"/>).
        /// </summary>
        public void SetOverride(WindOverride ov)
        {
            var m = Model;
            if (m == null || sentParams == null) return;
            ov = WindOverride.Checked(ov);
            if (ov != null) ov.ActiveSample = 0;
            Changed();   // (before and after: a wave sample worked out across the change is worked out again)
            m.History = Array.Empty<WindOverride>();
            m.Override = ov;
            Publish(m);
        }

        /// <summary>
        /// (0.4.2) Change the fixed wind as an admin does (/sea setwind, /sea clearwind): it takes over at the wave
        /// sample a few seconds ahead (<see cref="AnchorLead"/>), on the server and every client alike, whose game hour
        /// is <see cref="NextActivation"/>; the fixed wind before it ends there, and the sea it raised dies down over the
        /// next game hours rather than vanishing (<see cref="WindOverride.Change"/>). Give the new one's hours from
        /// <see cref="NextActivation"/>.
        /// </summary>
        public void ChangeOverride(WindOverride ov)
        {
            var m = Model;
            if (m == null || sentParams == null) return;
            double t = WaveTime;
            long now = WavePlace.SampleIndex(t), n = now + AnchorLead;
            WindOverride current = m.Override;
            WindOverride[] history = m.History;
            // (ended ones are dropped by the hour of the oldest sample still blended, not the calendar: after a /time set
            // the samples follow it a few seconds later)
            double oldest = Math.Min(Hours, waveSource.HoursOf(now - SampleHours.Behind, t));
            int dropped = WindOverride.Change(ref current, ref history, ov, n, waveSource.HoursOf(n, t), oldest);
            if (dropped > 0) Mod.Logger.Warning("More than {0} fixed-wind changes within 17 game hours: the oldest {1} no longer shape the sea", WindOverride.MaxHistory, dropped);
            Changed();
            m.History = history;   // (first: until the new one's sample, the old one ended there reads as before)
            m.Override = current;
            Publish(m);
        }

        /// <summary>
        /// (0.4.2) The game hour of the wave sample a fixed wind changed now takes over at (a few samples ahead, so
        /// every client hears of it first).
        /// </summary>
        public double NextActivation
        {
            get
            {
                double t = WaveTime;
                return waveSource.HoursOf(WavePlace.SampleIndex(t) + AnchorLead, t);
            }
        }

        void Publish(WindModel m)
        {
            Changed();
            sentParams.Override = m.Override;
            sentParams.History = m.History;
            serverChannel.BroadcastPacket(sentParams);
        }

        WindOverride LoadOverride()
        {
            byte[] data = sapi.WorldManager.SaveGame.GetData(OverrideKey);
            if (data == null || data.Length == 0) return null;
            try { return SerializerUtil.Deserialize<WindOverride>(data)?.Restarted(); }
            catch (Exception e) { Mod.Logger.Warning("Could not read the saved fixed wind: {0}", e.Message); return null; }
        }

        WindOverride[] LoadHistory()
        {
            byte[] data = sapi.WorldManager.SaveGame.GetData(HistoryKey);
            if (data == null || data.Length == 0) return Array.Empty<WindOverride>();
            try { return Checked(SerializerUtil.Deserialize<WindOverrideHistory>(data)?.Items, true); }
            catch (Exception e) { Mod.Logger.Warning("Could not read the saved ended fixed winds: {0}", e.Message); return Array.Empty<WindOverride>(); }
        }

        /// <summary>The usable ones (restarted: from a save), at most <see cref="WindOverride.MaxHistory"/>, the newest.</summary>
        static WindOverride[] Checked(WindOverride[] items, bool restarted)
        {
            if (items == null) return Array.Empty<WindOverride>();
            var ok = items.Select(o => restarted ? o?.Restarted() : WindOverride.Checked(o)).Where(o => o != null).ToArray();
            return ok.Length > WindOverride.MaxHistory ? ok[^WindOverride.MaxHistory..] : ok;
        }

        void SaveOverride()
        {
            var m = Model;
            if (m == null) return;
            var save = sapi.WorldManager.SaveGame;
            var ov = m.Override;
            save.StoreData(OverrideKey, ov == null ? Array.Empty<byte>() : SerializerUtil.Serialize(ov));
            save.StoreData(HistoryKey, SerializerUtil.Serialize(new WindOverrideHistory { Items = m.History }));
        }

        // ---------------------------------------------------------------- client

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            clientInstance = this;
            api.Network.GetChannel(ChannelName)
                .SetMessageHandler<SeaParamsPacket>(OnParams)
                .SetMessageHandler<SeaClockPacket>(p =>
                {
                    if (!double.IsFinite(p.Seconds)) return;
                    clock.OnServerTime(p.Seconds, LocalSeconds);
                    long now = WavePlace.SampleIndex(clock.Now(LocalSeconds));
                    if (p.Anchors != null) foreach (var a in p.Anchors) sampleHours.Add(a, now);
                });
            // if a water shader won't compile with Sea's code on this machine, Sea steps down and asks for this
            Hooks.RequestShaderReload = () => api.Event.EnqueueMainThreadTask(() => api.Shader.ReloadShaders(), "sea-shaders");

            frameStart = new FrameStart(this);
            api.Event.RegisterRenderer(frameStart, EnumRenderStage.Before, "sea-waves");
            waterRenderer = new WaterRenderer(api, this);
            api.Event.RegisterRenderer(waterRenderer, EnumRenderStage.Before, "sea-water");
            celestialRenderer = new CelestialRenderer(api, this);
            api.Event.RegisterRenderer(celestialRenderer, EnumRenderStage.Opaque, "sea-celestial");
            api.Event.RegisterGameTickListener(_ => UpdateLocalWind(), 250);
            SeaCommands.RegisterClient(api, this);

            // the water shaders were compiled before our patch existed: recompile them with Sea's code
            api.Event.LevelFinalize += () =>
            {
                if (!Hooks.ShaderPatched || WaterRenderer.Injected) return;
                api.Shader.ReloadShaders();
                if (!WaterRenderer.Injected) Mod.Logger.Warning("Could not add Sea's code to the water shaders; waves may show inside ships");
            };
        }

        void OnParams(SeaParamsPacket p)
        {
            RegionSize = capi.World.BlockAccessor.RegionSize;
            Config = (p.Config ?? new SeaConfig()).Checked();
            var ov = WindOverride.Checked(p.Override);
            var history = Checked(p.History, false);
            var m = Model;
            // the same inputs again (only the fixed wind changed): keep the model (mods bound to it keep it too)
            if (m != null && m.Shape.RingStart == p.RingStart && m.Shape.Circumference == p.Circumference && sentKey == p.Key && SameConfig(m.Config, Config))
            {
                Changed();
                m.History = history;
                m.Override = ov;
                Changed();
                return;
            }
            sentKey = p.Key;
            UseModel(new WindModel(MakeShape(p.RingStart, p.Circumference, p.DaysPerYear, p.HoursPerDay), Config) { Override = ov, History = history });
            BuildSpectrum(p.Key, p.Circumference, Config.WaveScale);
        }

        double LocalSeconds => capi.InWorldEllapsedMilliseconds / 1000.0;
        FrameStart frameStart;

        /// <summary>First thing every frame: the wave clock moves on and the waves at the camera are worked out.</summary>
        void OnFrameStart(float dt)
        {
            clock.Advance(dt);
            clientWaveTime = clock.Now(LocalSeconds);
            var ep = capi.World?.Player?.Entity;
            if (ep != null) playerDimension = ep.Pos.Dimension;
            if (ep == null || Model == null) return;
            SampleWaves(CameraWaves, ep.CameraPos.X, ep.CameraPos.Z);
        }

        /// <summary>Client: the dimension the local player is in (0 = the world itself), as of this frame. Safe from any thread.</summary>
        public int PlayerDimension => playerDimension;
        volatile int playerDimension;

        sealed class FrameStart : IRenderer
        {
            readonly SeaSystem sys;
            public FrameStart(SeaSystem sys) { this.sys = sys; }
            /// <summary>After the camera moved (0), before ships (Magelan, 0.1) and the water.</summary>
            public double RenderOrder => 0.05;
            public int RenderRange => 9999;
            public void OnRenderFrame(float dt, EnumRenderStage stage) { if (!sys.capi.IsGamePaused) sys.OnFrameStart(dt); }
            public void Dispose() { }
        }

        long sentKey;

        static bool SameConfig(SeaConfig a, SeaConfig b) =>
            a.WaveScale == b.WaveScale && a.SeaWindFrom == b.SeaWindFrom && a.SeaWindStrength == b.SeaWindStrength;

        /// <summary>(0.4.2) The fixed wind in force now, or null.</summary>
        public WindOverride FixedWindNow => Model?.FixedAt(Hours);

        /// <summary>Client: the wind at the player right now (updated 4× a second).</summary>
        public Wind LocalWind { get; private set; }
        public int DrawnHullCount => waterRenderer?.LastClipCount ?? 0;
        public int DrawnWaveCount => waterRenderer?.LastWaveCount ?? 0;
        public int DrawnDryHullCount => waterRenderer?.LastDryCount ?? 0;

        void UpdateLocalWind()
        {
            var ep = capi.World?.Player?.Entity;
            LocalWind = ep == null ? default : WindAt(ep.Pos.X, ep.Pos.Z);
        }
    }
}
