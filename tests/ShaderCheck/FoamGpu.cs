using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using Sea;

namespace ShaderCheck
{
    static partial class Program
    {
        static WavePlace CrestPlace(double strength, double scale=1, double from=270)
        {
            var model=new WindModel(new WorldShape(),new SeaConfig {SeaWindStrength=(float)strength,SeaWindFrom=(float)from,StormHeightScale=(float)scale});
            var p=new WavePlace();p.Update(new WaveSpectrum(4242,254000),300,model.SeaAt(50000,50000,3000),Wind.FromCompass(from,strength));return p;
        }
        static void WaveUpload(int prog, WavePlace p, double x=0, double z=0, bool before=false)
        {
            float[] u=new float[96];int count=p.FillUniforms(u,x,z);
            GL.UseProgram(prog);GL.Uniform1(GL.GetUniformLocation(prog,"sea_WaveCount"),count);
            if(count>0)GL.Uniform4(GL.GetUniformLocation(prog,"sea_Wave"),count,u);
            GL.Uniform1(GL.GetUniformLocation(prog,"sea_WaveShape"),before?0:(float)p.Shape);
        }
        static void FoamGpu()
        {
            // Production vertex geometry, not a second C# implementation masquerading as a GPU check.
            string pointVertex="#version 330 core\n"+WaterShader.Geometry+@"
uniform float testFactor; uniform int testMode; uniform int testGradient=0; flat out vec4 result;
void main(){
    vec2 cell=vec2(gl_VertexID%16,gl_VertexID/16);
    vec2 q=(cell-7.5)*7.3;
    float factor=testGradient!=0?clamp(0.6+q.x*0.004+q.y*0.002,0.2,1.0):testFactor;
    vec3 p=sea_Surface(q,factor);vec2 slope;float rough;vec3 crest;
    sea_Differential(q,factor,0.0,slope,rough,crest);
    result=testMode==0?vec4(p,1):vec4(slope,crest.y,1);
    gl_Position=vec4((cell+0.5)/16.0*2.0-1.0,0,1);gl_PointSize=1.0;
}";
            int prog=Build("issue 37: production Gerstner vertex positions/Jacobian",pointVertex,
                "#version 330 core\nflat in vec4 result;out vec4 color;void main(){color=result;}",new Dictionary<string,int>());
            if(prog==0)return;
            int vao=GL.GenVertexArray(),tex=GL.GenTexture(),fbo=GL.GenFramebuffer();GL.BindVertexArray(vao);
            GL.BindTexture(TextureTarget.Texture2D,tex);GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,512,512,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,tex,0);
            GL.Viewport(0,0,16,16);
            double inverseError=0,positionError=0,normalError=0;int points=0;
            foreach(double strength in new[]{0.3,0.8,1.5}) foreach(double scale in new[]{1.0,2.2}) foreach(double from in new[]{270.0,315})
            foreach(double origin in new[]{0.0,812345.5}) foreach(float factor in new[]{1f,.2f,1f/90}) {
                var p=CrestPlace(strength,scale,from);double oz=origin==0?0:-233007.25;
                WaveUpload(prog,p,origin,oz);GL.Uniform1(GL.GetUniformLocation(prog,"testFactor"),factor);
                float[] Render(int mode){GL.Uniform1(GL.GetUniformLocation(prog,"testMode"),mode);GL.DrawArrays(PrimitiveType.Points,0,256);
                    var data=new float[1024];GL.ReadPixels(0,0,16,16,PixelFormat.Rgba,PixelType.Float,data);return data;}
                var pos=Render(0);var normals=Render(1);
                for(int i=0;i<256;i++) {
                    double qx=(i%16-7.5)*7.3,qz=(i/16-7.5)*7.3;
                    var cpu=p.SurfaceAt(origin+qx,oz+qz,factor);
                    positionError=Math.Max(positionError,Math.Max(Math.Abs(cpu.X-origin-pos[i*4]),Math.Max(Math.Abs(cpu.Y-pos[i*4+1]),Math.Abs(cpu.Z-oz-pos[i*4+2]))));
                    double x=origin+pos[i*4],z=oz+pos[i*4+2];
                    inverseError=Math.Max(inverseError,Math.Abs(p.HeightAt(x,z)*factor-pos[i*4+1]));
                    const double d=.005;
                    normalError=Math.Max(normalError,Math.Max(Math.Abs((p.HeightAt(x+d,z)-p.HeightAt(x-d,z))*factor/(2*d)-normals[i*4]),
                        Math.Abs((p.HeightAt(x,z+d)-p.HeightAt(x,z-d))*factor/(2*d)-normals[i*4+1])));points++;
                }
            }
            Check(positionError<.001,$"GPU forward positions = CPU parametric surface: {points} vertices, max {positionError:E3} blocks");
            Check(inverseError<.001,$"CPU ship height at GPU-displaced world XZ: max {inverseError:E3} blocks, factors 1/.2/1:90, scales 1/2.2, large origins, two bearings");
            Check(normalError<.003,$"GPU inverse-Jacobian normals = CPU world-height finite differences: max slope error {normalError:E3}");
            var coast=CrestPlace(1.5);WaveUpload(prog,coast);
            GL.Uniform1(GL.GetUniformLocation(prog,"testGradient"),1);GL.Uniform1(GL.GetUniformLocation(prog,"testMode"),0);
            GL.DrawArrays(PrimitiveType.Points,0,256);var coastal=new float[1024];GL.ReadPixels(0,0,16,16,PixelFormat.Rgba,PixelType.Float,coastal);
            double gradientError=0,wrongLookupError=0;
            double Factor(double x,double z)=>Math.Clamp(.6+x*.004+z*.002,.2,1);
            for(int i=0;i<256;i++) {
                double x=coastal[i*4],z=coastal[i*4+2],y=coastal[i*4+1];var q=coast.ParameterAt(x,z);
                gradientError=Math.Max(gradientError,Math.Abs(coast.HeightAt(x,z)*Factor(q.X,q.Z)-y));
                wrongLookupError=Math.Max(wrongLookupError,Math.Abs(coast.HeightAt(x,z)*Factor(x,z)-y));
            }
            Check(gradientError<.001 && wrongLookupError>.005,$"GPU: shelter sampled at inverse mesh coordinate across a gradient: {gradientError:E3} blocks (world-coordinate lookup would miss by {wrongLookupError:F4})");
            GL.DeleteProgram(prog);
            CrestDryGpu(pointVertex);
            FoamPatchesGpu();
            CrestRendersGpu();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,0);GL.UseProgram(0);GL.DeleteFramebuffer(fbo);GL.DeleteTexture(tex);GL.DeleteVertexArray(vao);
            Check(GL.GetError()==ErrorCode.NoError,"GPU: crest rendering and readback cleanup has no GL error");
        }
        static void CrestDryGpu(string vertex)
        {
            vertex=vertex.Replace("flat out vec4 result;","flat out vec4 result;out vec3 sea_FragPos;")
                .Replace("result=testMode==0?","sea_FragPos=p;result=testMode==0?");
            string fragment=WaterShader.InjectFragment("#version 330 core\nflat in vec4 result;out vec4 color;void main(){color=result;}");
            int prog=Build("issue 37: displaced Gerstner vertices + production dry cut",vertex,fragment,new Dictionary<string,int>());if(prog==0)return;
            var place=CrestPlace(1.5);WaveUpload(prog,place);GL.Uniform1(GL.GetUniformLocation(prog,"testFactor"),1f);
            GL.Uniform1(GL.GetUniformLocation(prog,"testMode"),0);
            var clip=new WaterClipBox {HalfLength=24,HalfWidth=12,Dry=true,DryDepth=20,DryHeight=20,Yaw=.4,SlopeAlong=.1f,SlopeAcross=-.2f};
            GL.Uniform1(GL.GetUniformLocation(prog,"sea_ClipCount"),1);
            GL.Uniform4(GL.GetUniformLocation(prog,"sea_ClipA"),1,new[]{0f,0f,(float)Math.Cos(clip.Yaw),(float)Math.Sin(clip.Yaw)});
            GL.Uniform4(GL.GetUniformLocation(prog,"sea_ClipB"),1,new[]{24f,-1000f,0f,0f});
            GL.Uniform4(GL.GetUniformLocation(prog,"sea_ClipC"),1,new[]{.1f,-.2f,0f,0f});
            GL.Uniform4(GL.GetUniformLocation(prog,"sea_ClipD"),1,new[]{0f,20f,20f,1f});
            GL.Uniform4(GL.GetUniformLocation(prog,"sea_ClipP"),4,Enumerable.Repeat(12f,16).ToArray());
            GL.ClearColor(0,0,0,0);GL.Clear(ClearBufferMask.ColorBufferBit);GL.DrawArrays(PrimitiveType.Points,0,256);
            var data=new float[1024];GL.ReadPixels(0,0,16,16,PixelFormat.Rgba,PixelType.Float,data);
            int inside=0,mismatch=0;
            for(int i=0;i<256;i++) {var at=place.SurfaceAt((i%16-7.5)*7.3,(i/16-7.5)*7.3);
                bool dry=clip.InDryHull(at.X,at.Y,at.Z);if(dry)inside++;if(dry!=(data[i*4+3]==0))mismatch++;}
            Check(inside>10 && inside<200 && mismatch==0,$"GPU: dry hull uses horizontally displaced XZ under pitch/heel ({inside} interior vertices, {mismatch} mismatches)");
            GL.DeleteProgram(prog);
        }
        static void FoamPatchesGpu()
        {
            const int n=512;
            const string full="#version 330 core\nvoid main(){vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);gl_Position=vec4(p*2.0-1.0,0,1);}";
            string fragment="#version 330 core\n"+WaterShader.Geometry+CelestialShader.FoamDeclarations+@"
uniform float testFactor;uniform float testCoverage;uniform float testSpan;uniform int testMode;
out vec4 color;
void main(){
    vec2 q=(gl_FragCoord.xy/512.0-0.5)*testSpan;
    vec2 slope;float rough;vec3 crest;sea_Differential(q,testFactor,0.0,slope,rough,crest);
    float foam=sea_WindFoam(q,crest,testCoverage);
    if(testMode==1)foam=sea_FoamCells(q,0.12);
    if(testMode==2)foam=sea_FoamCells(q,1.0);
    if(testMode==3)foam=sea_WindFoam(q,vec3(2.0,0.5,0.0),testCoverage);
    if(testMode==4)foam=sea_WindFoam(q,vec3(2.0,0.0,0.0),testCoverage);
    if(testMode==5)foam=sea_WindFoam(q,vec3(2.0,0.5,2.0),testCoverage);
    color=vec4(foam,crest.x,crest.y,1);
}";
            int prog=Build("issue 37: crest-only production foam",full,fragment,new Dictionary<string,int>());if(prog==0)return;
            GL.Viewport(0,0,n,n);
            float[] Draw(double strength=1.5,float factor=1,int mode=0,float span=128) {
                WaveUpload(prog,CrestPlace(strength));GL.Uniform1(GL.GetUniformLocation(prog,"testCoverage"),(float)WaveSpectrum.WhitecapCoverage(strength));
                GL.Uniform1(GL.GetUniformLocation(prog,"testFactor"),factor);GL.Uniform1(GL.GetUniformLocation(prog,"testMode"),mode);GL.Uniform1(GL.GetUniformLocation(prog,"testSpan"),span);
                GL.DrawArrays(PrimitiveType.Triangles,0,3);var data=new float[n*n*4];GL.ReadPixels(0,0,n,n,PixelFormat.Rgba,PixelType.Float,data);return data;
            }
            double Mean(float[] data)=>Enumerable.Range(0,n*n).Average(i=>(double)data[i*4]);
            double Area(float[] data,float threshold)=>Enumerable.Range(0,n*n).Count(i=>data[i*4]>threshold)/(double)(n*n);
            Directory.CreateDirectory("obj/crests37");var table=new List<string>{"strength,span,whitecap_input,mean_foam,area_over_0.1,max_compression,sheltered_foam"};
            foreach(double s in new[]{.3,.8,1.5}) {
                float span=(float)Math.Max(128,4*CrestPlace(s).Sea.Wavelength);
                var data=Draw(s,span:span);var sheltered=Draw(s,.2f,span:span);double mean=Mean(data),area=Area(data,.1f);
                double compression=Enumerable.Range(0,n*n).Max(i=>data[i*4+2]);
                table.Add(FormattableString.Invariant($"{s},{span},{WaveSpectrum.WhitecapCoverage(s):F7},{mean:F7},{area:F7},{compression:F7},{Mean(sheltered):F7}"));
                Console.WriteLine($"  crests37 {s}: mean foam {100*mean:F4}%, area>.1 {100*area:F4}%, max compression {compression:F4}, sheltered {Mean(sheltered):F7}");
                Check(Mean(sheltered)==0,$"GPU: sheltered factor .2 has zero wind foam at strength {s}, even with sky exposure");
                Check(s!=.3 || mean<.002,"GPU: default breeze remains sparse, not a wind-foam sheet");
                bool onlyBreaking=Enumerable.Range(0,n*n).All(i=>data[i*4]==0 || (data[i*4+1]>.8 && data[i*4+2]>.22));
                Check(onlyBreaking,$"GPU: wind foam {s} is confined to compressed high crest tops");
            }
            var patches=Draw(mode:3);var flat=Draw(mode:4);var old=Draw(mode:5);
            Check(Mean(patches)>0 && Mean(flat)==0 && Mean(old)==0,"GPU: strong wind alone cannot produce foam; patches fade behind crest");
            int longest=0;for(int x=0;x<n;x++){int run=0;for(int y=0;y<n;y++){run=patches[(y*n+x)*4]>.1?run+1:0;longest=Math.Max(longest,run);}}
            Check(longest*128.0/n<16,$"GPU: continuous crest breaks into short patches (longest {longest*128.0/n:F2} blocks)");
            var cells=Draw(mode:1,span:8);var lace=Draw(mode:2,span:8);
            Check(Area(cells,.01f)<.95 && Area(lace,.01f)<Area(cells,.01f)-.1,"GPU: actual bubble holes grow into lace");
            var small=Draw(mode:2,span:512);Check(Area(small,.45f)==1 && Area(small,.5f)==0,"GPU: subpixel bubbles filter to mean");
            Check(Mean(Draw(0))==0,"GPU: no wind, no wind foam");
            File.WriteAllLines("obj/crests37/foam.csv",table);GL.DeleteProgram(prog);
        }
        static void CrestRendersGpu()
        {
            const int width=1024,height=400;
            // Both versions use the same trains/time. Before = pre.10 sinusoidal geometry (Q=0).
            // Side is a vertical cross-section with labelled unequal axes; low view has physical proportions.
            string vertex="#version 330 core\n"+WaterShader.Geometry+@"
uniform float testSpan;uniform float testHeight;uniform int testSide;uniform mat4 testView;
out vec3 position;out vec2 slope;out vec3 crest;
void main(){
    vec2 corner[6]=vec2[6](vec2(0,0),vec2(1,0),vec2(1,1),vec2(0,0),vec2(1,1),vec2(0,1));
    vec2 c=corner[gl_VertexID%6];int cell=gl_VertexID/6;
    vec2 q;
    if(testSide!=0) q=vec2(float(cell)+c.x-testSpan*0.5,0.0);
    else q=(vec2(cell%int(testSpan),cell/int(testSpan))+c)-testSpan*0.5;
    vec3 p=sea_Surface(q,1.0);float rough;sea_Differential(q,1.0,0.0,slope,rough,crest);position=p;
    if(testSide!=0){if(c.y==0.0)p.y=-testHeight;gl_Position=vec4(p.x/(testSpan*.5),p.y/testHeight,0,1);}
    else gl_Position=testView*vec4(p,1);
}";
            // Shading is deliberately a simple shared diagnostic sun/sky, not a copied game shader/texture.
            string fragment="#version 330 core\n"+CelestialShader.FoamDeclarations+@"
in vec3 position;in vec2 slope;in vec3 crest;
uniform vec3 testEye;uniform float testCoverage;uniform int testBefore;uniform int testSide;out vec4 color;
void main(){
    vec3 normal=normalize(vec3(-slope.x,1,-slope.y));
    vec3 eye=normalize(testEye-position),sun=normalize(vec3(-.55,.25,.7));
    float light=.35+.65*max(dot(normal,sun),0.0),fres=.02+.98*pow(1.0-max(dot(normal,eye),0.0),5.0);
    vec3 water=vec3(.035,.20,.24)*light;
    vec3 sky=mix(vec3(.85,.48,.28),vec3(.18,.34,.47),clamp(reflect(-eye,normal).y,0.0,1.0));
    water=mix(water,sky,fres*.8);
    float foam=sea_WindFoam(position.xz,crest,testCoverage);
    // Before geometry render isolates shape; the removed pre.10 windrow code is not retained as a fixture.
    if(testBefore!=0)foam=0.0;
    if(testSide!=0)water=mix(vec3(.025,.17,.22),vec3(.1,.42,.46),.5+.5*normal.y);
    color=vec4(mix(water,vec3(.88,.94,.92),foam),1);
}";
            int prog=Build("issue 37: side/low-angle GPU mesh renders",vertex,fragment,new Dictionary<string,int>());if(prog==0)return;
            int fbo=GL.GenFramebuffer(),tex=GL.GenTexture(),depth=GL.GenRenderbuffer();
            GL.BindTexture(TextureTarget.Texture2D,tex);GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba8,width,height,0,PixelFormat.Rgba,PixelType.UnsignedByte,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,tex,0);
            GL.BindRenderbuffer(RenderbufferTarget.Renderbuffer,depth);GL.RenderbufferStorage(RenderbufferTarget.Renderbuffer,RenderbufferStorage.DepthComponent24,width,height);
            GL.FramebufferRenderbuffer(FramebufferTarget.Framebuffer,FramebufferAttachment.DepthAttachment,RenderbufferTarget.Renderbuffer,depth);
            GL.Viewport(0,0,width,height);GL.Enable(EnableCap.DepthTest);
            var html=new StringBuilder("<!doctype html><meta charset='utf-8'><title>Sea issue 37 crest geometry</title><style>body{background:#182e38;color:#e9f0f4;font:16px system-ui;margin:2rem}main{display:grid;grid-template-columns:repeat(2,minmax(300px,1024px));gap:1rem}figure{margin:0}img{width:100%}p{max-width:95ch}</style><h1>Sea pre.10 → pre.11: crest shape</h1><p>Real GPU vertex geometry, production 24-train spectrum, key 4242, t=300 s. Wind from W270° travels east (+X); waves travel left → right in the side profiles and away from the low camera. Heights, wavelengths, phases and short texture amplitudes are identical. Before uses Q=0; after uses bounded Gerstner orbits. Warm arrow shows travel. The side cross-section enlarges the vertical axis for inspection; the low-angle 1-block mesh has physical proportions. Diagnostic sunset lighting, not a game capture. Removed pre.10 windrows are not reproduced; before foam is hidden to isolate shape. After uses production breaking-only foam. Wakes off.</p><main>");
            var table=new List<string>{"strength,Hs,Lp,shape,compression_bound,side_width,side_vertical_range,low_width,mesh_inverse_max,mesh_inverse_rms"};
            foreach(double s in new[]{.3,.8,1.5}) {
                var p=CrestPlace(s);double sum=0;for(int i=0;i<p.Count;i++)sum+=Math.Abs(p.Amp[i])*Math.Sqrt(p.Kx[i]*p.Kx[i]+p.Kz[i]*p.Kz[i])*WavePlace.ShapeWeight(Math.Sqrt(p.Kx[i]*p.Kx[i]+p.Kz[i]*p.Kz[i]));
                float sideSpan=(float)Math.Ceiling(Math.Clamp(p.Sea.Wavelength*2.5,48,480));
                float lowSpan=(float)Math.Ceiling(Math.Clamp(p.Sea.Wavelength*3,96,600));
                float vertical=(float)Math.Max(.3,p.SignificantHeight*1.1);
                // Quantify the existing 1-block triangulation error separately from the analytic CPU/GPU equality.
                double meshMax=0,mesh2=0;int meshN=0;
                for(int z=-40;z<40;z++)for(int x=-40;x<40;x++) {
                    var a=p.SurfaceAt(x,z);var b=p.SurfaceAt(x+1,z);var c=p.SurfaceAt(x+1,z+1);
                    double h=p.HeightAt((a.X+b.X+c.X)/3,(a.Z+b.Z+c.Z)/3),e=h-(a.Y+b.Y+c.Y)/3;
                    meshMax=Math.Max(meshMax,Math.Abs(e));mesh2+=e*e;meshN++;
                }
                table.Add(FormattableString.Invariant($"{s},{p.SignificantHeight:F6},{p.Sea.Wavelength:F6},{p.Shape:F6},{sum*p.Shape:F6},{sideSpan},{vertical*2},{lowSpan},{meshMax:F6},{Math.Sqrt(mesh2/meshN):F6}"));
                Check(meshMax<.06,$"1-block mesh vs continuous CPU surface {s}: max {meshMax:F5}, RMS {Math.Sqrt(mesh2/meshN):F5} blocks");
                foreach(bool side in new[]{true,false}) foreach(bool before in new[]{true,false}) {
                    WaveUpload(prog,p,before:before);float span=side?sideSpan:lowSpan;
                    var eye=new Vector3(-span*.48f,(float)(p.SignificantHeight*.75+1),span*.03f);
                    var view=Matrix4.LookAt(eye,eye+new Vector3(1,-.14f,0),Vector3.UnitY)*Matrix4.CreatePerspectiveFieldOfView(.65f,width/(float)height,.1f,span*3);
                    GL.UniformMatrix4(GL.GetUniformLocation(prog,"testView"),false,ref view);
                    GL.Uniform3(GL.GetUniformLocation(prog,"testEye"),eye);GL.Uniform1(GL.GetUniformLocation(prog,"testSpan"),span);
                    GL.Uniform1(GL.GetUniformLocation(prog,"testHeight"),vertical);GL.Uniform1(GL.GetUniformLocation(prog,"testSide"),side?1:0);
                    GL.Uniform1(GL.GetUniformLocation(prog,"testBefore"),before?1:0);GL.Uniform1(GL.GetUniformLocation(prog,"testCoverage"),(float)WaveSpectrum.WhitecapCoverage(s));
                    GL.ClearColor(.31f,.39f,.43f,1);GL.Clear(ClearBufferMask.ColorBufferBit|ClearBufferMask.DepthBufferBit);
                    GL.DrawArrays(PrimitiveType.Triangles,0,(int)(side?span:span*span)*6);
                    byte[] data=new byte[width*height*4];GL.ReadPixels(0,0,width,height,PixelFormat.Rgba,PixelType.UnsignedByte,data);
                    string name=$"{(before?"before":"after")}-{s.ToString("0.0",CultureInfo.InvariantCulture)}-{(side?"side":"low")}";
                    WriteCrestPng("obj/crests37/"+name+".png",data,width,height,side);
                    html.Append($"<figure><img src='{name}.png'><figcaption>{name}, Hs {p.SignificantHeight:F3} blocks; {(side?$"width {span:F0}, vertical ±{vertical:F3} blocks":$"1-block mesh, span {span:F0} blocks; no vertical exaggeration")}</figcaption></figure>");
                }
            }
            html.Append("</main><p>CPU/GPU identity and sheltered/breaking/morphology tests are in the console log; see geometry.csv and foam.csv for numbers. Coastal bathymetry is not simulated.</p>");
            File.WriteAllText("obj/crests37/index.html",html.ToString());File.WriteAllLines("obj/crests37/geometry.csv",table);
            GL.Disable(EnableCap.DepthTest);GL.BindFramebuffer(FramebufferTarget.Framebuffer,0);
            GL.DeleteFramebuffer(fbo);GL.DeleteTexture(tex);GL.DeleteRenderbuffer(depth);GL.DeleteProgram(prog);
        }
        static void WriteCrestPng(string path,byte[] rgba,int width,int height,bool side)
        {
            // PNG encoder only; no game assets or external image tooling. Arrow is in the actual view's travel direction.
            var rgb=new byte[width*height*3];
            for(int y=0;y<height;y++)for(int x=0;x<width;x++)for(int c=0;c<3;c++)rgb[(y*width+x)*3+c]=rgba[((height-1-y)*width+x)*4+c];
            void Line(int ax,int ay,int bx,int by){int steps=Math.Max(Math.Abs(bx-ax),Math.Abs(by-ay));
                for(int k=0;k<=steps;k++){int x=ax+(bx-ax)*k/Math.Max(steps,1),y=ay+(by-ay)*k/Math.Max(steps,1);
                    for(int d=-1;d<=1;d++){int j=((Math.Clamp(y+d,0,height-1))*width+Math.Clamp(x,0,width-1))*3;rgb[j]=255;rgb[j+1]=190;rgb[j+2]=40;}}}
            if(side){Line(35,30,115,30);Line(115,30,102,23);Line(115,30,102,37);}
            else{Line(55,70,55,20);Line(55,20,48,33);Line(55,20,62,33);}
            using var png=File.Create(path);png.Write(new byte[]{137,80,78,71,13,10,26,10});
            void Chunk(string type,byte[] bytes){byte[] tag=Encoding.ASCII.GetBytes(type);png.Write(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(bytes.Length)));png.Write(tag);png.Write(bytes);
                uint crc=0xffffffff;foreach(byte b in tag.Concat(bytes)){crc^=b;for(int k=0;k<8;k++)crc=(crc>>1)^((crc&1)!=0?0xedb88320u:0);}
                png.Write(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(unchecked((int)~crc))));}
            byte[] header=new byte[13];Array.Copy(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(width)),header,4);
            Array.Copy(BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(height)),0,header,4,4);header[8]=8;header[9]=2;Chunk("IHDR",header);
            using var memory=new MemoryStream();using(var zip=new ZLibStream(memory,CompressionLevel.Fastest,true))for(int y=0;y<height;y++){zip.WriteByte(0);zip.Write(rgb,y*width*3,width*3);}
            Chunk("IDAT",memory.ToArray());Chunk("IEND",Array.Empty<byte>());
        }
    }
}
