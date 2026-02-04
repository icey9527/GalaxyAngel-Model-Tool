using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using OpenTK.Graphics.OpenGL4;
using Pfim;
using PfimImageFormat = Pfim.ImageFormat;
using DrawingImageFormat = System.Drawing.Imaging.ImageFormat;
using GlPixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;
using DrawingPixelFormat = System.Drawing.Imaging.PixelFormat;

namespace ScnViewer;

static class TexturePipeline
{
    // Defensive defaults: large textures + mipmaps can trigger driver instability on some systems.
    // Keep this conservative; allow override via env var SCN_TEX_MAX (e.g. 4096) and SCN_TEX_MIPMAP=1.
    private const int DefaultMaxTexDim = 2048;
    // Some external tools (and some in-game textures) rely on alpha for cutouts; default to preserving alpha.
    // If you need to ignore alpha (e.g. junk alpha causing dark/black appearance), set SCN_TEX_FORCE_OPAQUE=1.
    private const string ForceOpaqueEnv = "SCN_TEX_FORCE_OPAQUE";

    public static void RewriteTexturesToPng(string srcDir, string outDir, ScnMesh mesh)
    {
        foreach (var ms in mesh.MaterialSets.Values)
        {
            ms.ColorMap = ConvertOne(srcDir, outDir, ms.ColorMap);
            ms.NormalMap = ConvertOne(srcDir, outDir, ms.NormalMap);
            ms.LuminosityMap = ConvertOne(srcDir, outDir, ms.LuminosityMap);
            ms.ReflectionMap = ConvertOne(srcDir, outDir, ms.ReflectionMap);
        }
    }

    private static string? ConvertOne(string srcDir, string outDir, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        var baseName = Path.GetFileName(name);
        var src = Path.Combine(srcDir, baseName);
        if (!File.Exists(src)) return baseName;

        var dstName = ReplaceLastSuffixWithPng(baseName);
        var dst = Path.Combine(outDir, dstName);
        if (File.Exists(dst)) return dstName;

        using var bmp = LoadBitmap32(src);
        if (bmp == null) return baseName;

        // Preserve alpha by default; allow forcing opaque for pipelines that treat alpha as a multiply/visibility mask.
        if (Environment.GetEnvironmentVariable(ForceOpaqueEnv) == "1")
        {
            var px = GetPixelsBgra32(bmp, out var w, out var h);
            ForceOpaqueBgra32(px);
            using var outBmp = BitmapFromBgra32(px, w, h);
            outBmp.Save(dst, DrawingImageFormat.Png);
        }
        else
        {
            bmp.Save(dst, DrawingImageFormat.Png);
        }
        return dstName;
    }

