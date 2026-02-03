using System;
using System.IO;
using System.Linq;
using OpenTK.Mathematics;

namespace ScnViewer;

static class ObjWriter
{
    public static void Write(string outDir, string baseName, ScnMesh mesh)
    {
        var safe = Sanitize(baseName);
        var objPath = Path.Combine(outDir, safe + ".obj");
        var mtlPath = Path.Combine(outDir, safe + ".mtl");

        using (var mw = new StreamWriter(mtlPath, false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            if (mesh.Subsets.Count > 0 && mesh.MaterialSets.Count > 0)
            {
                var used = mesh.Subsets.Select(s => s.MaterialId).Distinct().OrderBy(x => x).ToList();
                foreach (var mid in used)
                {
                    mw.WriteLine($"newmtl {safe}_mat{mid}");
                    mw.WriteLine("Ka 1.000000 1.000000 1.000000");
                    mw.WriteLine("Kd 1.000000 1.000000 1.000000");
                    mw.WriteLine("Ks 0.000000 0.000000 0.000000");
                    mw.WriteLine("d 1.000000");
                    mw.WriteLine("illum 1");
                    if (mesh.MaterialSets.TryGetValue(mid, out var ms))
                    {
                        if (!string.IsNullOrWhiteSpace(ms.ColorMap)) mw.WriteLine($"map_Kd {Path.GetFileName(ms.ColorMap)}");
                    mw.WriteLine($"map_Ka {Path.GetFileName(ms.ColorMap)}");
                    mw.WriteLine($"map_Ka {Path.GetFileName(ms.ColorMap)}");
                        mw.WriteLine($"map_Ka {Path.GetFileName(ms.ColorMap)}");
                        if (!string.IsNullOrWhiteSpace(ms.NormalMap)) mw.WriteLine($"map_Bump {Path.GetFileName(ms.NormalMap)}");
                        if (!string.IsNullOrWhiteSpace(ms.LuminosityMap)) mw.WriteLine($"map_Ke {Path.GetFileName(ms.LuminosityMap)}");
                        if (!string.IsNullOrWhiteSpace(ms.ReflectionMap)) mw.WriteLine($"# ReflectionMap {Path.GetFileName(ms.ReflectionMap)}");
                    }
                    mw.WriteLine();
                }
            }
            else
            {
                mw.WriteLine($"newmtl {safe}_mat0");
                mw.WriteLine("Ka 1.000000 1.000000 1.000000");
                mw.WriteLine("Kd 1.000000 1.000000 1.000000");
                mw.WriteLine("Ks 0.000000 0.000000 0.000000");
                mw.WriteLine("d 1.000000");
                mw.WriteLine("illum 1");
                if (mesh.MaterialSets.TryGetValue(0, out var ms) && !string.IsNullOrWhiteSpace(ms.ColorMap))
                    mw.WriteLine($"map_Kd {Path.GetFileName(ms.ColorMap)}");
                    mw.WriteLine($"map_Ka {Path.GetFileName(ms.ColorMap)}");
                    mw.WriteLine($"map_Ka {Path.GetFileName(ms.ColorMap)}");
                        mw.WriteLine($"map_Ka {Path.GetFileName(ms.ColorMap)}");
                mw.WriteLine();
            }
        }

        using (var ow = new StreamWriter(objPath, false, new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
        {
            ow.WriteLine($"mtllib {Path.GetFileName(mtlPath)}");
            ow.WriteLine($"o {safe}");

            foreach (var v in mesh.Positions)
                ow.WriteLine(FormattableString.Invariant($"v {v.X:0.######} {v.Y:0.######} {v.Z:0.######}"));
            foreach (var uv in mesh.UVs.Length == mesh.Positions.Length ? mesh.UVs : Enumerable.Repeat(Vector2.Zero, mesh.Positions.Length))
                ow.WriteLine(FormattableString.Invariant($"vt {uv.X:0.######} {uv.Y:0.######}"));
            foreach (var n in mesh.Normals.Length == mesh.Positions.Length ? mesh.Normals : Enumerable.Repeat(new Vector3(0, 0, 1), mesh.Positions.Length))
                ow.WriteLine(FormattableString.Invariant($"vn {n.X:0.######} {n.Y:0.######} {n.Z:0.######}"));

            if (mesh.Subsets.Count > 0)
            {
                foreach (var s in mesh.Subsets.OrderBy(s => s.StartTri))
                {
                    ow.WriteLine($"usemtl {safe}_mat{s.MaterialId}");
                    var start = Math.Max(0, s.StartTri) * 3;
                    var end = start + Math.Max(0, s.TriCount) * 3;
                    if (start >= mesh.Indices.Length) continue;
                    end = Math.Min(end, mesh.Indices.Length);
                    end -= (end - start) % 3;
                    for (var i = start; i < end; i += 3)
                    {
                        var a = (int)mesh.Indices[i + 0] + 1;
                        var b = (int)mesh.Indices[i + 1] + 1;
                        var c = (int)mesh.Indices[i + 2] + 1;
                        ow.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
                    }
                }
            }
            else
            {
                ow.WriteLine($"usemtl {safe}_mat0");
                for (var i = 0; i < mesh.Indices.Length; i += 3)
                {
                    var a = (int)mesh.Indices[i + 0] + 1;
                    var b = (int)mesh.Indices[i + 1] + 1;
                    var c = (int)mesh.Indices[i + 2] + 1;
                    ow.WriteLine($"f {a}/{a}/{a} {b}/{b}/{b} {c}/{c}/{c}");
                }
            }
        }
    }

    private static string Sanitize(string name)
    {
        var s = new string(name.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray());
        if (string.IsNullOrWhiteSpace(s)) s = "mesh";
        return s;
    }
}


