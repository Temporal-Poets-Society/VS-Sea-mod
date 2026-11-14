using System;
using OpenTK.Graphics.OpenGL4;
using Vintagestory.API.Client;

namespace Sea
{
    /// <summary>
    /// (0.6.0) Client: the coast around the camera as the water shader's sea_CoastTex — <see cref="Size"/>² columns of
    /// <see cref="CoastMath.Stride"/> floats in 4 RGBA32F texels side by side (depth, travel time, away-from-shore
    /// direction × trust; the lee of 8 directions; the rip and the beach), read with texelFetch so the shader's bilinear
    /// sample is the CPU's (<see cref="CoastMath.Bilinear"/>). Tiles not solved yet are open sea in both.
    /// </summary>
    public sealed class CoastTexture : IDisposable
    {
        public const int Tiles = 8, Size = Tiles * CoastMath.Tile;
        const double RefillSeconds = 2;

        const int S = CoastMath.Stride;
        readonly float[] pixels = new float[Size * Size * S];
        int texture, version = -1;
        long ox = long.MinValue, oz = long.MinValue;
        bool missing;
        double refilled = double.NegativeInfinity;

        /// <summary>World block x/z of column (0, 0); whether anything is drawn from it.</summary>
        public long OriginX => ox;
        public long OriginZ => oz;
        public int TextureId => texture;
        public bool Ready => texture != 0;

        /// <summary>
        /// Follow the camera and the field (a new tile, a moved window, or every 2 s while tiles are missing). (SE-0040)
        /// Solved tiles are never changed in place (a re-solve makes a new array), so only the tiles whose array differs
        /// from the one shown are copied and uploaded, each as its own rectangle; the whole map only when the window moves.
        /// </summary>
        public void Update(CoastField field, double camX, double camZ, double seconds)
        {
            // (SE-0055) centred on the camera: its tile rounded, not floored, so the map reaches 112 to 144 blocks every way
            // (96 to 160 before), and the shader eases its coast out over the outer 16
            long tx = (long)Math.Round(camX / CoastMath.Tile) - Tiles / 2, tz = (long)Math.Round(camZ / CoastMath.Tile) - Tiles / 2;
            long nx = tx * CoastMath.Tile, nz = tz * CoastMath.Tile;
            bool first = texture == 0, moved = nx != ox || nz != oz;
            if (!first && !moved && field.Version == version && !(missing && seconds - refilled > RefillSeconds)) return;
            version = field.Version;
            refreshes++;
            ox = nx; oz = nz;
            refilled = seconds;
            missing = false;
            const int T = CoastMath.Tile;
            int changed = 0;
            for (int j = 0; j < Tiles; j++)
                for (int i = 0; i < Tiles; i++)
                {
                    long wtx = tx + i, wtz = tz + j;
                    var data = field.TileData(wtx, wtz);
                    if (data == null) missing = true;
                    // (SE-0068) each world tile has its slot in the texture (a ring: tile mod 8), so a window that moves on a
                    // tile line uploads only the tiles that came into it; the shader reads the ring from where the window
                    // starts (sea_CoastWrap). Up to 0.6.0-rc.4 the whole map (4 MB) went up at every 32-block line.
                    int si = (int)Mod(wtx, Tiles), sj = (int)Mod(wtz, Tiles), k = sj * Tiles + si;
                    if (!first && slotTileX[k] == wtx && slotTileZ[k] == wtz && ReferenceEquals(data, shown[k]) && (data != null || openShown[k])) continue;
                    shown[k] = data;
                    openShown[k] = data == null;
                    slotTileX[k] = wtx; slotTileZ[k] = wtz;
                    for (int z = 0; z < T; z++)
                    {
                        int row = ((sj * T + z) * Size + si * T) * S;
                        if (data != null) Array.Copy(data, z * T * S, pixels, row, T * S);
                        else
                            for (int x = 0; x < T; x++) CoastMath.OpenColumn(pixels, row + x * S);
                    }
                    changedTile[changed++] = k;
                }
            if (changed == 0) return;
            Upload(first ? null : changedTile, changed);
        }

