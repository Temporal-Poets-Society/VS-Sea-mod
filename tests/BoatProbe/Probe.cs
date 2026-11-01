using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using Sea;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace SeaBoatProbe
{
    public class Config { public bool WithShips; }
    public partial class Probe : ModSystem
    {
        ICoreServerAPI api;
        SeaSystem sea;
        int checks, failures;
        long physicsTick;
        readonly List<EntityBoat> boats = new();
        readonly Dictionary<long, double> first = new();
        void Check(bool ok, string what)
        { checks++; if (!ok) failures++; api.Logger.Notification("BOATCHECK {0} {1}", ok ? "OK" : "FAIL", what); }
        void Guard(Action action)
        { try { action(); } catch (Exception e) { Check(false, e.ToString()); Finish(); } }
        void Finish()
        {
            if (physicsTick != 0) api.Event.UnregisterGameTickListener(physicsTick);
            api.Logger.Notification("BOATCHECK RESULT {0}: {1} checks, {2} failures", failures == 0 ? "PASS" : "FAIL", checks, failures);
            api.Event.RegisterCallback(_ => api.Server.ShutDown(), 250);
        }
        public override bool ShouldLoad(EnumAppSide side) => side == EnumAppSide.Server;
        public override void StartServerSide(ICoreServerAPI api)
        {
            this.api = api;
            api.Event.ServerRunPhase(EnumServerRunPhase.RunGame, () => api.Event.RegisterCallback(_ => Guard(Start), 1500));
        }
        void Start()
        {
            sea = api.ModLoader.GetModSystem<SeaSystem>();
            new Harmony("sea.boatprobe").Patch(AccessTools.PropertyGetter(typeof(SeaSystem), nameof(SeaSystem.WaveTime)),
                postfix: new HarmonyMethod(typeof(Probe), nameof(WaveTime)));
            new Harmony("sea.boatprobe").Patch(AccessTools.Method(typeof(SeaSystem), nameof(SeaSystem.WaveFactorAt)),
                postfix: new HarmonyMethod(typeof(Probe), nameof(OceanFactor)));
            new Harmony("sea.boatprobe").Patch(AccessTools.Method(typeof(SeaSystem), "MeshWaveFactorAt"),
                postfix: new HarmonyMethod(typeof(Probe), nameof(OceanFactor)));
            Check(typeof(SeaSystem).Assembly.GetType("Sea.WakeRenderer") == null && typeof(SeaSystem).Assembly.GetType("Sea.WakeField") == null
                && typeof(SeaSystem).Assembly.GetType("Sea.WakeMap") == null, "wake renderer, field and atlas are absent from the actual shipped assembly");
            Check(typeof(SeaConfig).GetField("WakeEnabled") == null && typeof(SeaConfig).GetField("WakeStrength") == null,
                "wake configuration cannot reactivate the removed feature");
            Check(!Hooks.Active.Contains("wake splash contacts"), "no wake contact patch is installed");
            sea.RemoveWakeHull(-999);
            Check(!sea.SetWakeHull(-999, 8, 3, 1), "retired companion declaration safely reports wakes unavailable");
            bool withShips = api.LoadModConfig<Config>("boat-probe.json")?.WithShips ?? false;
            Check(api.ModLoader.IsModEnabled("ships") == withShips, withShips ? "Ships case explicitly loaded" : "Sea-alone case has no Ships mod");
            Check(!api.ModLoader.IsModEnabled("earth") && !api.ModLoader.IsModEnabled("earthmap"), "no Earth or other wind provider required");
            foreach (string label in new[] { "boat seats ride waves", "sailboat sails by wind", "boats ride waves", "boat hulls heave", "boat interactions ride waves" })
                Check(Hooks.Active.Contains(label), "native patch installed: " + label);
            var p = api.World.DefaultSpawnPosition.AsBlockPos;
            int pending = 0;
            int x0 = (p.X >> 5) * 32, z0 = (p.Z >> 5) * 32;
            for (int x = -1; x <= 1; x++)
                for (int z = -1; z <= 1; z++)
                {
                    pending++;
                    api.WorldManager.LoadChunkColumnPriority((x0 >> 5) + x, (z0 >> 5) + z, new ChunkLoadOptions
                    { KeepLoaded = true, OnLoaded = () => api.Event.EnqueueMainThreadTask(() => { if (--pending == 0) Guard(() => Launch(x0, z0, withShips)); }, "boat-probe-load") });
                }
        }
        void Launch(int bx, int bz, bool withShips)
        {
            var ba = api.World.BlockAccessor;
            int water = api.World.GetBlock(new AssetLocation("game", "water-still-7")).Id;
            int stone = api.World.GetBlock(new AssetLocation("game", "rock-granite")).Id;
            int by = api.WorldManager.MapSizeY - 24;
            var bp = new BlockPos(0);
            for (int x = bx - 20; x <= bx + 40; x++)
                for (int z = bz - 20; z <= bz + 40; z++)
                    for (int y = by; y <= by + 12; y++)
                    {
                        bp.Set(x, y, z);
                        ba.SetBlock(y == by ? stone : 0, bp);
                        ba.SetBlock(y > by && y <= by + 8 ? water : 0, bp, BlockLayersAccess.Fluid);
                    }
            foreach (string code in withShips ? new[] { "game:boat-raft-oak", "game:boat-sailed-oak", "ships:rowboat-oak" }
                : new[] { "game:boat-raft-oak", "game:boat-sailed-oak" })
            {
                var type = api.World.GetEntityType(new AssetLocation(code));
                Check(type != null, "native entity type present: " + code);
                var boat = api.World.ClassRegistry.CreateEntity(type) as EntityBoat;
                Check(boat != null, "native EntityBoat class: " + code);
                boat.Pos.SetPos(bx + 4 + boats.Count * 10, by + 8.6, bz + 5);
                boat.AlwaysActive = true;
                api.World.SpawnEntity(boat);
                boats.Add(boat);
            }
            // Dedicated servers do not schedule native passive physics without nearby clients. Drive that same
            // native physics entry point here; do not fake Swimming or bypass water/collision detection.
            physicsTick = api.Event.RegisterGameTickListener(dt =>
            {
                foreach (var boat in boats) boat.GetBehavior<EntityBehaviorPassivePhysicsMultiBox>().OnPhysicsTick(dt);
            }, 20);
            api.Event.RegisterCallback(_ => Guard(Observe), 2500);
        }
        void Observe()
        {
            FrozenTime = sea.WaveTime;
            foreach (var boat in boats)
            {
                Check(boat.Alive && (boat.Swimming || boat.FeetInLiquid) && !boat.OnGround, $"native physics floats {boat.Code} (state={boat.State}, swimming={boat.Swimming}, wet={boat.FeetInLiquid}, ground={boat.OnGround}, y={boat.Pos.Y:0.000})");
                bool active = BoatWaves.TryGetPose(boat, out var pose);
                Check(active && double.IsFinite(pose.Heave + pose.Pitch + pose.Roll), "Sea samples a finite pose for " + boat.Code);
                first[boat.EntityId] = pose.Heave;
                bool swimming = boat.Swimming;
                boat.Swimming = false;
                Check(BoatWaves.TryGetPose(boat, out _), "water contact follows waves between Swimming ticks: " + boat.Code);
                boat.Swimming = swimming;
                foreach (var seat in boat.GetBehavior<EntityBehaviorSeatable>().Seats.OfType<EntityRideableSeat>())
                {
                    // Server attachment poses are absent, so the native baseline is the boat's own position.
                    double y = seat.SeatPosition.Y, again = seat.SeatPosition.Y;
                    Check(Math.Abs(y - again) < 1e-9, "repeated seat reads do not accumulate heave: " + boat.Code);
                    bool wasSwimming = boat.Swimming;
                    bool wasWet = boat.FeetInLiquid;
                    boat.Swimming = boat.FeetInLiquid = false;
                    double still = seat.SeatPosition.Y;
                    boat.Swimming = wasSwimming;
                    boat.FeetInLiquid = wasWet;
                    Check(Math.Abs(y - still - pose.Heave) < 1e-8, "actual native seat getter follows heave: " + boat.Code);
                }
                RenderPipeline(boat, pose);
                if (!boat.Code.Path.StartsWith("boat-sailed-")) Rowing(boat);
                if (boat.Code.Domain == "ships")
                {
                    var clip = (WaterClipBox)boat.GetType().GetMethod("WaterClip").Invoke(boat, new object[] { boat.Pos.XYZ });
                    Check(Math.Abs(clip.SlopeAlong) + Math.Abs(clip.SlopeAcross) > 1e-8, "Ships rowing boat dry cut tilts with the hull");
                    Check(Math.Abs(clip.WaterY - (boat.Pos.Y + 0.55)) > 1e-6, "Ships rowing boat dry cut rises with the hull");
                }
            }
            FrozenTime = double.NaN;
            Propulsion(boats.First(b => b.Code.Path.StartsWith("boat-sailed-")));
            SailAsset();
            Camera(boats.First(b => BoatWaves.Sailed(b)));
            Flooding(boats.First(b => BoatWaves.Sailed(b)));
            api.Event.RegisterCallback(elapsed => Guard(() =>
            {
                foreach (var boat in boats)
                {
                    Check(BoatWaves.TryGetPose(boat, out var p) && Math.Abs(p.Heave - first[boat.EntityId]) > 1e-5,
                        "wave following changes with the shared wave clock: " + boat.Code);
                    boat.OnGround = true;
                    Check(!BoatWaves.TryGetPose(boat, out _), "beached boat has no artificial wave lift: " + boat.Code);
                    boat.OnGround = false;
                    boat.Pos.Dimension = 1;
                    Check(!BoatWaves.TryGetPose(boat, out _), "other dimensions have no ocean motion: " + boat.Code);
                    boat.Pos.Dimension = 0;
                }
                Finish();
            }), 1200);
        }
        public static double FrozenTime = double.NaN;
        public static bool OpenOcean;
        public static void OceanFactor(ref double __result) { if (OpenOcean) __result = 1; }
        public static void WaveTime(ref double __result) { if (double.IsFinite(FrozenTime)) __result = FrozenTime; }
        void Flooding(EntityBoat boat)
        {
            var water = (NativeBoats)AccessTools.Field(typeof(SeaSystem), "Boats").GetValue(sea);
            Check(water.Boats.Contains(boat) && boat.MaterialDensity == 100 && Math.Abs(boat.SwimmingOffsetY - 1) < 1e-8,
                "dry vanilla sailboat registers its water volume and retains native buoyancy");
            double time = sea.WaveTime;
            var oldWind = sea.Model.Override;
            sea.SetOverride(new WindOverride { Strength = 0, StartHours = sea.Hours - 48 });
            for (int i = 0; i < 30; i++) { FrozenTime = time + i * .2; NativeBoats.Tick(boat, .2f); }
            Check(NativeBoats.Fill(boat) == 0, "native sailboat stays dry in calm water");
            sea.SetOverride(new WindOverride { Strength = 1.5, FromDegrees = 270, StartHours = sea.Hours - 48 });
            OpenOcean = true;
            for (int i = 0; i < 1200; i++) { FrozenTime = time + i * .2; NativeBoats.Tick(boat, .2f); }
            Check(NativeBoats.Fill(boat) > .01, $"1.5 storm ships actual wave water over the gunwale (fill={NativeBoats.Fill(boat):0.000})");
            sea.SetOverride(oldWind); FrozenTime = time; OpenOcean = false;
            boat.WatchedAttributes.SetFloat(NativeBoats.FloodKey, .5f);
            var clip = water.Clip(boat);
            var low = NativeBoats.Point(clip, clip.CentreOffset, -clip.DryDepth + .05, 0);
            var high = NativeBoats.Point(clip, clip.CentreOffset, clip.DryHeight - .05, 0);
            Check(!clip.InDryHull(low.X, low.Y, low.Z) && clip.InDryHull(high.X, high.Y, high.Z), "legitimate water aboard remains wet below its flood plane");
            Check(boat.SwimmingOffsetY < .7 && boat.MaterialDensity > 100, "shipped water deepens the native physics draft and adds weight");
            var draw = AccessTools.TypeByName("Sea.BoatFloodRenderer");
            var mesh = (MeshData)AccessTools.Method(draw, "Build").Invoke(null, new object[] { water.Clip(boat, false), clip.FloodY, boat.Pos.XYZ, 0.0 });
            Check(mesh.VerticesCount > 16 && Enumerable.Range(0, mesh.VerticesCount).All(i => Math.Abs(mesh.xyz[i * 3 + 1] + boat.Pos.Y - clip.FloodY) < 1e-5),
                "stored floodwater produces a level surface mesh inside the hull");
            var dryClip = water.Clip(boat, false); dryClip.Flooded = false;
            Check(Enumerable.Range(0, mesh.VerticesCount).All(i => dryClip.InDryHull(mesh.xyz[i * 3] + boat.Pos.X, mesh.xyz[i * 3 + 1] + boat.Pos.Y, mesh.xyz[i * 3 + 2] + boat.Pos.Z)),
                "every flood mesh edge stays inside the moving hull");
            var bucket = api.World.GetBlock(new AssetLocation("game", "woodbucket"));
            var slot = new DummySlot(new ItemStack(bucket));
            var driver = new Driver(); driver.Controls.ShiftKey = true;
            boat.OnInteract(driver, slot, boat.Pos.XYZ, EnumInteractMode.Interact);
            Check(Math.Abs(NativeBoats.Fill(boat) - .46) < 1e-6 && boat.Alive, "empty-bucket sneak interaction bails instead of picking up the boat");
            boat.OnInteract(driver, slot, boat.Pos.XYZ, EnumInteractMode.Interact);
            Check(Math.Abs(NativeBoats.Fill(boat) - .46) < 1e-6, "bailing cannot bypass the one-second bucket cooldown");
            bool pickedUp = (bool)AccessTools.Method(typeof(EntityBoat), "tryPickup").Invoke(boat, new object[] { driver, EnumInteractMode.Interact });
            Check(!pickedUp && boat.Alive && NativeBoats.Fill(boat) > .45f, "pickup cannot silently erase stored floodwater");
            using (var stream = new System.IO.MemoryStream())
            {
                boat.ToBytes(new System.IO.BinaryWriter(stream), false); stream.Position = 0;
                var copy = new EntityBoat(); copy.FromBytes(new System.IO.BinaryReader(stream), false);
                Check(Math.Abs(NativeBoats.Fill(copy) - .46) < 1e-6, "water aboard survives actual native entity save serialization");
            }
            boat.WatchedAttributes.SetFloat(NativeBoats.FloodKey, 1);
            Check(boat.MaterialDensity > 1000 && (!BoatWaves.TryGetPose(boat, out var pose) || Math.Abs(pose.Heave) < 1e-9),
                "a swamped boat loses reserve buoyancy and wave lift");
            double beforeY = boat.Pos.Y;
            for (int i = 0; i < 100; i++) boat.GetBehavior<EntityBehaviorPassivePhysicsMultiBox>().OnPhysicsTick(.02f);
            Check(boat.Pos.Y < beforeY - .3, "the actual native physics sinks a swamped sailboat");
            boat.Pos.Y = beforeY; boat.Pos.Motion.Set(0, 0, 0);
            boat.WatchedAttributes.SetFloat(NativeBoats.FloodKey, 0); FrozenTime = double.NaN;
        }
        void Rowing(EntityBoat boat)
        {
            var seat = boat.GetBehavior<EntityBehaviorSeatable>().Seats.OfType<EntityBoatSeat>().First(s => s.CanControl);
            var driver = new Driver { EntityId = 987654322, AnimManager = null };
            seat.Passenger = driver;
            AccessTools.Field(typeof(EntityBoat), "CurrentlyControllingEntityId").SetValue(boat, driver.EntityId);
            seat.controls.Forward = true;
            try
            {
                if (boat.Code.Domain == "game")
                {
                    var oar = api.World.Items.FirstOrDefault(i => i.Attributes?["paddlingTool"].AsBool() == true);
                    Check(oar != null, "native raft oar registered with Sea alone");
                    driver.Paddle = new DummySlot(new ItemStack(oar));
                    Check(boat.SeatsToMotion(0.05f).X > 0, "native raft rows with the oar while wave following is active");
                    driver.Paddle = null;
                    Check(boat.SeatsToMotion(0.05f).X == 0, "native raft still requires an oar");
                    driver.Paddle = new DummySlot(new ItemStack(oar));
                }
                else Check(boat.SeatsToMotion(0.05f).X > 0, "Ships rowing boat rows without a handheld oar");
                seat.controls.Left = true;
                Check(boat.SeatsToMotion(0.05f).Y > 0, "native rowing/turning controls preserved: " + boat.Code);
            }
            finally { seat.Passenger = null; seat.controls.Forward = seat.controls.Left = false; }
        }
        void RenderPipeline(EntityBoat boat, BoatWavePose pose)
        {
            // Execute the real native render methods without a GL context. Their matrix/seat work needs only these
            // client services; no tessellation/drawing is invoked. This tests Harmony entry points, not just math.
            var viewer = new EntityPlayer();
            viewer.Pos.SetFrom(boat.Pos);
            var playerType = AccessTools.TypeByName("Vintagestory.Client.NoObf.ClientPlayer");
            var player = (IClientPlayer)RuntimeHelpers.GetUninitializedObject(playerType);
            var worldDataField = AccessTools.Field(playerType, "worlddata");
            var worldData = RuntimeHelpers.GetUninitializedObject(worldDataField.FieldType);
            AccessTools.Field(worldDataField.FieldType, "entityplayer").SetValue(worldData, viewer);
            worldDataField.SetValue(player, worldData);
            var calendar = Proxy.Make<IClientGameCalendar>(m => m.Name == "get_SpeedOfTime" ? 60f : null);
            var world = Proxy.Make<IClientWorldAccessor>(m => m.Name == "get_Player" ? player : m.Name == "get_Calendar" ? calendar : null);
            var client = Proxy.Make<ICoreClientAPI>(m => m.Name == "get_World" ? world : m.Name == "get_InWorldEllapsedMilliseconds" ? api.World.ElapsedMilliseconds : null);
            var renderer = (EntityShapeRenderer)RuntimeHelpers.GetUninitializedObject(typeof(EntityShapeRenderer));
            renderer.ModelMat = Mat4f.Create();
            AccessTools.Field(typeof(EntityShapeRenderer).BaseType, "capi").SetValue(renderer, client);
            var old = boat.Properties.Client.Renderer;
            boat.Properties.Client.Renderer = renderer;
            AccessTools.Field(typeof(EntityBoat), "capi").SetValue(boat, client);
            try
            {
                boat.OnRenderFrame(0, EnumRenderStage.Before);
                Check(Math.Abs(boat.mountAngle.X - pose.Roll) < 1e-6 && Math.Abs(boat.mountAngle.Z - pose.Pitch) < 1e-6,
                    "actual native frame replaces rocking with wave angles: " + boat.Code);
                float previousX = renderer.xangle, previousZ = renderer.zangle;
                boat.OnRenderFrame(0, EnumRenderStage.Before);
                Check(Math.Abs(previousX - renderer.xangle) < 1e-7 && Math.Abs(previousZ - renderer.zangle) < 1e-7,
                    "successive native frames do not accumulate wave angles: " + boat.Code);
                renderer.loadModelMatrix(boat, 0, true);
                float moved = renderer.ModelMat[13];
                if (boat.Code.Domain == "ships")
                {
                    var clip = (WaterClipBox)boat.GetType().GetMethod("WaterClip").Invoke(boat, new object[] { boat.Pos.XYZ });
                    var point = new Matrixf().Set(renderer.ModelMat).TransformVector(new Vec4f(-2.05f + 0.5f, 0.55f, 0.5f, 1));
                    Check(clip.InDryHull(point.X + viewer.CameraPos.X, point.Y + viewer.CameraPos.Y, point.Z + viewer.CameraPos.Z),
                        "rowing boat dry cut contains the native-rendered waterline centre");
                    float savedX = renderer.xangle, savedZ = renderer.zangle;
                    renderer.xangle = .31f; renderer.zangle = -.23f;
                    renderer.loadModelMatrix(boat, 0, true);
                    clip = (WaterClipBox)boat.GetType().GetMethod("WaterClip").Invoke(boat, new object[] { boat.Pos.XYZ });
                    int errors = 0, edges = 0;
                    foreach (double level in new[] { .1, .5, .95 })
                        for (int station = 1; station < 31; station++)
                            foreach (int side in new[] { -1, 1 })
                                foreach (bool inside in new[] { true, false })
                                {
                                    double u = station * 2.0 / 31 - 1;
                                    double width = clip.DryHalfWidthAt(u, level - .55);
                                    if (width < .025) continue; // A closed stem has no free interior to sample.
                                    double w = Math.Max(0, width + (inside ? -.02 : .02));
                                    var sample = new Matrixf().Set(renderer.ModelMat).TransformVector(new Vec4f(
                                        (float)(.5 - clip.CentreOffset - u * clip.HalfLength), (float)level, (float)(.5625 + side * w), 1));
                                    bool dry = clip.InDryHull(sample.X + viewer.CameraPos.X, sample.Y + viewer.CameraPos.Y, sample.Z + viewer.CameraPos.Z);
                                    if (dry != inside) errors++; edges++;
                                }
                    Check(errors == 0, $"rowing cut tracks {edges} actual native-rendered boundary points with combined wave/turn/forward lean (errors={errors})");
                    Check(boat.SidedProperties.Behaviors.All(b => b is not EntityBehaviorHideWaterSurface),
                        "rowing boat has one water volume, without the duplicate flat native depth mask");
                    renderer.xangle = savedX; renderer.zangle = savedZ;
                }
                bool wasSwimming = boat.Swimming, wasWet = boat.FeetInLiquid;
                boat.Swimming = boat.FeetInLiquid = false;
                renderer.loadModelMatrix(boat, 0, true);
                float still = renderer.ModelMat[13];
                boat.Swimming = wasSwimming; boat.FeetInLiquid = wasWet;
                Check(Math.Abs(moved - still - pose.Heave) < 1e-5, "actual native hull matrix follows heave: " + boat.Code);
                var behavior = boat.GetBehavior<EntityBehaviorSelectionBoxes>();
                var transform = AccessTools.Method(typeof(EntityBehaviorSelectionBoxes), "applyBoxTransform");
                var box = new AttachmentPointAndPose { AnimModelMatrix = Mat4f.Create(), AttachPoint = new AttachmentPoint
                    { ParentElement = new ShapeElement { From = new double[] { 0, 0, 0 }, To = new double[] { 16, 16, 16 } } } };
                var matrix = new Matrixf();
                transform.Invoke(behavior, new object[] { matrix, box });
                double selected = matrix.Values[13];
                boat.Swimming = boat.FeetInLiquid = false;
                matrix.Identity(); transform.Invoke(behavior, new object[] { matrix, box });
                boat.Swimming = wasSwimming; boat.FeetInLiquid = wasWet;
                Check(Math.Abs(selected - matrix.Values[13] - pose.Heave) < 1e-5, "native seat/cargo/sail selection follows hull: " + boat.Code);
                var seat = boat.GetBehavior<EntityBehaviorSeatable>().Seats.OfType<EntitySeat>().First();
                viewer.EntityId = -9876543;
                seat.Passenger = viewer;
                AccessTools.Field(typeof(EntityShapeRenderer), "ims").SetValue(renderer, boat.GetBehavior<EntityBehaviorSeatable>());
                renderer.loadModelMatrix(boat, 0, true);
                float aboard = renderer.ModelMat[13];
                boat.Swimming = boat.FeetInLiquid = false;
                renderer.loadModelMatrix(boat, 0, true);
                Check(Math.Abs(aboard - renderer.ModelMat[13]) < 1e-5, "native mounted-camera compensation applies heave once: " + boat.Code);
                boat.Swimming = wasSwimming; boat.FeetInLiquid = wasWet; seat.Passenger = null;
            }
            finally { boat.Properties.Client.Renderer = old; AccessTools.Field(typeof(EntityBoat), "capi").SetValue(boat, null); }
        }
        void Propulsion(EntityBoat boat)
        {
            var seat = boat.GetBehavior<EntityBehaviorSeatable>().Seats.OfType<EntityBoatSeat>().First(s => s.CanControl);
            var driver = new Driver { EntityId = 987654321, AnimManager = null };
            seat.Passenger = driver;
            AccessTools.Field(typeof(EntityBoat), "CurrentlyControllingEntityId").SetValue(boat, driver.EntityId);
            boat.WatchedAttributes.SetInt("sailPosition", 2);
            float yaw = boat.Pos.Yaw;
            try
            {
                sea.SetWindProvider("boat-test", (x, z, h, g) => (0.3, 0), null);
                boat.Pos.Yaw = 0;
                double reach = boat.SeatsToMotion(0.05f).X;
                boat.Pos.Yaw = -(float)Math.PI / 2;
                double head = boat.SeatsToMotion(0.05f).X;
                boat.Pos.Yaw = (float)Math.PI / 2;
                double run = boat.SeatsToMotion(0.05f).X;
                Check(reach > 0 && head < 1e-9 && run > 0 && run < reach, "real native sail thrust: beam wind, headwind, downwind");
                boat.Pos.Yaw = -(float)Math.PI / 4; double port = boat.SeatsToMotion(.05f).X;
                boat.Pos.Yaw = -(float)Math.PI * 3 / 4; double starboard = boat.SeatsToMotion(.05f).X;
                Check(port > reach * .25 && Math.Abs(port - starboard) < 1e-6, "native sailboat makes equal close-hauled progress on either tack");
                sea.ClearWindProvider("boat-test");
                sea.SetWindProvider("boat-calm", (x, z, h, g) => (0, 0), null);
                Check(boat.SeatsToMotion(0.05f).X == 0, "real native sail thrust stops in calm");
                sea.ClearWindProvider("boat-calm");
                boat.WatchedAttributes.SetInt("sailPosition", 0); seat.controls.Forward = true;
                Check(boat.SeatsToMotion(0.05f).X > 0, "native rowing works with sails furled even in calm");
                boat.WatchedAttributes.SetInt("sailPosition", 2); seat.controls.Forward = false; seat.controls.Left = true;
                var patch = AccessTools.Method(typeof(BoatWaves), nameof(BoatWaves.SailMotion));
                var original = AccessTools.Method(typeof(EntityBoat), nameof(EntityBoat.SeatsToMotion));
                var harmony = new Harmony("sea");
                harmony.Unpatch(original, patch);
                double vanilla = boat.SeatsToMotion(0.05f).X;
                Check(vanilla > 0, "removing Sea's propulsion hook restores vanilla sail thrust");
                harmony.Patch(original, postfix: new HarmonyMethod(patch));
                var turn = boat.SeatsToMotion(0.05f);
                Check(turn.Y > 0, "native rudder control remains available");
            }
            finally { seat.Passenger = null; seat.controls.Left = false; boat.Pos.Yaw = yaw; boat.WatchedAttributes.SetInt("sailPosition", 0); }
        }
    }
    public class Driver : EntityPlayer
    {
        public override IAnimationManager AnimManager { get; set; }
        public ItemSlot Paddle;
        public override ItemSlot RightHandItemSlot => Paddle;
    }
    public class Proxy : DispatchProxy
    {
        public System.Func<MethodInfo, object> Resolve;
        public static T Make<T>(System.Func<MethodInfo, object> resolve) where T : class
        { var value = Create<T, Proxy>(); ((Proxy)(object)value).Resolve = resolve; return value; }
        protected override object Invoke(MethodInfo method, object[] args)
        { var result = Resolve(method); return result ?? (method.ReturnType.IsValueType ? Activator.CreateInstance(method.ReturnType) : null); }
    }
}
