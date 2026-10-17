using System;
using OpenTK.Graphics.OpenGL4;

namespace Sea
{
    /// <summary>A filtered, bounded copy of the native full star cube; never changes the sky renderer's texture.</summary>
    public static class StarCubemap
    {
        public static int Create(int source)
        {
            GL.GetInteger(GetPName.TextureBindingCubeMap, out int cube);
            GL.GetInteger(GetPName.ReadFramebufferBinding, out int read);
            GL.GetInteger(GetPName.DrawFramebufferBinding, out int draw);
            int result = GL.GenTexture(), rf = GL.GenFramebuffer(), df = GL.GenFramebuffer();
            try
            {
                GL.BindTexture(TextureTarget.TextureCubeMap, source);
                GL.GetTexLevelParameter(TextureTarget.TextureCubeMapPositiveX, 0, GetTextureParameter.TextureWidth, out int width);
                GL.GetTexLevelParameter(TextureTarget.TextureCubeMapPositiveX, 0, GetTextureParameter.TextureHeight, out int height);
                if (width <= 0 || height <= 0) throw new Exception("vanilla star cube not ready");
                int size = Math.Min(512, width);
                GL.BindTexture(TextureTarget.TextureCubeMap, result);
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, rf);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, df);
                GL.ReadBuffer(ReadBufferMode.ColorAttachment0);
                GL.DrawBuffer(DrawBufferMode.ColorAttachment0);
                for (int i = 0; i < 6; i++)
                {
                    var face = (TextureTarget)((int)TextureTarget.TextureCubeMapPositiveX + i);
                    GL.TexImage2D(face, 0, PixelInternalFormat.Rgba8, size, size, 0, PixelFormat.Rgba, PixelType.UnsignedByte, IntPtr.Zero);
                    GL.FramebufferTexture2D(FramebufferTarget.ReadFramebuffer, FramebufferAttachment.ColorAttachment0, face, source, 0);
                    GL.FramebufferTexture2D(FramebufferTarget.DrawFramebuffer, FramebufferAttachment.ColorAttachment0, face, result, 0);
                    if (GL.CheckFramebufferStatus(FramebufferTarget.ReadFramebuffer) != FramebufferErrorCode.FramebufferComplete
                        || GL.CheckFramebufferStatus(FramebufferTarget.DrawFramebuffer) != FramebufferErrorCode.FramebufferComplete) throw new Exception("star copy framebuffer incomplete");
                    GL.BlitFramebuffer(0, 0, width, height, 0, 0, size, size, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);
                }
                GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
                GL.TexParameter(TextureTarget.TextureCubeMap, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                foreach (var wrap in new[] { TextureParameterName.TextureWrapS, TextureParameterName.TextureWrapT, TextureParameterName.TextureWrapR })
                    GL.TexParameter(TextureTarget.TextureCubeMap, wrap, (int)TextureWrapMode.ClampToEdge);
                GL.GenerateMipmap(GenerateMipmapTarget.TextureCubeMap);
                return result;
            }
            catch { GL.DeleteTexture(result); throw; }
            finally
            {
                GL.BindTexture(TextureTarget.TextureCubeMap, cube);
                GL.BindFramebuffer(FramebufferTarget.ReadFramebuffer, read);
                GL.BindFramebuffer(FramebufferTarget.DrawFramebuffer, draw);
                GL.DeleteFramebuffer(rf); GL.DeleteFramebuffer(df);
            }
        }
    }
}
