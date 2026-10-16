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
    /// Sea's mod system, on both sides. The server builds the wind model from its config, the world seed and the
    /// world's shape, and sends clients the same inputs so they compute the same wind. Both sides then answer
    /// vanilla's wind lookups (trees, waves, rain, wind chill, sound) with Sea's wind strength.
    ///
    /// The waves are the same on every side too: a fixed library of wave trains (<see cref="WaveSpectrum"/>) whose
    /// strengths follow the sea state, run on a clock the server shares (<see cref="WaveClock"/>), so a ship riding
    /// them moves the same for everyone.
    ///
    /// Other mods: <c>api.ModLoader.GetModSystem&lt;SeaSystem&gt;()</c>, then <see cref="WindAt"/>,
    /// <see cref="SeaAt"/>, <see cref="SampleWaves"/>, and on clients <see cref="AddWaterClipSource"/>.
    /// </summary>
    public class SeaSystem : ModSystem
    {
        public const string ChannelName = "sea";
        const string ConfigFile = "sea.json";
        const string OverrideKey = "sea-override";
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
        readonly WavePlace serverProbe = new();
        double probeX = double.NaN, probeZ = double.NaN;
        const int ClockPacketMs = 5000;

        public override double ExecuteOrder() => 0.15;

        public override void Start(ICoreAPI api)
        {
            this.api = api;
            api.Network.RegisterChannel(ChannelName).RegisterMessageType<SeaParamsPacket>().RegisterMessageType<SeaClockPacket>();
            Hooks.Apply(Mod.Logger);
            api.Event.RegisterGameTickListener(_ => WatchLatitudes(), 1000);
        }

        // ---------------------------------------------------------------- latitudes

        double latitudePrint = double.NaN;
        WindModel latitudeModel;

        /// <summary>
        /// (0.3.6) Storms are rolled by latitude. On a client the game's latitudes can arrive after the model was built
        /// (vanilla swaps the data behind a function it installed earlier; a map mod may swap the function), so once a
        /// second the latitudes are fingerprinted, and the storms worked out from other ones are forgotten.
        /// </summary>
        void WatchLatitudes()
        {
            var m = Model;
            var cal = api.World?.Calendar;
            if (m == null || cal?.OnGetLatitude == null) return;
            double print;
            try { print = LatitudePrint(cal, api.World.BlockAccessor.MapSizeZ); }
            catch (Exception) { return; }
            if (m == latitudeModel && print.Equals(latitudePrint)) return;
            // a new model, or new latitudes (the model may have rolled storms before this first look)
            m.Storms.Forget();
            latitudeModel = m;
            latitudePrint = print;
        }

        /// <summary>The latitudes at a few places down the map, folded into one number.</summary>
        static double LatitudePrint(IGameCalendar cal, int mapSizeZ)
        {
            double print = 0, size = Math.Max(1024, mapSizeZ);
            for (int k = 0; k < 9; k++) print = print * 1.6180339887 + cal.OnGetLatitude(size * (k + 0.37) / 9);
            return print;
        }

        public override void Dispose()
        {
            if (api == null) return;
            if (api.Side == EnumAppSide.Server && serverInstance == this) serverInstance = null;
            if (api.Side == EnumAppSide.Client && clientInstance == this)
            {
                clientInstance = null;
                Hooks.LocalWind = null;
            }
            if (waterRenderer != null) capi?.Event.UnregisterRenderer(waterRenderer, EnumRenderStage.Opaque);
            if (frameStart != null) capi?.Event.UnregisterRenderer(frameStart, EnumRenderStage.Before);
            Hooks.Remove();
        }

        // ---------------------------------------------------------------- API

        /// <summary>Is Sea deciding the wind right now (a wind model is on, or an admin fixed the wind)?</summary>
        public bool Controls(double hours)
        {
            var m = Model;
            if (m == null) return false;
            if (m.Mode != WindMode.Off) return true;
            var ov = m.Override;
            return ov != null && ov.Covers(hours);
        }

        /// <summary>The wind at a place now. Without a model (or with WindModel "Off") it's vanilla's strength, blowing east like vanilla's.</summary>
        public Wind WindAt(double x, double z) => WindAt(x, z, Hours);

        /// <summary>The wind at a place and game time (vanilla's is only known now).</summary>
        Wind WindAt(double x, double z, double hours)
        {
            if (Controls(hours)) return Model.At(x, z, hours);
            return new Wind(api.World.BlockAccessor.GetWindSpeedAt(new Vec3d(x, api.World.SeaLevel, z)).X, 0);
        }

        /// <summary>The waves at a place now.</summary>
        public SeaState SeaAt(double x, double z) => SeaAt(x, z, Hours);

        SeaState SeaAt(double x, double z, double hours)
        {
            var m = Model;
            // (with WindModel "Off" too: the sea follows Sea's steady winds, the same on every side; WindModel.SeaWind)
            if (m != null) return m.SeaAt(x, z, hours);
            // no model yet: a sea fully built for vanilla's wind strength right away, running east like it
            var w = WindAt(x, z, hours);
            double hgt = (0.1 + 2.4 * w.Strength * w.Strength) * Config.WaveScale;
            return new SeaState(hgt, 1, 0, 2.5 + 3.2 * Math.Sqrt(hgt));
        }

        /// <summary>
        /// Replace Earth's belts with a map's own prevailing winds (e.g. Earth Map's real monthly winds). Call it on
        /// both sides with the same source, any time; null goes back to the belts.
        /// </summary>
        public void SetPrevailingWinds(IPrevailingWinds source)
        {
            prevailingSource = source;
            if (Model != null) Model.PrevailingSource = source;
        }

        IPrevailingWinds prevailingSource;

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
        public void SampleWaves(WavePlace place, double x, double z)
        {
            var spec = Spectrum;
            double t = WaveTime;
            if (spec == null || !place.Due(spec, t))
            {
                place.Update(spec, t, default, default);
                return;
            }
            // both ends of this second's blend, each at its own sample's game hour (the server's anchors), not whenever
            // this caller got here: a ship new to the place, one that missed a second, the server's tick and a client's
            // frame all get the same surface there
            long n = WavePlace.SampleIndex(t);
            double s = WavePlace.SampleSeconds, perSample = GameHoursPerWaveSecond * s;
            double estimate = Hours - (t - n * s) * GameHoursPerWaveSecond;   // only until the server's anchors arrive
            double h1 = sampleHours.At(n, estimate, perSample), h0 = sampleHours.At(n - 1, estimate - perSample, perSample);
            place.Update(spec, t, SeaAt(x, z, h0), SeaWindAt(x, z, h0), SeaAt(x, z, h1), SeaWindAt(x, z, h1));
        }

        /// <summary>The wind the waves' chop follows (<see cref="WindModel.SeaWind"/>).</summary>
        Wind SeaWindAt(double x, double z, double hours) => Model?.SeaWind(x, z, hours) ?? WindAt(x, z, hours);

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
            WavePlace place;
            if (api.Side == EnumAppSide.Client) place = CameraWaves;
            else
            {
                // one probe on the server, re-sampled where it's asked
                place = serverProbe;
                if (double.IsNaN(probeX) || (x - probeX) * (x - probeX) + (z - probeZ) * (z - probeZ) > 64 * 64)
                {
                    probeX = x;
                    probeZ = z;
                    serverProbe.Reset();
                }
                SampleWaves(serverProbe, probeX, probeZ);
            }
            return place.HeightAt(x, z) * WaveFactorAt(x, z);
        }

        void BuildSpectrum(long key, double circumference, double waveScale)
        {
            if (Spectrum == null || Spectrum.Key != key || Spectrum.Circumference != circumference || Spectrum.ChopScale != waveScale)
                Spectrum = new WaveSpectrum(key, circumference, waveScale);
        }

        /// <summary>Client: register something with hulls in the water; Sea keeps the waves out of them. Any time, even before Sea started.</summary>
        public void AddWaterClipSource(IWaterClipSource source) { if (!waterSources.Contains(source)) waterSources.Add(source); }
        public void RemoveWaterClipSource(IWaterClipSource source) => waterSources.Remove(source);

        readonly System.Collections.Generic.List<IWaterClipSource> waterSources = new();

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

        void OnGetWindSpeed(Vec3d pos, ref Vec3d windSpeed)
        {
            var m = Model;
            if (m == null || api.World?.Calendar == null) return;
            double h = api.World.Calendar.TotalHours;
            if (!Controls(h)) return;
            windSpeed.X = Hooks.AltitudeScale(m.At(pos.X, pos.Z, h).Strength, pos.Y, api.World.SeaLevel);
        }

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
            // after vanilla's handler (subscribed in Start), so ours has the last word
            api.Event.OnGetWindSpeed += OnGetWindSpeed;
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
            var model = new WindModel(shape, Config, key) { PrevailingSource = prevailingSource };

            byte[] data = sapi.WorldManager.SaveGame.GetData(OverrideKey);
            if (data != null && data.Length > 0)
            {
                try { model.Override = WindOverride.Checked(SerializerUtil.Deserialize<WindOverride>(data)); }
                catch (Exception e) { Mod.Logger.Warning("Could not read the saved fixed wind: {0}", e.Message); }
            }
            Model = model;
            BuildSpectrum(key, circumference, Config.WaveScale);
            sampleHours.Clear();
            PublishAnchor(0);
            sentParams = new SeaParamsPacket
            {
                Key = key, Config = Config, RingStart = ringStart, Circumference = circumference,
                DaysPerYear = cal.DaysPerYear, HoursPerDay = cal.HoursPerDay, Override = model.Override
            };
            serverChannel.BroadcastPacket(sentParams);

            Mod.Logger.Notification("Wind model {0}{1}; storms {2}", Config.Mode,
                circumference > 0 ? $", wrapping every {circumference:0} blocks from x={ringStart:0}" : "",
                Config.Mode == WindMode.Earthlike && Config.StormFrequency > 0 ? "on" : "off");
        }

        /// <summary>
        /// (0.3.6) The world's wind key: random, made once and kept in the save. It used to be a mix of the seed, which
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
            double t = WaveTime;
            serverChannel.SendPacket(new SeaClockPacket
            {
                Seconds = t + Math.Clamp(p.Ping, 0, 2) / 2,
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

        /// <summary>Fix the wind everywhere (null = back to the model), tell every client, remember it in the save.</summary>
        public void SetOverride(WindOverride ov)
        {
            if (Model == null || sentParams == null) return;
            ov = WindOverride.Checked(ov);
            Model.Override = ov;
            sentParams.Override = ov;
            serverChannel.BroadcastPacket(sentParams);
        }

        void SaveOverride()
        {
            if (Model == null) return;
            var ov = Model.Override;
            sapi.WorldManager.SaveGame.StoreData(OverrideKey, ov == null ? Array.Empty<byte>() : SerializerUtil.Serialize(ov));
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
                    clock.OnServerTime(p.Seconds, LocalSeconds);
                    long now = WavePlace.SampleIndex(clock.Now(LocalSeconds));
                    if (p.Anchors != null) foreach (var a in p.Anchors) sampleHours.Add(a, now);
                });
            api.Event.OnGetWindSpeed += OnGetWindSpeed;
            // if a water shader won't compile with Sea's code on this machine, Sea steps down and asks for this
            Hooks.RequestShaderReload = () => api.Event.EnqueueMainThreadTask(() => api.Shader.ReloadShaders(), "sea-shaders");

            frameStart = new FrameStart(this);
            api.Event.RegisterRenderer(frameStart, EnumRenderStage.Before, "sea-waves");
            waterRenderer = new WaterRenderer(api, this, waterSources);
            api.Event.RegisterRenderer(waterRenderer, EnumRenderStage.Opaque, "sea-water");
            api.Event.RegisterGameTickListener(_ => UpdateLocalWind(), 250);
            SeaCommands.RegisterClient(api, this);

            // the water and particle shaders were compiled before our patch existed: recompile them with Sea's code
            api.Event.LevelFinalize += () =>
            {
                if (Hooks.ShaderPatched && (!WaterRenderer.Injected || !Hooks.RainStreaksInjected)) api.Shader.ReloadShaders();
                if (Hooks.ShaderPatched && !WaterRenderer.Injected) Mod.Logger.Warning("Could not add Sea's code to the water shaders; waves may show inside ships");
                if (Hooks.ShaderPatched && !Hooks.RainStreaksInjected) Mod.Logger.Warning("Could not add Sea's rain streaks to the particle shader; rain keeps leaning east");
            };
        }

        void OnParams(SeaParamsPacket p)
        {
            RegionSize = capi.World.BlockAccessor.RegionSize;
            Config = (p.Config ?? new SeaConfig()).Checked();
            var ov = WindOverride.Checked(p.Override);
            var m = Model;
            // the same inputs again (only the fixed wind changed): keep the model and its storm cache
            if (m != null && m.Shape.RingStart == p.RingStart && m.Shape.Circumference == p.Circumference && sentKey == p.Key && SameConfig(m.Config, Config))
            {
                m.Override = ov;
                return;
            }
            sentKey = p.Key;
            var model = new WindModel(MakeShape(p.RingStart, p.Circumference, p.DaysPerYear, p.HoursPerDay), Config, p.Key) { Override = ov, PrevailingSource = prevailingSource };
            Model = model;
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
            a.WindModel == b.WindModel && a.PrevailingStrength == b.PrevailingStrength && a.Variability == b.Variability && a.Gusts == b.Gusts &&
            a.StormFrequency == b.StormFrequency && a.StormRadius == b.StormRadius && a.StormSpeed == b.StormSpeed &&
            a.SeasonShift == b.SeasonShift && a.WaveScale == b.WaveScale;

        /// <summary>Client: the wind at the player right now (updated 4× a second).</summary>
        public Wind LocalWind { get; private set; }
        public int DrawnHullCount => waterRenderer?.LastClipCount ?? 0;
        public int DrawnWaveCount => waterRenderer?.LastWaveCount ?? 0;
        public int DrawnDryHullCount => waterRenderer?.LastDryCount ?? 0;

        void UpdateLocalWind()
        {
            Hooks.ClientMs = capi.ElapsedMilliseconds;
            var ep = capi.World?.Player?.Entity;
            if (ep == null || Model == null || !Controls(Hours))
            {
                Hooks.LocalWind = null;
                // (.sea's "wind here": vanilla's strength then, not the last of Sea's)
                LocalWind = ep == null ? default : WindAt(ep.Pos.X, ep.Pos.Z);
                return;
            }
            double h = Hours;
            var w = Model.At(ep.Pos.X, ep.Pos.Z, h);
            LocalWind = w;
            double s = w.Strength;
            float dx = s < 1e-6 ? 1 : (float)(w.X / s), dz = s < 1e-6 ? 0 : (float)(w.Z / s);
            Hooks.LocalWind = new[] { dx, dz, (float)s, (float)Hooks.AltitudeScale(s, ep.Pos.Y, capi.World.SeaLevel) };
        }
    }
}
