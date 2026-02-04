using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows.Forms;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.WinForms;

namespace ScnViewer;

sealed class Renderer
{
    private readonly GLControl _gl;
    private int _program;
    private int _uMvp;
    private int _uViewRot;
    private int _uUseTex;
    private int _uTex;
    private int _uTint;
    private int _whiteTex;

    private int _width = 1;
    private int _height = 1;

    private bool _drag;
    private Vector2 _lastMouse;
    private float _yaw = 0.6f;
    private float _pitch = 0.2f;
    private float _dist = 8.0f;
    private Vector3 _center = Vector3.Zero;
    private float _radius = 1.0f;
    private bool _normalizeToUnit = true;
    private const bool UploadTransposed = false;
    private const bool UseAltMvpOrder = true; // matches assets we tested
    private const bool UpAxisIsZ = false;
    private const float DefaultPointSize = 6.0f;

    private readonly List<GpuMesh> _gpuMeshes = new();
    private bool[] _visible = Array.Empty<bool>();
    private readonly Dictionary<string, int> _texCache = new(StringComparer.OrdinalIgnoreCase);

    // Pre-transform applied on CPU during upload to avoid matrix convention issues and ensure
    // the mesh is centered / in-range for the camera.
    private Vector3 _preCenter = Vector3.Zero;
    private float _preScale = 1.0f;

    public Renderer(GLControl gl) => _gl = gl;

    public void Initialize()
    {
        _gl.MakeCurrent();
        _program = GlUtil.CompileProgram(GlUtil.VertexShaderSrc, GlUtil.FragmentShaderSrc);
        _uMvp = GL.GetUniformLocation(_program, "uMVP");
        _uViewRot = GL.GetUniformLocation(_program, "uViewRot");
        _uUseTex = GL.GetUniformLocation(_program, "uUseTex");
        _uTex = GL.GetUniformLocation(_program, "uTex");
        _uTint = GL.GetUniformLocation(_program, "uTint");
        _whiteTex = GlUtil.CreateWhiteTexture();

        GL.Enable(EnableCap.DepthTest);
        GL.Enable(EnableCap.Multisample);
        GL.Disable(EnableCap.CullFace);
        GL.PolygonMode(MaterialFace.FrontAndBack, PolygonMode.Fill);
        GL.ClearColor(0.12f, 0.12f, 0.14f, 1f);

        // Ensure viewport is valid even if no Resize event fired yet.
        Resize(_gl.ClientSize.Width, _gl.ClientSize.Height);
    }

    public void Resize(int w, int h)
    {
        _width = Math.Max(1, w);
        _height = Math.Max(1, h);
        GL.Viewport(0, 0, _width, _height);
    }

    public void LoadMesh(ScnMesh mesh, string folder)
    {
        LoadScene(new[] { new ScnModel("mesh", mesh) }, folder, new[] { 0 });
    }

    public void LoadScene(IReadOnlyList<ScnModel> models, string folder, IReadOnlyCollection<int>? visibleModelIndices = null)
    {
        _gl.MakeCurrent();
        ClearScene();

        if (models.Count == 0) return;

        // Compute global bounds for a consistent pre-transform across all uploaded meshes.
        if (!TryComputeBounds(models.Select(m => m.Mesh), out var min, out var max))
            return;

        var calcCenter = (min + max) * 0.5f;
        var calcRadius = MathF.Max(0.01f, (max - min).Length * 0.6f);

        if (_normalizeToUnit)
        {
            _preCenter = calcCenter;
            _preScale = 1.0f / calcRadius;
        }
        else
        {
            _preCenter = Vector3.Zero;
            _preScale = 1.0f;
        }

        // Upload all meshes with the shared pre-transform.
        for (var i = 0; i < models.Count; i++)
        {
            var m = models[i].Mesh;
            var gm = new GpuMesh
            {
                Mesh = m,
                Vao = GL.GenVertexArray(),
                Vbo = GL.GenBuffer(),
                Ebo = m.Indices.Length > 0 ? GL.GenBuffer() : 0,
            };
            UploadMesh(gm, m);
            LoadMaterials(gm, m, folder);
            _gpuMeshes.Add(gm);
        }

        _visible = new bool[models.Count];
        if (visibleModelIndices == null || visibleModelIndices.Count == 0)
        {
            for (var i = 0; i < _visible.Length; i++) _visible[i] = true;
        }
        else
        {
            foreach (var i in visibleModelIndices)
            {
                if ((uint)i < (uint)_visible.Length)
                    _visible[i] = true;
            }
        }

        FitCameraForVisible();
    }

