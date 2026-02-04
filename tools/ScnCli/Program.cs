using System.Text;
using ScnViewer;

static class Program
{
    static int Main(string[] args)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        if (args.Length != 1)
        {
            Console.Error.WriteLine("Usage: ScnCli <path-to-model>");
            return 2;
        }

        var path = args[0];
        var data = File.ReadAllBytes(path);
        if (data.Length < 4)
        {
            Console.Error.WriteLine("File too small.");
            return 2;
        }

        var res = ModelLoader.Load(path, data);
        var magic = res.Magic;
        var models = res.Models;

        Console.WriteLine($"magic={magic} models={models.Count}");
        if (magic.StartsWith("AXO", StringComparison.OrdinalIgnoreCase))
        {
            Console.Write(AxoParser.DumpTopLevel(data));
        }
        for (var i = 0; i < models.Count; i++)
        {
            var m = models[i];
            var mesh = m.Mesh;
            Console.WriteLine($"[{i}] name={m.Name} v={mesh.Positions.Length} f={mesh.Indices.Length / 3} subsets={mesh.Subsets.Count} mats={mesh.MaterialSets.Count} group={m.GroupIndex} container={m.ContainerIndex}");

            if (mesh.Positions.Length > 0)
            {
                var min = mesh.Positions[0];
                var max = mesh.Positions[0];
                var nan = 0;
                for (var p = 0; p < mesh.Positions.Length; p++)
                {
                    var v = mesh.Positions[p];
                    if (float.IsNaN(v.X) || float.IsNaN(v.Y) || float.IsNaN(v.Z) || float.IsInfinity(v.X) || float.IsInfinity(v.Y) || float.IsInfinity(v.Z))
                    {
                        nan++;
                        continue;
                    }
                    min.X = Math.Min(min.X, v.X);
                    min.Y = Math.Min(min.Y, v.Y);
                    min.Z = Math.Min(min.Z, v.Z);
                    max.X = Math.Max(max.X, v.X);
                    max.Y = Math.Max(max.Y, v.Y);
                    max.Z = Math.Max(max.Z, v.Z);
                }
                Console.WriteLine($"  bbox min=({min.X:F3},{min.Y:F3},{min.Z:F3}) max=({max.X:F3},{max.Y:F3},{max.Z:F3}) nanOrInf={nan}");
            }

            if (mesh.Indices.Length > 0)
            {
                var minI = uint.MaxValue;
                var maxI = 0u;
                var deg = 0;
                var used = new HashSet<uint>();
                for (var k = 0; k < mesh.Indices.Length; k++)
                {
                    var idx = mesh.Indices[k];
                    used.Add(idx);
                    if (idx < minI) minI = idx;
                    if (idx > maxI) maxI = idx;
                }
                for (var t = 0; t + 2 < mesh.Indices.Length; t += 3)
                {
                    var i0 = (int)mesh.Indices[t + 0];
                    var i1 = (int)mesh.Indices[t + 1];
                    var i2 = (int)mesh.Indices[t + 2];
                    if ((uint)i0 >= (uint)mesh.Positions.Length || (uint)i1 >= (uint)mesh.Positions.Length || (uint)i2 >= (uint)mesh.Positions.Length)
                        continue;
                    if (i0 == i1 || i1 == i2 || i0 == i2) { deg++; continue; }
                    var p0 = mesh.Positions[i0];
                    var p1 = mesh.Positions[i1];
                    var p2 = mesh.Positions[i2];
                    var a = p1 - p0;
                    var b = p2 - p0;
                    var c = OpenTK.Mathematics.Vector3.Cross(a, b);
                    if (c.LengthSquared < 1e-10f) deg++;
                }
                if (minI == uint.MaxValue) minI = 0;
                Console.WriteLine($"  idx tri={mesh.Indices.Length / 3} degTri={deg} idxMin={minI} idxMax={maxI} uniqueVtxUsed={used.Count}");
            }
            foreach (var s in m.Mesh.Subsets.OrderBy(s => s.StartTri))
            {
                var start = Math.Max(0, s.StartTri) * 3;
                var count = Math.Max(0, s.TriCount) * 3;
                var end = Math.Min(m.Mesh.Indices.Length, start + count);
                var min = uint.MaxValue;
                var max = 0u;
                var deg = 0;
                for (var k = start; k < end; k++)
                {
                    var idx = m.Mesh.Indices[k];
                    if (idx < min) min = idx;
                    if (idx > max) max = idx;
                }
                for (var t = start; t + 2 < end; t += 3)
                {
                    var i0 = (int)m.Mesh.Indices[t + 0];
                    var i1 = (int)m.Mesh.Indices[t + 1];
                    var i2 = (int)m.Mesh.Indices[t + 2];
                    if ((uint)i0 >= (uint)m.Mesh.Positions.Length || (uint)i1 >= (uint)m.Mesh.Positions.Length || (uint)i2 >= (uint)m.Mesh.Positions.Length)
                        continue;
                    if (i0 == i1 || i1 == i2 || i0 == i2) { deg++; continue; }
                    var p0 = m.Mesh.Positions[i0];
                    var p1 = m.Mesh.Positions[i1];
                    var p2 = m.Mesh.Positions[i2];
                    var a = p1 - p0;
                    var b = p2 - p0;
                    var c = OpenTK.Mathematics.Vector3.Cross(a, b);
                    if (c.LengthSquared < 1e-10f) deg++;
                }
                if (min == uint.MaxValue) min = 0;
                Console.WriteLine($"  subset mat={s.MaterialId} startTri={s.StartTri} tri={s.TriCount} baseV={s.BaseVertex} vCnt={s.VertexCount} idxMin={min} idxMax={max} degTri={deg} idxMax+baseV={(long)max + s.BaseVertex}");
            }
        }

