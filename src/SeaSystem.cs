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
    public partial class SeaSystem : ModSystem
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
        internal NativeBoats Boats;
        BoatFloodRenderer boatFloodRenderer;
        CelestialRenderer celestialRenderer;
        public string CelestialStatus => celestialRenderer?.Status ?? "Celestial presentation unavailable.";
        /// <summary>The client's sky and night presentation (null on the server, or before the world is ready).</summary>
        public CelestialRenderer Celestial => celestialRenderer;
        /// <summary>Client-only diagnostic switch for breaking-wave foam.</summary>
        public FoamView FoamView;
        public string FoamStatus => waterRenderer?.FoamStatus ?? "Foam input unavailable.";

        /// <summary>Retired wake declaration API; retained for compiled companions. Wakes are unavailable.</summary>
        public bool SetWakeHull(long entityId, double length, double beam, double draft, double centreOffset = 0) => false;
        public void RemoveWakeHull(long entityId) { }

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

        public SeaSystem()
        {
            // (the corners of the 64-block grid, worked out once for every place in a cell: the camera and ships alike;
            // nothing is kept while a change is being made)
            waveSource = new SharedCorners(new WaveSource(this), () =>
            {
                long r = System.Threading.Interlocked.Read(ref revision);
                return Model == null || (r & 1) != 0 ? long.MinValue : r;
            });
        }

        public override double ExecuteOrder() => 0.15;

        public override void Start(ICoreAPI api)
        {
            this.api = api;
            mainThread = Environment.CurrentManagedThreadId;
            LogOnce.Reset();
            api.Network.RegisterChannel(ChannelName).RegisterMessageType<SeaParamsPacket>().RegisterMessageType<SeaClockPacket>();
            // the client's patches only by the client (or a dedicated server, which has none): see Hooks
            clientHooks = api.Side == EnumAppSide.Client || (api as ICoreServerAPI)?.Server.IsDedicated == true;
            Hooks.Apply(Mod.Logger, clientHooks);
            coast = new CoastField(api);
            coastLookup = coast.Lookup;
            // (0.6.0) the coast is scanned a little at a time on the main thread and solved on a background task: tiles
            // asked for by the water's map, by the waves sampled for ships and boats, and by the open contracts
            Listen(api.Event.RegisterGameTickListener(_ => { if (SurfOn) coast.Work(api.Side == EnumAppSide.Server ? 3 : 2); }, 50));
            // (the server's tiles around each player, as the client's water map asks for those in view: a boat there gets
            // the same coast on both sides from the start, not the open sea until its first lookup's tile is solved)
            if (api is ICoreServerAPI coastApi)
                Listen(api.Event.RegisterGameTickListener(_ =>
                {
                    if (!SurfOn) return;
                    foreach (var p in coastApi.World.AllOnlinePlayers)
                        if (p?.Entity?.Pos is { } pos && pos.Dimension == 0) coast.Ask(pos.X, pos.Z, CoastAround);
                }, 1000));
            // (SE-0017) vanilla's wind where the players are, read on the main thread for any other
            Listen(api.Event.RegisterGameTickListener(_ => RefreshVanillaWind(), 1000));
            contracts = new SeaContracts(this, api, Mod.Logger);
            contracts.Publish();
        }

        bool clientHooks;

        // (SE-0018) the tick listeners and events registered here, taken off again in Dispose
        readonly System.Collections.Generic.List<long> listeners = new();
        long Listen(long id) { listeners.Add(id); return id; }
        PlayerDelegate onPlayerJoin;
        Action onLevelFinalize;

        /// <summary>(0.6.0) Blocks around each player whose coast tiles the server keeps solved.</summary>
        const double CoastAround = 128;
        SeaContracts contracts;

        /// <summary>(0.6.0) Sea's open contracts on this side (docs/open-sea-contracts.md), or null before Start.</summary>
        public SeaContracts Contracts => contracts;

        // (0.6.0) for the open contracts: the same answers as the API above, at any game hour or wave time
        internal SeaState SeaAtHours(double x, double z, double hours) => SeaAt(x, z, hours);
        internal Wind WindAtHours(double x, double z, double hours) => WindAt(x, z, hours);
        internal long ContractRevision => StableRevision() + sampleHours.Version;
        internal ICoreAPI Api => api;

        public override void Dispose()
        {
            contracts?.Dispose();
            coast?.Dispose();
            if (api == null) return;
            // (SE-0018) nothing of this world's Sea is called again, should the game keep its event lists
            try
            {
                foreach (long id in listeners) api.Event.UnregisterGameTickListener(id);
                listeners.Clear();
                // (GameWorldSave stays: the server disposes mods before its save on shutdown, which must still keep the
                // fixed wind; ServerMain.Stop runs ServerSystemModHandler's shutdown before ServerSystemLoadAndSaveGame's)
                if (sapi != null && onPlayerJoin != null) sapi.Event.PlayerJoin -= onPlayerJoin;
                if (capi != null && onLevelFinalize != null) capi.Event.LevelFinalize -= onLevelFinalize;
            }
            catch (Exception e) { Mod?.Logger.Warning("Could not take Sea's listeners off: {0}", e.Message); }
            if (api.Side == EnumAppSide.Server && serverInstance == this) serverInstance = null;
            if (api.Side == EnumAppSide.Client && clientInstance == this) clientInstance = null;
            if (capi != null)
            {
                if (boatFloodRenderer != null) { capi.Event.UnregisterRenderer(boatFloodRenderer, EnumRenderStage.Opaque); boatFloodRenderer.Dispose(); }
                if (waterRenderer != null)
                {
                    capi.Event.UnregisterRenderer(waterRenderer, EnumRenderStage.Before);
                    waterRenderer.Dispose();
                }
                if (swash != null) { capi.Event.UnregisterRenderer(swash, EnumRenderStage.Opaque); swash.Dispose(); }
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

    }
}
