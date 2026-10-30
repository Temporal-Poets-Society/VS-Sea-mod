using System;
using System.Diagnostics;
using System.Linq;
using Vintagestory.API.Util;
using Sea;

namespace SeaTests
{
    static class WakeChecks
    {
        public static void Run(Action<bool,string> check)
        {
            Console.WriteLine("visual wakes (geometry, lifetime, budgets, wave isolation)");
            var hull=new WakeHull(26,7,2.5);
            check(hull.Valid && !new WakeHull(double.NaN,2,1).Valid && !new WakeHull(2,-1,1).Valid && !new WakeHull(201,2,1).Valid,
                "hull declarations reject nonfinite, negative and oversized dimensions");
            double angle=WakeMath.Angle(4,hull.Length)*180/Math.PI;
            check(Math.Abs(angle-19.4712206345)<1e-8 && WakeMath.Angle(30,26)<WakeMath.Angle(15,26),
                $"Kelvin visible arms {angle:F8} degrees at low Fr, narrowing at high Fr");
            var n=new WakeNode(0,0,0,1,0,4,0,hull);
            WakeMath.Arm(n,9,-1,out double x0,out double z0); WakeMath.Arm(n,9,1,out double x1,out double z1);
            double aft=4*9+hull.Length/2-x0,across=Math.Abs(z0)-hull.Beam/2;
            check(Math.Abs(across/aft-Math.Tan(WakeMath.KelvinAngle))<1e-12 && x0==x1 && z0==-z1,
                "cusp group motion gives symmetric 19.47-degree arms relative to the moving bow");
            check(WakeMath.Energy(0,hull)==0 && WakeMath.Energy(4,hull)>WakeMath.Energy(1,new WakeHull(0.8,0.5,0.1))
                && WakeMath.Spray(8,5)>WakeMath.Spray(8,0) && WakeMath.Spray(0,5)==0,
                "foam scales with speed/hull; bow spray with speed and sea state, none at rest");
            check(WakeMath.FoamLife(1)==29 && WakeMath.FoamLife(26)==129 && WakeMath.FoamLife(200)==180
                && WakeMath.Fade(0,129)==1 && WakeMath.Fade(60,129)>WakeMath.Fade(100,129) && WakeMath.Fade(129,129)==0,
                "swimmer foam lasts 29s, caravel 129s, maximum 180s, continuous fade and exact expiry");
            var f=new WakeField();
            for(int i=0;i<100;i++) f.Observe(1,i*0.01,i*0.04,0,0,4,0,hull);
            check(f.NodeCount==2 && f.Tracks.Count==1,"high-rate observations retain at most two trail nodes per second");
            f.Observe(1,1.5,10000,0,0,4,0,hull);
            check(f.NodeCount==1,"teleport drops the connecting trail");
            f.Observe(1,0,0,0,0,4,0,hull);
            check(f.NodeCount==1 && f.Tracks[0].Nodes[0].Born==0,"backwards clock starts fresh trail");
            f.Clear(); f.Observe(1,0,0,0,0,0,0,hull); f.Observe(1,0,0,0,0,double.NaN,0,hull);
            check(f.NodeCount==0,"stationary or invalid observations cannot create a wake");
            f.Splash(2,0,0,0,0,8,0.3);f.Splash(2,0.1,0,0,0,8,0.3);
            check(f.Rings.Count==1 && f.Rings[0].Speed>0,"impact hook and wet-transition polling deduplicate splash rings");
            f.Expire(8); check(f.Rings.Count==0,"splash rings expire at eight seconds");
            for(int t=0;t<60;t++)for(int id=0;id<64;id++)f.Observe(id,t*0.5,t*2,id,0,4,0,hull);
            check(f.Tracks.Count<=WakeField.MaxTracks && f.NodeCount==WakeField.MaxNodes,"source and node caps hold under overload");
            for(int id=0;id<100;id++) f.Splash(id,30,id,0,0,6,1);
            check(f.Rings.Count==WakeField.MaxRings,"ring cap holds under overload");
            var map=new WakeMap(); var watch=Stopwatch.StartNew();
            map.Paint(f,30,40,0,40);watch.Stop();
            check(map.Samples<=WakeMap.MaxSamples && map.Drawn<=WakeField.MaxDrawn && map.Pixels.All(float.IsFinite),
                $"overload raster bounded: {map.Samples} samples, {map.Drawn} sources, {watch.Elapsed.TotalMilliseconds:F1}ms (not game performance)");
            for(int id=64;id<100;id++)f.Observe(id,30,0,id,0,4,0,hull);
            check(f.Tracks.Count==WakeField.MaxTracks && f.NodeCount<=WakeField.MaxNodes,"new sources evict oldest tracks at the source cap");
            var dense=new WakeField();
            for(int i=0;i<40;i++)for(int id=0;id<32;id++)dense.Observe(id,i*0.5,-80+i*2,id*0.2,0,4,0,new WakeHull(180,45,15));
            watch.Restart();map.Paint(dense,19.5,0,0,0);watch.Stop();
            check(map.Samples==WakeMap.MaxSamples && map.Drawn<=WakeField.MaxDrawn,
                $"forced worst-case raster stops exactly at {map.Samples} samples ({watch.Elapsed.TotalMilliseconds:F1}ms, offline CPU only)");
            f.Expire(220);
            check(f.NodeCount==0 && f.Tracks.Count==0 && f.Rings.Count==0,"all histories expire without further source updates");
            var cfg=new SeaConfig {WakeEnabled=false,WakeStrength=0};
            {
                var copy=SerializerUtil.Deserialize<SeaConfig>(SerializerUtil.Serialize(cfg));
                check(!copy.WakeEnabled && copy.WakeStrength==0,"config sends explicit disabled/zero values through protobuf");
            }
            check(new SeaConfig().WakeEnabled && new SeaConfig {WakeStrength=float.NaN}.Checked().WakeStrength==1
                && new SeaConfig {WakeStrength=9}.Checked().WakeStrength==2,"old config defaults on; bad strength repaired and bounded 0-2");
            // Evaluate one wave place before/after arbitrary wake work. There is no wake input in any wave method.
            var p=new WavePlace();var spec=new WaveSpectrum(4242,254000);
            p.Update(spec,10,new SeaState(5.5,1,0,Math.Sqrt(165*2*Math.PI/9.81)),new Wind(1.5,0));
            double[] before=Enumerable.Range(0,100).Select(i=>p.HeightAt(i*1.37,i*2.19)).ToArray();
            f.Observe(1,10,0,0,0,8,0,hull);map.Paint(f,10,0,0,0);f.Clear();
            check(before.SequenceEqual(Enumerable.Range(0,100).Select(i=>p.HeightAt(i*1.37,i*2.19))),
                "wake creation/raster/disposal leave the existing ship wave samples bit-identical");
            f.Observe(1,0,0,0,125.875,4,0,hull);map.Paint(f,0,0,120,0);
            bool height=true,any=false;
            for(int i=0;i<map.Pixels.Length;i+=4)if(map.Pixels[i+3]>0.01) {any=true;height &= Math.Abs(map.Pixels[i+2]/map.Pixels[i+3]-5.875)<1e-5;}
            check(any && height,"atlas stores premultiplied rest-water height for filtering and stacked-water rejection");
            var shift=new WakeField();shift.Observe(1,0,1e8,1e8,125.875,4,0,hull);
            float[] origin=(float[])map.Pixels.Clone();map.Paint(shift,0,1e8,120,1e8);
            check(origin.Zip(map.Pixels,(a,b)=>Math.Abs(a-b)).Max()<1e-6,"large world-coordinate translation preserves the wake raster within float precision");
            f.Clear();map.Paint(f,0,0,0,0);
            check(map.Pixels.All(v=>v==0),"empty/off history clears the atlas without stale foam");
        }
    }
}