    public void SetVisibleModels(IReadOnlyCollection<int> modelIndices)
    {
        if (_gpuMeshes.Count == 0) return;
        Array.Fill(_visible, false);
        foreach (var i in modelIndices)
        {
            if ((uint)i < (uint)_visible.Length)
                _visible[i] = true;
        }
        FitCameraForVisible();
    }

    private void ClearScene()
    {
        foreach (var gm in _gpuMeshes)
        {
            if (gm.Vao != 0) GL.DeleteVertexArray(gm.Vao);
            if (gm.Vbo != 0) GL.DeleteBuffer(gm.Vbo);
            if (gm.Ebo != 0) GL.DeleteBuffer(gm.Ebo);
        }
        _gpuMeshes.Clear();

        foreach (var kv in _texCache)
        {
            if (kv.Value != 0 && kv.Value != _whiteTex)
                GL.DeleteTexture(kv.Value);
        }
        _texCache.Clear();
        _visible = Array.Empty<bool>();
    }

    private void LoadMaterials(GpuMesh gm, ScnMesh mesh, string folder)
    {
        gm.MaterialTex.Clear();
        foreach (var (mid, ms) in mesh.MaterialSets)
        {
            if (string.IsNullOrWhiteSpace(ms.ColorMap)) continue;
            var p = System.IO.Path.Combine(folder, ms.ColorMap);
            if (!_texCache.TryGetValue(p, out var tex))
            {
                tex = TexturePipeline.TryLoadTextureToGl(p);
                _texCache[p] = tex;
            }
            if (tex != 0) gm.MaterialTex[mid] = tex;
        }
    }

    private void FitCameraForVisible()
    {
        if (_gpuMeshes.Count == 0) return;
        var visibleMeshes = new List<ScnMesh>();
        for (var i = 0; i < _gpuMeshes.Count && i < _visible.Length; i++)
            if (_visible[i]) visibleMeshes.Add(_gpuMeshes[i].Mesh);
        if (visibleMeshes.Count == 0) return;

        if (!TryComputeBounds(visibleMeshes, out var min, out var max)) return;

        // Bounds are computed in original coordinates; transform them the same way we upload vertices.
        // This allows us to center the camera on a subset without reuploading buffers.
        var calcCenter = ((min + max) * 0.5f - _preCenter) * _preScale;
        var calcRadius = MathF.Max(0.01f, ((max - min) * _preScale).Length * 0.6f);
        _center = calcCenter;
        _radius = calcRadius;

        // Desired default size: not full-screen; keep a bit more distance.
        _dist = MathF.Max(1.6f, _radius * 3.0f);
        _yaw = 0.6f;
        _pitch = 0.2f;
    }

    private static bool TryComputeBounds(IEnumerable<ScnMesh> meshes, out Vector3 min, out Vector3 max)
    {
        var have = false;
        min = Vector3.Zero;
        max = Vector3.Zero;
        foreach (var mesh in meshes)
        {
            for (var i = 0; i < mesh.Positions.Length; i++)
            {
                var p = mesh.Positions[i];
                if (!float.IsFinite(p.X) || !float.IsFinite(p.Y) || !float.IsFinite(p.Z)) continue;
                if (!have) { min = max = p; have = true; continue; }
                min = Vector3.ComponentMin(min, p);
                max = Vector3.ComponentMax(max, p);
            }
        }
        return have;
    }

