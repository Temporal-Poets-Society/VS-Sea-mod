using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace Sea
{
    /// <summary>The stored water is a level, textured surface clipped to the same hull volume as the sea.</summary>
    sealed class BoatFloodRenderer : IRenderer
    {
        readonly ICoreClientAPI api;
        readonly NativeBoats boats;
        readonly Dictionary<long, Surface> surfaces = new();
        readonly Matrixf model = new();
        readonly Vec4f tint = new(0.45f, 0.65f, 0.7f, 0.7f);
        int texture;
        sealed class Surface : IDisposable
        {
            public MeshRef Mesh;
            public MeshData Data;
            public long Built;
            public void Dispose() => Mesh?.Dispose();
        }
        public double RenderOrder => 1;
        public int RenderRange => 128;
        public BoatFloodRenderer(ICoreClientAPI api, NativeBoats boats) { this.api = api; this.boats = boats; }
        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            var eye = api.World.Player.Entity.CameraPos;
            var present = new HashSet<long>();
            foreach (var b in boats.Boats)
            {
                float fill = NativeBoats.Fill(b);
                if (!b.Alive || b.Pos.Dimension != 0 || fill <= 0.001 || fill >= 0.98
                    || b.Pos.SquareDistanceTo(eye) > 128 * 128) continue;
                present.Add(b.EntityId);
                if (!surfaces.TryGetValue(b.EntityId, out var s)) surfaces[b.EntityId] = s = new Surface();
                if (s.Mesh == null || api.ElapsedMilliseconds - s.Built >= 100)
                {
                    var clip = boats.Clip(b, false); clip.Dry = true;
                    double level = NativeBoats.FloodY(b, clip);
                    var data = Build(clip, level, b.Pos.XYZ, api.ElapsedMilliseconds / 1000.0);
                    if (s.Mesh == null)
                    {
                        // Reserve all 64 strips once. Clear/update changes only the active vertex/index counts.
                        var capacity = new MeshData(256, 384, false, true, true, true);
                        for (int q = 0; q < 64; q++)
                        {
                            for (int k = 0; k < 4; k++) capacity.AddWithFlagsVertex(0, 0, 0, 0, 0, -1, 0);
                            capacity.AddIndices(q * 4, q * 4 + 1, q * 4 + 2, q * 4, q * 4 + 2, q * 4 + 3);
                        }
                        s.Mesh = api.Render.UploadMesh(capacity);
                    }
                    s.Data = data; api.Render.UpdateMesh(s.Mesh, data); s.Built = api.ElapsedMilliseconds;
                }
                if (s.Data.IndicesCount == 0) continue;
                texture = texture == 0 ? api.Render.GetOrLoadTexture(new AssetLocation("game", "block/liquid/saltwater.png")) : texture;
                var shader = api.Render.PreparedStandardShader((int)b.Pos.X, (int)b.Pos.Y, (int)b.Pos.Z);
                try
                {
                    shader.ViewMatrix = api.Render.CameraMatrixOriginf;
                    shader.ProjectionMatrix = api.Render.CurrentProjectionMatrix;
                    shader.ModelMatrix = model.Identity().Translate(b.Pos.X - eye.X, b.Pos.Y - eye.Y, b.Pos.Z - eye.Z).Values;
                    shader.Tex2D = texture; shader.RgbaTint = tint;
                    api.Render.GlDisableCullFace(); api.Render.GlToggleBlend(true); api.Render.GLDepthMask(false);
                    api.Render.RenderMesh(s.Mesh);
                }
                finally
                { shader.Stop(); api.Render.GLDepthMask(true); api.Render.GlEnableCullFace(); api.Render.GlToggleBlend(false); }
            }
            foreach (long id in new List<long>(surfaces.Keys))
                if (!present.Contains(id)) { surfaces[id].Dispose(); surfaces.Remove(id); }
        }
        internal static MeshData Build(WaterClipBox clip, double y, Vec3d origin, double seconds)
        {
            var data = new MeshData(256, 384, false, true, true, true);
            clip.Flooded = false;
            double sn = Math.Sin(clip.Yaw), cs = Math.Cos(clip.Yaw);
            Vec3d At(double along, double side) => new(clip.X + along * sn - side * cs, y, clip.Z + along * cs + side * sn);
            double Width(double along, int sign)
            {
                var centre = At(along, 0);
                if (!clip.InDryHull(centre.X, y, centre.Z)) return 0;
                double lo = 0, hi = clip.HalfWidth * 1.5;
                for (int k = 0; k < 15; k++)
                {
                    double mid = (lo + hi) / 2; var p = At(along, sign * mid);
                    if (clip.InDryHull(p.X, y, p.Z)) lo = mid; else hi = mid;
                }
                return Math.Max(0, lo - 0.012);
            }
            void Vertex(Vec3d p)
            {
                float x = (float)(p.X - origin.X), z = (float)(p.Z - origin.Z);
                data.AddWithFlagsVertex(x, (float)(p.Y - origin.Y), z, x * 0.5f + (float)(seconds * 0.01 % 1),
                    z * 0.5f, -1, VertexFlags.PackNormal(0, 1, 0));
            }
            double start = clip.CentreOffset - clip.HalfLength, step = clip.HalfLength * 2 / 64;
            double left0 = Width(start, -1), right0 = Width(start, 1);
            for (int i = 1; i <= 64; i++)
            {
                double a = start + (i - 1) * step, b = a + step;
                double left1 = Width(b, -1), right1 = Width(b, 1);
                if (left0 + right0 > 0.01 && left1 + right1 > 0.01)
                {
                    int index = data.VerticesCount;
                    Vertex(At(a, -left0)); Vertex(At(b, -left1)); Vertex(At(b, right1)); Vertex(At(a, right0));
                    data.AddIndices(index, index + 1, index + 2, index, index + 2, index + 3);
                }
                left0 = left1; right0 = right1;
            }
            return data;
        }
        public void Dispose() { foreach (var s in surfaces.Values) s.Dispose(); surfaces.Clear(); }
    }
}
