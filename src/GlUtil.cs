using System;
using System.Linq;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using GlPixelFormat = OpenTK.Graphics.OpenGL4.PixelFormat;

namespace ScnViewer;

static class GlUtil
{
    public static Vector3 Spherical(float yaw, float pitch, float r)
    {
        var cp = MathF.Cos(pitch);
        return new Vector3(
            MathF.Cos(yaw) * cp,
            MathF.Sin(pitch),
            MathF.Sin(yaw) * cp) * r;
    }

    public static int CompileProgram(string vs, string fs)
    {
        static int Compile(ShaderType type, string src)
        {
            var s = GL.CreateShader(type);
            GL.ShaderSource(s, src);
            GL.CompileShader(s);
            GL.GetShader(s, ShaderParameter.CompileStatus, out var ok);
            if (ok == 0)
                throw new Exception($"Shader compile failed ({type}): {GL.GetShaderInfoLog(s)}");
            return s;
        }

        var v = Compile(ShaderType.VertexShader, vs);
        var f = Compile(ShaderType.FragmentShader, fs);
        var p = GL.CreateProgram();
        GL.AttachShader(p, v);
        GL.AttachShader(p, f);
        GL.LinkProgram(p);
        GL.GetProgram(p, GetProgramParameterName.LinkStatus, out var ok2);
        if (ok2 == 0)
            throw new Exception($"Program link failed: {GL.GetProgramInfoLog(p)}");
        GL.DeleteShader(v);
        GL.DeleteShader(f);
        return p;
    }

    public static int CreateWhiteTexture()
    {
        var tex = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, tex);
        Span<byte> px = stackalloc byte[] { 255, 255, 255, 255 };
        GL.TexImage2D(TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba8, 1, 1, 0, GlPixelFormat.Rgba,
            PixelType.UnsignedByte, ref px[0]);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.Repeat);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.Repeat);
        return tex;
    }

    public const string VertexShaderSrc = """
        #version 330 core
        layout(location=0) in vec3 aPos;
        layout(location=1) in vec3 aNrm;
        layout(location=2) in vec2 aUv;
        uniform mat4 uMVP;
        uniform mat3 uViewRot;
        out vec2 vUv;
        out vec3 vNrm;
        void main() {
          // Most image loaders treat (0,0) as top-left, but OpenGL's texture coord origin is bottom-left.
          // Flip V so textures match what the OBJ exporter produces in common DCC tools.
          vUv = vec2(aUv.x, 1.0 - aUv.y);
          // Rotate normals into view-space so lighting follows the camera (headlight style).
          vNrm = normalize(uViewRot * aNrm);
          gl_Position = uMVP * vec4(aPos, 1.0);
        }
        """;

    public const string FragmentShaderSrc = """
        #version 330 core
        in vec2 vUv;
        in vec3 vNrm;
        uniform sampler2D uTex;
        uniform int uUseTex;
        uniform vec4 uTint;
        out vec4 FragColor;
        void main() {
          // View-relative light direction so turning the camera doesn't make the model go dark.
          vec3 L = normalize(vec3(0.25, 0.35, 1.0));
          float ndl = max(0.35, dot(normalize(vNrm), L));
          vec4 base = (uUseTex != 0) ? texture(uTex, vUv) : vec4(1,1,1,1);
          FragColor = vec4(base.rgb * ndl, 1.0) * uTint;
        }
        """;
}
