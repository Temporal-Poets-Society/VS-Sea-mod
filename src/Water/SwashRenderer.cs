using System;
using System.Collections.Generic;
using System.Diagnostics;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Sea
{
    /// <summary>
    /// (0.6.0) Client: swash, the sheet of water each wave runs up a beach and draws back down it, and the wet sand it
    /// leaves. Beach cells near the camera come from the coast map (land within a few blocks of the water, its top at
    /// most a block over it, with the slope of the sea floor off it). How high the water runs is the run-up of
    /// Stockdon et al. (2006) for the sea reaching that beach (less in the lee of land) and its peak period; when is the
    /// surf trains' phase on the shore line, the same clock the shader's breakers follow. Drawn as thin translucent quads
    /// on the sand: the film, white at its front, and sand darkened where it has just been wet.
    /// </summary>
    public sealed class SwashRenderer : IRenderer
    {
        const int Reach = 40, MaxQuads = 6000;
        const double CollectSeconds = 0.5, WetSeconds = 5;

        readonly ICoreClientAPI capi;
        readonly SeaSystem sys;
        readonly Matrixf model = new();
        readonly Vec4f tint = new(1, 1, 1, 1);
        readonly float[] cell = new float[CoastMath.Stride];
        readonly List<Beach> beaches = new();
        readonly Dictionary<(long, long), float> wet = new();
        readonly Stopwatch watch = new();
        LoadedTexture white;
        MeshRef mesh;
        MeshData data;
        long cx = long.MinValue, cz = long.MinValue;
        int version = -1;
        double collected = double.NegativeInfinity;
        int originX, originY, originZ;

        struct Beach { public long X, Z; public float Dist, Height, Slope; public float L0, L1, L2, L3, L4, L5, L6, L7; }

        public SwashRenderer(ICoreClientAPI capi, SeaSystem sys) { this.capi = capi; this.sys = sys; }

        public double RenderOrder => 0.45;
        public int RenderRange => Reach;

        /// <summary>Beach cells near the camera and what the last frame cost (for `.sea coast`).</summary>
        public int LastCells { get; private set; }
        public double LastMs { get; private set; }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            watch.Restart();
            try { Frame(dt); }
            finally { LastMs = watch.Elapsed.TotalMilliseconds; }
        }

        void Frame(float dt)
        {
            var ep = capi.World?.Player?.Entity;
            var place = sys.CameraWaves;
            if (ep == null || ep.Pos.Dimension != 0 || !sys.SurfOn || sys.Coast == null || place.Count == 0) { LastCells = 0; return; }
            var eye = ep.CameraPos;
            double rest = capi.World.SeaLevel - 0.125;
            if (Math.Abs(eye.Y - rest) > 64) { LastCells = 0; return; }
            double now = capi.InWorldEllapsedMilliseconds / 1000.0;
            Collect(eye.X, eye.Z, now);
            LastCells = beaches.Count;
            if (beaches.Count == 0 && wet.Count == 0) return;

            // the sea at the shore line: the surf trains' phase there now (as the shader's sea_WaveClock), their height,
            // the energy-weighted period
            double e = 0, w = 0, eta = 0;
            for (int i = 0; i < place.Count; i++)
            {
                double a = place.Amp[i];
                e += a * a; w += a * a * place.Omega[i];
                eta += a * Math.Sin(place.Phase[i] - place.Omega[i] * place.Time);
            }
            if (e <= 0) return;
            double sigma = Math.Sqrt(e / 2), hs = 4 * sigma, period = 2 * Math.PI / (w / e);
            // 0 at the trough to 1 at about the 2 % crest: the level the run-up's 2 % figure belongs to
            double u = Math.Clamp(0.5 + 0.5 * eta / (2.05 * sigma), 0, 1), level = Math.Pow(u, 1.5);

            data ??= new MeshData(MaxQuads * 4, MaxQuads * 6, false, true, true, true);
            data.Clear();
            originX = (int)Math.Floor(eye.X); originY = (int)Math.Floor(rest); originZ = (int)Math.Floor(eye.Z);
            float fade = (float)(1 - Math.Exp(-Math.Clamp(dt, 0, 0.5) / WetSeconds));
            var seen = new HashSet<(long, long)>();
            foreach (var b in beaches)
            {
                double h0 = hs * Exposure(b, place);
                double runUp = CoastMath.RunUp(h0, period, b.Slope), z = runUp * level;
                // the water's edge is z / slope past the shore line; the first land cell's near side is on it
                double cover = Math.Clamp(z / Math.Max(b.Slope, 0.02) - (b.Dist - 1), 0, 1);
                if (b.Height > 0.5) cover *= Math.Clamp((z - (b.Height - 0.125)) * 4, 0, 1);
                var key = (b.X, b.Z);
                wet.TryGetValue(key, out float wetness);
                wetness = Math.Max(wetness * (1 - fade), (float)cover);
                if (wetness > 0.01f) { wet[key] = wetness; seen.Add(key); } else wet.Remove(key);
                if (data.VerticesCount / 4 >= MaxQuads - 2) continue;
                float top = (float)(rest + b.Height - originY);
                if (wetness > 0.02f) Quad(b, top + 0.006f, ColorUtil.ToRgba((int)(110 * wetness), 38, 32, 26));
                if (cover > 0.01)
                {
                    // the film, whiter toward its front (a cell partly covered)
                    double front = 1 - Math.Abs(cover - 0.5) * 2;
                    int r = (int)(95 + 150 * front), g = (int)(150 + 95 * front), bl = (int)(165 + 80 * front);
                    Quad(b, top + 0.014f, ColorUtil.ToRgba((int)(255 * (0.35 + 0.35 * front) * cover), r, g, bl));
                }
            }
            if (wet.Count > seen.Count)
                foreach (var key in new List<(long, long)>(wet.Keys)) if (!seen.Contains(key)) wet.Remove(key);
            if (data.VerticesCount == 0) return;

            if (white == null)
            {
                white = new LoadedTexture(capi, 0, 1, 1);
                capi.Render.LoadOrUpdateTextureFromRgba(new[] { -1 }, false, 0, ref white);
            }
            if (mesh == null)
            {
                var capacity = new MeshData(MaxQuads * 4, MaxQuads * 6, false, true, true, true);
                for (int q = 0; q < MaxQuads; q++)
                {
                    for (int k = 0; k < 4; k++) capacity.AddVertexWithFlags(0, 0, 0, 0, 0, 0, 0);
                    capacity.AddIndices(q * 4, q * 4 + 1, q * 4 + 2, q * 4, q * 4 + 2, q * 4 + 3);
                }
                mesh = capi.Render.UploadMesh(capacity);
            }
            capi.Render.UpdateMesh(mesh, data);
            var shader = capi.Render.PreparedStandardShader(originX, originY + 1, originZ);
            try
            {
                shader.ViewMatrix = capi.Render.CameraMatrixOriginf;
                shader.ProjectionMatrix = capi.Render.CurrentProjectionMatrix;
                shader.ModelMatrix = model.Identity().Translate(originX - eye.X, originY - eye.Y, originZ - eye.Z).Values;
                shader.Tex2D = white.TextureId; shader.RgbaTint = tint;
                capi.Render.GlDisableCullFace(); capi.Render.GlToggleBlend(true); capi.Render.GLDepthMask(false);
                capi.Render.RenderMesh(mesh);
            }
            finally
            { shader.Stop(); capi.Render.GLDepthMask(true); capi.Render.GlEnableCullFace(); capi.Render.GlToggleBlend(false); }
        }

        void Quad(in Beach b, float y, int color)
        {
            float x0 = b.X - originX, z0 = b.Z - originZ;
            int i = data.VerticesCount, flags = VertexFlags.PackNormal(0, 1, 0);
            data.AddVertexWithFlags(x0, y, z0, 0, 0, color, flags);
            data.AddVertexWithFlags(x0 + 1, y, z0, 1, 0, color, flags);
            data.AddVertexWithFlags(x0 + 1, y, z0 + 1, 1, 1, color, flags);
            data.AddVertexWithFlags(x0, y, z0 + 1, 0, 1, color, flags);
            data.AddIndices(i, i + 1, i + 2, i, i + 2, i + 3);
        }

        // the share of the sea's height that reaches this beach past the land (energy-weighted over the trains)
        static double Exposure(in Beach b, WavePlace place)
        {
            var c = new CoastSample { Lee0 = b.L0, Lee1 = b.L1, Lee2 = b.L2, Lee3 = b.L3, Lee4 = b.L4, Lee5 = b.L5, Lee6 = b.L6, Lee7 = b.L7 };
            double e = 0, el = 0;
            for (int i = 0; i < place.Count; i++)
            {
                double a2 = place.Amp[i] * place.Amp[i], l = CoastMath.Lee(c, place.Kx[i], place.Kz[i]);
                e += a2; el += a2 * l * l;
            }
            return e > 0 ? Math.Sqrt(el / e) : 0;
        }

        // the beach cells around the camera, again when it moves a few blocks or the coast map changes
        void Collect(double x, double z, double now)
        {
            long nx = (long)Math.Floor(x / 4), nz = (long)Math.Floor(z / 4);
            int v = sys.Coast.Version;
            if (nx == cx && nz == cz && v == version && now - collected < 5) return;
            if (now - collected < CollectSeconds) return;
            cx = nx; cz = nz; version = v; collected = now;
            beaches.Clear();
            long bx = (long)Math.Floor(x), bz = (long)Math.Floor(z);
            for (long j = bz - Reach; j <= bz + Reach; j++)
                for (long i = bx - Reach; i <= bx + Reach; i++)
                {
                    if ((i - bx) * (i - bx) + (j - bz) * (j - bz) > Reach * Reach) continue;
                    if (!sys.Coast.Cell(i, j, cell)) continue;
                    float dist = cell[CoastMath.ILandDist];
                    if (cell[CoastMath.IDepth] > 0 || dist <= 0 || dist > 10 || cell[CoastMath.ILandHeight] > 1.2f) continue;
                    int l = CoastMath.ILee;
                    beaches.Add(new Beach
                    {
                        X = i, Z = j, Dist = dist, Height = cell[CoastMath.ILandHeight], Slope = cell[CoastMath.IBeachSlope],
                        L0 = cell[l], L1 = cell[l + 1], L2 = cell[l + 2], L3 = cell[l + 3], L4 = cell[l + 4], L5 = cell[l + 5], L6 = cell[l + 6], L7 = cell[l + 7],
                    });
                }
        }

        public void Dispose()
        {
            mesh?.Dispose(); mesh = null;
            white?.Dispose(); white = null;
        }
    }
}
