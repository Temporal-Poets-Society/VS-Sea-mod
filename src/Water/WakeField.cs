// Sea wake implementation, GPL-3.0. Original physics-based implementation; no upstream code copied.
// Feature reference/credit: Bobrik00, Immersive Wakes 0.9.1 (port of Goby56's Minecraft Wakes).
using System;
using System.Collections.Generic;

namespace Sea
{
    /// <summary>Visual waterline dimensions in blocks. CentreOffset is along the entity's forward axis.</summary>
    public readonly struct WakeHull
    {
        public readonly double Length, Beam, Draft, CentreOffset;
        public WakeHull(double length, double beam, double draft, double centreOffset = 0)
        { Length = length; Beam = beam; Draft = draft; CentreOffset = centreOffset; }
        public bool Valid => double.IsFinite(Length) && Length >= 0.1 && Length <= 200
            && double.IsFinite(Beam) && Beam >= 0.1 && Beam <= 50 && double.IsFinite(Draft) && Draft >= 0.05 && Draft <= 20
            && double.IsFinite(CentreOffset) && Math.Abs(CentreOffset) <= Length;
    }

    public static class WakeMath
    {
        public static readonly double KelvinAngle = Math.Asin(1.0 / 3);
        /// <summary>Angle of the visible high-amplitude arms; Kelvin's outer envelope still exists at high Fr.</summary>
        public static double Angle(double speed, double length)
        {
            double fr = Math.Abs(speed) / Math.Sqrt(9.81 * Math.Max(0.1, length));
            return Math.Atan(Math.Tan(KelvinAngle) * Math.Min(1, 0.5 / Math.Max(fr, 1e-6)));
        }
        public static double FoamLife(double length) => Math.Clamp(25 + 4 * length, 25, 180);
        public static double Fade(double age, double life) => age < 0 || age >= life ? 0 : Math.Pow(1 - age / life, 2);
        public static double Energy(double speed, in WakeHull hull) => Math.Clamp((speed - 0.2) / 3, 0, 1)
            * Math.Clamp(Math.Sqrt(hull.Beam * hull.Draft), 0.18, 1);
        public static double Spray(double speed, double seaHeight) => Math.Clamp((speed - 0.8) / 8, 0, 1)
            * (0.3 + 0.7 * Math.Clamp(seaHeight / 5, 0, 1));
        /// <summary>Slow sideways mixing of aged stern foam. Stable by emission position/time, not a per-frame RNG.</summary>
        public static void Stern(in WakeNode n,double now,out double x,out double z)
        {
            double age=Math.Max(0,now-n.Born);
            double mixing=Math.Min(n.Hull.Beam*0.22,0.07*Math.Sqrt(age))
                * Math.Sin(n.Born*1.7+n.X*0.13+n.Z*0.17);
            x=n.X-n.Fx*n.Hull.Length*0.5-n.Fz*mixing;
            z=n.Z-n.Fz*n.Hull.Length*0.5+n.Fx*mixing;
        }
        /// <summary>Cusp packet: cg along = U/3, relative aft = 2U/3; at low Fr lateral/aft = tan(Kelvin).</summary>
        public static void Arm(in WakeNode n, double now, int side, out double x, out double z)
        {
            double age = Math.Max(0, now - n.Born), forward = n.Hull.Length * 0.5 + n.Speed * age / 3;
            double across = n.Hull.Beam * 0.5 + n.Speed * age * (2.0 / 3) * Math.Tan(Angle(n.Speed, n.Hull.Length));
            x = n.X + n.Fx * forward - n.Fz * side * across;
            z = n.Z + n.Fz * forward + n.Fx * side * across;
        }
    }

    public readonly struct WakeNode
    {
        public readonly double X, Z, SurfaceY, Fx, Fz, Speed, Born;
        public readonly WakeHull Hull;
        public WakeNode(double x, double z, double surfaceY, double fx, double fz, double speed, double born, WakeHull hull)
        { X=x; Z=z; SurfaceY=surfaceY; Fx=fx; Fz=fz; Speed=speed; Born=born; Hull=hull; }
    }

    public sealed class WakeTrack
    {
        public readonly long Id;
        public readonly List<WakeNode> Nodes = new();
        public WakeNode Current;
        public double Seen;
        public WakeTrack(long id) { Id=id; }
    }

    public readonly struct WakeRing
    {
        public readonly long Id;
        public readonly double X,Z,SurfaceY,Born,Speed,Energy;
        public WakeRing(long id,double x,double z,double surfaceY,double born,double impact,double size)
        { Id=id; X=x; Z=z; SurfaceY=surfaceY; Born=born; Speed=Math.Sqrt(9.81*Math.Clamp(0.4+impact*0.08,0.4,4)/(2*Math.PI)); Energy=Math.Clamp(impact/6,0.12,1)*Math.Clamp(size,0.2,1); }
    }

