using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Sea
{
    /// <summary>Sea-only vanilla sailboat water intake, buoyancy and dry volume. No ship-mod state or references.</summary>
    public sealed class NativeBoats : IWaterClipSource, IDrySpaces
    {
        public const string FloodKey = "seaBoatFlood";
        readonly SeaSystem sea;
        readonly ICoreAPI api;
        readonly ICoreClientAPI client;
        readonly object sync = new();
        volatile EntityBoat[] boats = Array.Empty<EntityBoat>();
        readonly Hull definition;
        sealed class TickState { public double Seconds; public long BailAt = long.MinValue / 2; public readonly WavePlace Waves = new(); }
        static readonly ConditionalWeakTable<EntityBoat, TickState> ticks = new();

        public NativeBoats(SeaSystem sea, ICoreAPI api)
        {
            this.sea = sea; this.api = api; client = api as ICoreClientAPI;
            definition = api.Assets.Get(new AssetLocation("sea", "config/sailboat-waterclip.json")).ToObject<Hull>();
            if (definition.DryProfile?.Length != 128) throw new InvalidOperationException("Invalid vanilla sailboat water outline");
            sea.AddDrySpaces(this);
            if (client != null) sea.AddWaterClipSource(this);
        }
        public EntityBoat[] Boats => boats;
        public static float Fill(EntityBoat boat)
        {
            float f = boat.WatchedAttributes.GetFloat(FloodKey);
            return float.IsFinite(f) ? Math.Clamp(f, 0, 1) : 0;
        }
        public static void Initialized(EntityBoat __instance)
        {
            if (!BoatWaves.Sailed(__instance)) return;
            var water = SeaSystem.For(__instance.Api.Side)?.Boats;
            if (water == null) return;
            lock (water.sync) water.boats = water.boats.Append(__instance).ToArray();
        }
        public static void Despawned(EntityBoat __instance)
        {
            var water = SeaSystem.For(__instance.Api.Side)?.Boats;
            if (water == null) return;
            lock (water.sync) water.boats = water.boats.Where(b => b != __instance).ToArray();
        }
        public WaterClipBox Clip(EntityBoat boat, bool flood = true)
        {
            double yaw = boat.Pos.Yaw;
            var d = definition;
            var c = new WaterClipBox {
                X = boat.Pos.X + Math.Cos(yaw) * d.CentreRight, Z = boat.Pos.Z - Math.Sin(yaw) * d.CentreRight,
                WaterY = boat.Pos.Y + d.WaterY, Yaw = yaw, HalfLength = d.HalfLength, HalfWidth = d.HalfWidth,
                CentreOffset = d.CentreOffset, Profile = d.Profile, Dry = true, DryProfile = d.DryProfile,
                DryLevel0 = d.DryLevel0, DryLevelStep = d.DryLevelStep, DryDepth = d.DryDepth, DryHeight = d.DryHeight
            };
            BoatWaves.ApplyToClip(boat, ref c);
            if (flood && Fill(boat) > 0.001f)
            { c.Flooded = true; c.FloodY = FloodY(boat, c); }
            if (Fill(boat) >= 0.98f) c.Dry = false;
            return c;
        }
        public static Vec3d Point(WaterClipBox c, double along, double up, double across)
        {
            double cp = 1 / Math.Sqrt(1 + c.SlopeAlong * c.SlopeAlong), sp = c.SlopeAlong * cp;
            double tr = -c.SlopeAcross * cp, cr = 1 / Math.Sqrt(1 + tr * tr), sr = tr * cr;
            double forward = along * cp - up * cr * sp + across * sr * sp;
            double dy = along * sp + up * cr * cp - across * sr * cp, side = up * sr + across * cr;
            return new Vec3d(c.X + forward * Math.Sin(c.Yaw) - side * Math.Cos(c.Yaw), c.WaterY + dy,
                c.Z + forward * Math.Cos(c.Yaw) + side * Math.Sin(c.Yaw));
        }
        public static double FloodY(EntityBoat boat, WaterClipBox clip)
            => Point(clip, clip.CentreOffset, -clip.DryDepth + Fill(boat) * (clip.DryDepth + clip.DryHeight), 0).Y;

        public void AddWaterClips(List<WaterClipBox> clips)
        {
            foreach (var b in boats)
                if (b.Alive && b.Pos.Dimension == 0 && (b.Swimming || b.FeetInLiquid)) clips.Add(Clip(b));
        }
        public bool IsDry(double x, double y, double z)
        {
            foreach (var b in boats)
                if (b.Alive && b.Pos.Dimension == 0 && Math.Abs(x - b.Pos.X) < 12 && Math.Abs(z - b.Pos.Z) < 12
                    && (b.Swimming || b.FeetInLiquid) && Clip(b).InDryHull(x, y, z)) return true;
            return false;
        }
        // The native mask writes flat depth as well as Sea cutting the displaced surface. Keep only Sea's volume
        // when it is available, and retain the native fallback if the driver's dry shader cannot compile.
        public static bool NativeMask(EntityBehaviorHideWaterSurface __instance)
            => !BoatWaves.Sailed(__instance.entity as EntityBoat) || !Hooks.DryHullsInjected;

        public static void Density(EntityBoat __instance, ref float __result)
        { if (BoatWaves.Sailed(__instance)) __result = (float)BoatWaveMath.FloodDensity(Fill(__instance)); }
        public static void Draft(EntityBoat __instance, ref double __result)
        { if (BoatWaves.Sailed(__instance)) __result -= BoatWaveMath.FloodDraft(Fill(__instance)); }

        public static void Tick(EntityBoat __instance, float dt)
        {
            if (!BoatWaves.Sailed(__instance) || __instance.Api.Side != EnumAppSide.Server || __instance.Pos.Dimension != 0) return;
            var water = SeaSystem.For(EnumAppSide.Server)?.Boats;
            if (water?.sea.Spectrum == null) return;
            var t = ticks.GetValue(__instance, _ => new TickState());
            t.Seconds += Math.Clamp(dt, 0, 0.5);
            if (t.Seconds < 0.2) return;
            double seconds = t.Seconds; t.Seconds = 0;
            water.Intake(__instance, seconds, t.Waves);
        }
        sealed class DriftState { public long At = long.MinValue / 2; public double X, Z; public readonly WavePlace Waves = new(); }
        static readonly ConditionalWeakTable<EntityBoat, DriftState> drifts = new();

        /// <summary>
        /// (0.6.0) Rafts, sailboats and Ships' rowboats afloat are carried by the surf's currents (SeaSystem.CurrentAt: along the
        /// beach, and out through a rip). After the game's own motion, on the server's tick and the client's frame alike:
        /// under way the current adds to the boat's speed through the water; left alone the boat is taken up to the
        /// current's speed over a couple of seconds. Ships' own ships (not boats) do their own.
        /// </summary>
        public static void Drift(EntityBoat __instance, float dt)
        {
            var b = __instance;
            if (!BoatWaves.Supported(b) || !b.Swimming || b.Pos.Dimension != 0) return;
            var sea = SeaSystem.For(b.Api.Side);
            if (sea?.Spectrum == null || !sea.SurfOn) return;
            var d = drifts.GetValue(b, _ => new DriftState());
            long now = b.World.ElapsedMilliseconds;
            if (now - d.At >= 250)
            {
                d.At = now;
                sea.SampleWaves(d.Waves, b.Pos.X, b.Pos.Z);
                (d.X, d.Z) = sea.CurrentAt(d.Waves, b.Pos.X, b.Pos.Z);
            }
            if (d.X == 0 && d.Z == 0) return;
            // m/s to the game's motion (blocks per 1/60 s)
            double vx = d.X / 60, vz = d.Z / 60;
            var m = b.Pos.Motion;
            if (b.ForwardSpeed != 0) { m.X += vx; m.Z += vz; return; }
            double f = 1 - Math.Exp(-Math.Clamp(dt, 0, 0.5) / 2);
            m.X += (vx - m.X) * f; m.Z += (vz - m.Z) * f;
        }

        void Intake(EntityBoat b, double dt, WavePlace waves)
        {
            float before = Fill(b);
            if (before >= 1 || !(b.Swimming || b.FeetInLiquid)) return;
            var c = Clip(b, false);
            sea.SampleWaves(waves, b.Pos.X, b.Pos.Z);
            double over = 0;
            for (int a = -1; a <= 1; a++)
                for (int side = -1; side <= 1; side += 2)
                {
                    double along = a * c.HalfLength * 0.65;
                    var p = Point(c, c.CentreOffset + along, c.DryHeight + 0.025,
                        side * (c.DryHalfWidthAt(along / c.HalfLength, c.DryHeight) + 0.025));
                    if (Surface(p.X, b.Pos.Y, p.Z, 12, out double level))
                        over = Math.Max(over, level + waves.HeightAt(p.X, p.Z) * sea.WaveFactorAt(p.X, p.Z) - p.Y);
                }
            float after = (float)BoatWaveMath.FloodStep(before, over, dt);
            if (after != before) b.WatchedAttributes.SetFloat(FloodKey, after);
        }
        public bool Surface(double x, double y, double z, int reach, out double height)
        {
            var ba = api.World.BlockAccessor; var p = new BlockPos(0); height = 0;
            int start = Math.Clamp((int)Math.Floor(y), 1, ba.MapSizeY - 2);
            for (int d = 0; d <= reach; d++)
                for (int side = 0; side < (d == 0 ? 1 : 2); side++)
                {
                    int by = start + (side == 0 ? d : -d);
                    if (by < 1 || by >= ba.MapSizeY - 1) continue;
                    p.Set((int)Math.Floor(x), by, (int)Math.Floor(z));
                    var block = ba.GetBlock(p, BlockLayersAccess.Fluid);
                    if (block?.IsLiquid() != true || block.BlockMaterial == EnumBlockMaterial.Lava) continue;
                    p.Y++; if (ba.GetBlock(p, BlockLayersAccess.Fluid)?.IsLiquid() == true) continue;
                    height = by + Math.Clamp(block.LiquidLevel, 1, 8) / 8.0; return true;
                }
            return false;
        }
        /// <summary>Only mounted native boats use this correction; other liquids and unrelated camera code stay native.</summary>
        public bool CameraDepth(Vec3d eye, out float depth)
            => EyeDepth(client?.World.Player?.Entity, eye, out depth);

        /// <summary>Use the same liquid surface for the mounted client's view and the server's breathing check.</summary>
        public bool EyeDepth(EntityAgent player, Vec3d eye, out float depth)
        {
            depth = 0;
            if (player?.MountedOn?.Entity is not EntityBoat b || !BoatWaves.Supported(b) || b.Pos.Dimension != 0) return false;
            if (sea.IsDry(eye.X, eye.Y, eye.Z)) return true;
            if (BoatWaves.Sailed(b) && Fill(b) > 0.001)
            {
                var hull = Clip(b, false); hull.Dry = true;
                if (hull.InDryHull(eye.X, eye.Y, eye.Z))
                { depth = (float)Math.Max(0, FloodY(b, hull) - eye.Y); return true; }
            }
            if (!Surface(eye.X, eye.Y, eye.Z, 12, out double level)) return false;
            double wave = api.Side == EnumAppSide.Client
                ? sea.CameraWaves.HeightAt(eye.X, eye.Z) * sea.WaveFactorAt(eye.X, eye.Z)
                : sea.WaveHeightAt(eye.X, eye.Z);
            depth = (float)Math.Max(0, level + wave - eye.Y);
            return true;
        }
        public static bool Bail(EntityBoat __instance, EntityAgent byEntity, ItemSlot itemslot, EnumInteractMode mode)
        {
            if (!BoatWaves.Sailed(__instance) || mode != EnumInteractMode.Interact || byEntity?.Controls.ShiftKey != true
                || itemslot?.Itemstack?.Block is not BlockLiquidContainerBase container || !container.IsTopOpened
                || container.GetContent(itemslot.Itemstack) != null) return true;
            if (__instance.Api.Side == EnumAppSide.Server)
            {
                var t = ticks.GetValue(__instance, _ => new TickState());
                long now = __instance.World.ElapsedMilliseconds;
                if (now - t.BailAt >= 1000)
                { __instance.WatchedAttributes.SetFloat(FloodKey, Math.Max(0, Fill(__instance) - 0.04f)); t.BailAt = now; }
            }
            return false;
        }
        public static void Info(EntityBoat __instance, ref string __result)
        {
            if (BoatWaves.Sailed(__instance) && Fill(__instance) > 0.001f)
                __result += "\n" + Lang.Get("sea:boat-flood", (int)Math.Round(Fill(__instance) * 100));
        }
        public static bool Pickup(EntityBoat __instance, ref bool __result)
        {
            if (!BoatWaves.Sailed(__instance) || Fill(__instance) <= 0.001f) return true;
            __result = false; return false; // Native pickup makes a fresh item and would silently erase stored water.
        }
        public sealed class Hull
        {
            public float WaterY, CentreOffset, CentreRight, HalfLength, HalfWidth, DryDepth, DryHeight, DryLevel0, DryLevelStep;
            public float[] Profile, DryProfile;
        }
    }
}
