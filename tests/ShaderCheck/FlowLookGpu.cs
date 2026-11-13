using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using Sea;

namespace ShaderCheck
{
    static partial class Program
    {
        /// <summary>
        /// (EA-0050) Sea's even flow as the GPU runs it (the production GLSL, <see cref="FlowLook.Functions"/>) against the C#
        /// mirror the offline tests check: vanilla's eight labels at water's and rapid water's speeds, still water, and flows
        /// in every direction, on up and side faces.
        /// </summary>
        static void FlowLookGpu()
        {
            Console.WriteLine("even flowing water, run on the GPU (EA-0050)");
            string vertex = "#version 330 core\n" + FlowLook.Functions + @"
uniform vec4 testIn[256]; flat out vec4 result;
void main(){
    vec4 t = testIn[gl_VertexID];
    vec2 e = sea_EvenFlow(t.xy);
    result = vec4(e, sea_FlowAlpha(length(e), t.z), 1.0);
    vec2 cell = vec2(gl_VertexID % 16, gl_VertexID / 16);
    gl_Position = vec4((cell + 0.5) / 16.0 * 2.0 - 1.0, 0, 1); gl_PointSize = 1.0;
}";
            int prog = Build("EA-0050: production even-flow functions", vertex,
                "#version 330 core\nflat in vec4 result;out vec4 color;void main(){color=result;}", new Dictionary<string, int>());
            if (prog == 0) return;

            var input = new float[256 * 4];
            int n = 0;
            void Add(double x, double z, double up) { input[n * 4] = (float)x; input[n * 4 + 1] = (float)z; input[n * 4 + 2] = (float)up; n++; }
            int[,] labels = { { 0, -1 }, { 1, -1 }, { 1, 0 }, { 1, 1 }, { 0, 1 }, { -1, 1 }, { -1, 0 }, { -1, -1 } };
            for (int i = 0; i < 8; i++)
            {
                var w = FlowLook.TesselatorFlow(labels[i, 0], labels[i, 1], 0.0015);
                var r = FlowLook.TesselatorFlow(labels[i, 0], labels[i, 1], 0.004, 2);
                Add(w.X, w.Z, 1); Add(r.X, r.Z, 1); Add(w.X, w.Z, 0);
            }
            Add(0, 0, 1); Add(0, -0.65, 0);
            var rnd = new Random(50);
            while (n < 256) Add(rnd.NextDouble() * 12 - 6, rnd.NextDouble() * 12 - 6, rnd.Next(2));

            int vao = GL.GenVertexArray(), tex = GL.GenTexture(), fbo = GL.GenFramebuffer();
            GL.BindVertexArray(vao);
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 16, 16, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
            GL.Viewport(0, 0, 16, 16);
            GL.UseProgram(prog);
            GL.Uniform4(GL.GetUniformLocation(prog, "testIn"), 256, input);
            GL.ClearColor(0, 0, 0, 0); GL.Clear(ClearBufferMask.ColorBufferBit);
            GL.DrawArrays(PrimitiveType.Points, 0, 256);
            var data = new float[1024];
            GL.ReadPixels(0, 0, 16, 16, PixelFormat.Rgba, PixelType.Float, data);

            double worst = 0, labelSpeedMin = double.MaxValue, labelSpeedMax = 0, labelAlphaMin = double.MaxValue, labelAlphaMax = 0;
            for (int i = 0; i < 256; i++)
            {
                var e = FlowLook.EvenFlow(input[i * 4], input[i * 4 + 1]);
                double a = FlowLook.FlowAlpha(Math.Sqrt(e.X * e.X + e.Z * e.Z), input[i * 4 + 2]);
                worst = Math.Max(worst, Math.Max(Math.Abs(e.X - data[i * 4]), Math.Max(Math.Abs(e.Z - data[i * 4 + 1]), Math.Abs(a - data[i * 4 + 2]) / Math.Max(1, a))));
                if (i < 24 && i % 3 == 0)
                {
                    double s = Math.Sqrt(data[i * 4] * data[i * 4] + data[i * 4 + 1] * data[i * 4 + 1]);
                    labelSpeedMin = Math.Min(labelSpeedMin, s); labelSpeedMax = Math.Max(labelSpeedMax, s);
                    labelAlphaMin = Math.Min(labelAlphaMin, data[i * 4 + 2]); labelAlphaMax = Math.Max(labelAlphaMax, data[i * 4 + 2]);
                }
            }
            Check(worst < 1e-5, $"GPU even flow = the C# mirror at 256 flows (largest difference {worst:e1})");
            Check(Math.Abs(labelSpeedMax - labelSpeedMin) < 1e-5 && Math.Abs(labelAlphaMin - 1) < 1e-5 && Math.Abs(labelAlphaMax - 1) < 1e-5,
                $"GPU: water's eight labels flow at one speed ({labelSpeedMin:0.0000} to {labelSpeedMax:0.0000}) and are as opaque as still water on top ({labelAlphaMin:0.0000} to {labelAlphaMax:0.0000})");
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); GL.UseProgram(0);
            GL.DeleteFramebuffer(fbo); GL.DeleteTexture(tex); GL.DeleteVertexArray(vao); GL.DeleteProgram(prog);
            Check(GL.GetError() == ErrorCode.NoError, "GPU: even-flow check has no GL error");
        }
    }
}
