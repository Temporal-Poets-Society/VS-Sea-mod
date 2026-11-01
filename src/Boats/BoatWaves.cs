using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace Sea
{
    /// <summary>
    /// Vanilla boat integration. Still-water collision/buoyancy remains native; hulls, seats and their camera ride
    /// Sea's surface. No Ships assembly is referenced. Ships' EntityRowBoat calls the same native base methods.
    /// </summary>
    public static class BoatWaves
    {
        sealed class State
        {
            public readonly WavePlace Waves = new();
            public readonly System.Func<double, double, double> Height;
            public SeaSystem Sea;
            public BoatWaterplane Hull;
            public BoatWavePose Pose;
            public double Time = double.NaN, X, Z, Yaw, Fill;
            public bool Applied;
            public float MountX, MountY, MountZ, RenderX, RenderY, RenderZ;
            public double Trim;
            public State() { Height = (x, z) => Waves.HeightAt(x, z) * Sea.WaveFactorAt(x, z); }
        }
        static readonly ConditionalWeakTable<EntityBoat, State> states = new();

        public static bool Supported(EntityBoat boat)
        {
            var c = boat?.Code;
            return c != null && (c.Domain == "game" && (c.Path.StartsWith("boat-raft-", StringComparison.Ordinal)
                || c.Path.StartsWith("boat-sailed-", StringComparison.Ordinal))
                || c.Domain == "ships" && c.Path.StartsWith("rowboat-", StringComparison.Ordinal));
        }

        public static bool Sailed(EntityBoat boat) => boat?.Code?.Domain == "game" && boat.Code.Path.StartsWith("boat-sailed-", StringComparison.Ordinal);

        public static BoatWaterplane Waterplane(EntityBoat boat)
        {
            // Native multibox bounds are in collision-local coordinates, turned by yaw + pi.
            // Dimensions from the three supported entities' native multibox definitions. Unlike CollisionBox,
            // these bounds do not change when the boat turns (and are available on either side).
            if (boat.Code.Domain == "ships") return new BoatWaterplane(3.9, 1.4, 2);
            return boat.Code.Path.StartsWith("boat-raft-", StringComparison.Ordinal)
                ? new BoatWaterplane(1.5, 0.75) : new BoatWaterplane(4.4, 1.4, 1.5);
        }

        public static bool TryGetPose(EntityBoat boat, out BoatWavePose pose)
        {
            pose = default;
            if (!Supported(boat) || !boat.Swimming && !boat.FeetInLiquid || boat.OnGround || boat.Pos.Dimension != 0) return false;
            var sea = SeaSystem.For(boat.Api.Side);
            if (sea?.Spectrum == null) return false;
            var s = states.GetValue(boat, _ => new State());
            double time = sea.WaveTime;
            double fill = Sailed(boat) ? NativeBoats.Fill(boat) : 0;
            if (s.Sea != sea || s.Time != time || s.X != boat.Pos.X || s.Z != boat.Pos.Z || s.Yaw != boat.Pos.Yaw || s.Fill != fill)
            {
                s.Sea = sea; s.Time = time; s.X = boat.Pos.X; s.Z = boat.Pos.Z; s.Yaw = boat.Pos.Yaw; s.Fill = fill;
                s.Hull = Waterplane(boat);
                // A loaded sailboat cannot rise instantaneously with every crest. A short deterministic response
                // delay lets green water overtake the gunwale; both sides sample the same past wave time.
                sea.SampleWaves(s.Waves, s.X, s.Z, time - (Sailed(boat) ? 0.65 : 0));
                var fit = BoatWaveMath.Sample(s.Height, s.X, s.Z, s.Yaw, s.Hull);
                double pivot = (boat.SelectionBox?.Y2 ?? 1.2) / 2;
                double follow = Sailed(boat) ? Math.Clamp((1 - NativeBoats.Fill(boat)) / 0.1, 0, 1) : 1;
                s.Pose = new BoatWavePose((fit.Heave - pivot * (1 - Math.Cos(fit.Roll) * Math.Cos(fit.Pitch))) * follow, fit.Pitch * follow, fit.Roll * follow);
            }
            pose = s.Pose;
            return true;
        }

        public static void BeforeRenderBoat(EntityBoat __instance)
        {
            if (!states.TryGetValue(__instance, out var s) || !s.Applied) return;
            __instance.mountAngle.Set(s.MountX, s.MountY, s.MountZ);
            if (__instance.Properties.Client.Renderer is EntityShapeRenderer r)
            { r.xangle = s.RenderX; r.yangle = s.RenderY; r.zangle = s.RenderZ; }
            s.Applied = false;
        }

        // Postfix on the native method: retain the native turning/forward lean, replace its unrelated rocking.
        public static void RenderBoat(EntityBoat __instance, float dt)
        {
            if (!TryGetPose(__instance, out var pose) || __instance.Properties.Client.Renderer is not EntityShapeRenderer r) return;
            var s = states.GetValue(__instance, _ => new State());
            s.MountX = __instance.mountAngle.X; s.MountY = __instance.mountAngle.Y; s.MountZ = __instance.mountAngle.Z;
            s.RenderX = r.xangle; s.RenderY = r.yangle; s.RenderZ = r.zangle; s.Applied = true;
            float turn = r.xangle - __instance.mountAngle.X, lean = r.zangle - __instance.mountAngle.Z;
            __instance.mountAngle.Set((float)pose.Roll, 0, (float)pose.Pitch);
            r.xangle = (float)pose.Roll + turn; r.yangle = 0; r.zangle = (float)pose.Pitch + lean;
            if (Sailed(__instance) && __instance.AnimManager?.Animator != null)
            {
                var wind = s.Sea.WindAt(__instance.Pos.X, __instance.Pos.Z);
                double target = BoatWaveMath.SailTrim(wind.X, wind.Z, __instance.Pos.Yaw, s.Trim);
                s.Trim += (target - s.Trim) * (1 - Math.Exp(-Math.Min(dt, 0.1) * 3));
                if (!__instance.AnimManager.IsAnimationActive("seasailtrim")) __instance.AnimManager.StartAnimation("seasailtrim");
                var animation = __instance.AnimManager.GetAnimationState("seasailtrim");
                if (animation != null)
                { animation.CurrentFrame = (float)(1 + s.Trim / 65); animation.BlendedWeight = 1; animation.EasingFactor = 1; }
            }
        }

        public static void ModelMatrix(EntityShapeRenderer __instance, Entity entity)
        {
            if (entity is EntityBoat boat && TryGetPose(boat, out var pose)) __instance.ModelMat[13] += (float)pose.Heave;
        }

        public static void SeatPosition(EntityRideableSeat __instance, EntityPos __result)
        {
            if (__instance.Entity is EntityBoat boat && TryGetPose(boat, out var pose)) __result.Y += pose.Heave;
        }

        public static void SelectionTransform(EntityBehaviorSelectionBoxes __instance, Matrixf mvmat)
        {
            if (__instance.entity is EntityBoat boat && TryGetPose(boat, out var pose)) mvmat.Translate(0, pose.Heave, 0);
        }

        public static void SailMotion(EntityBoat __instance, ref Vec2d __result)
        {
            if (__instance.Code?.Domain != "game" || !__instance.Code.Path.StartsWith("boat-sailed-", StringComparison.Ordinal)
                || !__instance.Swimming && !__instance.FeetInLiquid || __instance.Pos.Dimension != 0 || __instance.WatchedAttributes.GetInt("sailPosition") <= 0) return;
            var sea = SeaSystem.For(__instance.Api.Side);
            if (sea?.Model == null) return;
            var wind = sea.WindAt(__instance.Pos.X, __instance.Pos.Z);
            // The native method supplies only sail thrust in X while unfurled; steering remains in Y.
            __result.X *= BoatWaveMath.SailDrive(wind.X, wind.Z, __instance.Pos.Yaw);
            __result.X *= Math.Max(0, 1 - NativeBoats.Fill(__instance));
        }

        /// <summary>Move a boat's existing dry outline with the same pose as its hull and passengers.</summary>
        public static void ApplyToClip(EntityBoat boat, ref WaterClipBox clip)
        {
            if (!TryGetPose(boat, out var p)) return;
            double sn = Math.Sin(boat.Pos.Yaw), cs = Math.Cos(boat.Pos.Yaw);
            double pitch = p.Pitch, roll = p.Roll;
            if (boat.Properties.Client.Renderer is EntityShapeRenderer renderer)
            { pitch = renderer.zangle; roll = renderer.xangle; }
            double sp = Math.Sin(pitch), cp = Math.Cos(pitch), sr = Math.Sin(roll), cr = Math.Cos(roll);
            double pivot = (boat.SelectionBox?.Y2 ?? 1.2) / 2, h = clip.WaterY - boat.Pos.Y - pivot;
            // Apply the renderer's complete pose about its pivot, including an off-centre source outline.
            double dx = clip.X - boat.Pos.X, dz = clip.Z - boat.Pos.Z;
            double along = dx * sn + dz * cs, right = dx * cs - dz * sn;
            double localX = -cp * along - sp * h, localY = -sp * along + cp * h;
            double turnedY = cr * localY - sr * right, turnedZ = sr * localY + cr * right;
            clip.X = boat.Pos.X - sn * localX + cs * turnedZ;
            clip.Z = boat.Pos.Z - cs * localX - sn * turnedZ;
            clip.WaterY = boat.Pos.Y + p.Heave + pivot + turnedY;
            double fx = sn * cp - cs * sr * sp, fz = cs * cp + sn * sr * sp;
            clip.Yaw = Math.Atan2(fx, fz);
            clip.SlopeAlong = (float)(-cr * sp / Math.Sqrt(fx * fx + fz * fz));
            double nx = sn * sp + cs * sr * cp, nz = cs * sp - sn * sr * cp, ny = cr * cp;
            clip.SlopeAcross = clip.CalmSlopeAcross = (float)((nx * Math.Cos(clip.Yaw) - nz * Math.Sin(clip.Yaw)) / ny);
        }
    }
}
