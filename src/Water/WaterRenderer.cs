using System;
using System.Collections.Generic;
using Vintagestory.API.Client;

namespace Sea
{
    /// <summary>
    /// Feeds Sea's water shader code every frame: the waves at the camera (<see cref="SeaSystem.CameraWaves"/>), and
    /// the hull footprints from the registered sources (ship mods), nearest first, camera-relative, with which of them
    /// are dry inside (their water is cut out per pixel).
    /// </summary>
    public sealed class WaterRenderer : IRenderer
    {
        /// <summary>Just before Magelan's ship renderer (0.45); water is drawn after both.</summary>
        public double RenderOrder => 0.449;
        public int RenderRange => 9999;

        readonly ICoreClientAPI capi;
        readonly SeaSystem sys;
        readonly List<IWaterClipSource> sources;
        readonly List<WaterClipBox> clips = new();
        readonly float[] clipA = new float[WaterShader.MaxClips * 4];
        readonly float[] clipB = new float[WaterShader.MaxClips * 4];
        readonly float[] clipC = new float[WaterShader.MaxClips * 4];
        readonly float[] clipD = new float[WaterShader.MaxClips * 4];
        readonly float[] clipP = new float[WaterShader.MaxClips * WaterShader.ProfileStations];
        readonly float[] dryE = new float[WaterShader.MaxDryShapes * 4];
        readonly float[] dryP = new float[WaterShader.MaxDryShapes * WaterShader.DryStations * WaterShader.DryLevels];
        public readonly float[] WaveUniform = new float[WaterShader.MaxWaves * 4];

        /// <summary>Set once a water shader was loaded with Sea's code in it.</summary>
        public static bool Injected;
        /// <summary>Set once a water shader got Sea's waves instead of vanilla's.</summary>
        public static bool WavesInjected;

        public int LastClipCount { get; private set; }
        public int LastWaveCount { get; private set; }
        public int LastDryCount { get; private set; }
        public int LastDryShapeCount { get; private set; }

        public WaterRenderer(ICoreClientAPI capi, SeaSystem sys, List<IWaterClipSource> sources)
        {
            this.capi = capi;
            this.sys = sys;
            this.sources = sources;
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (!Injected) return;
            var ep = capi.World.Player?.Entity;
            if (ep == null) return;
            var cam = ep.CameraPos;

            // the waves ships ride (SampleWaves), whoever decides the wind: with WindModel "Off" they follow Sea's
            // steady winds (WindModel.SeaWind). Vanilla's warp only until the first trains are worked out.
            var place = sys.CameraWaves;
            int wn = WavesInjected && place.Count > 0 ? place.FillUniforms(WaveUniform, cam.X, cam.Z) : 0;
            if (wn == 0 && WavesInjected && place.Spectrum != null && sys.Spectrum != null)
            {
                // (0.3.7) Sea's sea, and it's flat (WaveScale 0): one train of nothing, since a count of 0 tells the
                // shader to use vanilla's waves
                Array.Clear(WaveUniform, 0, 4);
                wn = 1;
            }
            LastWaveCount = wn;

            clips.Clear();
            foreach (var s in sources)
            {
                try { s.AddWaterClips(clips); }
                catch (Exception e) { capi.Logger.Error("[sea] water clip source {0} failed: {1}", s.GetType().Name, e.Message); }
            }
            // nearest first: the nearest get a place (there are MaxClips) and a detailed dry outline (MaxDryShapes)
            if (clips.Count > 1)
            {
                nearest.X = cam.X;
                nearest.Z = cam.Z;
                clips.Sort(nearest);
            }

            int n = 0, dry = 0, shapes = 0;
            const int P = WaterShader.ProfileStations, DP = WaterShader.DryStations * WaterShader.DryLevels;
            foreach (var c in clips)
            {
                if (n >= WaterShader.MaxClips) break;
                double rx = c.X - cam.X, rz = c.Z - cam.Z;
                if (rx * rx + rz * rz > 250 * 250) continue;
                clipA[n * 4] = (float)rx;
                clipA[n * 4 + 1] = (float)rz;
                clipA[n * 4 + 2] = (float)Math.Cos(c.Yaw);
                clipA[n * 4 + 3] = (float)Math.Sin(c.Yaw);
                clipB[n * 4] = c.HalfLength;
                // (a flooded dry hull: the sea shows under the water in her)
                clipB[n * 4 + 1] = c.FloodUniform;
                clipB[n * 4 + 2] = (float)(c.WaterY - cam.Y);
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
                for (int i = 0; i < P; i++) clipP[n * P + i] = c.Profile != null && c.Profile.Length == P ? c.Profile[i] : c.HalfWidth;
                n++;
            }
            LastClipCount = n;
            LastDryCount = dry;
            LastDryShapeCount = shapes;

            foreach (var pass in Passes)
            {
                var prog = capi.Shader.GetProgram((int)pass);
                if (prog == null || prog.Disposed || !prog.HasUniform("seaClipCount")) continue;
                prog.Use();
                prog.Uniform("seaClipCount", n);
                if (n > 0)
                {
                    prog.Uniforms4("seaClipA", n, clipA);
                    prog.Uniforms4("seaClipB", n, clipB);
                    prog.Uniforms4("seaClipC", n, clipC);
                    prog.Uniforms4("seaClipD", n, clipD);
                    prog.Uniforms4("seaClipP", n * P / 4, clipP);
                    // only the fragment stage has the dry outlines
                    if (shapes > 0 && prog.HasUniform("seaDryE"))
                    {
                        prog.Uniforms4("seaDryE", shapes, dryE);
                        prog.Uniforms4("seaDryP", shapes * DP / 4, dryP);
                    }
                }
                if (prog.HasUniform("seaWaveCount"))
                {
                    prog.Uniform("seaWaveCount", wn);
                    if (wn > 0) prog.Uniforms4("seaWave", wn, WaveUniform);
                }
                prog.Stop();
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

        public void Dispose() { }
    }
}
