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
    /// <summary>(SE-0046) The client: its renderers and listeners, the parameters from the server, and the frame start.</summary>
    public partial class SeaSystem
    {
        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;
            clientInstance = this;
            Boats = new NativeBoats(this, api);
            boatFloodRenderer = new BoatFloodRenderer(api, Boats);
            api.Event.RegisterRenderer(boatFloodRenderer, EnumRenderStage.Opaque, "sea-boat-flood");
            api.Network.GetChannel(ChannelName)
                .SetMessageHandler<SeaParamsPacket>(OnParams)
                .SetMessageHandler<SeaClockPacket>(p =>
                {
                    if (!double.IsFinite(p.Seconds)) return;
                    // (SE-0061) before LevelFinalize the in-world clock stands at 0: the server sends the clock again once this
                    // player is playing
                    if (!clientClockRuns) return;
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
            Listen(api.Event.RegisterGameTickListener(_ => UpdateLocalWind(), 250));
            // (0.6.0) spray off breaking surf near the camera
            surfSpray = new SurfSpray(api, this);
            Listen(api.Event.RegisterGameTickListener(_ => { if (Model != null) surfSpray.Tick(); }, 100));
            // (0.6.0) spray and, from a gale, spindrift off the open sea's breaking crests
            crestSpray = new CrestSpray(api, this);
            Listen(api.Event.RegisterGameTickListener(_ => { if (Model != null) crestSpray.Tick(); }, 100));
            // (0.6.0) water running up the beaches
            swash = new SwashRenderer(api, this);
            api.Event.RegisterRenderer(swash, EnumRenderStage.Opaque, "sea-swash");
            SeaCommands.RegisterClient(api, this);
            // (SE-0039) a step-down from an earlier world of this game session still holds: say so in this world's log
            if (Hooks.StepDowns() is string steps)
                api.Logger.Warning("[sea] Sea's shader code is still stepped down from earlier in this game session (a water shader would not compile with it); restart the game to try it again: {0}", steps);
            // (SE-0034, SEA-D76) SheyderMod's screen-space water reflection is skipped while Sea draws the water (Hooks); say so
            if (api.ModLoader.IsModEnabled("sheydermod"))
                api.Logger.Notification("[sea] SheyderMod is installed: Sea draws the water's reflections (sun, moon, sky) and skips SheyderMod's water reflection; SheyderMod's other features stay on");

            // the water shaders were compiled before our patch existed: recompile them with Sea's code
            onLevelFinalize = () =>
            {
                clientClockRuns = true;
                if (!Hooks.ShaderPatched || WaterRenderer.Injected) return;
                api.Shader.ReloadShaders();
                if (!WaterRenderer.Injected) Mod.Logger.Warning("Could not add Sea's code to the water shaders; waves may show inside ships");
            };
            api.Event.LevelFinalize += onLevelFinalize;
        }

        void OnParams(SeaParamsPacket p)
        {
            // (SE-0066) the ring and the calendar's shape as numbers a model can be made of, or the packet is not taken
            if (p == null || !double.IsFinite(p.RingStart) || !double.IsFinite(p.Circumference) || p.Circumference < 0
                || !(p.DaysPerYear > 0) || !double.IsFinite(p.DaysPerYear) || !(p.HoursPerDay > 0) || !double.IsFinite(p.HoursPerDay))
            {
                Mod.Logger.Warning("[sea] The server sent parameters Sea can't use (ring {0} from {1}, {2} days of {3} hours); keeping what it had",
                    p?.Circumference, p?.RingStart, p?.DaysPerYear, p?.HoursPerDay);
                return;
            }
            RegionSize = capi.World.BlockAccessor.RegionSize;
            Config = (p.Config ?? new SeaConfig()).Checked();
            var ov = WindOverride.Checked(p.Override);
            var history = Checked(p.History, false);
            var m = Model;
            // the same inputs again (only the fixed wind changed): keep the model (mods bound to it keep it too)
            // (SE-0019) the calendar's shape too: days per year and hours per day reach the model only when it's made
            if (m != null && m.Shape.RingStart == p.RingStart && m.Shape.Circumference == p.Circumference && sentKey == p.Key && SameConfig(m.Config, Config)
                && m.Shape.DaysPerYear == p.DaysPerYear && m.Shape.HoursPerDay == p.HoursPerDay)
            {
                Change(() =>
                {
                    m.History = history;
                    m.Override = ov;
                });
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
            UseCost.Frame();
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
            a.WaveScale == b.WaveScale && a.SeaWindFrom == b.SeaWindFrom && a.SeaWindStrength == b.SeaWindStrength
            && a.StormHeightScale == b.StormHeightScale;

        /// <summary>(0.4.2) The fixed wind in force now, or null.</summary>
        public WindOverride FixedWindNow => Model?.FixedAt(Hours, NowSample);

        /// <summary>Client: the wind at the player right now (updated 4× a second).</summary>
        public Wind LocalWind { get; private set; }
        public int DrawnHullCount => waterRenderer?.LastClipCount ?? 0;
        /// <summary>(SE-0070) Blocks per pixel per block of distance on this client (the drawn water's sea_Pixel; 0 before the first frame).</summary>
        public double PixelSize => waterRenderer?.Pixel ?? 0;
        /// <summary>(SE-0040) What the client's coast map has uploaded so far (for `.sea coast`).</summary>
        public (int Refreshes, int Whole, int Tiles, double MB) CoastUploads => waterRenderer?.CoastUploads ?? default;
        public int DrawnWaveCount => waterRenderer?.LastWaveCount ?? 0;
        public int DrawnDryHullCount => waterRenderer?.LastDryCount ?? 0;

        void UpdateLocalWind()
        {
            var ep = capi.World?.Player?.Entity;
            LocalWind = ep == null ? default : WindAt(ep.Pos.X, ep.Pos.Z);
        }
    }
}