        if (res.Scn0Index is not null)
        {
            var scn0 = res.Scn0Index;
            Console.WriteLine($"[scn0] containers={scn0.Containers.Count} groups={scn0.Groups.Count} meshGroups={scn0.MeshTable.Count} extraEntries={scn0.ExtraTable.Count}");

            if (Environment.GetEnvironmentVariable("SCN_DUMP_AUTO") == "1")
            {
                try
                {
                    DumpScn0AutoTable(data);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"[scn0] auto dump failed: {ex.Message}");
                }
            }
        }
        return 0;
    }

    private static uint ReadU32(byte[] data, int off) => BitConverter.ToUInt32(data, off);

    private static (string s, int next) ReadCString(byte[] data, int off)
    {
        var end = Array.IndexOf(data, (byte)0, off);
        if (end < 0) end = data.Length;
        var s = Encoding.GetEncoding(932).GetString(data, off, Math.Max(0, end - off));
        return (s, Math.Min(data.Length, end + 1));
    }

    private static int CalcTreeEnd(byte[] data, int start)
    {
        int Node(int off)
        {
            var (_, next) = ReadCString(data, off);
            off = next + 0x40;
            var hasChild = (int)ReadU32(data, off); off += 4;
            if (hasChild == 1) off = Node(off);
            var hasSibling = (int)ReadU32(data, off); off += 4;
            if (hasSibling == 1) off = Node(off);
            return off;
        }
        return Node(start);
    }

    private static void DumpScn0AutoTable(byte[] data)
    {
        var pos = CalcTreeEnd(data, 4);
        var outerCount = (int)ReadU32(data, pos); pos += 4;
        Console.WriteLine($"[scn0] auto.outerCount={outerCount} at=0x{pos - 4:X}");
        if (outerCount < 0 || outerCount > 1_000_000) return;

        for (var oi = 0; oi < outerCount && pos < data.Length; oi++)
        {
            var (outerName, next) = ReadCString(data, pos);
            pos = next;
            var innerCount = (int)ReadU32(data, pos); pos += 4;
            Console.WriteLine($"[scn0] auto.outer[{oi}] name='{outerName}' inner={innerCount}");
            if (innerCount < 0 || innerCount > 1_000_000) return;

            for (var ii = 0; ii < innerCount && pos < data.Length; ii++)
            {
                var (innerName, next2) = ReadCString(data, pos);
                pos = next2;
                var flag = ReadU32(data, pos); pos += 4;

                var c1 = (int)ReadU32(data, pos); pos += 4; pos += 16 * c1;
                var c2 = (int)ReadU32(data, pos); pos += 4; pos += 20 * c2;
                var c3 = (int)ReadU32(data, pos); pos += 4; pos += 16 * c3;
                var c4 = (int)ReadU32(data, pos); pos += 4;

                var firstTex = "";
                for (var k = 0; k < c4; k++)
                {
                    if (pos + 0x68 > data.Length) break;
                    var rec = data.AsSpan(pos, 0x68);
                    pos += 0x68;
                    var texNameSpan = rec.Slice(0x58, 16);
                    var end = texNameSpan.IndexOf((byte)0);
                    if (end < 0) end = 16;
                    var tex = Encoding.ASCII.GetString(texNameSpan.Slice(0, end));
                    if (!string.IsNullOrWhiteSpace(tex)) { firstTex = tex; break; }
                }
                // If we broke early due to firstTex, still need to skip remaining records.
                if (!string.IsNullOrEmpty(firstTex))
                {
                    var remaining = c4 - 1;
                    pos += remaining * 0x68;
                }

                Console.WriteLine($"[scn0]   inner[{ii}] name='{innerName}' flag=0x{flag:X} c4={c4} tex='{firstTex}'");
            }
        }
    }
}