    /// <summary>Client-local cosmetic history, independent of WavePlace and all server physics.</summary>
    public sealed class WakeField
    {
        public const int MaxTracks=64, MaxNodes=1024, MaxRings=64, MaxDrawn=32;
        public const double NodeInterval=0.5, RingLife=8;
        public readonly List<WakeTrack> Tracks=new();
        public readonly List<WakeRing> Rings=new();
        public int NodeCount { get; private set; }
        public void Clear() { Tracks.Clear(); Rings.Clear(); NodeCount=0; }
        public void Observe(long id,double now,double x,double z,double surfaceY,double vx,double vz,WakeHull hull)
        {
            double speed=Math.Sqrt(vx*vx+vz*vz);
            if (!hull.Valid || !double.IsFinite(now+x+z+surfaceY+speed) || speed<0.25 || speed>80) return;
            WakeTrack track=Tracks.Find(t=>t.Id==id);
            if(track==null) {
                if(Tracks.Count>=MaxTracks) {
                    int oldest=0; for(int i=1;i<Tracks.Count;i++) if(Tracks[i].Seen<Tracks[oldest].Seen) oldest=i;
                    NodeCount-=Tracks[oldest].Nodes.Count; Tracks.RemoveAt(oldest);
                }
                track=new WakeTrack(id); Tracks.Add(track);
            }
            var node=new WakeNode(x,z,surfaceY,vx/speed,vz/speed,speed,now,hull);
            track.Current=node; track.Seen=now;
            var nodes=track.Nodes;
            if(nodes.Count>0 && now<nodes[^1].Born) { NodeCount-=nodes.Count;nodes.Clear(); }
            if(nodes.Count>0 && now-nodes[^1].Born<NodeInterval) return;
            if(nodes.Count>0) {
                var last=nodes[^1]; double dx=x-last.X,dz=z-last.Z;
                // Teleport/seam jumps cannot leave a connecting stripe across the world.
                if(dx*dx+dz*dz>Math.Pow(80*Math.Max(NodeInterval,now-last.Born)+8,2)) {
                    NodeCount-=nodes.Count; nodes.Clear();
                }
            }
            nodes.Add(node); NodeCount++;
            if(NodeCount>MaxNodes) {
                WakeTrack oldest=null;
                foreach(var t in Tracks) if(t.Nodes.Count>0 && (oldest==null || t.Nodes[0].Born<oldest.Nodes[0].Born)) oldest=t;
                oldest.Nodes.RemoveAt(0); NodeCount--;
            }
        }
        public void Splash(long id,double now,double x,double z,double surfaceY,double impact,double size)
        {
            if(!double.IsFinite(now+x+z+surfaceY+impact+size)||impact<0.5) return;
            foreach(var r in Rings) if(r.Id==id && now-r.Born<0.7) return; // polling + liquid collision hook
            if(Rings.Count>=MaxRings) Rings.RemoveAt(0);
            Rings.Add(new WakeRing(id,x,z,surfaceY,now,Math.Min(impact,40),size));
        }
        public void Expire(double now)
        {
            for(int i=Tracks.Count-1;i>=0;i--) {
                var t=Tracks[i]; int gone=0;
                while(gone<t.Nodes.Count && now-t.Nodes[gone].Born>=WakeMath.FoamLife(t.Nodes[gone].Hull.Length)) gone++;
                if(gone>0) { t.Nodes.RemoveRange(0,gone); NodeCount-=gone; }
                if(t.Nodes.Count==0 && now-t.Seen>1) Tracks.RemoveAt(i);
            }
            Rings.RemoveAll(r=>now-r.Born>=RingLife || now<r.Born);
        }
    }

