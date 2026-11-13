using System;
using System.Collections.Generic;
using System.Linq;
using OpenTK.Graphics.OpenGL4;
using Sea;

namespace ShaderCheck
{
    static partial class Program
    {
        /// <summary>
        /// (SE-0024, <c>--vertex-cost</c>) What the water's vertex stage costs on this GPU, timed with GL time-elapsed
        /// queries over a million vertices with 24 storm trains: Sea's surface (<c>sea_Surface</c>, what every water vertex
        /// runs), the slope and crest (<c>sea_Differential</c>, run besides it while glints, sky reflection or foam are on)
        /// and both, at open sea and on a coast (every vertex in shallow water). One loop for both could save at most
        /// what the surface costs on its own. A measurement, not a check: it prints and passes.
        /// </summary>
        /// <summary>
        /// (SE-0024) The surface the slope keeps on its way (one loop over the trains) is the surface worked out on its own,
        /// at open sea and on a coast, with the trains of a storm.
        /// </summary>
        static void KeptSurfaceGpu()
        {
            string vertex = "#version 330 core\n" + WaterShader.Geometry + @"
uniform int testMode; flat out vec4 result;
void main(){
    vec2 cell = vec2(gl_VertexID % 16, gl_VertexID / 16);
    vec2 q = cell * 3.7 + 0.31;
    vec2 slope; float rough; vec3 crest;
    if (testMode == 1) { sea_KeepSurface = true; sea_Differential(q, 0.37, 0.0, slope, rough, crest); sea_KeepSurface = false; }
    result = vec4(sea_Surface(q, 0.37), 1.0);
    gl_Position = vec4((cell + 0.5) / 16.0 * 2.0 - 1.0, 0, 1); gl_PointSize = 1.0;
}";
            int prog = Build("SE-0024: the slope keeps the surface it passes by", vertex,
                "#version 330 core\nflat in vec4 result;out vec4 color;void main(){color=result;}", new Dictionary<string, int>());
            if (prog == 0) return;
            int vao = GL.GenVertexArray(), target = GL.GenTexture(), fbo = GL.GenFramebuffer();
            GL.BindVertexArray(vao);
            GL.BindTexture(TextureTarget.Texture2D, target);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 16, 16, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, target, 0);
            GL.Viewport(0, 0, 16, 16);
            int coastTex = SetUpWaves(prog);
            double worst = 0;
            foreach (bool coast in new[] { false, true })
            {
                GL.Uniform4(GL.GetUniformLocation(prog, "sea_CoastArea"), 0f, 0f, 64f, coast ? 1f : 0f);
                float[] Render(int mode)
                {
                    GL.Uniform1(GL.GetUniformLocation(prog, "testMode"), mode);
                    GL.ClearColor(0, 0, 0, 0);
                    GL.Clear(ClearBufferMask.ColorBufferBit);
                    GL.DrawArrays(PrimitiveType.Points, 0, 256);
                    var data = new float[1024];
                    GL.ReadPixels(0, 0, 16, 16, PixelFormat.Rgba, PixelType.Float, data);
                    return data;
                }
                float[] alone = Render(0), kept = Render(1);
                for (int i = 0; i < alone.Length; i++) worst = Math.Max(worst, Math.Abs(alone[i] - kept[i]));
            }
            Check(worst <= 1e-5, $"SE-0024: the surface kept by the slope's loop is the surface worked out alone, open sea and coast (max difference {worst:E1} blocks)");
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(fbo);
            GL.DeleteTexture(target);
            GL.DeleteTexture(coastTex);
            GL.DeleteVertexArray(vao);
            GL.DeleteProgram(prog);
        }

