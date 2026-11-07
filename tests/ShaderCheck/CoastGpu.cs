using System;
using System.Collections.Generic;
using OpenTK.Graphics.OpenGL4;
using Sea;

namespace ShaderCheck
{
    static partial class Program
    {
        /// <summary>
        /// (0.6.0) A beach drawn by the production vertex code from a coast texture equals the CPU's surface over the same
        /// map (what boats ride): forward positions, the inverse height at the drawn points, and the slope's direction.
        /// The map has a curved beach, an island offshore (its lee) and the rip channels the beach gets.
        /// </summary>
        static void CoastGpu(string pointVertex)
        {
            int prog = Build("0.6.0: production surf geometry on a coast map", pointVertex,
                "#version 330 core\nflat in vec4 result;out vec4 color;void main(){color=result;}", new Dictionary<string, int>());
            if (prog == 0) return;
            // a curved shore through the sampled points: land west of x = -20 + 0.004 z², sloping 1:12 out to sea
            const int margin = 2, n = 252, w = n + 2 * margin;
            double positionError = 0, inverseError = 0, normalError = 0, normalExcess = 0, surfMax = 0;
            int points = 0, sheltered = 0, rips = 0, folds = 0, reflecting = 0;
            double foldError = 0;
            foreach (double origin in new[] { 0.0, 812345.5 })
            {
                double oz = origin == 0 ? 0 : -233007.25;
                long ox = (long)Math.Floor(origin) - w / 2, ozi = (long)Math.Floor(oz) - w / 2;
                var depth = new float[w * w];
                for (int j = 0; j < w; j++)
                    for (int i = 0; i < w; i++)
                    {
                        double x = ox + i + 0.5 - origin, z = ozi + j + 0.5 - oz, shore = -20 + 0.004 * z * z;
                        bool island = (x - 45) * (x - 45) + (z - 15) * (z - 15) < 49;
                        depth[j * w + i] = x < shore ? -0.125f : island ? -1.125f : (float)((x - shore) / 12 + 0.4);
                    }
                var time = new float[w * w];
                var dist = new float[w * w];
                CoastMath.Solve(depth, w, time, dist);
                var pixels = CoastMath.TileOf(depth, time, dist, w, margin, n, ox, ozi);
                const int S = CoastMath.Stride;
                long tx = ox + margin, tz = ozi + margin;
                CoastLookup lookup = (double x, double z, out CoastSample s) =>
                {
                    s = CoastMath.Bilinear(x, z, (ci, cj, into) =>
                    {
                        long a = ci - tx, b = cj - tz;
                        if (a < 0 || b < 0 || a >= n || b >= n) return false;
                        Array.Copy(pixels, (b * n + a) * S, into, 0, S);
                        return true;
                    }, new float[S]);
                    return true;
                };
                int coastTex = GL.GenTexture();
                GL.ActiveTexture(TextureUnit.Texture1);
                GL.BindTexture(TextureTarget.Texture2D, coastTex);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, n * S / 4, n, 0, PixelFormat.Rgba, PixelType.Float, pixels);
                GL.ActiveTexture(TextureUnit.Texture0);
                foreach (double strength in new[] { 0.5, 1.5 })
                    foreach (double from in new[] { 270.0, 315 })
                    {
                        var p = CrestPlace(strength, 1, from);
                        p.Coast = lookup;
                        WaveUpload(prog, p, origin, oz);
                        var clock = new float[48];
                        int c = p.FillClock(clock);
                        GL.Uniform2(GL.GetUniformLocation(prog, "sea_WaveClock"), c, clock);
                        GL.Uniform1(GL.GetUniformLocation(prog, "sea_CoastTex"), 1);
                        GL.Uniform4(GL.GetUniformLocation(prog, "sea_CoastArea"), (float)(tx + 0.5 - origin), (float)(tz + 0.5 - oz), (float)n, 1f);
                        var coastSea = new float[4]; p.FillCoastSea(coastSea);
                        GL.Uniform4(GL.GetUniformLocation(prog, "sea_CoastSea"), coastSea[0], coastSea[1], coastSea[2], coastSea[3]);
                        GL.Uniform1(GL.GetUniformLocation(prog, "testFactor"), 1f);
                        GL.Uniform1(GL.GetUniformLocation(prog, "testGradient"), 0);
                        float[] Render(int mode)
                        {
                            GL.Uniform1(GL.GetUniformLocation(prog, "testMode"), mode);
                            GL.DrawArrays(PrimitiveType.Points, 0, 256);
                            var data = new float[1024];
                            GL.ReadPixels(0, 0, 16, 16, PixelFormat.Rgba, PixelType.Float, data);
                            return data;
                        }
                        var pos = Render(0);
                        var normals = Render(1);
                        for (int i = 0; i < 256; i++)
                        {
                            double qx = (i % 16 - 7.5) * 7.3, qz = (i / 16 - 7.5) * 7.3;
                            var cpu = p.SurfaceAt(origin + qx, oz + qz);
                            positionError = Math.Max(positionError, Math.Max(Math.Abs(cpu.X - origin - pos[i * 4]),
                                Math.Max(Math.Abs(cpu.Y - pos[i * 4 + 1]), Math.Abs(cpu.Z - oz - pos[i * 4 + 2]))));
                            double x = origin + pos[i * 4], z = oz + pos[i * 4 + 2];
                            // (water is drawn only over water columns)
                            long col = (long)Math.Floor(qx + origin) - ox, row = (long)Math.Floor(qz + oz) - ozi;
                            if (depth[row * w + col] <= 0) continue;
                            if (p.CoastAt(qx + origin, qz + oz, out var cs, out _, out _) && cs.Depth > 0.6)
                            {
                                if (Math.Min(Math.Min(Math.Min(cs.Lee0, cs.Lee1), Math.Min(cs.Lee2, cs.Lee3)), Math.Min(Math.Min(cs.Lee4, cs.Lee5), Math.Min(cs.Lee6, cs.Lee7))) < 0.8) sheltered++;
                                if (cs.Rip > 0.05) rips++;
                                if (cs.ShoreSlope > 0.3 && cs.Trust > 0.5) reflecting++;
                                // Where the size of the waves changes within a block or two (a cliff's foot, a rip's edge) the
                                // drawn surface can fold over itself: a world point there has two surface points, and the CPU's
                                // lookup finds one of them. Those are counted, not compared.
                                var found = p.ParameterAt(x, z);
                                if (Math.Abs(found.X - origin - qx) + Math.Abs(found.Z - oz - qz) > 1e-4) { folds++; foldError = Math.Max(foldError, Math.Abs(p.HeightAt(x, z) - pos[i * 4 + 1])); continue; }
                                inverseError = Math.Max(inverseError, Math.Abs(p.HeightAt(x, z) - pos[i * 4 + 1]));
                                const double d = .01;
                                bool fold = false;
                                foreach (var (ex, ez) in new[] { (d, 0.0), (-d, 0.0), (0.0, d), (0.0, -d) })
                                {
                                    var fq = p.ParameterAt(x + ex, z + ez);
                                    if (Math.Abs(fq.X - origin - qx) + Math.Abs(fq.Z - oz - qz) > 0.2) fold = true;
                                }
                                if (fold) { folds++; continue; }
                                // (0.6.0) against the slope itself: a storm's breaker face is steeper than 1, where the GPU's
                                // 1/50-block differences of the coast terms and these 1/100-block ones part by more than on gentle water
                                double gx = (p.HeightAt(x + d, z) - p.HeightAt(x - d, z)) / (2 * d), gz = (p.HeightAt(x, z + d) - p.HeightAt(x, z - d)) / (2 * d);
                                double miss = Math.Max(Math.Abs(gx - normals[i * 4]), Math.Abs(gz - normals[i * 4 + 1]));
                                normalError = Math.Max(normalError, miss);
                                normalExcess = Math.Max(normalExcess, miss / Math.Max(0.15, 0.2 * Math.Sqrt(gx * gx + gz * gz)));
                                surfMax = Math.Max(surfMax, p.SurfAt(x, z).Breaking);
                                points++;
                            }
                        }
                    }
                GL.DeleteTexture(coastTex);
            }
            Check(positionError < .001, $"GPU surf positions = CPU surface on a coast map: max {positionError:E3} blocks");
            Check(inverseError < .002 && points > 200, $"CPU ship height at GPU-drawn surf points: max {inverseError:E3} blocks ({points} points in water, breaking up to {surfMax:0.00})");
            // (the folds are where the long waves' size changes faster than their orbits can follow: a rip's edge in a storm;
            // the two layers there differ most where a steep breaker front crosses the fold)
            Check(folds * 50 < points && foldError < .3, $"the surface folds over itself at few points: {folds} of {points + folds}, where a ship's height is up to {foldError:E2} blocks off the drawn one");
            Check(sheltered > 20 && rips > 5 && reflecting > 5, $"the coast map exercised the lee of land, rips and a shore that throws waves back: {sheltered} sheltered points, {rips} over rip channels, {reflecting} before a steep shore");
            Check(normalExcess < 1, $"GPU surf normals near CPU finite differences: within 0.15 or 20% of the slope (max error {normalError:E3}, {100 * normalExcess:F0}% of its allowance)");
            GL.DeleteProgram(prog);
        }
    }
}
