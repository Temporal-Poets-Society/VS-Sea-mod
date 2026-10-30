// Sea, GPL-3.0. Original implementation; feature reference: Bobrik00's Immersive Wakes 0.9.1,
// a port of Goby56's Minecraft Wakes. No upstream implementation code copied.
using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace Sea
{
    /// <summary>Client-local contact observation and a bounded foam atlas. The actual liquid triangles carry the
    /// foam, including their wave displacement and per-pixel dry cut. Never feeds WavePlace or the server.</summary>
    public sealed class WakeRenderer : IDisposable
    {
        const int MaxObserved = 128, MaxContacts = 64;
        const double UpdateSeconds = 0.1, Range = 120;
        readonly ICoreClientAPI api;
        readonly SeaSystem sea;
        readonly WakeField field = new();
        readonly WakeMap map = new();
        readonly Dictionary<long, Observation> observed = new();
        readonly List<long> oldIds = new();
        readonly List<(Entity entity, double distance, bool vessel)> candidates = new();
        readonly Queue<Impact> impacts = new();
        readonly object impactSync = new();
        readonly BlockPos blockPos = new(0);
        readonly Vec3d queryPos = new();
        readonly Random random = new(29048);
        int texture, dimension = -1;
        double last = double.NegativeInfinity, queryX, queryZ;
        float strength;
        bool failed, atlasContainsInk;
        public float? StrengthOverride;
        public float Strength {
            get {
                float value=StrengthOverride ?? (sea.Config.WakeEnabled ? sea.Config.WakeStrength : 0);
                return float.IsFinite(value) ? Math.Clamp(value,0,2) : 1;
            }
        }
        public string Status => $"Wakes: {(failed ? "disabled after error" : strength > 0 ? "on" : "off")}, strength {Strength:0.##}; {field.Tracks.Count} sources, {field.NodeCount}/{WakeField.MaxNodes} trail nodes, {field.Rings.Count} rings, {map.Drawn}/{WakeField.MaxDrawn} sources drawn, {map.Samples}/{WakeMap.MaxSamples} raster samples.";

        sealed class Observation
        {
            public double X,Y,Z,Time,Seen,Vy;
            public bool Wet;
        }
        readonly struct Impact
        {
            public readonly long Id;
            public readonly double X,Y,Z,Speed,Size;
            public Impact(Entity e) {
                Id=e.EntityId; X=e.Pos.X; Y=e.Pos.Y; Z=e.Pos.Z;
                // Motion is blocks/tick. The position poll below also retains pre-contact vertical velocity, as
                // vanilla's collision handler can already have damped Motion when the postfix runs.
                Speed=Math.Max(Math.Abs(e.Pos.Motion.Y)*60, Math.Sqrt(19.62*Math.Max(0,(e.PositionBeforeFalling?.Y ?? Y)-Y)));
                Size=Math.Max(0.2,e.CollisionBox?.XSize ?? 0.3);
            }
        }
        public WakeRenderer(ICoreClientAPI api,SeaSystem sea) { this.api=api; this.sea=sea; }

        /// <summary>Liquid-collision postfix: queue numbers only; no world/GL work on a simulation thread.</summary>
        public void Contact(Entity e)
        {
            if(e?.Pos == null || !double.IsFinite(e.Pos.X+e.Pos.Y+e.Pos.Z) || e.Pos.Dimension!=0 || e is EntityAgent agent && agent.MountedOn!=null || Strength<=0 || failed) return;
            lock(impactSync) { if(impacts.Count<MaxContacts) impacts.Enqueue(new Impact(e)); }
        }

        public void Prepare(double camX,double camY,double camZ,IReadOnlyList<WaterClipBox> clips,bool available)
        {
            int dim=api.World.Player.Entity.Pos.Dimension;
            strength=available && !failed && dim==0 && (sea.DrySpaceSources==0 || Hooks.DryHullsInjected)
                ? Math.Clamp(Strength,0,2) : 0;
            if(strength<=0) { Clear(); return; }
            double now=sea.WaveTime;
            if(dim!=dimension || now<last || double.IsFinite(last) && now-last>5) Clear();
            dimension=dim;
            if(now-last<UpdateSeconds) return;
            try {
                double elapsed=double.IsFinite(last) ? now-last : UpdateSeconds;
                last=now;
                Poll(now,camX,camY,camZ,clips);
                lock(impactSync) while(impacts.Count>0) {
                    var i=impacts.Dequeue();
                    if((i.X-camX)*(i.X-camX)+(i.Z-camZ)*(i.Z-camZ)>Range*Range || sea.IsDry(i.X,i.Y+0.1,i.Z)) continue;
                    if(Surface(i.X,i.Y,i.Z,3,out double y)) {
                        double speed=i.Speed;
                        if(observed.TryGetValue(i.Id,out var prev)) speed=Math.Max(speed,Math.Abs(prev.Vy));
                        field.Splash(i.Id,now,i.X,i.Z,y,speed,i.Size);
                    }
                }
                field.Expire(now);
                if(field.NodeCount==0 && field.Rings.Count==0 && !atlasContainsInk) return;
                map.Paint(field,now,camX,camY,camZ);
                if(map.Samples>0 || atlasContainsInk) {
                    UploadTexture();
                    atlasContainsInk=map.Samples>0;
                }
                // Cosmetic particles have their own small shared budget, independent of the history/raster cap.
                if(!api.IsGamePaused) Spray(now,Math.Min(elapsed,0.25),camX,camZ);
            }
            catch(Exception e) {
                Fail(e);
            }
        }

        void Poll(double now,double camX,double camY,double camZ,IReadOnlyList<WaterClipBox> clips)
        {
            candidates.Clear(); queryX=camX;queryZ=camZ;
            queryPos.Set(camX,camY,camZ);
            // The game's spatial partition does the broad phase. Returning false avoids allocating its result array;
            // retain at most 64 nearest candidates, vessels first, rather than walking LoadedEntities every frame.
            api.World.GetEntitiesAround(queryPos,(float)Range,48,Collect);
            oldIds.Clear();
            foreach(var kv in observed) if(now-kv.Value.Seen>2) oldIds.Add(kv.Key);
            foreach(long id in oldIds) observed.Remove(id);
            foreach(var candidate in candidates) {
                var e=candidate.entity; var p=e.Pos;
                if(!observed.TryGetValue(e.EntityId,out var o)) {
                    if(observed.Count>=MaxObserved) continue;
                    o=new Observation {X=p.X,Y=p.Y,Z=p.Z,Time=now}; observed.Add(e.EntityId,o);
                }
                o.Seen=now;
                double dt=now-o.Time;
                if(dt<0.05) continue; // observe first; spawn/network placement is not motion
                double vx=(p.X-o.X)/dt,vz=(p.Z-o.Z)/dt,vy=(p.Y-o.Y)/dt;
                bool jump=vx*vx+vz*vz+vy*vy>80*80;
                WakeHull hull=Hull(e,clips,candidate.vessel);
                double search=candidate.vessel ? Math.Min(32,hull.Draft+sea.CameraWaves.SignificantHeight+2) : 3;
                double surface=0;
                bool wet=!jump && Surface(p.X,p.Y,p.Z,(int)Math.Ceiling(search),out surface);
                // A distant water column alone is not contact. Boats can heave; swimmers/items must meet the
                // drawn surface (or vanilla reports contact with water), not cruise metres above or below it.
                double wave=sea.CameraWaves.HeightAt(p.X,p.Z)*sea.WaveFactorAt(p.X,p.Z);
                if(wet) wet=candidate.vessel ? Math.Abs(p.Y-surface-wave)<hull.Draft+2
                    : (e.FeetInLiquid || e.Swimming || p.Y<=surface+wave+0.4)
                        && p.Y+(e.CollisionBox?.Height ?? 0.3)>=surface+wave-0.5;
                if(!candidate.vessel && sea.IsDry(p.X,p.Y+0.1,p.Z)) wet=false;
                if(wet) {
                    if(!o.Wet) field.Splash(e.EntityId,now,p.X,p.Z,surface,Math.Max(-vy,-o.Vy),hull.Beam);
                    double fx=Math.Sin(p.Yaw),fz=Math.Cos(p.Yaw);
                    field.Observe(e.EntityId,now,p.X+fx*hull.CentreOffset,p.Z+fz*hull.CentreOffset,surface,vx,vz,hull);
                }
                o.X=p.X;o.Y=p.Y;o.Z=p.Z;o.Time=now;o.Vy=vy;o.Wet=wet;
            }
        }
        bool Collect(Entity e)
        {
            if(e?.Pos==null || !double.IsFinite(e.Pos.X+e.Pos.Y+e.Pos.Z) || e.Pos.Dimension!=0 || e is EntityAgent a && a.MountedOn!=null) return false;
            bool vessel=sea.TryWakeHull(e.EntityId,out _) || IsVessel(e);
            if(!vessel && !e.FeetInLiquid && !e.Swimming && Math.Abs(e.Pos.Motion.Y)<0.002) return false;
            double dx=e.Pos.X-queryX,dz=e.Pos.Z-queryZ,dist=dx*dx+dz*dz;
            if(dist>Range*Range) return false;
            int at=0;
            while(at<candidates.Count && (candidates[at].vessel && !vessel || candidates[at].vessel==vessel && candidates[at].distance<dist)) at++;
            if(at<MaxContacts) { candidates.Insert(at,(e,dist,vessel)); if(candidates.Count>MaxContacts)candidates.RemoveAt(MaxContacts); }
            return false;
        }
        static bool IsVessel(Entity e) {
            string type=e.GetType().Name,path=e.Code?.Path ?? "";
            return type.Contains("Boat",StringComparison.OrdinalIgnoreCase) || type.Contains("Ship",StringComparison.OrdinalIgnoreCase)
                || path.Contains("raft",StringComparison.OrdinalIgnoreCase) || path.Contains("sailboat",StringComparison.OrdinalIgnoreCase);
        }
        WakeHull Hull(Entity e,IReadOnlyList<WaterClipBox> clips,bool vessel)
        {
            if(sea.TryWakeHull(e.EntityId,out var declared)) return declared;
            // Hansa/Magelan publish actual waterline footprints while their entity collision boxes can be square.
            // Only associate a clip whose origin is very close, avoiding another ship beside a swimmer/boat.
            if(vessel) {
                double nearest=4; WaterClipBox found=default; bool match=false;
                foreach(var c in clips) {
                    double d=(c.X-e.Pos.X)*(c.X-e.Pos.X)+(c.Z-e.Pos.Z)*(c.Z-e.Pos.Z);
                    if(d<nearest) {nearest=d;found=c;match=true;}
                }
                if(match) {
                    var h=new WakeHull(Math.Max(0.1,2*(found.HalfLength-found.DryInset)),Math.Max(0.1,2*(found.HalfWidth-found.DryInset)),Math.Max(0.05,found.DryDepth),found.CentreOffset);
                    if(h.Valid) return h;
                }
            }
            var b=e.CollisionBox;
            double beam=Math.Clamp(b?.XSize ?? 0.5,0.2,50),length=Math.Clamp(b?.ZSize ?? 0.5,0.2,200);
            if(vessel) length=Math.Max(length,beam*1.5);
            return new WakeHull(length,beam,Math.Clamp((b?.Height ?? 0.5)*(vessel?0.35:0.15),0.05,20));
        }
        bool Surface(double x,double y,double z,int reach,out double height)
        {
            height=0;
            var ba=api.World.BlockAccessor;
            int start=Math.Clamp((int)Math.Floor(y),1,ba.MapSizeY-2);
            // Nearest exposed water top. Never query unloaded/out-of-world columns as a sea-level assumption.
            for(int d=0;d<=reach;d++) for(int side=0;side<(d==0?1:2);side++) {
                int by=start+(side==0?d:-d); if(by<1||by>=ba.MapSizeY-1)continue;
                blockPos.Set((int)Math.Floor(x),by,(int)Math.Floor(z));
                var b=ba.GetBlock(blockPos,BlockLayersAccess.Fluid);
                if(b?.IsLiquid()!=true || b.BlockMaterial==EnumBlockMaterial.Lava)continue;
                blockPos.Y=by+1; var above=ba.GetBlock(blockPos,BlockLayersAccess.Fluid);
                if(above?.IsLiquid()==true)continue;
                height=by+Math.Clamp(b.LiquidLevel,1,8)/8.0; return true;
            }
            return false;
        }
        void Spray(double now,double dt,double camX,double camZ)
        {
            int budget=4;
            foreach(var t in field.Tracks) {
                if(budget<=0)break;
                var n=t.Current;
                if(now-t.Seen>0.25 || n.Hull.Length<1.5 || (n.X-camX)*(n.X-camX)+(n.Z-camZ)*(n.Z-camZ)>64*64) continue;
                double power=WakeMath.Spray(n.Speed,sea.CameraWaves.SignificantHeight*sea.WaveFactorAt(n.X,n.Z))*strength;
                if(random.NextDouble()>power*dt*8)continue;
                for(int side=-1;side<=1 && budget>0;side+=2) {
                    double x=n.X+n.Fx*n.Hull.Length*0.5-n.Fz*side*n.Hull.Beam*0.5;
                    double z=n.Z+n.Fz*n.Hull.Length*0.5+n.Fx*side*n.Hull.Beam*0.5;
                    double y=n.SurfaceY+sea.CameraWaves.HeightAt(x,z)*sea.WaveFactorAt(x,z)+0.15;
                    double vx=n.Fx*n.Speed*0.35-n.Fz*side, vz=n.Fz*n.Speed*0.35+n.Fx*side;
                    if(sea.IsDryWithin(x,y,z,0.5) || sea.IsDryWithin(x+vx*0.5,y+0.5,z+vz*0.5,0.5))continue;
                    var p=new SimpleParticleProperties {
                        MinQuantity=1,AddQuantity=0,Color=unchecked((int)0xc8edf5f2),
                        MinPos=new Vec3d(x,y,z),AddPos=new Vec3d(0.15,0.1,0.15),
                        MinVelocity=new Vec3f((float)vx,1+(float)power,(float)vz),AddVelocity=new Vec3f(0.3f,0.6f,0.3f),
                        LifeLength=0.5f,MinSize=0.08f,MaxSize=0.17f,GravityEffect=0.8f,
                        ParticleModel=EnumParticleModel.Quad,ShouldDieInLiquid=true,WithTerrainCollision=false,WindAffected=true
                    };
                    api.World.SpawnParticles(p); budget--;
                }
            }
        }
        void UploadTexture()
        {
            GL.GetInteger(GetPName.ActiveTexture,out int active);
            GL.ActiveTexture(TextureUnit.Texture12); GL.GetInteger(GetPName.TextureBinding2D,out int bound);
            try {
                if(texture==0) {
                    texture=GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D,texture);
                    GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
                    GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);
                    GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);
                    GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba16f,WakeMap.Size,WakeMap.Size,0,PixelFormat.Rgba,PixelType.Float,map.Pixels);
                } else {
                    GL.BindTexture(TextureTarget.Texture2D,texture);
                    GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,WakeMap.Size,WakeMap.Size,PixelFormat.Rgba,PixelType.Float,map.Pixels);
                }
            } finally { GL.BindTexture(TextureTarget.Texture2D,bound); GL.ActiveTexture((TextureUnit)active); }
        }
        /// <summary>Bind on EVERY use: another program can have changed this unit since this frame's first upload.</summary>
        public void Bind(IShaderProgram p,double camX,double camY,double camZ)
        {
            if(!p.HasUniform("seaWakeArea")) return;
            try {
                bool visible=sea.FoamView!=FoamView.Wind && sea.FoamView!=FoamView.Off;
                p.Uniform("seaWakeArea",(float)(map.X-camX),(float)(map.Z-camZ),(float)(1/WakeMap.Span),texture!=0 && atlasContainsInk && visible?strength:0);
                if(strength<=0 || texture==0)return;
                p.Uniform("seaWakeHeight",(float)(map.CameraY-camY));
                if(p.HasUniform("seaWakeOrigin")) p.Uniform("seaWakeOrigin",(float)WorldShape.Mod(camX,8192),(float)WorldShape.Mod(camZ,8192));
                GL.GetInteger(GetPName.ActiveTexture,out int active);
                try { p.BindTexture2D("seaWakeTex",texture,12); } finally { GL.ActiveTexture((TextureUnit)active); }
            } catch(Exception e) {
                Fail(e);
                // A failed optional atlas must not enter WaterRenderer's wave/hull failure path.
                try { GL.Uniform4(GL.GetUniformLocation(p.ProgramId,"seaWakeArea"),0f,0f,0f,0f); } catch(Exception) { }
            }
        }
        void Fail(Exception e) {
            if(failed)return;failed=true;strength=0;Clear();
            api.Logger.Error("[sea] Visual wakes disabled after an error (shared waves unchanged): {0}",e);
        }
        void Clear() { field.Clear();observed.Clear();last=double.NegativeInfinity; lock(impactSync)impacts.Clear(); }
        public void Dispose() { strength=0;Clear();if(texture!=0){GL.DeleteTexture(texture);texture=0;} }
    }
}