    public static int TryLoadTextureToGl(string path)
    {
        try
        {
            var pixels = TryLoadPixelsBgra32(path, out var w, out var h);
            if (pixels == null || w <= 0 || h <= 0) return 0;

            var tex = GL.GenTexture();
            try
            {
                GL.BindTexture(TextureTarget.Texture2D, tex);

                // Cap texture size (both for VRAM and driver stability).
                GL.GetInteger(GetPName.MaxTextureSize, out var glMax);
                var maxDim = Math.Min(glMax > 0 ? glMax : DefaultMaxTexDim, ReadEnvInt("SCN_TEX_MAX", DefaultMaxTexDim));
                if (w > maxDim || h > maxDim)
                {
                    var s = Math.Min(maxDim / (float)w, maxDim / (float)h);
                    var nw = Math.Max(1, (int)MathF.Round(w * s));
                    var nh = Math.Max(1, (int)MathF.Round(h * s));
                    pixels = DownscaleNearestBgra32(pixels, w, h, nw, nh);
                    w = nw;
                    h = nh;
                }

                if (Environment.GetEnvironmentVariable(ForceOpaqueEnv) == "1")
                    ForceOpaqueBgra32(pixels);

                GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, w, h, 0,
                    GlPixelFormat.Bgra, PixelType.UnsignedByte, pixels);

                // Prefer no mipmaps by default; mipmap generation can be expensive and sometimes unstable on bad drivers.
                var wantMips = Environment.GetEnvironmentVariable("SCN_TEX_MIPMAP") == "1";
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter,
                    (int)(wantMips ? TextureMinFilter.LinearMipmapLinear : TextureMinFilter.Linear));
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
                GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
                if (wantMips) GL.GenerateMipmap(GenerateMipmapTarget.Texture2D);

                return tex;
            }
            catch
            {
                GL.DeleteTexture(tex);
                return 0;
            }
        }
        catch
        {
            return 0;
        }
    }

    private static string ReplaceLastSuffixWithPng(string name)
    {
        var lastDot = name.LastIndexOf('.');
        return lastDot < 0 ? name + ".png" : name.Substring(0, lastDot) + ".png";
    }

    private static Bitmap? LoadBitmap32(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".dds")
        {
            using var fs = File.OpenRead(path);
            using var dds = Pfimage.FromStream(fs);
            var w = dds.Width;
            var h = dds.Height;
            var bytes = dds.Data;

            // We keep everything in BGRA order (Bitmap Format32bppArgb expects BGRA).
            if (dds.Format == PfimImageFormat.Rgba32)
            {
                // Pfim 0.11.0's IImage.Data for Rgba32 is already BGRA in practice.
                // (Verified by comparing against the Python pipeline output.)
                var need = checked(w * h * 4);
                if (bytes.Length < need) return null;
                var bgra = new byte[need];
                System.Buffer.BlockCopy(bytes, 0, bgra, 0, need);
                return BitmapFromBgra32(bgra, w, h);
            }

            if (dds.Format == PfimImageFormat.Rgb24)
            {
                // Pfim's Rgb24 is BGR in practice; expand to BGRA for GDI+.
                var bgra = new byte[w * h * 4];
                for (var i = 0; i < w * h; i++)
                {
                    var si = i * 3;
                    var di = i * 4;
                    bgra[di + 0] = bytes[si + 0]; // B
                    bgra[di + 1] = bytes[si + 1]; // G
                    bgra[di + 2] = bytes[si + 2]; // R
                    bgra[di + 3] = 255;           // A
                }
                return BitmapFromBgra32(bgra, w, h);
            }

            return null;
        }

        using var src = (Bitmap)Image.FromFile(path);
        return src.PixelFormat == DrawingPixelFormat.Format32bppArgb
            ? (Bitmap)src.Clone()
            : src.Clone(new Rectangle(0, 0, src.Width, src.Height), DrawingPixelFormat.Format32bppArgb);
    }

    private static byte[]? TryLoadPixelsBgra32(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".dds")
        {
            using var fs = File.OpenRead(path);
            using var dds = Pfimage.FromStream(fs);
            width = dds.Width;
            height = dds.Height;
            var bytes = dds.Data;

            if (dds.Format == PfimImageFormat.Rgba32)
            {
                var need = checked(width * height * 4);
                if (bytes.Length < need) return null;
                var bgra = new byte[need];
                System.Buffer.BlockCopy(bytes, 0, bgra, 0, need);
                return bgra;
            }
            if (dds.Format == PfimImageFormat.Rgb24)
            {
                var bgra = new byte[width * height * 4];
                for (var i = 0; i < width * height; i++)
                {
                    var si = i * 3;
                    var di = i * 4;
                    bgra[di + 0] = bytes[si + 0];
                    bgra[di + 1] = bytes[si + 1];
                    bgra[di + 2] = bytes[si + 2];
                    bgra[di + 3] = 255;
                }
                return bgra;
            }
            return null;
        }

        using var bmp = LoadBitmap32(path);
        if (bmp == null) return null;
        return GetPixelsBgra32(bmp, out width, out height);
    }

    private static int ReadEnvInt(string name, int fallback)
    {
        var s = Environment.GetEnvironmentVariable(name);
        return int.TryParse(s, out var v) && v > 0 ? v : fallback;
    }

    private static void ForceOpaqueBgra32(byte[] bgra)
    {
        for (var i = 3; i < bgra.Length; i += 4)
            bgra[i] = 255;
    }

    private static byte[] DownscaleNearestBgra32(byte[] src, int sw, int sh, int dw, int dh)
    {
        var dst = new byte[dw * dh * 4];
        for (var y = 0; y < dh; y++)
        {
            var sy = (int)((y + 0.5f) * sh / dh);
            if (sy >= sh) sy = sh - 1;
            for (var x = 0; x < dw; x++)
            {
                var sx = (int)((x + 0.5f) * sw / dw);
                if (sx >= sw) sx = sw - 1;
                var si = (sy * sw + sx) * 4;
                var di = (y * dw + x) * 4;
                dst[di + 0] = src[si + 0];
                dst[di + 1] = src[si + 1];
                dst[di + 2] = src[si + 2];
                dst[di + 3] = src[si + 3];
            }
        }
        return dst;
    }

    private static Bitmap BitmapFromBgra32(byte[] bgra, int width, int height)
    {
        var bmp = new Bitmap(width, height, DrawingPixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, width, height);
        var data = bmp.LockBits(rect, ImageLockMode.WriteOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            System.Runtime.InteropServices.Marshal.Copy(bgra, 0, data.Scan0, bgra.Length);
        }
        finally
        {
            bmp.UnlockBits(data);
        }
        return bmp;
    }

    private static byte[] GetPixelsBgra32(Bitmap bmp, out int width, out int height)
    {
        width = bmp.Width;
        height = bmp.Height;
        var rect = new Rectangle(0, 0, width, height);
        using var clone = bmp.PixelFormat == DrawingPixelFormat.Format32bppArgb
            ? (Bitmap)bmp.Clone()
            : bmp.Clone(rect, DrawingPixelFormat.Format32bppArgb);

        var data = clone.LockBits(rect, ImageLockMode.ReadOnly, DrawingPixelFormat.Format32bppArgb);
        try
        {
            var bytes = new byte[width * height * 4];
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, bytes, 0, bytes.Length);
            return bytes;
        }
        finally
        {
            clone.UnlockBits(data);
        }
    }
}