    /// <summary>Bounded camera-near raster: foam, reserved, premultiplied rest-water height, coverage. Linear-filtered
    /// onto the actual displaced liquid triangles; no independently sampled floating wake mesh.</summary>
    public sealed class WakeMap
    {
        public const int Size=512, MaxSamples=250000;   // bounds a worst-case repaint to a few ms (1 500 000 took 45 ms in a debug build)
        public const double Span=256, Texel=Span/Size;
        public readonly float[] Pixels=new float[Size*Size*4];
        public double X,Z,CameraY;
        public int Samples {get;private set;}
        public int Drawn {get;private set;}
        readonly List<(WakeTrack track,double distance)> visible=new();
        public void Paint(WakeField field,double now,double camX,double camY,double camZ)
        {
            // Snap to texels; the map stays in world space as the camera pans or moves between uploads.
            X=Math.Floor(camX/Texel)*Texel-Span/2; Z=Math.Floor(camZ/Texel)*Texel-Span/2; CameraY=camY;
            Array.Clear(Pixels); Samples=0; Drawn=0; visible.Clear();
            foreach(var t in field.Tracks) {
                double near=double.MaxValue;
                foreach(var n in t.Nodes) near=Math.Min(near,(n.X-camX)*(n.X-camX)+(n.Z-camZ)*(n.Z-camZ));
                if(near<Span*Span*0.65) visible.Add((t,near));
            }
            visible.Sort((a,b)=> {int c=a.distance.CompareTo(b.distance); return c!=0?c:a.track.Id.CompareTo(b.track.Id);});
            foreach(var v in visible) {
                if(Drawn>=WakeField.MaxDrawn || Samples>=MaxSamples) break;
                Draw(v.track,now); Drawn++;
            }
            foreach(var r in field.Rings) {
                double age=now-r.Born;
                Ring(r.X,r.Z,0.2+r.Speed*age,0.24+age*0.025,r.Energy*WakeMath.Fade(age,WakeField.RingLife),r.SurfaceY);
            }
        }
        void Draw(WakeTrack t,double now)
        {
            var nodes=t.Nodes;
            for(int i=0;i<nodes.Count;i++) {
                var n=nodes[i]; double age=now-n.Born,energy=WakeMath.Energy(n.Speed,n.Hull);
                double tail=energy*WakeMath.Fade(age,WakeMath.FoamLife(n.Hull.Length));
                double width=(n.Hull.Beam*0.24+0.08*Math.Sqrt(Math.Max(0,age)))
                    *(0.85+0.15*Math.Sin(n.Born*2.3));
                WakeMath.Stern(n,now,out double sx,out double sz);
                if(i>0) {
                    var p=nodes[i-1];
                    if(n.Born-p.Born<=3 && Math.Abs(p.SurfaceY-n.SurfaceY)<0.5)
                    {
                        WakeMath.Stern(p,now,out double px,out double pz);
                        Line(px,pz,sx,sz,width,tail*0.75,n.SurfaceY);
                    }
                }
                if(age<12 && i>0) {
                    var p=nodes[i-1];
                    if(n.Born-p.Born>3 || Math.Abs(p.SurfaceY-n.SurfaceY)>=0.5) continue;
                    for(int side=-1;side<=1;side+=2) {
                        WakeMath.Arm(n,now,side,out double ax,out double az);
                        WakeMath.Arm(p,now,side,out double bx,out double bz);
                        Line(ax,az,bx,bz,0.22+n.Hull.Beam*0.055,energy*WakeMath.Fade(age,12)*0.7,n.SurfaceY);
                    }
                    // Transverse crests have the deep-water stationary wavelength, rather than a fixed grid.
                    double wavelength=Math.Clamp(2*Math.PI*n.Speed*n.Speed/9.81,2,4*n.Hull.Length);
                    double crest=Math.Pow(Math.Max(0,Math.Cos(2*Math.PI*age*n.Speed/wavelength)),8);
                    if(crest>0.3 && (i&1)==0) {
                        WakeMath.Arm(n,now,-1,out double ax,out double az); WakeMath.Arm(n,now,1,out double bx,out double bz);
                        double mx=(ax+bx)*0.5-n.Fx*n.Hull.Beam*0.3,mz=(az+bz)*0.5-n.Fz*n.Hull.Beam*0.3;
                        Line(ax,az,mx,mz,0.2,crest*energy*WakeMath.Fade(age,12)*0.25,n.SurfaceY);
                        Line(mx,mz,bx,bz,0.2,crest*energy*WakeMath.Fade(age,12)*0.25,n.SurfaceY);
                    }
                }
            }
            if(now-t.Seen>0.25) return;
            var c=t.Current; double e=WakeMath.Energy(c.Speed,c.Hull),lastX=0,lastZ=0;
            for(int i=0;i<=12;i++) {
                double a=-Math.PI/2+Math.PI*i/12;
                double bx=c.X+c.Fx*(c.Hull.Length*0.5+c.Hull.Beam*0.18*Math.Cos(a))-c.Fz*c.Hull.Beam*0.55*Math.Sin(a);
                double bz=c.Z+c.Fz*(c.Hull.Length*0.5+c.Hull.Beam*0.18*Math.Cos(a))+c.Fx*c.Hull.Beam*0.55*Math.Sin(a);
                if(i>0) Line(lastX,lastZ,bx,bz,0.22+c.Hull.Beam*0.06,e*0.8,c.SurfaceY);
                lastX=bx;lastZ=bz;
            }
            double sternX=c.X-c.Fx*c.Hull.Length*0.5,sternZ=c.Z-c.Fz*c.Hull.Length*0.5;
            Line(sternX,sternZ,sternX-c.Fx*c.Hull.Beam,sternZ-c.Fz*c.Hull.Beam,c.Hull.Beam*0.35,e*0.85,c.SurfaceY);
        }
        void Put(int x,int z,double value,double surfaceY)
        {
            double edge=Math.Clamp(Math.Min(Math.Min(x,z),Math.Min(Size-1-x,Size-1-z))/16.0,0,1);
            float f=(float)Math.Clamp(value*edge,0,1); int i=(z*Size+x)*4;
            if(f<=Pixels[i+3]) return;
            Pixels[i]=f; Pixels[i+1]=0; Pixels[i+2]=(float)(surfaceY-CameraY)*f; Pixels[i+3]=f;
        }
        void Line(double ax,double az,double bx,double bz,double width,double energy,double surfaceY)
        {
            if(energy<=0.005 || Samples>=MaxSamples) return;
            width=Math.Max(Texel*0.6,width);
            // Clip before subdivision, so an old faraway trail has bounded work too.
            double dx=bx-ax,dz=bz-az,lo=0,hi=1;
            if(!Clip(-dx,ax-X+width,ref lo,ref hi)||!Clip(dx,X+Span+width-ax,ref lo,ref hi)
                ||!Clip(-dz,az-Z+width,ref lo,ref hi)||!Clip(dz,Z+Span+width-az,ref lo,ref hi)) return;
            bx=ax+hi*dx; bz=az+hi*dz; ax+=lo*dx; az+=lo*dz;
            int parts=Math.Clamp((int)Math.Ceiling(Math.Sqrt((bx-ax)*(bx-ax)+(bz-az)*(bz-az))/(8*Texel)),1,128);
            for(int p=0;p<parts && Samples<MaxSamples;p++) {
                double x0=ax+(bx-ax)*p/parts,z0=az+(bz-az)*p/parts,x1=ax+(bx-ax)*(p+1)/parts,z1=az+(bz-az)*(p+1)/parts;
                int ix0=Math.Clamp((int)Math.Floor((Math.Min(x0,x1)-width-X)/Texel),0,Size-1),ix1=Math.Clamp((int)Math.Ceiling((Math.Max(x0,x1)+width-X)/Texel),0,Size-1);
                int iz0=Math.Clamp((int)Math.Floor((Math.Min(z0,z1)-width-Z)/Texel),0,Size-1),iz1=Math.Clamp((int)Math.Ceiling((Math.Max(z0,z1)+width-Z)/Texel),0,Size-1);
                double vx=x1-x0,vz=z1-z0,d2=Math.Max(vx*vx+vz*vz,1e-9);
                for(int z=iz0;z<=iz1 && Samples<MaxSamples;z++) for(int x=ix0;x<=ix1 && Samples<MaxSamples;x++) {
                    Samples++; double rx=X+(x+0.5)*Texel-x0,rz=Z+(z+0.5)*Texel-z0;
                    double u=Math.Clamp((rx*vx+rz*vz)/d2,0,1),d=Math.Sqrt((rx-u*vx)*(rx-u*vx)+(rz-u*vz)*(rz-u*vz));
                    double f=Math.Clamp(1-d/width,0,1); Put(x,z,energy*f*f*(3-2*f),surfaceY);
                }
            }
        }
        static bool Clip(double p,double q,ref double lo,ref double hi)
        {
            if(Math.Abs(p)<1e-12) return q>=0;
            double t=q/p; if(p<0)lo=Math.Max(lo,t);else hi=Math.Min(hi,t);return lo<=hi;
        }
        void Ring(double cx,double cz,double radius,double width,double energy,double surfaceY)
        {
            if(energy<0.005 || radius<0 || Samples>=MaxSamples) return;
            width=Math.Max(width,Texel*0.65);
            int x0=Math.Clamp((int)Math.Floor((cx-radius-width-X)/Texel),0,Size-1),x1=Math.Clamp((int)Math.Ceiling((cx+radius+width-X)/Texel),0,Size-1);
            int z0=Math.Clamp((int)Math.Floor((cz-radius-width-Z)/Texel),0,Size-1),z1=Math.Clamp((int)Math.Ceiling((cz+radius+width-Z)/Texel),0,Size-1);
            for(int z=z0;z<=z1 && Samples<MaxSamples;z++) for(int x=x0;x<=x1 && Samples<MaxSamples;x++) {
                Samples++; double dx=X+(x+0.5)*Texel-cx,dz=Z+(z+0.5)*Texel-cz;
                double f=Math.Clamp(1-Math.Abs(Math.Sqrt(dx*dx+dz*dz)-radius)/width,0,1);
                Put(x,z,energy*f*f*(3-2*f),surfaceY);
            }
        }
    }
}
