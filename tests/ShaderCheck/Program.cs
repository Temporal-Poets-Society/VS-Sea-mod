using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using Sea;

namespace ShaderCheck
{
    /// <summary>
    /// Compiles and links the game's water and particle shaders with Sea's edits on this machine's graphics driver, the
    /// way the game does it (includes expanded, its #define lines put after #version), with graphics settings low and
    /// high. Vanilla's own shaders go through first: if they don't compile here, the check itself is off.
    ///   dotnet run --project tests/ShaderCheck
    /// </summary>
    static class Program
    {
        static int failures;
        static readonly Dictionary<string, string> includes = new();

        static void Check(bool ok, string what, string log = null)
        {
            Console.WriteLine((ok ? "  ok   " : "  FAIL ") + what);
            if (!ok && !string.IsNullOrWhiteSpace(log)) Console.WriteLine("         " + log.Trim().Replace("\n", "\n         "));
            if (!ok) failures++;
        }

        static int Main()
        {
            string vs = Environment.GetEnvironmentVariable("VINTAGE_STORY");
            if (string.IsNullOrEmpty(vs)) vs = Path.Combine(Environment.GetEnvironmentVariable("APPDATA") ?? "", "Vintagestory");
            string shaders = Path.Combine(vs, "assets", "game", "shaders");
            foreach (string dir in new[] { Path.Combine(vs, "assets", "game", "shaderincludes"), shaders })
                foreach (string f in Directory.GetFiles(dir)) includes[Path.GetFileName(f).ToLowerInvariant()] = File.ReadAllText(f);

            var settings = new NativeWindowSettings { StartVisible = false, Title = "Sea shader check", APIVersion = new Version(3, 3), Profile = ContextProfile.Core };
            using var window = new NativeWindow(settings);
            window.MakeCurrent();
            Console.WriteLine($"driver: {GL.GetString(StringName.Renderer)}, OpenGL {GL.GetString(StringName.Version)}");

            // the game's #define lines (ShaderRegistry.registerDefaultShaderCodePrefixes), with settings low and high
            var low = new Dictionary<string, int>
            {
                ["USESSBO"] = 0, ["WAVINGSTUFF"] = 0, ["FOAMEFFECT"] = 0, ["SHINYEFFECT"] = 0, ["SSAOLEVEL"] = 0, ["NORMALVIEW"] = 0,
                ["GODRAYS"] = 0, ["MINBRIGHT"] = 0, ["SHADOWQUALITY"] = 0, ["DYNLIGHTS"] = 0, ["MAXANIMATEDELEMENTS"] = 230,
                ["FXAA"] = 0, ["BLOOM"] = 0, ["USEOIT"] = 1,
            };
            var high = new Dictionary<string, int>
            {
                ["USESSBO"] = 0, ["WAVINGSTUFF"] = 1, ["FOAMEFFECT"] = 1, ["SHINYEFFECT"] = 1, ["SSAOLEVEL"] = 2, ["NORMALVIEW"] = 0,
                ["GODRAYS"] = 1, ["MINBRIGHT"] = 0, ["SHADOWQUALITY"] = 2, ["DYNLIGHTS"] = 20, ["MAXANIMATEDELEMENTS"] = 230,
                ["FXAA"] = 1, ["BLOOM"] = 1, ["USEOIT"] = 1,
            };

            foreach (var (name, defs) in new[] { ("low settings", low), ("high settings", high) })
            {
                Console.WriteLine(name);
                foreach (string pass in WaterShader.Passes)
                {
                    string v = Load(shaders, pass + ".vsh"), f = Load(shaders, pass + ".fsh");
                    Build(pass + ": vanilla (the check itself)", v, f, defs);
                    int prog = Build(pass + ": with Sea's waves, hull clip and dry-hull cut", WaterShader.Inject(v, out _, true), WaterShader.InjectFragment(f), defs);
                    if (prog != 0)
                    {
                        // Sea's uniforms must survive the driver's optimiser, or its uploads would miss
                        var missing = new List<string>();
                        foreach (string u in new[] { "seaClipCount", "seaClipA", "seaClipB", "seaClipC", "seaClipD", "seaClipP", "seaDryE", "seaDryP", "seaWaveCount", "seaWave" })
                            if (GL.GetUniformLocation(prog, u) < 0 && GL.GetUniformLocation(prog, u + "[0]") < 0) missing.Add(u);
                        Check(missing.Count == 0, pass + ": Sea's uniforms are live" + (missing.Count > 0 ? " (missing " + string.Join(", ", missing) + ")" : ""));
                        GL.DeleteProgram(prog);
                    }
                    int fallback = Build(pass + ": with Sea's code but no dry-hull cut (the fallback)", WaterShader.Inject(v, out _, false), f, defs);
                    if (fallback != 0) GL.DeleteProgram(fallback);
                }
                string pv = Load(shaders, RainShader.Pass + ".vsh"), pf = Load(shaders, RainShader.Pass + ".fsh");
                Build(RainShader.Pass + ": vanilla (the check itself)", pv, pf, defs);
                int rain = Build(RainShader.Pass + ": with Sea's rain streaks", RainShader.Inject(pv), pf, defs);
                if (rain != 0) GL.DeleteProgram(rain);
            }

            Console.WriteLine("the dry-hull cut, run on the GPU");
            DryCutMatches();

            Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        /// <summary>
        /// Runs Sea's fragment-stage dry test (seaDry, exactly as injected) over a grid of points around a turned, shaped
        /// hull, into a small framebuffer, and compares every pixel with Sea's C# twin (WaterClipBox.InDryHull).
        /// </summary>
        static void DryCutMatches()
        {
            var clip = new WaterClipBox
            {
                X = 3.25, Z = -1.5, WaterY = 0.4, Yaw = 0.7, HalfLength = 15.7f, CentreOffset = -0.2f, HalfWidth = 5.1f,
                Profile = new float[WaterShader.ProfileStations], Dry = true, DryInset = 1, DryDepth = 3.2f, DryHeight = 4
            };
            for (int i = 0; i < WaterShader.ProfileStations; i++) clip.Profile[i] = (float)(2 + 3.1 * Math.Sin(Math.PI * (i + 0.5) / 16));
            DryCutMatches("level, the footprint's outline", clip);
            var leaning = clip;
            leaning.SlopeAlong = 0.06f;
            leaning.SlopeAcross = -0.17f;
            DryCutMatches("pitched and rolled, the footprint's outline", leaning);
            var detailed = leaning;
            const int S = WaterShader.DryStations, L = WaterShader.DryLevels;
            detailed.DryProfile = new float[S * L];
            detailed.DryLevel0 = -1.5f;
            detailed.DryLevelStep = 1.5f;
            for (int j = 0; j < L; j++)
                for (int i = 0; i < S; i++)   // a transom stern: square and wide aft, wider higher up, a fine bow
                    detailed.DryProfile[j * S + i] = (float)((1.4 + 0.5 * j) * (i < 4 ? 1.0 : Math.Sin(Math.PI * (i + 2) / (S + 4))) + (i < 4 ? 0.8 * j : 0));
            DryCutMatches("pitched and rolled, a detailed outline", detailed);
            var raked = detailed;
            raked.DryRake = -0.35f;   // the transom leans out aft going up
            DryCutMatches("pitched and rolled, a detailed outline ending at a raked transom", raked);
            // flooded: under the water in her hold (a level plane) the sea isn't cut away
            var flooded = clip;
            flooded.Flooded = true;
            flooded.FloodY = clip.WaterY - 1.63;
            DryCutMatches("flooded to 1.63 under her waterline, level, the footprint's outline", flooded);
            var floodedRaked = raked;
            floodedRaked.Flooded = true;
            floodedRaked.FloodY = clip.WaterY - 0.52;
            DryCutMatches("flooded to 0.52 under her waterline, pitched and rolled, a detailed outline ending at a raked transom", floodedRaked);
        }

        static void DryCutMatches(string what, WaterClipBox clip)
        {
            const int N = 128;
            const string vsh = @"#version 330 core
layout(location = 0) in vec2 corner;
uniform vec4 area;
uniform float fragY;
out vec3 seaFragPos;
void main() {
	vec2 t = corner * 0.5 + 0.5;
	seaFragPos = vec3(mix(area.x, area.z, t.x), fragY, mix(area.y, area.w, t.y));
	gl_Position = vec4(corner, 0.0, 1.0);
}
";
            string fsh = WaterShader.InjectFragment("#version 330 core\nout vec4 outColor;\nvoid main() {\n\toutColor = vec4(1.0);\n}\n");
            int vs = Compile(ShaderType.VertexShader, vsh, out string vlog), fs = Compile(ShaderType.FragmentShader, fsh, out string flog);
            if (vs == 0 || fs == 0) { Check(false, "the test program compiles", vlog + flog); return; }
            int prog = GL.CreateProgram();
            GL.AttachShader(prog, vs);
            GL.AttachShader(prog, fs);
            GL.LinkProgram(prog);
            GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int linked);
            if (linked != 1) { Check(false, "the test program links", GL.GetProgramInfoLog(prog)); return; }

            int tex = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.R8, N, N, 0, PixelFormat.Red, PixelType.UnsignedByte, IntPtr.Zero);
            int fbo = GL.GenFramebuffer();
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fbo);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
            int vao = GL.GenVertexArray(), vbo = GL.GenBuffer();
            GL.BindVertexArray(vao);
            GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
            float[] quad = { -1, -1, 1, -1, -1, 1, 1, 1 };
            GL.BufferData(BufferTarget.ArrayBuffer, quad.Length * 4, quad, BufferUsageHint.StaticDraw);
            GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 0, 0);
            GL.Viewport(0, 0, N, N);
            GL.UseProgram(prog);

            // the uniforms as Sea's water renderer uploads them (here the camera is at the origin)
            float[] a = { (float)clip.X, (float)clip.Z, (float)Math.Cos(clip.Yaw), (float)Math.Sin(clip.Yaw) };
            float[] b = { clip.HalfLength, clip.FloodUniform, (float)clip.WaterY, clip.CentreOffset };
            float[] c = { clip.SlopeAlong, clip.SlopeAcross, 0, 0 };
            float[] d = { clip.DryInset, clip.DryDepth, clip.DryHeight, clip.HasDryProfile ? 2 : 1 };
            GL.Uniform1(GL.GetUniformLocation(prog, "seaClipCount"), 1);
            GL.Uniform4(GL.GetUniformLocation(prog, "seaClipA"), 1, a);
            GL.Uniform4(GL.GetUniformLocation(prog, "seaClipB"), 1, b);
            GL.Uniform4(GL.GetUniformLocation(prog, "seaClipC"), 1, c);
            GL.Uniform4(GL.GetUniformLocation(prog, "seaClipD"), 1, d);
            GL.Uniform4(GL.GetUniformLocation(prog, "seaClipP"), 4, clip.Profile);
            if (clip.HasDryProfile)
            {
                GL.Uniform4(GL.GetUniformLocation(prog, "seaDryE"), 1, new[] { clip.DryLevel0, clip.DryLevelStep, clip.HalfLength - clip.DryInset, clip.DryRake });
                GL.Uniform4(GL.GetUniformLocation(prog, "seaDryP"), clip.DryProfile.Length / 4, clip.DryProfile);
            }
            double x0 = clip.X - 20, z0 = clip.Z - 20, x1 = clip.X + 20, z1 = clip.Z + 20;
            GL.Uniform4(GL.GetUniformLocation(prog, "area"), (float)x0, (float)z0, (float)x1, (float)z1);

            var pixels = new byte[N * N];
            int dry = 0, mismatched = 0, nearEdge = 0, shown = 0;
            var unflooded = clip;
            unflooded.Flooded = false;
            foreach (double y in new[] { clip.WaterY - 3.0, clip.WaterY - 1.1, clip.WaterY, clip.WaterY + 0.7, clip.WaterY + 2.2, clip.WaterY + 3.9, clip.WaterY - 3.4, clip.WaterY + 4.2 })
            {
                GL.Uniform1(GL.GetUniformLocation(prog, "fragY"), (float)y);
                GL.ClearColor(0, 0, 0, 0);
                GL.Clear(ClearBufferMask.ColorBufferBit);
                GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
                GL.ReadPixels(0, 0, N, N, PixelFormat.Red, PixelType.UnsignedByte, pixels);
                for (int j = 0; j < N; j++)
                    for (int i = 0; i < N; i++)
                    {
                        double x = x0 + (x1 - x0) * (i + 0.5) / N, z = z0 + (z1 - z0) * (j + 0.5) / N;
                        bool gpuDry = pixels[j * N + i] == 0, cpuDry = clip.InDryHull(x, y, z);
                        if (cpuDry) dry++;
                        // (flooded: the sea shown under the water in her, where the dry hull alone would cut it)
                        if (clip.Flooded && !gpuDry && unflooded.InDryHull(x, y, z)) shown++;
                        if (gpuDry == cpuDry) continue;
                        // a pixel right on the outline may round either way in single precision
                        bool edge = clip.InDryHull(x + 0.02, y, z) != cpuDry || clip.InDryHull(x - 0.02, y, z) != cpuDry ||
                                    clip.InDryHull(x, y, z + 0.02) != cpuDry || clip.InDryHull(x, y, z - 0.02) != cpuDry;
                        if (edge) nearEdge++; else mismatched++;
                    }
            }
            Check(mismatched == 0 && dry > 1000 && (!clip.Flooded || shown > 1000),
                $"{what}: the shader cuts exactly where Sea's own test says a hull is dry ({dry} dry points of {8 * N * N}, {nearEdge} on the outline itself" +
                (clip.Flooded ? $"; {shown} shown under the water in her" : "") + ")");

            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);
            GL.DeleteFramebuffer(fbo);
            GL.DeleteTexture(tex);
            GL.DeleteBuffer(vbo);
            GL.DeleteVertexArray(vao);
            GL.DeleteProgram(prog);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
        }

        static string Load(string dir, string file) => Includes(File.ReadAllText(Path.Combine(dir, file)), new HashSet<string>());

        /// <summary>#include lines replaced by the files, each once (ShaderRegistry.HandleIncludes).</summary>
        static string Includes(string code, HashSet<string> seen) => Regex.Replace(code, "^#include\\s+(.*)", m =>
        {
            string name = m.Groups[1].Value.Trim().ToLowerInvariant();
            if (!seen.Add(name)) return "";
            return includes.TryGetValue(name, out string text) ? Includes(text, seen) : "";
        }, RegexOptions.Multiline);

        /// <summary>The game's defines go on the line after #version (ClientPlatformWindows.CompileShader).</summary>
        static string Prefix(string code, Dictionary<string, int> defs)
        {
            var sb = new System.Text.StringBuilder();
            foreach (var kv in defs) sb.Append("#define ").Append(kv.Key).Append(' ').Append(kv.Value).Append("\r\n");
            int start = code.IndexOf('\n', Math.Max(0, code.IndexOf("#version", StringComparison.Ordinal))) + 1;
            return code.Insert(start, sb.ToString());
        }

        /// <summary>Compile both stages and link; 0 (and a failure) if anything went wrong.</summary>
        static int Build(string what, string vertex, string fragment, Dictionary<string, int> defs)
        {
            if (vertex == null || fragment == null)
            {
                Check(false, what + ": Sea's code couldn't be put in (anchors not found)");
                return 0;
            }
            int vs = Compile(ShaderType.VertexShader, Prefix(vertex, defs), out string vlog);
            int fs = Compile(ShaderType.FragmentShader, Prefix(fragment, defs), out string flog);
            if (vs == 0 || fs == 0)
            {
                Check(false, what + ": compiles", (vs == 0 ? "vertex: " + vlog : "") + (fs == 0 ? "\nfragment: " + flog : ""));
                if (vs != 0) GL.DeleteShader(vs);
                if (fs != 0) GL.DeleteShader(fs);
                return 0;
            }
            int prog = GL.CreateProgram();
            GL.AttachShader(prog, vs);
            GL.AttachShader(prog, fs);
            GL.LinkProgram(prog);
            GL.GetProgram(prog, GetProgramParameterName.LinkStatus, out int linked);
            string llog = GL.GetProgramInfoLog(prog);
            GL.DeleteShader(vs);
            GL.DeleteShader(fs);
            Check(linked == 1, what + ": compiles and links", llog);
            if (linked == 1) return prog;
            GL.DeleteProgram(prog);
            return 0;
        }

        static int Compile(ShaderType type, string code, out string log)
        {
            int id = GL.CreateShader(type);
            GL.ShaderSource(id, code);
            GL.CompileShader(id);
            GL.GetShader(id, ShaderParameter.CompileStatus, out int ok);
            log = GL.GetShaderInfoLog(id);
            if (ok == 1) return id;
            GL.DeleteShader(id);
            return 0;
        }
    }
}