    private void UploadMesh(GpuMesh gm, ScnMesh mesh)
    {
        var vCount = mesh.Positions.Length;
        var interleaved = new float[vCount * 8];
        for (var i = 0; i < vCount; i++)
        {
            var p0 = mesh.Positions[i];
            var p = (p0 - _preCenter) * _preScale;
            var n = i < mesh.Normals.Length ? mesh.Normals[i] : new Vector3(0, 0, 1);
            var uv = i < mesh.UVs.Length ? mesh.UVs[i] : Vector2.Zero;
            var o = i * 8;
            interleaved[o + 0] = p.X;
            interleaved[o + 1] = p.Y;
            interleaved[o + 2] = p.Z;
            interleaved[o + 3] = n.X;
            interleaved[o + 4] = n.Y;
            interleaved[o + 5] = n.Z;
            interleaved[o + 6] = uv.X;
            interleaved[o + 7] = uv.Y;
        }

        GL.BindVertexArray(gm.Vao);
        GL.BindBuffer(BufferTarget.ArrayBuffer, gm.Vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, interleaved.Length * sizeof(float), interleaved, BufferUsageHint.StaticDraw);
        if (gm.Ebo != 0 && mesh.Indices.Length > 0)
        {
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, gm.Ebo);
            GL.BufferData(BufferTarget.ElementArrayBuffer, mesh.Indices.Length * sizeof(uint), mesh.Indices, BufferUsageHint.StaticDraw);
        }

        gm.SubsetUseBaseVertex = new bool[mesh.Subsets.Count];
        var ordered = mesh.Subsets.OrderBy(s => s.StartTri).ToList();
        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var start = Math.Max(0, s.StartTri) * 3;
            var count = Math.Max(0, s.TriCount) * 3;
            var end = Math.Min(mesh.Indices.Length, start + count);
            var min = uint.MaxValue;
            var max = 0u;
            for (var k = start; k < end; k++)
            {
                var idx = mesh.Indices[k];
                if (idx < min) min = idx;
                if (idx > max) max = idx;
            }
            if (min == uint.MaxValue) min = 0;

