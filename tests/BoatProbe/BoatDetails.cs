using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Sea;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.GameContent;

namespace SeaBoatProbe
{
    public partial class Probe
    {
        void SailAsset()
        {
            var shapeToken = JObject.Parse(api.Assets.Get(new AssetLocation("game", "shapes/entity/nonliving/boat/sailed.json")).ToText());
            var entity = JObject.Parse(api.Assets.Get(new AssetLocation("game", "entities/nonliving/boat-sailed.json")).ToText());
            var patches = JArray.Parse(api.Assets.Get(new AssetLocation("sea", "patches/sailboat-trim.json")).ToText());
            foreach (JObject patch in patches)
            {
                var root = patch["file"].ToString().Contains("shapes/") ? shapeToken : entity;
                string[] path = patch["path"].ToString().Split('/').Skip(1).ToArray();
                JToken parent = root;
                for (int i = 0; i < path.Length - 1; i++) parent = parent is JArray arr ? arr[int.Parse(path[i])] : parent[path[i]];
                if (parent is JArray list) list.Add(patch["value"].DeepClone()); else parent[path.Last()] = patch["value"].DeepClone();
            }
            var shape = shapeToken.ToObject<Shape>();
            shape.InitForAnimations(api.Logger, "Sea sail trim probe");
            var animation = shape.Animations.Single(a => a.Code == "seasailtrim");
            animation.GenerateAllFrames(shape.Elements, shape.JointsById, true);
            Check(animation.QuantityFrames == 3 && shape.JointsById.Count > 4, "patched vanilla sail shape generates native animation frames");
            var names = new[] { "SparORIGIN", "SailUnfurled", "SailHalf", "SailFurled" };
            Check(names.All(n => animation.KeyFrames.All(k => k.Elements[n].ForElement == shape.GetElementByName(n))),
                "yard, full sail, half sail and furled bundle resolve to native animated joints");
            Check(names.All(n => shape.GetElementByName(n).RotationOrigin.SequenceEqual(new[] { 2.0, 0.0, 2.0 })),
                "cloth and yard brace about the same mast pivot");
            Check(entity["client"]["animations"].Any(a => a["code"].ToString() == "seasailtrim" && a["animationSpeed"].Value<float>() == 0),
                "native sailboat animation metadata supports explicit wind-driven trim frames");
        }
        void Camera(EntityBoat boat)
        {
            var viewer = new EntityPlayer(); viewer.Pos.SetFrom(boat.Pos);
            var seat = boat.GetBehavior<EntityBehaviorSeatable>().Seats.OfType<EntityRideableSeat>().First();
            AccessTools.PropertySetter(typeof(EntityAgent), nameof(EntityAgent.MountedOn)).Invoke(viewer, new object[] { seat });
            var playerType = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientPlayer");
            var player = (IClientPlayer)RuntimeHelpers.GetUninitializedObject(playerType);
            var wdField = AccessTools.Field(playerType, "worlddata");
            var wd = RuntimeHelpers.GetUninitializedObject(wdField.FieldType);
            AccessTools.Field(wdField.FieldType, "entityplayer").SetValue(wd, viewer); wdField.SetValue(player, wd);
            var world = Proxy.Make<IClientWorldAccessor>(m => m.Name switch {
                "get_Player" => player, "get_BlockAccessor" => api.World.BlockAccessor, _ => null });
            var client = Proxy.Make<ICoreClientAPI>(m => m.Name switch {
                "get_World" => world, "get_Assets" => api.Assets, "get_Side" => EnumAppSide.Client, _ => null });
            var water = new NativeBoats(sea, client);
            var oldClient = AccessTools.Field(typeof(SeaSystem), "clientInstance").GetValue(null);
            var oldBoats = AccessTools.Field(typeof(SeaSystem), "Boats").GetValue(sea);
            var oldWind = sea.Model.Override;
            AccessTools.Field(typeof(SeaSystem), "clientInstance").SetValue(null, sea);
            AccessTools.Field(typeof(SeaSystem), "Boats").SetValue(sea, water);
            var mainType = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientMain");
            var main = RuntimeHelpers.GetUninitializedObject(mainType);
            var camField = AccessTools.Field(mainType, "MainCamera");
            var camera = RuntimeHelpers.GetUninitializedObject(camField.FieldType); camField.SetValue(main, camera);
            var eyeField = AccessTools.Field(camField.FieldType, "CameraEyePos");
            var eye = new Vec3d(boat.Pos.X + 8, 0, boat.Pos.Z);
            eyeField.SetValue(camera, eye);
            var breather = new EntityAgent { World = api.World };
            AccessTools.PropertySetter(typeof(EntityAgent), nameof(EntityAgent.MountedOn)).Invoke(breather, new object[] { seat });
            AccessTools.PropertySetter(typeof(Entity), nameof(Entity.Properties)).Invoke(breather, new object[] { new EntityProperties { EyeHeight = 1.5f, SwimmingEyeHeight = .5f } });
            var breathe = new EntityBehaviorBreathe(breather);
            AccessTools.Field(typeof(EntityBehaviorBreathe), "oxygenTree").SetValue(breathe, new TreeAttribute());
            void NativeBreathing()
            {
                AccessTools.Field(typeof(SeaSystem), "Boats").SetValue(sea, oldBoats);
                try { breathe.Check(); }
                finally { AccessTools.Field(typeof(SeaSystem), "Boats").SetValue(sea, water); }
            }
            try
            {
                OpenOcean = true;
                sea.SetOverride(new WindOverride { Strength = 1.5, StartHours = sea.Hours - 48 });
                water.Surface(eye.X, boat.Pos.Y, eye.Z, 12, out double level);
                double time = sea.WaveTime;
                bool trough = false, crest = false;
                for (int i = 0; i < 500 && (!trough || !crest); i++)
                {
                    FrozenTime = time + i * .2; sea.SampleWaves(sea.CameraWaves, eye.X, eye.Z);
                    double wave = sea.CameraWaves.HeightAt(eye.X, eye.Z);
                    if (wave < -.8 && !trough)
                    {
                        eye.Y = level - .3;
                        object[] depth = { main, .3f }, tint = { main, 100 };
                        AccessTools.Method(typeof(Hooks), "EyesInWaterDepthPostfix").Invoke(null, depth);
                        AccessTools.Method(typeof(Hooks), "EyesInWaterColorShiftPostfix").Invoke(null, tint);
                        Check((float)depth[1] == 0 && (int)tint[1] == 0,
                            "mounted camera above a wave trough loses the false flat-water fog and tint"); trough = true;
                        breather.Pos.SetPos(eye.X, eye.Y - 1.5, eye.Z); NativeBreathing();
                        Check(breathe.HasAir, "actual native breathing keeps air above a storm trough below flat datum");
                    }
                    if (wave > .8 && !crest)
                    {
                        eye.Y = level + .3;
                        object[] depth = { main, 0f }, tint = { main, 0 };
                        AccessTools.Method(typeof(Hooks), "EyesInWaterDepthPostfix").Invoke(null, depth);
                        AccessTools.Method(typeof(Hooks), "EyesInWaterColorShiftPostfix").Invoke(null, tint);
                        Check((float)depth[1] > .4 && (int)tint[1] == 100,
                            "a real wave covering the mounted camera still produces underwater fog and tint"); crest = true;
                        breather.Pos.SetPos(eye.X, eye.Y - 1.5, eye.Z); NativeBreathing();
                        Check(!breathe.HasAir, "actual native breathing detects a covering storm crest above flat datum");
                    }
                }
                Check(trough && crest, "mounted camera regression samples both a storm crest and trough");
            }
            finally
            {
                sea.SetOverride(oldWind); OpenOcean = false; FrozenTime = double.NaN;
                AccessTools.Field(typeof(SeaSystem), "clientInstance").SetValue(null, oldClient);
                AccessTools.Field(typeof(SeaSystem), "Boats").SetValue(sea, oldBoats);
                sea.RemoveDrySpaces(water); sea.RemoveWaterClipSource(water);
            }
        }
    }
}