        /// <summary>A storm's 24 trains and a 64 x 64 coast map, 5 blocks deep everywhere, the shore 30 blocks to the west (texture unit 1).</summary>
        static int SetUpWaves(int prog)
        {
            var p = CrestPlace(1.0);
            WaveUpload(prog, p);
            var clock = new float[48];
            int c = p.FillClock(clock);
            GL.Uniform2(GL.GetUniformLocation(prog, "sea_WaveClock"), c, clock);
            var coastSea = new float[4];
            p.FillCoastSea(coastSea);
            GL.Uniform4(GL.GetUniformLocation(prog, "sea_CoastSea"), coastSea[0], coastSea[1], coastSea[2], coastSea[3]);
            const int n = 64;
            var texels = new float[n * n * 16];
            float[] column = { 5, 10, 1, 0, 1, 1, 1, 1, 1, 1, 1, 1, 0, 30, 0, 0.1f };
            for (int j = 0; j < n * n; j++) Array.Copy(column, 0, texels, j * 16, 16);
            int tex = GL.GenTexture();
            GL.ActiveTexture(TextureUnit.Texture1);
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, n * 4, n, 0, PixelFormat.Rgba, PixelType.Float, texels);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.Uniform1(GL.GetUniformLocation(prog, "sea_CoastTex"), 1);
            return tex;
        }

        static void VertexCost()
        {
            Console.WriteLine("water vertex stage cost (SE-0024)");
            string vertex = "#version 330 core\n" + WaterShader.Geometry + @"
uniform int testMode;
void main(){
    vec2 q = vec2(gl_VertexID % 1024, gl_VertexID / 1024) * 0.06 + 0.25;
    vec3 p = vec3(0.0); vec2 slope = vec2(0.0); float rough = 0.0; vec3 crest = vec3(0.0);
    // 0 the surface, 1 the slope, 2 both apart (before SE-0024), 3 both in one loop (the slope keeping the surface)
    if (testMode == 3) sea_KeepSurface = true;
    if (testMode != 0) sea_Differential(q, 0.2, 0.0, slope, rough, crest);
    sea_KeepSurface = false;
    if (testMode != 1) p = sea_Surface(q, 0.2);
    float sum = p.x + p.y + p.z + slope.x + slope.y + rough + crest.x + crest.y + crest.z;
    // (outside the view, so nothing is rasterised; the sum keeps every vertex's work)
    gl_Position = vec4(3.0 + 1e-20 * sum, 0.0, 0.0, 1.0);
}";
            int prog = Build("vertex cost: surface / slope / both", vertex, "#version 330 core\nout vec4 color;void main(){color=vec4(1);}", new Dictionary<string, int>());
            if (prog == 0) return;
            int vao = GL.GenVertexArray();
            GL.BindVertexArray(vao);
            int tex = SetUpWaves(prog);
            const int n = 64;
            const int vertices = 1024 * 1024;
            int query = GL.GenQuery();
            // (the modes take turns, so whatever else the GPU is doing meanwhile weighs on each alike; the median of 15)
            double[] Times(bool coast)
            {
                GL.Uniform4(GL.GetUniformLocation(prog, "sea_CoastArea"), 0f, 0f, (float)n, coast ? 1f : 0f);
                var ms = new List<double>[4];
                for (int m = 0; m < 4; m++) ms[m] = new List<double>();
                for (int r = 0; r < 17; r++)
                    for (int mode = 0; mode < 4; mode++)
                    {
                        GL.Uniform1(GL.GetUniformLocation(prog, "testMode"), mode);
                        GL.BeginQuery(QueryTarget.TimeElapsed, query);
                        GL.DrawArrays(PrimitiveType.Points, 0, vertices);
                        GL.EndQuery(QueryTarget.TimeElapsed);
                        GL.GetQueryObject(query, GetQueryObjectParam.QueryResult, out long ns);
                        if (r >= 2) ms[mode].Add(ns / 1e6);
                    }
                return ms.Select(l => { l.Sort(); return l[l.Count / 2]; }).ToArray();
            }
            foreach (bool coast in new[] { false, true })
            {
                var t = Times(coast);
                Console.WriteLine(FormattableString.Invariant(
                    $"  info {(coast ? "coast   " : "open sea")}: a million vertices, 24 trains: surface {t[0]:0.00} ms, slope {t[1]:0.00} ms, both apart {t[2]:0.00} ms, in one loop {t[3]:0.00} ms ({100 * (1 - t[3] / t[2]):0}% less)"));
            }
            GL.DeleteQuery(query);
            GL.DeleteTexture(tex);
            GL.DeleteVertexArray(vao);
            GL.DeleteProgram(prog);
        }
    }
}
