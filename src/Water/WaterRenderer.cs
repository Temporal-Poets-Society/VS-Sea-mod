using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace Sea
{
    /// <summary>
    /// Feeds Sea's water shader code every frame: the waves at the camera (<see cref="SeaSystem.CameraWaves"/>), and
    /// the hull footprints from the registered sources (ship mods), nearest first, camera-relative, with which of them
    /// are dry inside (their water is cut out per pixel). (0.4.2) The numbers are worked out here once a frame, before
    /// the game's water depth pass, and go into a water program when the game itself uses it (<see cref="OnUse"/>), once
    /// per program and frame.
    /// </summary>
    public sealed class WaterRenderer : IRenderer
    {
        /// <summary>
        /// (0.4.2) In the Before stage, after the camera (0), Sea's frame start (0.05) and the ships' (Magelan, 0.1), and
        /// before the game's water depth pass (the terrain's "ret-prep", 0.995): the depth pass and the drawn water get the
        /// same frame's numbers (at Opaque 0.449, the depth pass had the last frame's).
        /// </summary>
        public double RenderOrder => 0.99;
        public int RenderRange => 9999;

        readonly ICoreClientAPI capi;
        readonly SeaSystem sys;
        readonly List<WaterClipBox> clips = new();
        readonly float[] clipA = new float[WaterShader.MaxClips * 4];
        readonly float[] clipB = new float[WaterShader.MaxClips * 4];
        readonly float[] clipC = new float[WaterShader.MaxClips * 4];
        readonly float[] clipD = new float[WaterShader.MaxClips * 4];
        readonly float[] clipP = new float[WaterShader.MaxClips * WaterShader.ProfileStations];
        readonly float[] dryE = new float[WaterShader.MaxDryShapes * 4];
        readonly float[] dryP = new float[WaterShader.MaxDryShapes * WaterShader.DryStations * WaterShader.DryLevels];
        public readonly float[] WaveUniform = new float[WaterShader.MaxWaves * 4];
        // (0.6.0) the coast: its map around the camera and each train's clock (sea_WaveClock)
        readonly CoastTexture coastTexture = new();
        float coastSea;
        readonly float[] clockUniform = new float[WaterShader.MaxWaves * 2];
        double coastCamX, coastCamZ;
        bool coastOn;

        /// <summary>Set once a water shader was loaded with Sea's code in it.</summary>
        public static bool Injected;
        /// <summary>Set once a water shader got Sea's waves instead of vanilla's.</summary>
        public static bool WavesInjected;

        /// <summary>The renderer whose numbers go into the water programs (the client's Sea, while it runs).</summary>
        static WaterRenderer current;

        public int LastClipCount { get; private set; }
        public int LastWaveCount { get; private set; }
        public int LastDryCount { get; private set; }
        public int LastDryShapeCount { get; private set; }

        // this frame's numbers, and which programs already have them
        int frame, waveCount, clipCount, shapeCount;
        // (0.4.7) blocks per pixel per block of distance (the near water fades trains by it, as Wmap's far sea does), and
        // the open-sea wave factor at the camera (1 / the shader's divisor), which Wmap's far waves take
        float pixel, waveFactor = 0.2f;
        float stormX, stormZ, foamCoverage, foamAlong, foamAcross, waveShape;
        Wind foamWind;
        public string FoamStatus => $"Foam wind: {foamWind} (toward {Compass.Name(foamWind.ToDegrees)}, Beaufort {Beaufort.Force(foamWind.Strength)}); whitecap input {100*foamCoverage:0.000}%, breaking crests only (local compression > 0.22); view {sys.FoamView}. Input is the current local sea wind (provider/admin, otherwise steady sea config), not a measured foam area.";
        bool foamReady;
        double foamCamX, foamCamZ, foamTime, foamAngle, foamAnchorAlong, foamAnchorAcross;
        readonly IShaderProgram[] uploadedTo = new IShaderProgram[4];
        readonly int[] uploadedFrame = new int[4];
        bool uploadFailed;

        public WaterRenderer(ICoreClientAPI capi, SeaSystem sys)
        {
            this.capi = capi;
            this.sys = sys;
            current = this;
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (!Injected) return;
            var ep = capi.World.Player?.Entity;
            if (ep == null) return;
            Prepare(ep.CameraPos.X, ep.CameraPos.Y, ep.CameraPos.Z);
            frame++;
            if (Hooks.UniformsOnUse) return;
            // (the Use patch didn't go in) put them in now, as before 0.4.2
            foreach (var pass in Passes)
            {
                var prog = capi.Shader.GetProgram((int)pass);
                if (prog == null || prog.Disposed || !WaterShader.Has(prog, "sea_ClipCount")) continue;
                if (uploadFailed && (IsSwitchedOff(prog) || switchedOff.Count >= 8)) continue;
                try
                {
                    prog.Use();
                    try
                    {
                        // (after an error: Sea's waves and hulls set to nothing, as on the Use path, not uploaded again)
                        if (uploadFailed) SwitchOffOnce(prog);
                        else
                        {
                            try { Upload(prog); }
                            catch (Exception e)
                            {
                                FailOnce(e);
                                SwitchOffOnce(prog);
                            }
                        }
                    }
                    finally { prog.Stop(); }
                }
                catch (Exception e) { FailOnce(e); }
            }
        }

        void Prepare(double camX, double camY, double camZ)
        {
            // the waves ships ride (SampleWaves), whoever decides the wind: without a wind provider they follow the
            // config's steady wind (WindModel.SeaWind). Vanilla's warp only until the first trains are worked out.
            var place = sys.CameraWaves;
            int wn = WavesInjected && place.Count > 0 ? place.FillUniforms(WaveUniform, camX, camZ) : 0;
            if (wn == 0 && WavesInjected && place.Spectrum != null && sys.Spectrum != null)
            {
                // (0.3.7) Sea's sea, and it's flat (WaveScale 0): one train of nothing, since a count of 0 tells the
                // shader to use vanilla's waves
                Array.Clear(WaveUniform, 0, 4);
                wn = 1;
            }
            waveCount = LastWaveCount = wn;
            waveShape = (float)place.Shape;
            // (0.6.0) waves on the beaches: the coast map around the camera, solved by SeaSystem.Coast
            coastCamX = camX; coastCamZ = camZ;
            coastOn = wn > 0 && place.Count > 0 && sys.SurfOn;
            if (coastOn)
            {
                coastTexture.Update(sys.Coast, camX, camZ, capi.InWorldEllapsedMilliseconds / 1000.0);
                place.FillClock(clockUniform);
                coastSea = (float)place.SignificantHeight;
            }
            // Foam is cosmetic: use the local provider's current wind, not a grid/clock sample from the wave
            // history. SeaWind retains the configured steady sea wind when there is no wind provider.
            var wind = foamWind = sys.Model?.SeaWind(camX, camZ, sys.Hours, true) ?? place.Wind;
            double ws = wind.Strength;
            double angle = ws > 1e-6 ? Math.Atan2(wind.Z, wind.X) : foamAngle;
            double dt = foamReady ? Math.Max(0, place.Time - foamTime) : 0;
            if (!foamReady)
            {
                foamAngle = angle;
                foamAnchorAlong = camX * Math.Cos(angle) + camZ * Math.Sin(angle) - place.Time * 0.8;
                foamAnchorAcross = -camX * Math.Sin(angle) + camZ * Math.Cos(angle);
            }
            else
            {
                double turn = WorldShape.Mod(angle - foamAngle + Math.PI, 2 * Math.PI) - Math.PI;
                foamAngle += turn * (1 - Math.Exp(-dt / 3));
                // Integrate translation in doubles; turn the cosmetic foam frame about the camera, not the world's
                // origin (a tiny gust otherwise shifts the pattern tens of blocks at large world coordinates).
                foamAnchorAlong += (camX - foamCamX) * Math.Cos(foamAngle) + (camZ - foamCamZ) * Math.Sin(foamAngle) - dt * 0.8;
                foamAnchorAcross += -(camX - foamCamX) * Math.Sin(foamAngle) + (camZ - foamCamZ) * Math.Cos(foamAngle);
            }
            foamReady = true; foamCamX = camX; foamCamZ = camZ; foamTime = place.Time;
            stormX = (float)Math.Cos(foamAngle);
            stormZ = (float)Math.Sin(foamAngle);
            foamCoverage = wn > 0 && place.SignificantHeight > 1e-5
                && capi.World.Player.Entity.Pos.Dimension == 0 ? (float)WaveSpectrum.WhitecapCoverage(ws) : 0;
            // All procedural scales are powers of two; wrapping at 65536 is continuous at each noise octave.
            foamAnchorAlong = WorldShape.Mod(foamAnchorAlong, 65536);
            foamAnchorAcross = WorldShape.Mod(foamAnchorAcross, 65536);
            foamAlong = (float)foamAnchorAlong;
            foamAcross = (float)foamAnchorAcross;
            var proj = capi.Render.PerspectiveProjectionMat;
            int height = capi.Render.FrameHeight;
            pixel = proj != null && proj[5] > 0 && height > 0 ? (float)(2 / (proj[5] * height)) : 0;
            waveFactor = (float)sys.WaveFactorAt(camX, camZ);

            clips.Clear();
            foreach (var s in sys.WaterSources)
            {
                try { s.AddWaterClips(clips); }
                catch (Exception e) { capi.Logger.Error("[sea] water clip source {0} failed: {1}", s.GetType().Name, e.Message); }
            }
            // nearest first: the nearest get a place (there are MaxClips) and a detailed dry outline (MaxDryShapes)
            if (clips.Count > 1)
            {
                nearest.X = camX;
                nearest.Z = camZ;
                clips.Sort(nearest);
            }

            int n = 0, dry = 0, shapes = 0;
            const int P = WaterShader.ProfileStations, DP = WaterShader.DryStations * WaterShader.DryLevels;
            foreach (var c in clips)
            {
                if (n >= WaterShader.MaxClips) break;
                // (0.4.2) a hull the shader can't use (no length, numbers that aren't numbers) is left out, as the CPU's
                // test (InDryHull) has nothing dry there; the shader would divide by its length
                if (!Usable(c)) continue;
                double rx = c.X - camX, rz = c.Z - camZ;
                if (rx * rx + rz * rz > 250 * 250) break;   // (nearest first: the rest are further)
                clipA[n * 4] = (float)rx;
                clipA[n * 4 + 1] = (float)rz;
                clipA[n * 4 + 2] = (float)Math.Cos(c.Yaw);
                clipA[n * 4 + 3] = (float)Math.Sin(c.Yaw);
                clipB[n * 4] = c.HalfLength;
                // (a flooded dry hull: the sea shows under the water in her)
                clipB[n * 4 + 1] = c.FloodUniform;
                clipB[n * 4 + 2] = (float)(c.WaterY - camY);
                clipB[n * 4 + 3] = c.CentreOffset;
                clipC[n * 4] = c.SlopeAlong;
                clipC[n * 4 + 1] = c.SlopeAcross;
                clipC[n * 4 + 2] = c.CalmZone > 0 ? c.CalmZone : WaterShader.CalmZone;
                clipC[n * 4 + 3] = c.CalmSlopeAcross;
                clipD[n * 4] = c.DryInset;
                clipD[n * 4 + 1] = c.DryDepth;
                clipD[n * 4 + 2] = c.DryHeight;
                clipD[n * 4 + 3] = c.Dry ? 1 : 0;
                if (c.Dry)
                {
                    dry++;
                    // the nearest few dry hulls are cut by their detailed outline (clips are nearest first when there are many)
                    if (c.HasDryProfile && shapes < WaterShader.MaxDryShapes)
                    {
                        clipD[n * 4 + 3] = 2 + shapes;
                        dryE[shapes * 4] = c.DryLevel0;
                        dryE[shapes * 4 + 1] = c.DryLevelStep;
                        dryE[shapes * 4 + 2] = c.HalfLength - c.DryInset;
                        dryE[shapes * 4 + 3] = c.DryRake;
                        Array.Copy(c.DryProfile, 0, dryP, shapes * DP, DP);
                        shapes++;
                    }
                }
                bool profiled = c.Profile != null && c.Profile.Length == P;
                for (int i = 0; i < P; i++) clipP[n * P + i] = profiled ? c.Profile[i] : c.HalfWidth;
                n++;
            }
            clipCount = LastClipCount = n;
            LastDryCount = dry;
            shapeCount = LastDryShapeCount = shapes;
        }

        /// <summary>
        /// A hull footprint the shader can draw: a length, a dry outline left over after its inset, and every number it
        /// would upload finite (a NaN there becomes an array index in the shader).
        /// </summary>
        static bool Usable(in WaterClipBox c)
        {
            if (!double.IsFinite(c.X) || !double.IsFinite(c.Z) || !double.IsFinite(c.WaterY) || !double.IsFinite(c.Yaw)) return false;
            if (!(c.HalfLength > 0) || !float.IsFinite(c.HalfLength) || !float.IsFinite(c.HalfWidth) || !float.IsFinite(c.CentreOffset)
                || !float.IsFinite(c.SlopeAlong) || !float.IsFinite(c.SlopeAcross) || !float.IsFinite(c.CalmSlopeAcross) || !float.IsFinite(c.CalmZone)) return false;
            if (c.Profile != null && c.Profile.Length == WaterShader.ProfileStations && !AllFinite(c.Profile)) return false;
            if (!c.Dry) return true;
            if (!float.IsFinite(c.DryInset) || !(c.HalfLength - c.DryInset > 0) || !float.IsFinite(c.DryDepth) || !float.IsFinite(c.DryHeight) || !float.IsFinite(c.FloodUniform)) return false;
            // (its detailed outline: HasDryProfile has the size and a positive step)
            if (c.HasDryProfile && (!float.IsFinite(c.DryLevel0) || !float.IsFinite(c.DryLevelStep) || !float.IsFinite(c.DryRake) || !AllFinite(c.DryProfile))) return false;
            return true;
        }

        static bool AllFinite(float[] a)
        {
            foreach (float v in a) if (!float.IsFinite(v)) return false;
            return true;
        }

        /// <summary>(0.4.2) The game is using a program: a water program with Sea's code gets this frame's numbers, once.
        /// (0.4.7) A Wmap far-terrain program that declares the wave uniforms (Wmap 0.5) gets the waves, so its distant sea
        /// takes their normals, lined up with the near water's (both are evaluated relative to the same camera).</summary>
        public static void OnUse(IShaderProgram prog)
        {
            var r = current;
            if (r == null) return;
            if (prog.AssetDomain == "wmap")
            {
                // (playtest merge) after an error the far waves are set to nothing too, as the water's are: their last
                // numbers would otherwise freeze in Wmap's far sea
                if (r.uploadFailed) { if (WaterShader.Has(prog, "sea_WaveCount")) r.SwitchOffOnce(prog); return; }
                try { r.UploadFarWaves(prog); }
                catch (Exception e)
                {
                    r.FailOnce(e);
                    r.SwitchOffOnce(prog);
                }
                return;
            }
            if (!Injected || Array.IndexOf(WaterShader.Passes, prog.PassName) < 0) return;
            if (r.uploadFailed) { r.SwitchOffOnce(prog); return; }
            try
            {
                if (!WaterShader.Has(prog, "sea_ClipCount")) return;
                int slot = -1, free = -1;
                for (int i = 0; i < r.uploadedTo.Length; i++)
                {
                    if (ReferenceEquals(r.uploadedTo[i], prog)) { slot = i; break; }
                    if (free < 0 && (r.uploadedTo[i] == null || r.uploadedTo[i].Disposed)) free = i;
                }
                if (slot < 0) { slot = free < 0 ? 0 : free; r.uploadedTo[slot] = prog; r.uploadedFrame[slot] = r.frame - 1; }
                if (r.uploadedFrame[slot] == r.frame) return;
                r.uploadedFrame[slot] = r.frame;
                r.Upload(prog);
            }
            catch (Exception e)
            {
                r.FailOnce(e);
                r.SwitchOffOnce(prog);
            }
        }

        /// <summary>(0.4.7) The waves into a Wmap far-terrain program in use: none unless Sea's own waves are in the water
        /// (with vanilla's waves the near water has no Sea waves to continue). A program without the uniforms is left alone.</summary>
        void UploadFarWaves(IShaderProgram prog)
        {
            if (!WaterShader.Has(prog, "sea_WaveCount")) return;
            int wn = Injected && WavesInjected ? waveCount : 0;
            prog.Uniform(WaterShader.N(prog, "sea_WaveCount"), wn);
            if (wn > 0) prog.Uniforms4(WaterShader.N(prog, "sea_Wave"), wn, WaveUniform);
            if (WaterShader.Has(prog, "sea_WaveShape")) prog.Uniform(WaterShader.N(prog, "sea_WaveShape"), waveShape);
            // (0.4.7) the near water's divisor, so the far slopes match it (Wmap took the still-water 5, a fifth of the
            // open sea's)
            if (WaterShader.Has(prog, "sea_WaveFactor")) prog.Uniform(WaterShader.N(prog, "sea_WaveFactor"), waveFactor);
        }

        /// <summary>This frame's numbers into a program that's in use.</summary>
        void Upload(IShaderProgram prog)
        {
            const int P = WaterShader.ProfileStations, DP = WaterShader.DryStations * WaterShader.DryLevels;
            int n = clipCount, shapes = shapeCount, wn = waveCount;
            prog.Uniform(WaterShader.N(prog, "sea_ClipCount"), n);
            if (n > 0)
            {
                prog.Uniforms4(WaterShader.N(prog, "sea_ClipA"), n, clipA);
                prog.Uniforms4(WaterShader.N(prog, "sea_ClipB"), n, clipB);
                prog.Uniforms4(WaterShader.N(prog, "sea_ClipC"), n, clipC);
                prog.Uniforms4(WaterShader.N(prog, "sea_ClipD"), n, clipD);
                prog.Uniforms4(WaterShader.N(prog, "sea_ClipP"), n * P / 4, clipP);
                // only the fragment stage has the dry outlines
                if (shapes > 0 && WaterShader.Has(prog, "sea_DryE"))
                {
                    prog.Uniforms4(WaterShader.N(prog, "sea_DryE"), shapes, dryE);
                    prog.Uniforms4(WaterShader.N(prog, "sea_DryP"), shapes * DP / 4, dryP);
                }
            }
            if (WaterShader.Has(prog, "sea_WaveCount"))
            {
                prog.Uniform(WaterShader.N(prog, "sea_WaveCount"), wn);
                if (wn > 0) prog.Uniforms4(WaterShader.N(prog, "sea_Wave"), wn, WaveUniform);
                if (WaterShader.Has(prog, "sea_WaveShape")) prog.Uniform(WaterShader.N(prog, "sea_WaveShape"), waveShape);
                if (wn > 0 && coastOn && WaterShader.Has(prog, "sea_WaveClock"))
                    GL.Uniform2(GL.GetUniformLocation(prog.ProgramId, "sea_WaveClock"), wn, clockUniform);
                coastTexture.Bind(prog, coastCamX, coastCamZ, coastOn && wn > 0);
                if (coastOn && WaterShader.Has(prog, "sea_CoastSea")) prog.Uniform("sea_CoastSea", coastSea, 0f, 0f, 0f);
            }
            if (WaterShader.Has(prog, "sea_Pixel")) prog.Uniform(WaterShader.N(prog, "sea_Pixel"), pixel);
            // (0.6.0) w: surf whitewater on (it shows without wind foam too)
            if (WaterShader.Has(prog, "sea_Storm")) prog.Uniform(WaterShader.N(prog, "sea_Storm"), stormX, stormZ,
                sys.FoamView == FoamView.Off ? 0 : foamCoverage, sys.FoamView == FoamView.Off || !coastOn ? 0f : 1f);
            if (WaterShader.Has(prog, "sea_FoamOrigin")) prog.Uniform(WaterShader.N(prog, "sea_FoamOrigin"), foamAlong, foamAcross, 0f);
        }

        /// <summary>An error here would end the game (it's inside its drawing): Sea's water numbers stop instead, logged once.</summary>
        void FailOnce(Exception e)
        {
            if (uploadFailed) return;
            uploadFailed = true;
            capi.Logger.Error("[sea] Water uniforms disabled after an error (vanilla waves, no hull cuts, from now on): {0}", e);
        }

        readonly List<IShaderProgram> switchedOff = new();

        /// <summary>
        /// After an upload error, each water program the game uses gets Sea's waves and hulls set to nothing, once (it's in
        /// use: inside the Use postfix): its last numbers would freeze the waves, and the camera-relative hull cut-outs
        /// would move with the camera through open water. Straight through GL, as <see cref="SwitchOff"/>.
        /// </summary>
        void SwitchOffOnce(IShaderProgram prog)
        {
            if (switchedOff.Count >= 8 || IsSwitchedOff(prog)) return;
            try
            {
                int id = prog.ProgramId;
                if (id == 0) return;
                int clips = GL.GetUniformLocation(id, "sea_ClipCount"), waves = GL.GetUniformLocation(id, "sea_WaveCount");
                if (clips >= 0) GL.Uniform1(clips, 0);
                if (waves >= 0) GL.Uniform1(waves, 0);
                // (0.6.0) a Wmap 0.5 far program declares the wave count under its name before 0.6.0
                int legacyWaves = GL.GetUniformLocation(id, WaterShader.Legacy("sea_WaveCount"));
                if (legacyWaves >= 0) GL.Uniform1(legacyWaves, 0);
                int storm = GL.GetUniformLocation(id, "sea_Storm");
                if (storm >= 0) GL.Uniform4(storm, 0f, 0f, 0f, 0f);
                // (only once both went in: otherwise it's tried again at the next use)
                switchedOff.Add(prog);
            }
            catch (Exception e)
            {
                if (!switchOffWarned) capi.Logger.Warning("[sea] Could not switch Sea's water code off in {0}: {1}", prog.PassName, e.Message);
                switchOffWarned = true;
            }
        }

        bool switchOffWarned;

        bool IsSwitchedOff(IShaderProgram prog)
        {
            foreach (var p in switchedOff) if (ReferenceEquals(p, prog)) return true;
            return false;
        }

        /// <summary>
        /// (0.4.2) Leaving the world: Sea's code stays compiled in the game's water (and Wmap's far-terrain) programs, which
        /// the game keeps for the next world, so its numbers are set to nothing: vanilla waves, no hulls, no glints, no floor.
        /// (0.4.4) Straight through GL, not the program's Use: the game is tearing the world down, and Use also uploads
        /// vanilla's per-frame uniforms (the colour map rects), which ended the game with an access violation on leaving.
        /// </summary>
        public static void SwitchOff(ICoreClientAPI capi)
        {
            current = null;
            var programs = new List<IShaderProgram>();
            foreach (var pass in Passes) programs.Add(capi.Shader.GetProgram((int)pass));
            try { programs.Add(capi.Shader.GetProgramByName("farterrain")); } catch (Exception) { }
            // (0.4.7) Wmap 0.5's terraces take the same far-water uniforms
            try { programs.Add(capi.Shader.GetProgramByName("farcells")); } catch (Exception) { }
            foreach (var prog in programs)
            {
                try
                {
                    if (prog == null || prog.Disposed || prog.ProgramId == 0) continue;
                    bool water = WaterShader.Has(prog, "sea_ClipCount"), waves = WaterShader.Has(prog, "sea_WaveCount"),
                        sky = WaterShader.Has(prog, "sea_Effects"), floor = WaterShader.Has(prog, "sea_NightFloor");
                    if (!water && !waves && !sky && !floor) continue;
                    int id = prog.ProgramId;
                    GL.GetInteger(GetPName.CurrentProgram, out int previous);
                    GL.UseProgram(id);
                    try
                    {
                        if (water) GL.Uniform1(GL.GetUniformLocation(id, "sea_ClipCount"), 0);
                        if (waves) GL.Uniform1(GL.GetUniformLocation(id, "sea_WaveCount"), 0);
                        if (WaterShader.Has(prog, "sea_Storm")) GL.Uniform4(GL.GetUniformLocation(id, "sea_Storm"), 0f, 0f, 0f, 0f);
                        if (sky) GL.Uniform2(GL.GetUniformLocation(id, "sea_Effects"), 0f, 0f);
                        if (floor) GL.Uniform3(GL.GetUniformLocation(id, "sea_NightFloor"), 0f, 0f, 0f);
                        if (WaterShader.Has(prog, "sea_NightEnabled")) GL.Uniform1(GL.GetUniformLocation(id, "sea_NightEnabled"), 0f);
                        if (WaterShader.Has(prog, "sea_SkyReflect")) GL.Uniform1(GL.GetUniformLocation(id, "sea_SkyReflect"), 0f);
                    }
                    finally { GL.UseProgram(previous); }
                }
                catch (Exception e) { capi.Logger.Warning("[sea] Could not switch Sea's shader code off in {0}: {1}", prog?.PassName, e.Message); }
            }
        }

        static readonly EnumShaderProgram[] Passes = { EnumShaderProgram.Chunkliquid, EnumShaderProgram.Chunkliquiddepth };

        /// <summary>Nearest the camera first (one comparer, reused: nothing is allocated per frame).</summary>
        sealed class Nearest : IComparer<WaterClipBox>
        {
            public double X, Z;
            public int Compare(WaterClipBox p, WaterClipBox q) => Dist2(p).CompareTo(Dist2(q));
            double Dist2(in WaterClipBox c) => (c.X - X) * (c.X - X) + (c.Z - Z) * (c.Z - Z);
        }

        readonly Nearest nearest = new();

        public void Dispose()
        {
            if (current == this) current = null;
            coastTexture.Dispose();
        }
    }
}
