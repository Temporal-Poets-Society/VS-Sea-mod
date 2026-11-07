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

        /// <summary>Follow the camera and the field (a new tile, a moved window, or every 2 s while tiles are missing).</summary>
        public void Update(CoastField field, double camX, double camZ, double seconds)
        {
            long tx = CoastField.TileOf(camX) - Tiles / 2, tz = CoastField.TileOf(camZ) - Tiles / 2;
            long nx = tx * CoastMath.Tile, nz = tz * CoastMath.Tile;
            if (nx == ox && nz == oz && field.Version == version && !(missing && seconds - refilled > RefillSeconds) && texture != 0) return;
            version = field.Version;
            ox = nx; oz = nz;
            refilled = seconds;
            missing = false;
            const int T = CoastMath.Tile;
            for (int j = 0; j < Tiles; j++)
                for (int i = 0; i < Tiles; i++)
                {
                    var data = field.TileData(tx + i, tz + j);
                    if (data == null) missing = true;
                    for (int z = 0; z < T; z++)
                    {
                        int row = ((j * T + z) * Size + i * T) * S;
                        if (data != null) Array.Copy(data, z * T * S, pixels, row, T * S);
                        else
                            for (int x = 0; x < T; x++) CoastMath.OpenColumn(pixels, row + x * S);
                    }
                }
            Upload();
        }

        void Upload()
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
                }
                else
                {
                    GL.BindTexture(TextureTarget.Texture2D, texture);
                    GL.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, Size * S / 4, Size, PixelFormat.Rgba, PixelType.Float, pixels);
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
        }

        public void Dispose()
        {
            if (texture != 0) { GL.DeleteTexture(texture); texture = 0; }
        }
    }
}