            // Only apply BaseVertex when indices are clearly relative to the subset:
            // - min is 0
            // - max fits inside the subset's declared vertex window
            gm.SubsetUseBaseVertex[i] = s.BaseVertex != 0 && s.VertexCount > 0 && min == 0 && max < (uint)s.VertexCount;
        }

        const int stride = 8 * sizeof(float);
        GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0);
        GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(1, 3, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float));
        GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(2, 2, VertexAttribPointerType.Float, false, stride, 6 * sizeof(float));
    }

    public void Render()
    {
        _gl.MakeCurrent();
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (_gpuMeshes.Count == 0) return;

        var wire = Environment.GetEnvironmentVariable("SCN_WIREFRAME") == "1";
        GL.PolygonMode(MaterialFace.FrontAndBack, wire ? PolygonMode.Line : PolygonMode.Fill);
        GL.UseProgram(_program);

        var proj = Matrix4.CreatePerspectiveFieldOfView(
            MathHelper.DegreesToRadians(45f),
            _width / (float)_height,
            0.01f,
            MathF.Max(1000f, _dist * 50f));

        var camPos = _center + GlUtil.Spherical(_yaw, _pitch, _dist);
        var up = UpAxisIsZ ? Vector3.UnitZ : Vector3.UnitY;
        var view = Matrix4.LookAt(camPos, _center, up);

        // Keep the known-good MVP convention we validated on SCN samples.
        var mvp = UseAltMvpOrder ? (Matrix4.Identity * view * proj) : (proj * view * Matrix4.Identity);
        GL.UniformMatrix4(_uMvp, UploadTransposed, ref mvp);

        // Upload view rotation for view-relative lighting (ignore translation).
        var viewRot = new Matrix3(view);
        GL.UniformMatrix3(_uViewRot, false, ref viewRot);
        GL.Uniform1(_uTex, 0);

        for (var mi = 0; mi < _gpuMeshes.Count && mi < _visible.Length; mi++)
        {
            if (!_visible[mi]) continue;
            var gm = _gpuMeshes[mi];
            var mesh = gm.Mesh;
            GL.BindVertexArray(gm.Vao);

            if (mesh.Indices.Length == 0)
            {
                BindMaterial(gm, 0);
                var ps = DefaultPointSize;
                var s = Environment.GetEnvironmentVariable("SCN_POINT_SIZE");
                if (!string.IsNullOrWhiteSpace(s) && float.TryParse(s, out var v) && v > 0.1f && v < 200f)
                    ps = v;
                GL.PointSize(ps);
                GL.DrawArrays(PrimitiveType.Points, 0, mesh.Positions.Length);
                continue;
            }

            if (mesh.Subsets.Count > 0)
            {
                var ordered = mesh.Subsets.OrderBy(s => s.StartTri).ToList();
                for (var si = 0; si < ordered.Count; si++)
                {
                    var s = ordered[si];
                    BindMaterial(gm, s.MaterialId);
                    var elemOffset = (IntPtr)(s.StartTri * 3 * sizeof(uint));
                    var elemCount = s.TriCount * 3;
                    if (gm.SubsetUseBaseVertex != null && (uint)si < (uint)gm.SubsetUseBaseVertex.Length && gm.SubsetUseBaseVertex[si])
                        GL.DrawElementsBaseVertex(PrimitiveType.Triangles, elemCount, DrawElementsType.UnsignedInt, elemOffset, s.BaseVertex);
                    else
                        GL.DrawElements(PrimitiveType.Triangles, elemCount, DrawElementsType.UnsignedInt, elemOffset);
                }
            }
            else
            {
                BindMaterial(gm, 0);
                GL.DrawElements(PrimitiveType.Triangles, mesh.Indices.Length, DrawElementsType.UnsignedInt, IntPtr.Zero);
            }
        }

        _gl.SwapBuffers();
    }

    private void BindMaterial(GpuMesh gm, int materialId)
    {
        if (Environment.GetEnvironmentVariable("SCN_NOTEX") == "1")
        {
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, _whiteTex);
            GL.Uniform1(_uUseTex, 0);
            GL.Uniform4(_uTint, 1f, 1f, 1f, 1f);
            return;
        }

        var tex = gm.MaterialTex.TryGetValue(materialId, out var t) ? t : _whiteTex;
        GL.ActiveTexture(TextureUnit.Texture0);
        GL.BindTexture(TextureTarget.Texture2D, tex);
        GL.Uniform1(_uUseTex, tex != 0 ? 1 : 0);
        GL.Uniform4(_uTint, 1f, 1f, 1f, 1f);
    }

    public void OnMouseDown(MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Left)
        {
            _drag = true;
            _lastMouse = new Vector2(e.X, e.Y);
        }
    }

    public void OnMouseMove(MouseEventArgs e)
    {
        if (!_drag) return;
        if ((Control.MouseButtons & MouseButtons.Left) == 0) { _drag = false; return; }
        var cur = new Vector2(e.X, e.Y);
        var d = cur - _lastMouse;
        _lastMouse = cur;
        _yaw += d.X * 0.01f;
        _pitch += d.Y * 0.01f;
        _pitch = Math.Clamp(_pitch, -1.55f, 1.55f);
    }

    public void OnMouseWheel(MouseEventArgs e)
    {
        _dist *= e.Delta > 0 ? 0.9f : 1.1f;
        _dist = Math.Clamp(_dist, 0.2f, 50000f);
    }

    private sealed class GpuMesh
    {
        public required ScnMesh Mesh { get; init; }
        public int Vao { get; init; }
        public int Vbo { get; init; }
        public int Ebo { get; init; }
        public Dictionary<int, int> MaterialTex { get; } = new();
        public bool[]? SubsetUseBaseVertex { get; set; }
    }
}