        readonly float[][] shown = new float[Tiles * Tiles][];
        readonly long[] slotTileX = new long[Tiles * Tiles], slotTileZ = new long[Tiles * Tiles];
        static long Mod(long a, long m) { long r = a % m; return r < 0 ? r + m : r; }
        readonly bool[] openShown = new bool[Tiles * Tiles];
        readonly int[] changedTile = new int[Tiles * Tiles];

        /// <summary>(SE-0040) What the map has uploaded so far (for `.sea coast`): whole maps, single tiles, megabytes.</summary>
        /// Refreshes: each was a whole upload (4 MB) up to 0.6.0-rc.4.
        public (int Refreshes, int Whole, int Tiles, double MB) Uploads => (refreshes, wholeUploads, tileUploads, uploadedBytes / 1048576.0);
        int refreshes, wholeUploads, tileUploads;
        long uploadedBytes;

        void Upload(int[] tilesChanged, int count)
        {
            GL.GetInteger(GetPName.ActiveTexture, out int active);
            GL.GetInteger(GetPName.TextureBinding2D, out int bound);
            try
            {
                if (texture == 0)
                {
                    texture = GL.GenTexture();
                    GL.BindTexture(TextureTarget.Texture2D, texture);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
                    GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
                    GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba32f, Size * S / 4, Size, 0, PixelFormat.Rgba, PixelType.Float, pixels);
                    wholeUploads++; uploadedBytes += pixels.Length * 4L;
                    return;
                }
                GL.BindTexture(TextureTarget.Texture2D, texture);
                if (tilesChanged == null)
                {
                    GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, Size * S / 4, Size, PixelFormat.Rgba, PixelType.Float, pixels);
                    wholeUploads++; uploadedBytes += pixels.Length * 4L;
                    return;
                }
                // one tile's rectangle out of the whole array: rows of the full width, starting at the tile's corner
                const int T = CoastMath.Tile;
                GL.PixelStore(PixelStoreParameter.UnpackRowLength, Size * S / 4);
                try
                {
                    for (int n = 0; n < count; n++)
                    {
                        int k = tilesChanged[n], i = k % Tiles, j = k / Tiles;
                        GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, i * T * S / 4);
                        GL.PixelStore(PixelStoreParameter.UnpackSkipRows, j * T);
                        GL.TexSubImage2D(TextureTarget.Texture2D, 0, i * T * S / 4, j * T, T * S / 4, T, PixelFormat.Rgba, PixelType.Float, pixels);
                        tileUploads++; uploadedBytes += T * T * S * 4L;
                    }
                }
                finally
                {
                    GL.PixelStore(PixelStoreParameter.UnpackRowLength, 0);
                    GL.PixelStore(PixelStoreParameter.UnpackSkipPixels, 0);
                    GL.PixelStore(PixelStoreParameter.UnpackSkipRows, 0);
                }
            }
            finally
            {
                GL.BindTexture(TextureTarget.Texture2D, bound);
                GL.ActiveTexture((TextureUnit)active);
            }
        }

        /// <summary>Into a program in use: the texture on unit 12 and sea_CoastArea (off when there's none).</summary>
        public void Bind(IShaderProgram prog, double camX, double camZ, bool on)
        {
            if (!WaterShader.Has(prog, "sea_CoastArea")) return;
            if (!on || texture == 0)
            {
                prog.Uniform("sea_CoastArea", 0f, 0f, 0f, 0f);
                return;
            }
            GL.GetInteger(GetPName.ActiveTexture, out int active);
            try { prog.BindTexture2D("sea_CoastTex", texture, 12); }
            finally { GL.ActiveTexture((TextureUnit)active); }
            prog.Uniform("sea_CoastArea", (float)(ox + 0.5 - camX), (float)(oz + 0.5 - camZ), Size, 1f);
            // (SE-0068) where in the ring of tile slots the window's first column is
            if (WaterShader.Has(prog, "sea_CoastWrap")) GL.Uniform2(GL.GetUniformLocation(prog.ProgramId, "sea_CoastWrap"), (int)Mod(ox, Size), (int)Mod(oz, Size));
        }

        public void Dispose()
        {
            if (texture != 0) { GL.DeleteTexture(texture); texture = 0; }
        }
    }
}
