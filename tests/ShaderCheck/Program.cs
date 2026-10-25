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
                    if (pass == "chunkliquid")
                    {
                        foreach (bool dry in new[] { true, false })
                        {
                            string cv = CelestialShader.Vertex(WaterShader.Inject(v, out _, dry));
                            string cf = CelestialShader.Fragment(dry ? WaterShader.InjectFragment(f) : f);
                            int cp = Build("celestial water, dry cut " + dry, cv, cf, defs);
                            if (cp != 0)
                            {
                                foreach (string u in new[] { "seaSun", "seaMoon", "seaEffects", "seaSky", "seaSkyTex", "seaGlowTex", "seaWeather", "seaEye" })
                                    Check(GL.GetUniformLocation(cp, u) >= 0, "celestial uniform live: " + u);
                                GL.DeleteProgram(cp);
                            }
                        }
                    }
                }
                // Wmap's working tree next door, or another checkout of it (WMAP_ROOT, its project folder).
                string wm = Path.GetFullPath(Path.Combine(Environment.GetEnvironmentVariable("WMAP_ROOT") ?? "../Wmap", "assets/wmap"));
                // Wmap's includes first: from 0.5 its far shading is in them.
                if (Directory.Exists(wm)) foreach (string f in Directory.GetFiles(Path.Combine(wm, "shaderincludes"))) includes[Path.GetFileName(f)] = File.ReadAllText(f);
                if (Directory.Exists(wm) && Load(Path.Combine(wm, "shaders"), "farterrain.fsh").Contains("seaNightFloor"))
                {
                    // (0.4.7) Wmap 0.5 declares the far-water uniforms and shades its distant sea itself, in every far program:
                    // Sea leaves its shaders unpatched and only sets the uniforms (and the waves).
                    foreach (string f in Directory.GetFiles(Path.Combine(wm, "shaderincludes"))) includes[Path.GetFileName(f)] = File.ReadAllText(f);
                    Check(CelestialShader.FarFragment(Load(Path.Combine(wm, "shaders"), "farterrain.fsh")) == null, "Wmap 0.5 working tree: Sea leaves its far-water shader unpatched");
                    foreach (string far in new[] { "farterrain", "farcells" })
                    {
                        int fp = Build("Wmap 0.5 " + far + " with its far-water contract", Load(Path.Combine(wm, "shaders"), far + ".vsh"), Load(Path.Combine(wm, "shaders"), far + ".fsh"), defs);
                        if (fp == 0) continue;
                        foreach (string u in new[] { "seaNightFloor", "seaSkyReflect", "seaWaveCount", "seaWave" })
                            Check(GL.GetUniformLocation(fp, u) >= 0 || GL.GetUniformLocation(fp, u + "[0]") >= 0, "Wmap 0.5 " + far + ": " + u + " is live");
                        GL.DeleteProgram(fp);
                    }
                }
                else if (Directory.Exists(wm))
                {
                    foreach (string f in Directory.GetFiles(Path.Combine(wm, "shaderincludes"))) includes[Path.GetFileName(f)] = File.ReadAllText(f);
                    string farf = CelestialShader.FarFragment(Load(Path.Combine(wm, "shaders"), "farterrain.fsh"));
                    Check(farf != null && farf.Contains(CelestialShader.FarSkyReflection), "Wmap working tree: Sea's distant-sea sky reflection goes in");
                    int wp = Build("Wmap distant terrain with Sea night floor and sky reflection", Load(Path.Combine(wm, "shaders"), "farterrain.vsh"), farf, defs);
                    if (wp != 0)
                    {
                        Check(GL.GetUniformLocation(wp, "seaSkyReflect") >= 0 && GL.GetUniformLocation(wp, "seaNightFloor") >= 0, "Wmap: seaSkyReflect and seaNightFloor are live");
                        GL.DeleteProgram(wp);
                    }
                    const string installed = "obj/sky-inspection/installed-farterrain.fsh";
                    if (File.Exists(installed))
                    {
                        includes["wmapfartiles.ash"] = File.ReadAllText("obj/sky-inspection/installed-wmapfartiles.ash");
                        int ip = Build("installed Wmap 0.4.1 with Sea night floor and haze correction",
                            Includes(File.ReadAllText("obj/sky-inspection/installed-farterrain.vsh"), new HashSet<string>()),
                            CelestialShader.FarFragment(Includes(File.ReadAllText(installed), new HashSet<string>())), defs);
                        if (ip != 0) GL.DeleteProgram(ip);
                    }
                }
            }

            Console.WriteLine("the dry-hull cut, run on the GPU");
            DryCutMatches();
            CelestialLobes();
            CelestialVisible();
            SkyReflection();

            Console.WriteLine(failures == 0 ? "ALL OK" : $"{failures} FAILED");
            return failures == 0 ? 0 : 1;
        }

        // (0.4.4) The sky reflection: little looking down, the sky's own colour at a grazing angle; and alpha rises with it
        // (what reaches the screen is rgb * alpha). (0.4.6) At night the dome as shown: its fog colour times its fog amount
        // over dark space. 0.4.4 and 0.4.5 reflected the bare fog colour, a red-brown horizon (reported in game).
        static void SkyReflection()
        {
            const string v = "#version 330 core\nvoid main(){ vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2); gl_Position=vec4(p*2.0-1.0,0,1); }";
            string f = "#version 330 core\n" + CelestialShader.Declarations + @"
uniform vec3 testEye;
out vec4 outColor;
void main(){ vec3 e=normalize(testEye); vec3 n=vec3(0,1,0); vec3 r=reflect(e,n);
  float fres = 0.02 + 0.98 * pow(1.0 - max(0.0, dot(-e, n)), 5.0);
  vec4 c=seaSkyReflect(vec4(0.02,0.03,0.04,0.4), r, fres, vec3(0.10,0.12,0.16)); outColor=c; }";
            int vs = Compile(ShaderType.VertexShader, v, out string vl), fs = Compile(ShaderType.FragmentShader, f, out string fl);
            if (vs == 0 || fs == 0) { Check(false, "sky reflection program compiles", vl + fl); return; }
            int p = GL.CreateProgram(); GL.AttachShader(p, vs); GL.AttachShader(p, fs); GL.LinkProgram(p);
            GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int linked);
            if (linked != 1) { Check(false, "sky reflection program links", GL.GetProgramInfoLog(p)); return; }
            int Tex(float r, float g, float b, float a)
            {
                int t = GL.GenTexture(); GL.BindTexture(TextureTarget.Texture2D, t);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, new[] { r, g, b, a });
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                return t;
            }
            int sky = Tex(0.30f, 0.50f, 0.90f, 1), glow = Tex(0, 0, 0, 0);
            int tex = GL.GenTexture(), fb = GL.GenFramebuffer(), vao = GL.GenVertexArray();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
            GL.BindVertexArray(vao); GL.Viewport(0, 0, 1, 1); GL.UseProgram(p);
            GL.ActiveTexture(TextureUnit.Texture10); GL.BindTexture(TextureTarget.Texture2D, sky);
            GL.ActiveTexture(TextureUnit.Texture11); GL.BindTexture(TextureTarget.Texture2D, glow);
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.Uniform1(GL.GetUniformLocation(p, "seaSkyTex"), 10); GL.Uniform1(GL.GetUniformLocation(p, "seaGlowTex"), 11);
            GL.Uniform4(GL.GetUniformLocation(p, "seaSun"), 0f, 0.7f, 0.7f, 1.6f);
            GL.Uniform4(GL.GetUniformLocation(p, "seaWeather"), 1f, 0f, 0f, 0f);
            float[] Read(float strength, float skyLight, float ex, float ey, float ez, float fogMin = 0f)
            {
                GL.Uniform2(GL.GetUniformLocation(p, "seaEffects"), 0f, strength);
                GL.Uniform4(GL.GetUniformLocation(p, "seaSky"), skyLight, 0f, fogMin, 0f);
                GL.Uniform3(GL.GetUniformLocation(p, "testEye"), ex, ey, ez);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                float[] result = new float[4]; GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, result); return result;
            }
            var down = Read(1, 1, 0.05f, -1, 0);
            var grazing = Read(1, 1, 1, -0.02f, 0);
            var night = Read(1, 0, 1, -0.02f, 0);
            var nightFog = Read(1, 0, 1, -0.02f, 0, 0.5f);
            var off = Read(0, 1, 1, -0.02f, 0);
            Check(Math.Abs(down[2] - 0.04f) < 0.02f && down[3] < 0.45f && grazing[2] > 0.75f && grazing[3] > 0.9f
                && night[2] < 0.02f && Math.Abs(nightFog[2] - 0.08f) < 0.02f && Math.Abs(off[2] - 0.04f) < 1e-4f && Math.Abs(off[3] - 0.4f) < 1e-4f,
                $"GPU: sky reflection by Fresnel (looking down b {down[2]:0.000}, grazing {grazing[2]:0.00} alpha {grazing[3]:0.00}; at night the dome over dark space: clear {night[2]:0.00}, fog 0.5 {nightFog[2]:0.00}, not the fog colour 0.16); off leaves the water unchanged");
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); GL.UseProgram(0);
            GL.DeleteVertexArray(vao); GL.DeleteFramebuffer(fb); GL.DeleteTexture(tex); GL.DeleteTexture(sky); GL.DeleteTexture(glow);
            GL.DeleteProgram(p); GL.DeleteShader(vs); GL.DeleteShader(fs);
        }

        // What reaches the screen: the OIT pass shows rgb * alpha over the scene, so a glint must survive that.
        static void CelestialVisible()
        {
            const string v = "#version 330 core\nvoid main(){ vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2); gl_Position=vec4(p*2.0-1.0,0,1); }";
            string f = "#version 330 core\n" + CelestialShader.Declarations + @"
uniform vec3 testEye;
out vec4 outColor;
void main(){ vec3 r=reflect(normalize(testEye),vec3(0,1,0)); vec4 c=seaGlints(vec4(0.02,0.03,0.04,0.4), r, 0.6, 1.0); outColor=vec4(c.rgb*c.a, c.a); }";
            int vs = Compile(ShaderType.VertexShader, v, out string vl), fs = Compile(ShaderType.FragmentShader, f, out string fl);
            if (vs == 0 || fs == 0) { Check(false, "celestial visibility program compiles", vl + fl); return; }
            int p = GL.CreateProgram(); GL.AttachShader(p, vs); GL.AttachShader(p, fs); GL.LinkProgram(p);
            GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int linked);
            if (linked != 1) { Check(false, "celestial visibility program links", GL.GetProgramInfoLog(p)); return; }
            int tex = GL.GenTexture(), fb = GL.GenFramebuffer(), vao = GL.GenVertexArray();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
            GL.BindVertexArray(vao); GL.Viewport(0, 0, 1, 1); GL.UseProgram(p);
            GL.Uniform3(GL.GetUniformLocation(p, "testEye"), 0f, -0.5f, 0.8660254f);
            GL.Uniform3(GL.GetUniformLocation(p, "seaSunColor"), 1f, 0.95f, 0.85f);
            GL.Uniform4(GL.GetUniformLocation(p, "seaWeather"), 1f, 1f, 0f, 0f);
            float[] Read(float effects, float clear, vec4 sun, vec4 moon)
            {
                GL.Uniform2(GL.GetUniformLocation(p, "seaEffects"), effects, effects);
                GL.Uniform4(GL.GetUniformLocation(p, "seaWeather"), clear, 1f, 0f, 0f);
                GL.Uniform4(GL.GetUniformLocation(p, "seaSun"), sun.x, sun.y, sun.z, sun.w);
                GL.Uniform4(GL.GetUniformLocation(p, "seaMoon"), moon.x, moon.y, moon.z, moon.w);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                float[] result = new float[4]; GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, result); return result;
            }
            var down = new vec4(0, -1, 0, 0);
            // the renderer's strengths: sun 1.6; moon 0.75 × illuminated fraction × night
            var fullMoon = Read(1, 1, down, new vec4(0, 0.5f, 0.8660254f, 0.75f));
            var crescent = Read(1, 1, down, new vec4(0, 0.5f, 0.8660254f, 0.75f * 0.15f));
            var overcast = Read(1, 0.08f, down, new vec4(0, 0.5f, 0.8660254f, 0.75f));
            var sun = Read(1, 1, new vec4(0, 0.5f, 0.8660254f, 1.6f), down);
            var off = Read(0, 1, new vec4(0, 0.5f, 0.8660254f, 1.6f), new vec4(0, 0.5f, 0.8660254f, 0.75f));
            float baseB = 0.04f * 0.4f;
            Check(fullMoon[2] > 0.15f && crescent[2] > baseB * 2 && crescent[2] < fullMoon[2] * 0.5f && overcast[2] < fullMoon[2] * 0.2f
                && sun[0] > 0.8f && Math.Abs(off[2] - baseB) < 1e-4f,
                "GPU: glints are visible after OIT's alpha premultiply (water 0.04 → full moon " + fullMoon[2].ToString("0.00") + ", crescent " + crescent[2].ToString("0.000")
                + ", overcast " + overcast[2].ToString("0.000") + ", sun " + sun[0].ToString("0.00") + "); off leaves the water unchanged");
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); GL.UseProgram(0);
            GL.DeleteVertexArray(vao); GL.DeleteFramebuffer(fb); GL.DeleteTexture(tex); GL.DeleteProgram(p); GL.DeleteShader(vs); GL.DeleteShader(fs);
        }

        readonly struct vec4 { public readonly float x, y, z, w; public vec4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; } }

        static void CelestialLobes()
        {
            const string v = "#version 330 core\nvoid main(){ vec2 p=vec2((gl_VertexID<<1)&2,gl_VertexID&2); gl_Position=vec4(p*2.0-1.0,0,1); }";
            string f = "#version 330 core\n" + CelestialShader.Declarations + @"
uniform vec3 testEye;
uniform vec3 testBody;
out vec4 outColor;
void main(){ vec3 r=reflect(normalize(testEye),vec3(0,1,0)); outColor=vec4(seaLobe(r,testBody,0.002),seaLobe(r,testBody,0.02),0,1); }";
            int vs = Compile(ShaderType.VertexShader, v, out string vl), fs = Compile(ShaderType.FragmentShader, f, out string fl);
            if (vs == 0 || fs == 0) { Check(false, "celestial equation program compiles", vl + fl); return; }
            int p = GL.CreateProgram(); GL.AttachShader(p, vs); GL.AttachShader(p, fs); GL.LinkProgram(p);
            GL.GetProgram(p, GetProgramParameterName.LinkStatus, out int linked);
            if (linked != 1) { Check(false, "celestial equation program links", GL.GetProgramInfoLog(p)); return; }
            int tex = GL.GenTexture(), fb = GL.GenFramebuffer(), vao = GL.GenVertexArray();
            GL.BindTexture(TextureTarget.Texture2D, tex);
            GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, 1, 1, 0, PixelFormat.Rgba, PixelType.Float, IntPtr.Zero);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
            GL.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, tex, 0);
            GL.BindVertexArray(vao); GL.Viewport(0, 0, 1, 1); GL.UseProgram(p);
            GL.Uniform4(GL.GetUniformLocation(p, "seaWeather"), 1f, 1f, 0f, 0f);
            GL.Uniform3(GL.GetUniformLocation(p, "testEye"), 0f, -0.5f, 0.8660254f);
            float[] Read(float x, float y, float z)
            {
                GL.Uniform3(GL.GetUniformLocation(p, "testBody"), x, y, z);
                GL.DrawArrays(PrimitiveType.Triangles, 0, 3);
                float[] result = new float[4]; GL.ReadPixels(0, 0, 1, 1, PixelFormat.Rgba, PixelType.Float, result); return result;
            }
            var aligned = Read(0, 0.5f, 0.8660254f);
            var offset = Read(0.1f, 0.5f, MathF.Sqrt(0.74f));
            var below = Read(0, -0.5f, 0.8660254f);
            var setting = Read(0, -0.045f, MathF.Sqrt(1 - 0.002025f));
            GL.Uniform3(GL.GetUniformLocation(p, "testEye"), 0f, -0.01f, MathF.Sqrt(1 - 0.0001f));
            var sunrise = Read(0, 0.01f, MathF.Sqrt(1 - 0.0001f));
            GL.Uniform3(GL.GetUniformLocation(p, "testEye"), 0f, 0.01f, MathF.Sqrt(1 - 0.0001f));
            var halfSet = Read(0, -0.01f, MathF.Sqrt(1 - 0.0001f));
            Check(aligned[0] > 0.99 && aligned[1] > 0.99 && offset[1] > offset[0] && below[0] == 0 && setting[0] == 0
                && sunrise[0] > 0.99 && halfSet[0] > 0.6,
                "GPU: trail aligns with reflected celestial direction, rough lobe broadens, a sun 0.6° up glints fully ("
                + sunrise[0].ToString("0.00") + "), 0.6° down still " + halfSet[0].ToString("0.00") + ", a set or below-horizon body gives zero");
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0); GL.UseProgram(0);
            GL.DeleteVertexArray(vao); GL.DeleteFramebuffer(fb); GL.DeleteTexture(tex); GL.DeleteProgram(p); GL.DeleteShader(vs); GL.DeleteShader(fs);
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
