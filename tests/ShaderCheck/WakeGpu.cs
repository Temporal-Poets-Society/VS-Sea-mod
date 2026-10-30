using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using OpenTK.Graphics.OpenGL4;
using Sea;

namespace ShaderCheck
{
    static partial class Program
    {
        // Real atlas sampler + real dry cut on the driver, not a replacement model of the shader.
        static void WakeGpu()
        {
            const int size=512;
            const string vertex=@"#version 330 core
out vec3 seaFragPos;
uniform float testRest;
uniform float testWave;
void main() {
    vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2);
    vec2 xz=(p*2.0-1.0)*128.0;
    seaFragPos=vec3(xz.x,testRest + testWave * xz.x / 128.0,xz.y);
    gl_Position=vec4(p*2.0-1.0,0.0,1.0);
}";
            string fragment=WaterShader.InjectFragment(@"#version 330 core
"+CelestialShader.WakeDeclarations+@"
uniform float testRest;
uniform float testView;
out vec4 outColor;
void main() {
    float foam=seaWakeFoam(seaFragPos.xz,testRest,testView);
    outColor=vec4(foam,foam,foam,1.0);
}");
            int prog=Build("wake atlas + production dry cut diagnostic",vertex,fragment,new Dictionary<string,int>());
            if(prog==0)return;
            int vao=GL.GenVertexArray(),atlas=GL.GenTexture(),target=GL.GenTexture(),fbo=GL.GenFramebuffer();
            GL.BindVertexArray(vao);GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D,atlas);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMinFilter,(int)TextureMinFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureMagFilter,(int)TextureMagFilter.Linear);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapS,(int)TextureWrapMode.ClampToEdge);
            GL.TexParameter(TextureTarget.Texture2D,TextureParameterName.TextureWrapT,(int)TextureWrapMode.ClampToEdge);
            var field=new WakeField();var map=new WakeMap();var hull=new WakeHull(26,7,2.5);
            // 32 s of a caravel travelling east, a turning boat and swimmer; stopped sources retain foam.
            for(int i=0;i<=320;i++) {
                double t=i*0.1;
                field.Observe(1,t,-100+t*4,0,0,4,0,hull);
                double a=t*0.04;
                field.Observe(2,t,-25+25*Math.Sin(a),45-25*Math.Cos(a),0,Math.Cos(a),Math.Sin(a),new WakeHull(4,1.6,0.4));
                field.Observe(3,t,15+t*0.8,-24,0,0.8,0,new WakeHull(0.8,0.6,0.15));
            }
            field.Splash(4,31,10,-45,0,9,0.4);field.Expire(32);map.Paint(field,32,0,0,0);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba16f,size,size,0,PixelFormat.Rgba,PixelType.Float,map.Pixels);
            GL.BindTexture(TextureTarget.Texture2D,target);
            GL.TexImage2D(TextureTarget.Texture2D,0,PixelInternalFormat.Rgba32f,size,size,0,PixelFormat.Rgba,PixelType.Float,IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer,FramebufferAttachment.ColorAttachment0,TextureTarget.Texture2D,target,0);
            GL.Viewport(0,0,size,size);GL.UseProgram(prog);
            GL.BindTexture(TextureTarget.Texture2D,atlas);
            GL.Uniform1(GL.GetUniformLocation(prog,"seaWakeTex"),0);
            GL.Uniform4(GL.GetUniformLocation(prog,"seaWakeArea"),-128f,-128f,1f/256,1f);
            GL.Uniform1(GL.GetUniformLocation(prog,"testView"),512f);
            float[] Draw(float strength=1,float rest=0,float offset=0,float view=512,float wave=0) {
                GL.Uniform4(GL.GetUniformLocation(prog,"seaWakeArea"),-128f,-128f,1f/256,strength);
                GL.Uniform1(GL.GetUniformLocation(prog,"seaWakeHeight"),offset);
                GL.Uniform1(GL.GetUniformLocation(prog,"testRest"),rest);
                GL.Uniform1(GL.GetUniformLocation(prog,"testView"),view);
                GL.Uniform1(GL.GetUniformLocation(prog,"testWave"),wave);
                GL.ClearColor(0,0,0,0);GL.Clear(ClearBufferMask.ColorBufferBit);
                GL.DrawArrays(PrimitiveType.Triangles,0,3);
                float[] data=new float[size*size];GL.ReadPixels(0,0,size,size,PixelFormat.Red,PixelType.Float,data);return data;
            }
            var on=Draw();var off=Draw(0);var otherLevel=Draw(1,2);var movedY=Draw(1,-4,-4);
            Check(on.Max()>0.5 && on.Count(v=>v>0.02)>500 && off.All(v=>v==0),"GPU: hull/boat/swimmer/ring atlas visible; disabled is exactly zero");
            Check(otherLevel.All(v=>v==0) && on.Zip(movedY,(a,b)=>Math.Abs(a-b)).Max()<1e-5,
                "GPU: foam rejects another water level and stays anchored when camera y changes");
            var smallView=Draw(1,0,0,96);int outside=0,inside=0;
            for(int z=0;z<size;z++)for(int x=0;x<size;x++) {
                double px=(x+0.5)*0.5-128,pz=(z+0.5)*0.5-128;
                if(px*px+pz*pz>Math.Pow(96*0.68+0.5,2) && smallView[z*size+x]>0)outside++;
                if(smallView[z*size+x]>0.02)inside++;
            }
            Check(outside==0 && inside>100,"GPU: wakes fade completely by 0.68 view distance, before Wmap's 0.75 handoff");
            var displaced=Draw(1,0,0,512,5);
            Check(on.SequenceEqual(displaced),"GPU: changing displaced y leaves surface foam intact (rest-water test, no floating second mesh)");
            // Put an unflooded, heeled/pitched dry hold across the strongest atlas stamp. Test it at crests/troughs.
            int peak=Array.IndexOf(on,on.Max());float hx=(peak%size+0.5f)*0.5f-128,hz=(peak/size+0.5f)*0.5f-128;
            var clip=new WaterClipBox { X=hx,Z=hz,WaterY=0,Yaw=0.4,HalfLength=8,HalfWidth=4,Dry=true,DryInset=0,
                DryDepth=8,DryHeight=8,SlopeAlong=0.1f,SlopeAcross=-0.2f };
            GL.Uniform1(GL.GetUniformLocation(prog,"seaClipCount"),1);
            GL.Uniform4(GL.GetUniformLocation(prog,"seaClipA"),1,new[]{hx,hz,(float)Math.Cos(clip.Yaw),(float)Math.Sin(clip.Yaw)});
            GL.Uniform4(GL.GetUniformLocation(prog,"seaClipB"),1,new[]{8f,-1000f,0f,0f});
            GL.Uniform4(GL.GetUniformLocation(prog,"seaClipC"),1,new[]{0.1f,-0.2f,0f,0f});
            GL.Uniform4(GL.GetUniformLocation(prog,"seaClipD"),1,new[]{0f,8f,8f,1f});
            GL.Uniform4(GL.GetUniformLocation(prog,"seaClipP"),4,Enumerable.Repeat(4f,16).ToArray());
            var cut=Draw(1,0,0,512,5);int leaks=0,removed=0;
            for(int z=0;z<size;z++)for(int x=0;x<size;x++) {
                double px=(x+0.5)*0.5-128,pz=(z+0.5)*0.5-128,y=5*px/128;
                if(clip.InDryHull(px,y,pz) && on[z*size+x]>0.01) {removed++;if(cut[z*size+x]>0)leaks++;}
            }
            Check(leaks==0 && removed>20,$"GPU: production dry cut removes wake foam in a pitched/heeled hold under 5-block wave displacement ({removed} foamy pixels, {leaks} leaks)");
            GL.Uniform1(GL.GetUniformLocation(prog,"seaClipCount"),0);
            Directory.CreateDirectory("obj");
            SaveWakeImage("obj/wakes-before.ppm",off,size);
            SaveWakeImage("obj/wakes-after.ppm",on,size);
            SaveWakeImage("obj/wakes-dry-cut.ppm",cut,size);
            // Same sources after stopping: arms vanish first, foam persists, then the field expires completely.
            field.Expire(92);map.Paint(field,92,0,0,0);
            GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,size,size,PixelFormat.Rgba,PixelType.Float,map.Pixels);
            var aged=Draw();SaveWakeImage("obj/wakes-after-60s.ppm",aged,size);
            Check(aged.Sum()<on.Sum() && aged.Max()>0.01,"GPU: stopped ship leaves fading foam at 60 seconds");
            field.Expire(220);map.Paint(field,220,0,0,0);
            GL.TexSubImage2D(TextureTarget.Texture2D,0,0,0,size,size,PixelFormat.Rgba,PixelType.Float,map.Pixels);
            Check(Draw().All(v=>v==0),"GPU: expired field has no stale texture wake");
            GL.BindFramebuffer(FramebufferTarget.Framebuffer,0);GL.UseProgram(0);
            GL.DeleteFramebuffer(fbo);GL.DeleteTexture(atlas);GL.DeleteTexture(target);GL.DeleteVertexArray(vao);GL.DeleteProgram(prog);
            Check(GL.GetError()==ErrorCode.NoError,"GPU: wake atlas upload/render/cleanup reports no GL error");
        }
        static void SaveWakeImage(string path,float[] mask,int size)
        {
            // Top-down diagnostic mask over wave-shaded blue; not a game screenshot or an acceptance render.
            using var f=File.Create(path);f.Write(System.Text.Encoding.ASCII.GetBytes($"P6\n{size} {size}\n255\n"));
            for(int z=size-1;z>=0;z--)for(int x=0;x<size;x++) {
                double light=0.8+0.2*Math.Cos((x*0.5-128)*2*Math.PI/165),v=Math.Clamp(mask[z*size+x],0,1);
                foreach(double c in new[]{20*light,65*light,85*light})f.WriteByte((byte)Math.Clamp(c+(235-c)*v,0,255));
            }
        }
    }
}
