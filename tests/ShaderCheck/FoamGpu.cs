using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using OpenTK.Graphics.OpenGL4;
using Sea;

namespace ShaderCheck
{
    static partial class Program
    {
        // Exact pre.9 foam equation, retained only as the before-image fixture. Sea's own GPL-3.0 code.
        const string BeforeFoam = @"
float beforeFoam(vec2 p, float crest, float coverage, float spume) {
    float streak = 0.7 * seaFoamNoise(p * vec2(0.0625, 0.5))
        + 0.3 * seaFoamNoise(p * vec2(0.125, 1.0) + vec2(37,91));
    float aa = max(fwidth(streak), 0.025);
    float trails = min(1.0, 20.0 * coverage) * smoothstep(0.88 - 0.8 * coverage - aa, 0.88 - 0.8 * coverage + aa, streak);
    float broken = seaFoamNoise(p * 0.25 + vec2(13,71));
    float breaking = smoothstep(0.65, 1.65, crest) * smoothstep(0.35, 0.75, broken);
    breaking *= min(1.0, 8.0 * coverage);
    float grain = seaFoamNoise(p * 2.0);
    grain = mix(grain, 0.5, smoothstep(0.25, 1.0, length(fwidth(p * 2.0))));
    float blown = seaFoamNoise(vec2(p.x + seaFoamOrigin.z, p.y) * vec2(0.25, 2.0));
    float spray = min(1.0, 8.0 * coverage) * spume * smoothstep(0.1, 1.4, crest) * smoothstep(0.45, 0.7, blown);
    return clamp(max(trails, breaking) * (0.72 + 0.28 * grain) + spray * 0.35, 0.0, 1.0);
}
";
        static void FoamGpu()
        {
            const int n=512;
            const string vertex="#version 330 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0,1);}";
            string fragment="#version 330 core\nuniform vec3 seaFoamOrigin;\n"+CelestialShader.FoamDeclarations+CelestialShader.WakeDeclarations+BeforeFoam+@"
uniform vec2 testToward;
uniform vec2 testAnchor;
uniform vec2 testWorldOrigin;
uniform float testSpan;
uniform float testCoverage;
uniform float testStrength;
uniform float testFresh;
uniform int testBefore;
uniform int testPart;
uniform int testWakes;
uniform int testWaveCount;
uniform vec4 testWave[24];
out vec4 outColor;
void main() {
    // North up in the image: +X is screen right/east, +Z is screen down/south.
    vec2 world=(gl_FragCoord.xy / 512.0 - 0.5) * vec2(testSpan,-testSpan) + testWorldOrigin;
    vec2 p=seaFoamCoordinates(world,testToward,testAnchor);
    float crest=0.0, variance=0.0, fall=0.0, fallVariance=0.0;
    for(int i=0;i<testWaveCount;i++) {
        vec4 w=testWave[i]; float longWave=1.0-smoothstep(0.3,0.6,length(w.xy));
        crest+=longWave*w.w*sin(dot(w.xy,world)+w.z);
        variance+=0.5*longWave*longWave*w.w*w.w;
        float omega=sqrt(9.81*length(w.xy));
        fall+=longWave*w.w*omega*cos(dot(w.xy,world)+w.z);
        fallVariance+=0.5*longWave*longWave*w.w*w.w*omega*omega;
    }
    crest/=max(sqrt(variance),0.0001);
    fall/=max(sqrt(fallVariance),0.0001);
    float wind=testBefore!=0 ? beforeFoam(p,crest,testCoverage,clamp((testStrength-0.9)/0.6,0.0,1.0))
        : seaWindFoam(p,crest,testCoverage,testStrength,fall);
    float wake=0.0;
    if(testPart==1) wind=seaCrestFoam(p,2.0,testCoverage);
    if(testPart==2) wind=seaWindStreaks(p,testStrength);
    if(testPart==3) wind=seaFoamCells(world,0.12);
    if(testPart==4) wind=seaFoamCells(world,1.0);
    if(testPart==5) wind=seaWakeTexture(world,testFresh);
    if(testPart==0 && testWakes!=0) {
        vec2 sample=seaWakeSample(world,0.0,512.0);
        wake=sample.x * (testBefore!=0 ? 0.30+0.70*smoothstep(0.2,0.85,seaFoamNoise(p*1.5)) : seaWakeTexture(world,sample.y));
        if(testWakes==2) wind=0.0;
    }
    outColor=vec4(wind,wake,clamp(0.5+crest*0.2,0.0,1.0),1.0);
}";
            int prog=Build("issue 35: production foam functions + pre.9 before fixture",vertex,fragment,new Dictionary<string,int>());
            if(prog==0)return;
            int vao=GL.GenVertexArray(),target=GL.GenTexture(),atlas=GL.GenTexture(),fbo=GL.GenFramebuffer();
            GL.BindVertexArray(vao);GL.ActiveTexture(TextureUnit.Texture0);GL.BindTexture(TextureTarget.Texture2D,target);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,n,n,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);
            GL.Viewport(0,0,n,n);GL.UseProgram(prog);
            var field=new WakeField();var map=new WakeMap();var hull=new WakeHull(22,6,2);
            for(int i=0;i<=320;i++)field.Observe(1,i*0.1,6,-60+i*0.2,0,0,2,hull);
            map.Paint(field,32,0,0,0);
            GL.ActiveTexture(TextureUnit.Texture12);GL.BindTexture(TextureTarget.Texture2D,atlas);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba16f,n,n,0,PixelFormat.Rgba,PixelType.Float,map.Pixels);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            GL.Uniform1(GL.GetUniformLocation(prog,"seaWakeTex"),12);
            GL.Uniform4(GL.GetUniformLocation(prog,"seaWakeArea"),-128f,-128f,1f/256,1f);
            var spec=new WaveSpectrum(4242,254000);
            float[] Draw(double strength=1.2,double from=270,bool before=false,int part=0,double span=64,int wakes=0,float ax=0,float ay=0,float ox=0,float oz=0,float fresh=1)
            {
                var cfg=new SeaConfig {SeaWindFrom=(float)from,SeaWindStrength=(float)strength};
                var model=new WindModel(new WorldShape(),cfg);var wind=Wind.FromCompass(from,strength);
                var place=new WavePlace();place.Update(spec,300,model.SeaAt(50000,50000,3000),wind);
                float[] waves=new float[24*4];int count=place.FillUniforms(waves,0,0);
                GL.Uniform1(GL.GetUniformLocation(prog,"testWaveCount"),count);GL.Uniform4(GL.GetUniformLocation(prog,"testWave"),count,waves);
                var direction=Wind.FromCompass(from,1);
                GL.Uniform2(GL.GetUniformLocation(prog,"testToward"),(float)direction.X,(float)direction.Z);
                GL.Uniform2(GL.GetUniformLocation(prog,"testAnchor"),ax,ay);GL.Uniform2(GL.GetUniformLocation(prog,"testWorldOrigin"),ox,oz);
                GL.Uniform1(GL.GetUniformLocation(prog,"testSpan"),(float)span);
                GL.Uniform1(GL.GetUniformLocation(prog,"testCoverage"),(float)WaveSpectrum.WhitecapCoverage(strength));
                GL.Uniform1(GL.GetUniformLocation(prog,"testStrength"),(float)strength);
                GL.Uniform1(GL.GetUniformLocation(prog,"testFresh"),fresh);
                GL.Uniform1(GL.GetUniformLocation(prog,"testBefore"),before?1:0);GL.Uniform1(GL.GetUniformLocation(prog,"testPart"),part);GL.Uniform1(GL.GetUniformLocation(prog,"testWakes"),wakes);
                GL.DrawArrays(PrimitiveType.Triangles,0,3);
                float[] data=new float[n*n*4];GL.ReadPixels(0,0,n,n,PixelFormat.Rgba,PixelType.Float,data);return data;
            }
            double Mean(float[] data,int channel=0)=>Enumerable.Range(0,n*n).Average(i=>(double)data[i*4+channel]);
            double Area(float[] data,float threshold)=>Enumerable.Range(0,n*n).Count(i=>data[i*4]>threshold)/(double)(n*n);
            Directory.CreateDirectory("obj/foam35");
            var rows=new List<string>{"strength,input_coverage,version,mean_opacity,area_above_0.1,area_above_0.5"};
            var html=new StringBuilder("<!doctype html><meta charset='utf-8'><title>Sea issue 35 GPU foam</title><style>body{background:#152d38;color:#e5eef3;font:16px system-ui;margin:2rem}main{display:grid;grid-template-columns:repeat(2,minmax(200px,512px));gap:1rem}figure{margin:0}img{width:100%}figcaption{padding:.4rem 0}p{max-width:85ch}</style><h1>Sea pre.9 → pre.10 foam</h1><p>GPU production foam functions; actual 24-train Sea spectrum, fully developed, key 4242. North is up, east right, south down. Orange arrows show wind TOWARD, not wind-from. Each overview covers 64 × 64 blocks. These are diagnostic top views, not game screenshots; water lighting is schematic. Wakes are off in the four-strength comparison.</p><main>");
            double previous=-1;
            foreach(double s in new[]{0.3,0.8,1.2,1.5}) {
                foreach(bool before in new[]{true,false}) {
                    float[] data=Draw(s,before:before);double mean=Mean(data),area=Area(data,0.1f);
                    string version=before?"pre9":"pre10",name=$"{version}-{s.ToString("0.0",CultureInfo.InvariantCulture)}";
                    rows.Add(FormattableString.Invariant($"{s},{WaveSpectrum.WhitecapCoverage(s):F7},{version},{mean:F7},{area:F7},{Area(data,0.5f):F7}"));
                    SaveFoamImage("obj/foam35/"+name+".png",data,n,Wind.FromCompass(270,1));
                    html.Append($"<figure><img src='{name}.png'><figcaption>{version}, wind {s:0.0} from W270° → E (+X); mean {mean:P2}, area&gt;0.1 {area:P2}</figcaption></figure>");
                    Console.WriteLine($"  foam35 {s:0.0} {version}: input {100*WaveSpectrum.WhitecapCoverage(s):F3}%, mean {100*mean:F3}%, area > .1 {100*area:F3}%");
                    if(!before) {
                        Check(mean>previous && (s!=0.3 || mean<0.002),$"GPU: production-spectrum foam {s:0.0} follows wind; breeze sparse (mean {mean:F6})");previous=mean;
                    }
                }
            }
            html.Append("</main><h2>Rotated game compass inputs, 1.2</h2><main>");
            foreach(double from in new[]{270.0,0,90,315}) {
                var toward=Wind.FromCompass(from,1);var raw=Draw(from:from,part:2);
                double along=Gradient(raw,toward.X,-toward.Z,n),across=Gradient(raw,toward.Z,toward.X,n);
                Check(across>along*2,$"GPU: from {from:0}°, toward ({toward.X:F3},{toward.Z:F3}), cross/along windrow gradient {across/along:F2}");
                string name=$"bearing-{from:0}";SaveFoamImage("obj/foam35/"+name+".png",Draw(from:from),n,toward);
                html.Append($"<figure><img src='{name}.png'><figcaption>From {from:0}°, toward ({toward.X:F3}, {toward.Z:F3}); same world axes as game</figcaption></figure>");
            }
            // Signed advection, not just unsigned anisotropy: decreasing along anchor moves foam DOWNWIND.
            var start=Draw(part:2);var drift=Draw(part:2,ax:-4);double error=0;
            for(int y=0;y<n;y++)for(int x=0;x<n-32;x++)error=Math.Max(error,Math.Abs(start[(y*n+x)*4]-drift[(y*n+x+32)*4]));
            Check(error<0.0001,$"GPU: -4m along-anchor drift translates windrows +4m east/downwind (max error {error:E2})");
            var noRows=Draw(0.3,part:2);Check(noRows.Where((v,i)=>i%4==0).All(v=>v==0),"GPU: no wind streaks at 0.3; rows begin only around Beaufort 8");
            var patches=Draw(part:1);int longest=0;
            for(int x=0;x<n;x++){int run=0;for(int y=0;y<n;y++){run=patches[(y*n+x)*4]>.2?run+1:0;longest=Math.Max(longest,run);}}
            Check(longest*64.0/n<16,$"GPU: constant breaking crest still has finite patches, longest cross-crest run {longest*64.0/n:F2}m over 64m");
            var cells=Draw(part:3,span:8);var lace=Draw(part:4,span:8);
            Check(Area(cells,0.01f)<0.95 && Area(lace,0.01f)<Area(cells,0.01f)-0.1,"GPU: resolved bubbles have actual holes; old foam becomes lace with more holes");
            var tiny=Draw(part:4,span:512);
            Check(tiny.Where((v,i)=>i%4==0).Max()-tiny.Where((v,i)=>i%4==0).Min()<0.001,"GPU: subpixel cellular foam filters to its mean instead of shimmering cells");
            var calm=Draw(0);Check(Mean(calm)==0,"GPU: zero actual wind produces zero wind foam");
            var wrapped=Draw(part:2,ax:65536,ay:65536);
            Check(start.Where((v,i)=>i%4==0).Zip(wrapped.Where((v,i)=>i%4==0),(a,b)=>Math.Abs(a-b)).Max()<0.001,"GPU: windrow world-anchor wrap remains continuous");
            var wakeE=Draw(part:5,span:8,from:270);var wakeN=Draw(part:5,span:8,from:0);
            Check(wakeE.Where((v,i)=>i%4==0).SequenceEqual(wakeN.Where((v,i)=>i%4==0)),"GPU: wake texture is independent of wind direction");
            var movedWake=Draw(part:5,span:8,ox:8192,oz:8192);
            Check(wakeE.Where((v,i)=>i%4==0).Zip(movedWake.Where((v,i)=>i%4==0),(a,b)=>Math.Abs(a-b)).Max()<0.001,
                "GPU: wake film world-origin wrap is continuous");
            var windOnly=Draw(0.8);var wakeOnly=Draw(0.8,wakes:2);var combined=Draw(0.8,wakes:1);
            Check(Enumerable.Range(0,n*n).All(i=>windOnly[i*4]==combined[i*4] && wakeOnly[i*4+1]==combined[i*4+1]),
                "GPU: isolating wind/wakes preserves each contribution and the same wake history");
            html.Append("</main><h2>Isolation: same scene / wake history, wind 0.8 east</h2><main>");
            foreach(var item in new[]{("wind-only",0),("wakes-only",2),("combined",1)}) foreach(bool before in new[]{true,false}) {
                string name=(before?"pre9-":"pre10-")+item.Item1;
                var data=Draw(0.8,wakes:item.Item2,before:before);SaveFoamImage("obj/foam35/"+name+".png",data,n,Wind.FromCompass(270,1));
                html.Append($"<figure><img src='{name}.png'><figcaption>{name}: ship travels south (+Z), wind travels east (+X). Windrows cross crests; the broad curved trail belongs to the ship.</figcaption></figure>");
            }
            html.Append("</main><h2>8m close-ups: same breaking crest region, then fresh / old wake film</h2><main>");
            var overview=Draw();int peak=0;for(int i=0;i<n*n;i++)if(overview[i*4]>overview[peak*4])peak=i;
            float zx=(peak%n+0.5f)/n*64-32,zz=32-(peak/n+0.5f)/n*64;
            foreach(bool before in new[]{true,false}) {
                string name=before?"close-pre9":"close-pre10";
                SaveFoamImage("obj/foam35/"+name+".png",Draw(before:before,span:8,ox:zx,oz:zz),n,Wind.FromCompass(270,1));
                html.Append($"<figure><img src='{name}.png'><figcaption>{name}; 8m, same world location</figcaption></figure>");
            }
            foreach(var item in new[]{("wake-fresh",1f),("wake-lace",0f)}) {
                SaveFoamImage("obj/foam35/"+item.Item1+".png",Draw(part:5,span:8,fresh:item.Item2),n,Wind.FromCompass(270,1));
                html.Append($"<figure><img src='{item.Item1}.png'><figcaption>{item.Item1}; 8m texture test, holes are clear water</figcaption></figure>");
            }
            html.Append("</main><p>The screenshot alone cannot identify its toward-camera ribbon without a bearing/status capture. In game use .sea foam wind / wakes / off / all without deleting histories. If it survives off, it is outside Sea's wind/wake overlay (e.g. vanilla contact foam or reflection).</p>");
            File.WriteAllText("obj/foam35/index.html",html.ToString());File.WriteAllLines("obj/foam35/coverage.csv",rows);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,0);GL.UseProgram(0);GL.ActiveTexture(TextureUnit.Texture0);
            GL.DeleteFramebuffer(fbo);GL.DeleteTexture(target);GL.DeleteTexture(atlas);GL.DeleteVertexArray(vao);GL.DeleteProgram(prog);
            Check(GL.GetError()==ErrorCode.NoError,"GPU: issue 35 foam rendering/texture cleanup has no GL error");
        }
        static double Gradient(float[] data,double dx,double dy,int n)
        {
            int ix=(int)Math.Round(dx*3),iy=(int)Math.Round(dy*3);double sum=0;int count=0;
            for(int y=4;y<n-4;y++)for(int x=4;x<n-4;x++){sum+=Math.Abs(data[(y*n+x)*4]-data[((y+iy)*n+x+ix)*4]);count++;}
            return sum/count;
        }
        static void SaveFoamImage(string path,float[] data,int n,Wind toward)
        {
            var rgb=new byte[n*n*3];
            for(int y=0;y<n;y++)for(int x=0;x<n;x++) {
                int i=((n-1-y)*n+x)*4,j=(y*n+x)*3;double f=Math.Clamp(Math.Max(data[i],data[i+1]),0,1),light=0.7+0.3*data[i+2];
                rgb[j]=(byte)(18*light+(225-18*light)*f);rgb[j+1]=(byte)(58*light+(240-58*light)*f);rgb[j+2]=(byte)(79*light+(235-79*light)*f);
            }
            void Line(double ax,double ay,double bx,double by) {
                int steps=(int)Math.Ceiling(Math.Max(Math.Abs(bx-ax),Math.Abs(by-ay)));
                for(int k=0;k<=steps;k++){int x=(int)Math.Round(ax+(bx-ax)*k/Math.Max(1,steps)),y=(int)Math.Round(ay+(by-ay)*k/Math.Max(1,steps));
                    for(int dy=-1;dy<=1;dy++)for(int dx=-1;dx<=1;dx++)if(x+dx>=0&&x+dx<n&&y+dy>=0&&y+dy<n){int i=((y+dy)*n+x+dx)*3;rgb[i]=255;rgb[i+1]=190;rgb[i+2]=40;}}
            }
            double ex=60+45*toward.X,ey=60+45*toward.Z;Line(60,60,ex,ey);
            Line(ex,ey,ex-12*toward.X-7*toward.Z,ey-12*toward.Z+7*toward.X);Line(ex,ey,ex-12*toward.X+7*toward.Z,ey-12*toward.Z-7*toward.X);
            // Minimal portable PNG writer: no image library/game assets or external plotting installation needed.
            using var png=File.Create(path);png.Write(new byte[]{137,80,78,71,13,10,26,10});
            void Chunk(string type,byte[] bytes) {
                byte[] tag=Encoding.ASCII.GetBytes(type);byte[] len=BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(bytes.Length));png.Write(len);png.Write(tag);png.Write(bytes);
                uint crc=0xffffffff;foreach(byte b in tag.Concat(bytes)){crc^=b;for(int k=0;k<8;k++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}
                png.Write(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(unchecked((int)~crc))));
            }
            byte[] header=new byte[13];Array.Copy(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(n)),0,header,0,4);Array.Copy(header,0,header,4,4);header[8]=8;header[9]=2;Chunk("IHDR",header);
            using var memory=new MemoryStream();using(var zip=new ZLibStream(memory,CompressionLevel.Fastest,true))for(int y=0;y<n;y++){zip.WriteByte(0);zip.Write(rgb,y*n*3,n*3);}
            Chunk("IDAT",memory.ToArray());Chunk("IEND",Array.Empty<byte>());
        }
    }
}
