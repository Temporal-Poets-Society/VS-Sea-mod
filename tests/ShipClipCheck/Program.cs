using System;
using System.IO;
using System.Linq;
using System.Runtime.Loader;
using Hansa;
using Sea;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

class Program
{
    static string HansaRoot => Environment.GetEnvironmentVariable("HANSA_ROOT") ?? throw new InvalidOperationException("Set HANSA_ROOT to a read-only Hansa checkout.");
    static int failures;
    static int Main()
    {
        AssemblyLoadContext.Default.Resolving += (ctx,n) => {
            string root=Environment.GetEnvironmentVariable("VINTAGE_STORY");
            foreach(var dir in new[]{root,Path.Combine(root,"Lib"),Path.Combine(root,"Mods")}) {
                string path=Path.Combine(dir,n.Name+".dll"); if(File.Exists(path)) return ctx.LoadFromAssemblyPath(path);
            }
            return null;
        };
        Run();
        return failures;
    }
    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    static void Run()
    {
        var def=JsonConvert.DeserializeObject<ShipDefinition>(File.ReadAllText(Path.Combine(HansaRoot,"assets/hansa/config/ships/caravel.json")));
        def.Init();
        foreach (string file in new[] {typeof(ShipDefinition).Assembly.Location,
            Path.Combine(HansaRoot,"assets/hansa/config/ships/caravel.json"),
            Path.Combine(HansaRoot,"assets/hansa/config/shipmeshes/caravel.json"),
            Path.Combine(HansaRoot,"src/Ship/ShipWater.cs")})
            Console.WriteLine($"SHA256 {Path.GetFileName(file)} {Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant()}");
        Console.WriteLine($"caravel actual: keel {def.KeelY}, deck {def.DeckY}, clip h {def.WaterClip.Height}, len {def.WaterClip.HalfLength}, center {def.WaterClip.CenterX}, rake {def.WaterClip.DryRake}");
        foreach(double pitch in new[]{0.0,12.0,-12.0}) foreach(double roll in new[]{0.0,20.0,-20.0,35.0}) {
            var clip=ShipWater.Clip(def,0,0,0,0.37,pitch*Math.PI/180,roll*Math.PI/180,0,1.5f,double.NaN);
            int inside=0, missed=0, floorMiss=0; string example="";
            for(double x=-12;x<7;x+=.15) for(double y=-2.65;y<1.1;y+=.15) {
                double width=def.WaterClip.DryOutline.At(x,y);
                for(double z=-width+.06;z<width-.06;z+=.15) {
                    if(!def.DryMask.SolidAt(x,y,z)) continue;
                    if(Math.Abs(z)>width-.06)continue;
                    inside++;
                    ShipFrame.ToWorld(.37,pitch*Math.PI/180,roll*Math.PI/180,x,y,z,out double wx,out double wy,out double wz);
                    if(!clip.InDryHull(wx,wy,wz)) {
                        missed++; if(y<-1.6)floorMiss++;
                        if(example=="")example=$"local {x:F3},{y:F3},{z:F3}, world {wx:F3},{wy:F3},{wz:F3}";
                    }
                }
            }
            Console.WriteLine($"pitch {pitch}, roll {roll}: tested {inside}, missed {missed}, hold-bottom misses {floorMiss}; {example}");
            if (missed != 0 || inside < 100000) failures++;
        }
        Console.WriteLine($"Flooded iff Water > 0.5; capacity {def.DamageParts.Capacity:F3}, initial .6 tonne level {def.DamageParts.FloodLevel(.6):F3}, 5 tonne level {def.DamageParts.FloodLevel(5):F3}");
        foreach(var deck in def.Decks) Console.WriteLine($"deck {deck.Code}: Y {deck.Y}");
        var mesh=JObject.Parse(File.ReadAllText(Path.Combine(HansaRoot,"assets/hansa/config/shipmeshes/caravel.json")));
        foreach (bool correctOrigin in new[] {false,true})
        foreach (var tilt in new[] {(p:0.0,r:0.0),(p:12.0,r:35.0),(p:-12.0,r:-35.0)})
        foreach(var group in ((JObject)mesh["groups"]).Properties().Where(g => g.Name=="interior" || g.Name=="bottom")) {
            int points=0, missed=0; double worst=0; string example="";
            var clip=ShipWater.Clip(def,0,0,0,0,tilt.p*Math.PI/180,tilt.r*Math.PI/180,0,1.5f,double.NaN);
            if (correctOrigin) {
                // Proposed Hansa-only registration correction, tested here without changing Hansa's files.
                ShipFrame.ToWorld(0,tilt.p*Math.PI/180,tilt.r*Math.PI/180,0,def.WaterClip.Height,0,
                    out double hx,out double hy,out double hz);
                clip.X=hx; clip.WaterY=hy; clip.Z=hz;
            }
            foreach(JArray quad in group.Value) for(int v=0;v<4;v++) {
                double x=(double)quad[1+8*v], y=(double)quad[2+8*v], z=(double)quad[3+8*v];
                if(x<-12||x>7||y<-2.7||y>-.3)continue;
                points++;
                ShipFrame.ToWorld(0,tilt.p*Math.PI/180,tilt.r*Math.PI/180,x,y,z,out double wx,out double wy,out double wz);
                if(!clip.InDryHull(wx,wy,wz)) {
                    missed++;
                    WaterClipBox.HullFrame(clip.SlopeAlong,clip.SlopeAcross,wz-clip.Z,wy-clip.WaterY,-wx+clip.X,out double ax,out double ay,out double az);
                    double lack=Math.Abs(az)-clip.DryHalfWidthAt((ax-clip.CentreOffset)/(clip.HalfLength-clip.DryInset),ay);
                    worst=Math.Max(worst,lack); if(example=="")example=$"{x:F3},{y:F3},{z:F3} lack {lack:F4}";
                }
            }
            Console.WriteLine($"{(correctOrigin?"proposed rigid pivot":"current registration")} mesh {group.Name} pitch {tilt.p}/heel {tilt.r}: {points} vertices, {missed} outside clip, max width lack {worst:F4}; {example}");
            if ((correctOrigin && missed != 0) || points < 9000) failures++;
        }
        def.Waterplane(out double halfLength,out double halfBeam,out double centerX);
        ShipWater.Plane(0,20*Math.PI/180,out double floodAlong,out double floodAcross);
        var shallow=ShipWater.Surface(def,def.DamageParts.FloodLevel(.6),floodAlong,floodAcross,ShipWater.DefaultInset);
        double maxLocalWater=shallow.SelectMany(run=>run).SelectMany(point=>Enumerable.Range(0,point.Length/3)
            .Where(i=>point[3*i]>=-12&&point[3*i]<=7).Select(i=>point[3*i+1])).DefaultIfEmpty(double.NegativeInfinity).Max();
        Console.WriteLine($"only .6 m3 water, 20 deg heel: surface reaches local Y {maxLocalWater:F3}, hold floor -1.875 (a low-side strip can be visible)");
        foreach(bool legacy in new[]{true,false}) foreach(double strength in new[]{.3,1.5}) {
            var model=new WindModel(new WorldShape(),new SeaConfig{SeaWindStrength=(float)strength});
            var sea=model.SeaAt(0,0,100);
            var waves=new WavePlace(); waves.Update(new WaveSpectrum(4242,0),0,sea,new Wind(strength,0));
            if(legacy) waves=SeaTests.StormEvidence.Before(new WaveSpectrum(4242,0),strength,0.1+2.4*strength*strength,0);
            // This is a chosen cross-sea course, not a reconstruction of the screenshot's camera heading.
            double volume=0, first=-1, peak=0, yaw=Math.PI-358*Math.PI/180, speed=13.3;
            for(double t=0;t<120;t+=.25) {
                double x=382876+Math.Sin(yaw)*speed*t,z=65229+Math.Cos(yaw)*speed*t;
                var response=ShipMotion.Waves(waves,1,t,x,z,yaw,speed,halfLength,halfBeam,centerX,def.Motion,1);
                var pose=new FloodPose{X=x,Y=response.Heave,Z=z,Yaw=yaw,Pitch=response.Pitch,Roll=response.Roll,Surface=(px,pz)=>waves.HeightAt(px,pz,t)};
                double pour=ShipFlood.Downflooding(def.DamageParts,pose,_=>false);
                volume+=pour*.25; peak=Math.Max(peak,pour); if(volume>.5&&first<0)first=t;
            }
            Console.WriteLine($"{(legacy?"pre.7":"pre.8")} healthy caravel, closed hatches, 120s, strength {strength}: downflood {volume:F4} m3, peak {peak:F4} m3/s, crosses .5 at {first:F2}s (frozen intact response, no pump/damage/sinkage feedback)");
            if ((strength == .3 && volume != 0) || (strength == 1.5 && volume <= .5)) failures++;
        }
        File.WriteAllText("obj/caravel-clip-snapshot.json",JsonConvert.SerializeObject(ShipWater.Clip(def,0,0,0,0,0,0,0,1.5f,double.NaN),Formatting.Indented));
        Console.WriteLine(failures == 0 ? "ALL OK (Hansa rigid-pivot correction covers mesh; separate storm downflooding reproduced)" : $"{failures} FAILED");
    }
}
