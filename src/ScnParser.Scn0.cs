using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using OpenTK.Mathematics;

namespace ScnViewer;

static partial class ScnParser
{
    public static List<ScnModel> ParseScn0All(string path, byte[] data)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var baseHint = InferBaseHint(stem);
        var treeEnd = ParseTreeEnd(data, 4);

        var blocks = ScanScn0Stride32Blocks(data, treeEnd, Math.Max(0, data.Length - treeEnd));
        if (blocks.Count == 0) return new List<ScnModel>();

        var colorMaps = InferColorMapsFromStrings(data, baseHint);
        var models = new List<ScnModel>();
        var faceCounts = new Dictionary<Scn0Stride32Block, int>();

        // Sort by vertex count (high -> low), matching the viewer's usual "high first" behavior.
        var ordered = blocks
            .OrderByDescending(b => b.VertexCount)
            .ThenByDescending(b => b.TriangleCount)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var b = ordered[i];
            var mesh = DecodeStride32(data, b);
            mesh.MaterialSets = colorMaps.ToDictionary(kv => kv.Key, kv => new ScnMaterialSet { ColorMap = kv.Value });

            if (mesh.MaterialSets.Count > 1 && mesh.Indices.Length > 0)
            {
                var faceCount = mesh.Indices.Length / 3;
                var vcount = mesh.Positions.Length;
                mesh.Subsets = InferScn0SubsetsFromTextureStrings(data, vcount, faceCount, mesh.MaterialSets);
            }

            if (mesh.Subsets.Count == 0 && mesh.MaterialSets.Count > 1)
            {
                var first = mesh.MaterialSets.OrderBy(k => k.Key).First();
                mesh.MaterialSets = new Dictionary<int, ScnMaterialSet> { [0] = first.Value };
            }

            var name = ordered.Count == 1 ? stem : $"{stem}_{i}";
            models.Add(new ScnModel(name, mesh));
        }

        return models;
    }
    public static ScnMesh? ParseScn0High(string path, byte[] data)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(path);
        var baseHint = InferBaseHint(stem);
        var treeEnd = ParseTreeEnd(data, 4);

        var blocks = ScanScn0Stride32Blocks(data, treeEnd, Math.Max(0, data.Length - treeEnd));
        if (blocks.Count == 0) return null;
        // SCN0: prefer embedded filename strings (usually .dds families like ...E_0.dds/...E_1.dds)
        // for the high mesh blocks. auto-blocks can refer to .bmp variants and are not reliable here.
        var colorMaps = InferColorMapsFromStrings(data, baseHint);

        var bestBlock = blocks
            .OrderByDescending(b => b.VertexCount)
            .ThenByDescending(b => b.TriangleCount)
            .First();

        var outMesh = DecodeStride32(data, bestBlock);
        outMesh.MaterialSets = colorMaps.ToDictionary(kv => kv.Key, kv => new ScnMaterialSet { ColorMap = kv.Value });

        if (outMesh.MaterialSets.Count > 1 && outMesh.Indices.Length > 0)
        {
            var faceCount = outMesh.Indices.Length / 3;
            var vcount = outMesh.Positions.Length;
            outMesh.Subsets = InferScn0SubsetsFromTextureStrings(data, vcount, faceCount, outMesh.MaterialSets);
        }

        if (outMesh.Subsets.Count == 0 && outMesh.MaterialSets.Count > 1)
        {
            var first = outMesh.MaterialSets.OrderBy(k => k.Key).First();
            outMesh.MaterialSets = new Dictionary<int, ScnMaterialSet> { [0] = first.Value };
        }

        return outMesh;
    }


    // --- SCN0 stride32 high block ---

    private sealed record Scn0Stride32Block(int Offset, int VertexCount, int TriangleCount, int VbOff, int IbOff, int IbBytes);

    private static List<Scn0Stride32Block> ScanScn0Stride32Blocks(byte[] data, int start, int window)
    {
        const int stride = 32;
        var list = new List<Scn0Stride32Block>();
        var end = Math.Min(data.Length, start + window);
        for (var off = start; off < end - 16; off++)
        {
            var vcount = (int)ReadU32(data, off);
            if (vcount < 3 || vcount > 2_000_000) continue;
            var vbOff = off + 4;
            var vbSize = vcount * stride;
            var tagOff = vbOff + vbSize;
            if (tagOff + 8 >= data.Length) continue;
            var tag = (int)ReadU32(data, tagOff);
            if (tag != 101 && tag != 102) continue;
            var ibBytes = (int)ReadU32(data, tagOff + 4);
            if (ibBytes < 6 || (ibBytes % 2) != 0) continue;
            var ibOff = tagOff + 8;
            if (ibOff + ibBytes > data.Length) continue;
            var idxCount = ibBytes / 2;
            if ((idxCount % 3) != 0) continue;
            var sample = Math.Min(10, idxCount);
            var ok = true;
            for (var i = 0; i < sample; i++)
            {
                var idx = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(ibOff + i * 2, 2));
                if (idx >= vcount) { ok = false; break; }
            }
            if (!ok) continue;
            list.Add(new Scn0Stride32Block(off, vcount, idxCount / 3, vbOff, ibOff, ibBytes));
        }
        return list;
    }

    private static ScnMesh DecodeStride32(byte[] data, Scn0Stride32Block b)
    {
        const int stride = 32;
        var positions = new Vector3[b.VertexCount];
        var normals = new Vector3[b.VertexCount];
        var uvs = new Vector2[b.VertexCount];
        for (var i = 0; i < b.VertexCount; i++)
        {
            var at = b.VbOff + i * stride;
            var x = ReadF32(data, at + 0);
            var y = ReadF32(data, at + 4);
            var z = ReadF32(data, at + 8);
            var nx = ReadF32(data, at + 12);
            var ny = ReadF32(data, at + 16);
            var nz = ReadF32(data, at + 20);
            var u = ReadF32(data, at + 24);
            var v = 1.0f - ReadF32(data, at + 28);
            positions[i] = new Vector3(x, y, z);
            normals[i] = new Vector3(nx, ny, nz);
            uvs[i] = new Vector2(u, v);
        }

        var idxCount = b.IbBytes / 2;
        var indices = new uint[idxCount];
        for (var i = 0; i < idxCount; i++)
            indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(b.IbOff + i * 2, 2));

        return new ScnMesh { Positions = positions, Normals = normals, UVs = uvs, Indices = indices };
    }

    // --- SCN0 textures/subsets ---

    private static string? InferBaseHint(string stem)
        => stem.Length >= 4 && char.IsLetter(stem[0]) && char.IsLetter(stem[1]) && char.IsDigit(stem[2]) && char.IsDigit(stem[3])
            ? stem.Substring(0, 4)
            : null;

    private static Dictionary<int, string> InferColorMapsFromStrings(byte[] data, string? baseHint)
    {
        var byPrefix = new Dictionary<string, Dictionary<int, string>>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < data.Length - 5; i++)
        {
            var b = data[i];
            if (b < 32 || b >= 127) continue;
            var j = i;
            while (j < data.Length && data[j] != 0 && data[j] >= 32 && data[j] < 127 && (j - i) < 260) j++;
            if (j >= data.Length || data[j] != 0) continue;
            if (j - i < 4) { i = j; continue; }
            var s = System.Text.Encoding.ASCII.GetString(data, i, j - i);
            var dot = s.LastIndexOf('.');
            var us = s.LastIndexOf('_');
            if (dot < 0 || us <= 0 || us >= dot) { i = j; continue; }
            if (!int.TryParse(s.AsSpan(us + 1, dot - (us + 1)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
            { i = j; continue; }
            if (idx is < 0 or > 999) { i = j; continue; }
            var prefix = s.Substring(0, us);
            if (!byPrefix.TryGetValue(prefix, out var d)) byPrefix[prefix] = d = new Dictionary<int, string>();
            d[idx] = s;
            i = j;
        }
        if (byPrefix.Count == 0) return new Dictionary<int, string>();

        string? chosen = null;
        if (!string.IsNullOrWhiteSpace(baseHint))
        {
            foreach (var suf in new[] { "E", "U", "" })
            {
                var cand = baseHint + suf;
                if (byPrefix.ContainsKey(cand)) { chosen = cand; break; }
            }
        }
        chosen ??= byPrefix.OrderByDescending(kv => kv.Value.Count).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).First().Key;
        return byPrefix[chosen].OrderBy(kv => kv.Key).ToDictionary(kv => kv.Key, kv => kv.Value);
    }

    private static List<ScnSubset> InferSubsetsNearTextures(byte[] data, int vcount, int faceCount, Dictionary<int, string> colorMaps)
    {
        var subsets = new List<ScnSubset>();
        foreach (var (mid, tex) in colorMaps.OrderBy(kv => kv.Key))
        {
            var needle = System.Text.Encoding.ASCII.GetBytes(tex + "\0");
            var pos = IndexOf(data, needle, 0);
            if (pos < 0) continue;
            if (TryInferSubsetNearOffset(data, pos, mid, out var subset))
                subsets.Add(subset);
        }
        return subsets.OrderBy(s => s.StartTri).ThenBy(s => s.MaterialId).ToList();
    }

    private sealed record AutoBlock(int Offset, ScnMaterialSet Set);
    private sealed record Scn0MatPick(int MaterialId, int AnchorOffset, ScnMaterialSet Set);

    private static List<Scn0MatPick> ChooseScn0MaterialPicks(List<AutoBlock> autoBlocks, string? baseHint)
    {
        var blocks = autoBlocks.Where(b => !string.IsNullOrWhiteSpace(b.Set.ColorMap)).ToList();
        if (blocks.Count == 0) return new List<Scn0MatPick>();

        IEnumerable<AutoBlock> filtered = blocks;
        if (!string.IsNullOrWhiteSpace(baseHint))
        {
            var baseNameE = baseHint + "E";
            var baseNameU = baseHint + "U";
            var e = blocks.Where(b => StartsWithIgnoreCase(System.IO.Path.GetFileName(b.Set.ColorMap!), baseNameE)).ToList();
            if (e.Count > 0) filtered = e;
            else
            {
                var u = blocks.Where(b => StartsWithIgnoreCase(System.IO.Path.GetFileName(b.Set.ColorMap!), baseNameU)).ToList();
                if (u.Count > 0) filtered = u;
            }
        }

        var bestByColor = new Dictionary<string, AutoBlock>(StringComparer.OrdinalIgnoreCase);
        foreach (var b in filtered)
        {
            var key = System.IO.Path.GetFileName(b.Set.ColorMap!);
            if (!bestByColor.TryGetValue(key, out var cur) || ScoreMaterialSet(b.Set) > ScoreMaterialSet(cur.Set))
                bestByColor[key] = b;
        }

        var ordered = bestByColor.Values
            .Select(b =>
            {
                var idx = TryParseTrailingIndex(System.IO.Path.GetFileName(b.Set.ColorMap!), out var n) ? n : int.MaxValue;
                return (idx, name: System.IO.Path.GetFileName(b.Set.ColorMap!), b);
            })
            .OrderBy(t => t.idx)
            .ThenBy(t => t.name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var outList = new List<Scn0MatPick>();
        for (var i = 0; i < ordered.Count; i++)
            outList.Add(new Scn0MatPick(i, ordered[i].b.Offset, ordered[i].b.Set));
        return outList;
    }

    private static int ScoreMaterialSet(ScnMaterialSet s)
    {
        var score = 0;
        if (!string.IsNullOrWhiteSpace(s.ColorMap)) score += 1;
        if (!string.IsNullOrWhiteSpace(s.NormalMap)) score += 1;
        if (!string.IsNullOrWhiteSpace(s.LuminosityMap)) score += 1;
        if (!string.IsNullOrWhiteSpace(s.ReflectionMap)) score += 1;
        return score;
    }

    private static bool TryParseTrailingIndex(string name, out int idx)
    {
        idx = 0;
        var dot = name.LastIndexOf('.');
        var us = name.LastIndexOf('_');
        if (dot < 0 || us <= 0 || us >= dot) return false;
        return int.TryParse(name.AsSpan(us + 1, dot - (us + 1)), NumberStyles.Integer, CultureInfo.InvariantCulture, out idx);
    }

    private static bool StartsWithIgnoreCase(string s, string prefix)
        => s.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);

    private static bool TryInferSubsetNearOffset(byte[] data, int pos, int materialId, out ScnSubset subset)
    {
        subset = default;
        var bestTri = -1;
        var bestV = -1;
        var back = Math.Max(0, pos - 64);
        for (var ofs = pos - 16; ofs >= back; ofs -= 4)
        {
            if (ofs < 0 || ofs + 16 > data.Length) continue;
            var startTri = (int)ReadU32(data, ofs + 0);
            var triCount = (int)ReadU32(data, ofs + 4);
            var baseV = (int)ReadU32(data, ofs + 8);
            var vCnt = (int)ReadU32(data, ofs + 12);
            if (triCount <= 0 || vCnt <= 0) continue;
            if (startTri < 0 || baseV < 0) continue;
            if (triCount > bestTri || (triCount == bestTri && vCnt > bestV))
            {
                bestTri = triCount;
                bestV = vCnt;
                subset = new ScnSubset(materialId, startTri, triCount, baseV, vCnt);
            }
        }
        return bestTri > 0;
    }

    private static List<ScnSubset> InferScn0SubsetsFromTextureStrings(
        byte[] data,
        int vcount,
        int faceCount,
        Dictionary<int, ScnMaterialSet> materialSets)
    {
        var subsets = new List<ScnSubset>();
        if (vcount <= 0 || faceCount <= 0 || materialSets.Count == 0) return subsets;

        foreach (var (mid, ms) in materialSets.OrderBy(kv => kv.Key))
        {
            var tex = ms.ColorMap;
            if (string.IsNullOrWhiteSpace(tex)) continue;
            var name = System.IO.Path.GetFileName(tex);
            if (string.IsNullOrWhiteSpace(name)) continue;
            var needle = System.Text.Encoding.ASCII.GetBytes(name + "\0");

            var bestTri = -1;
            var bestV = -1;
            ScnSubset? best = null;

            // A texture string can appear multiple times; take the best validated match.
            for (var pos = IndexOf(data, needle, 0); pos >= 0; pos = IndexOf(data, needle, pos + 1))
            {
                var back = Math.Max(0, pos - 64);
                for (var ofs = pos - 16; ofs >= back; ofs -= 4)
                {
                    if (ofs < 0 || ofs + 16 > data.Length) continue;
                    var startTri = (int)ReadU32(data, ofs + 0);
                    var triCount = (int)ReadU32(data, ofs + 4);
                    var baseV = (int)ReadU32(data, ofs + 8);
                    var vCnt = (int)ReadU32(data, ofs + 12);
                    if (triCount <= 0 || vCnt <= 0) continue;
                    if (startTri < 0 || baseV < 0) continue;
                    if ((long)startTri + triCount > faceCount) continue;
                    if ((long)baseV + vCnt > vcount) continue;
                    if (Environment.GetEnvironmentVariable("SCN_DEBUG") == "1" && (startTri > faceCount || triCount > faceCount))
                        Console.WriteLine($"  [dbg] SCN0 subset candidate passed? mat={mid} start={startTri} tri={triCount} baseV={baseV} vCnt={vCnt} faceCount={faceCount} vcount={vcount} pos=0x{pos:X} ofs=0x{ofs:X}");

                    if (triCount > bestTri || (triCount == bestTri && vCnt > bestV))
                    {
                        bestTri = triCount;
                        bestV = vCnt;
                        best = new ScnSubset(mid, startTri, triCount, baseV, vCnt);
                    }
                }
            }

            if (best.HasValue)
                subsets.Add(best.Value);
        }

        return subsets.OrderBy(s => s.StartTri).ThenBy(s => s.MaterialId).ToList();
    }

    private static List<AutoBlock> ExtractAutoBlocks(byte[] buf)
    {
        var outList = new List<AutoBlock>();
        var needle = System.Text.Encoding.ASCII.GetBytes("auto\0");
        var start = 0;
        while (true)
        {
            var idx = IndexOf(buf, needle, start);
            if (idx < 0) break;
            start = idx + 1;
            if (idx + 9 >= buf.Length) continue;
            var entryCount = (int)ReadU32(buf, idx + 5);
            if (entryCount <= 0 || entryCount > 64) continue;
            var ofs = idx + 9;
            var ms = new ScnMaterialSet();
            for (var i = 0; i < entryCount; i++)
            {
                if (ofs >= buf.Length) break;
                var key = ReadCString(buf, ref ofs);
                var val = ReadCString(buf, ref ofs);
                if (ofs + 8 > buf.Length) break;
                ofs += 8; // flags/ints
                if (key.Equals("ColorMap", StringComparison.OrdinalIgnoreCase)) ms.ColorMap = val;
                else if (key.Equals("NormalMap", StringComparison.OrdinalIgnoreCase)) ms.NormalMap = val;
                else if (key.Equals("LuminosityMap", StringComparison.OrdinalIgnoreCase)) ms.LuminosityMap = val;
                else if (key.Equals("ReflectionMap", StringComparison.OrdinalIgnoreCase)) ms.ReflectionMap = val;
            }
            outList.Add(new AutoBlock(idx, ms));
        }
        return outList;
    }

    // --- SCN1 D3D decl520 blocks + subset table + auto blocks ---

    private sealed record D3dElem(ushort Stream, ushort Offset, byte Type, byte Method, byte Usage, byte UsageIndex);

    private static Dictionary<int, ScnMaterialSet> ExtractMaterialSetsFromAutoBlocksScn1(byte[] record)
    {
        // IMPORTANT (SCN1):
        // Many containers contain repeated `auto\0` blocks for the same texture family. However, the subset table
        // (D3DXATTRIBUTERANGE-style) frequently uses attribute/material IDs that refer to the *auto block index*,
        // not the deduped texture index. If we dedupe here, subset parsing for high meshes will fail (Subsets=0).
        var blocks = ExtractAutoBlocks(record);
        var outSets = new Dictionary<int, ScnMaterialSet>();
        for (var i = 0; i < blocks.Count; i++)
            outSets[i] = blocks[i].Set;
        return outSets;
    }

    private static List<ScnMesh> ExtractD3DMeshBlocks(ReadOnlySpan<byte> payload, Dictionary<int, ScnMaterialSet> materialSets)
    {
        var meshes = new List<ScnMesh>();
        if (payload.Length < 520 + 4 + 8) return meshes;

        // Quick prefilter: most decls start with stream=0, offset=0, type=float3(2), method=0, usage=POSITION(0), usageIndex=0.
        for (var off = 0; off <= payload.Length - (520 + 8); off++)
        {
            if (payload[off + 0] != 0 || payload[off + 1] != 0) continue;
            if (payload[off + 2] != 0 || payload[off + 3] != 0) continue;
            if (payload[off + 4] != 2 || payload[off + 5] != 0 || payload[off + 6] != 0 || payload[off + 7] != 0) continue;

            if (!TryParseDecl520(payload.Slice(off, 520), out var stride, out var elems))
                continue;

            var v0 = ReadU32(payload, off + 520);
            var v1 = ReadU32(payload, off + 524);
            var v2 = ReadU32(payload, off + 528);
            int vcount, vbOff;
            if (v0 is > 0 and <= 5_000_000) { vcount = (int)v0; vbOff = off + 524; }
            else if (v1 is > 0 and <= 5_000_000) { vcount = (int)v1; vbOff = off + 528; }
            else if (v2 is > 0 and <= 5_000_000) { vcount = (int)v2; vbOff = off + 532; }
            else continue;

            var vbSize = vcount * stride;
            var idxHdr = vbOff + vbSize;
            if (idxHdr + 8 > payload.Length) continue;
            var h0 = ReadU32(payload, idxHdr);
            var h1 = ReadU32(payload, idxHdr + 4);
            int bpi;
            int idxCount;
            if (h0 is 0 or 1 && h1 is > 0 and <= 50_000_000)
            {
                bpi = (h0 == 0) ? 2 : 4;
                idxCount = (int)h1;
            }
            else continue;

            if (idxCount < 3 || (idxCount % 3) != 0)
                continue;

            var ibOff = idxHdr + 8;
            var ibEnd = ibOff + idxCount * bpi;
            if (ibEnd > payload.Length) continue;

            var vb = payload.Slice(vbOff, vbSize);
            var positions = new Vector3[vcount];
            var normals = new Vector3[vcount];
            var uvs = new Vector2[vcount];
            for (var i = 0; i < vcount; i++)
            {
                DecodeVertexD3D(elems, vb, i, stride, out positions[i], out normals[i], out uvs[i]);
                uvs[i].Y = 1f - uvs[i].Y;
            }

            var indices = new uint[idxCount];
            if (bpi == 2)
            {
                for (var i = 0; i < idxCount; i++)
                    indices[i] = BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(ibOff + i * 2, 2));
            }
            else
            {
                for (var i = 0; i < idxCount; i++)
                    indices[i] = ReadU32(payload, ibOff + i * 4);
            }

            var faceCount = idxCount / 3;
            var subsets = FindSubsetTable(payload, off, vcount, faceCount, materialSets.Count);
            if (subsets.Count == 0)
                subsets = FindAttributeSubsets(payload, ibEnd, faceCount, vcount, materialSets.Count);

            meshes.Add(new ScnMesh
            {
                Positions = positions,
                Normals = normals,
                UVs = uvs,
                Indices = indices,
                Subsets = subsets,
                MaterialSets = new Dictionary<int, ScnMaterialSet>(materialSets),
            });
        }

        return meshes;
    }

    // Some containers store a per-face attribute buffer (often after IB) instead of a D3DXATTRIBUTERANGE table.
    // We detect it by looking for a u32 == faceCount followed by faceCount u32 material IDs.
    private static List<ScnSubset> FindAttributeSubsets(ReadOnlySpan<byte> payload, int startOff, int faceCount, int vcount, int materialCount)
    {
        if (faceCount <= 0) return new List<ScnSubset>();
        var need = 4 + faceCount * 4;
        if (need <= 0 || need > payload.Length) return new List<ScnSubset>();

        static int Align4(int x) => (x + 3) & ~3;
        var start = Align4(Math.Max(0, startOff));
        var end = Math.Min(payload.Length - need, start + 0x4000);

        for (var off = start; off <= end; off += 4)
        {
            var n = (int)ReadU32(payload, off);
            if (n != faceCount) continue;

            var attrs = new int[faceCount];
            var ok = true;
            var max = -1;
            for (var i = 0; i < faceCount; i++)
            {
                var a = (int)ReadU32(payload, off + 4 + i * 4);
                if (a < 0) { ok = false; break; }
                // don't hard-reject > materialCount, because the engine can index auto-blocks directly.
                if (a > 4096) { ok = false; break; }
                attrs[i] = a;
                if (a > max) max = a;
            }
            if (!ok) continue;
            if (materialCount > 0 && max >= 4096) continue;

            var subsets = new List<ScnSubset>();
            var runMat = attrs[0];
            var runStart = 0;
            for (var i = 1; i < faceCount; i++)
            {
                if (attrs[i] == runMat) continue;
                subsets.Add(new ScnSubset(runMat, runStart, i - runStart, 0, vcount));
                runMat = attrs[i];
                runStart = i;
            }
            subsets.Add(new ScnSubset(runMat, runStart, faceCount - runStart, 0, vcount));

            // Prefer meaningful splits: require at least 2 subsets and non-trivial coverage.
            if (subsets.Count >= 2 && subsets.Sum(s => s.TriCount) == faceCount)
                return subsets.OrderBy(s => s.StartTri).ToList();
        }

        return new List<ScnSubset>();
    }

    private static List<ScnSubset> FindSubsetTable(ReadOnlySpan<byte> payload, int declOff, int vcount, int faceCount, int materialCount)
    {
        var best = new List<ScnSubset>();
        // Based on `sub_10007860` (CDCMgr::LoadMesh a3==1):
        // the table can appear immediately before the decl block as:
        //   u32 subsetCount
        //   subsetCount * 20 bytes (5 dwords each): matId,startTri,triCount,baseV,vCnt
        //   decl520...
        //
        // Try this "anchored" parse first (strictly ends at declOff).
        for (var subsetCount = 1; subsetCount <= 256; subsetCount++)
        {
            var start = declOff - (4 + subsetCount * 20);
            if (start < 0) break;
            if (start + 4 + subsetCount * 20 != declOff) continue;
            if ((int)ReadU32(payload, start) != subsetCount) continue;

            if (TryReadSubsetRanges(payload, start, subsetCount, vcount, faceCount, out var subs))
                return subs;
        }

        // Fallback: Subset tables are not always located right before the decl block. Scan a wider area:
        // - prefer around the decl for speed
        // - but allow the whole payload as fallback
        var preferredStart = Math.Max(0, declOff - 0x8000);
        var preferredEnd = Math.Min(payload.Length, declOff + 0x20000);
        static IEnumerable<int> ScanOffsets(int start, int endExclusive, int maxLen)
        {
            var end = Math.Min(endExclusive, maxLen - 4);
            for (var off = Math.Max(0, start); off <= end; off += 4) yield return off;
        }

        foreach (var off in ScanOffsets(preferredStart, preferredEnd, payload.Length))
        {
            var subsetCount = (int)ReadU32(payload, off);
            if (subsetCount <= 0 || subsetCount > 256) continue;
            if (!TryReadSubsetRanges(payload, off, subsetCount, vcount, faceCount, out var subs)) continue;

            // Prefer more coverage (some tables omit hidden/unused faces).
            if (subs.Sum(s => s.TriCount) > best.Sum(s => s.TriCount))
                best = subs;
        }

        if (best.Count == 0)
        {
            foreach (var off in ScanOffsets(0, payload.Length, payload.Length))
            {
                var subsetCount = (int)ReadU32(payload, off);
                if (subsetCount <= 0 || subsetCount > 256) continue;
                if (!TryReadSubsetRanges(payload, off, subsetCount, vcount, faceCount, out var subs)) continue;
                if (subs.Sum(s => s.TriCount) > best.Sum(s => s.TriCount))
                    best = subs;
            }
        }

        return best;
    }

    private static bool TryReadSubsetRanges(ReadOnlySpan<byte> payload, int tableOff, int subsetCount, int vcount, int faceCount, out List<ScnSubset> subs)
    {
        subs = new List<ScnSubset>(subsetCount);
        var bytes = 4 + subsetCount * 20;
        if (tableOff < 0 || tableOff + bytes > payload.Length) return false;

        for (var i = 0; i < subsetCount; i++)
        {
            var o = tableOff + 4 + i * 20;
            var matId = (int)ReadU32(payload, o + 0);
            var startTri = (int)ReadU32(payload, o + 4);
            var triCount = (int)ReadU32(payload, o + 8);
            var baseV = (int)ReadU32(payload, o + 12);
            var vCnt = (int)ReadU32(payload, o + 16);

            if (matId < 0 || matId > 4096) return false;
            if (triCount <= 0 || startTri < 0) return false;
            if (faceCount > 0 && (long)startTri + triCount > faceCount) return false;
            if (vCnt <= 0 || baseV < 0) return false;
            if (vcount > 0 && (long)baseV + vCnt > vcount) return false;

            subs.Add(new ScnSubset(matId, startTri, triCount, baseV, vCnt));
        }

        return subs.Count > 0;
    }

    private static bool TryParseDecl520(ReadOnlySpan<byte> block, out int stride, out List<D3dElem> elems)
    {
        stride = 0;
        elems = new List<D3dElem>();
        if (block.Length != 520) return false;
        var endFound = false;
        for (var i = 0; i < 65; i++)
        {
            var o = i * 8;
            var stream = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(o, 2));
            var offs = BinaryPrimitives.ReadUInt16LittleEndian(block.Slice(o + 2, 2));
            var typ = block[o + 4];
            var method = block[o + 5];
            var usage = block[o + 6];
            var usageIdx = block[o + 7];
            elems.Add(new D3dElem(stream, offs, typ, method, usage, usageIdx));
            if (stream == 0xFF) { endFound = true; break; }
        }
        if (!endFound) return false;

        static int TypeSize(byte t) => t switch
        {
            0 => 4, 1 => 8, 2 => 12, 3 => 16,
            4 => 4, 5 => 4, 6 => 4, 7 => 4,
            8 => 4, 9 => 4, 10 => 8, 11 => 4,
            12 => 8, 13 => 4, 14 => 4, 15 => 4,
            16 => 8, _ => -1
        };

        foreach (var e in elems)
        {
            if (e.Stream == 0xFF) break;
            if (e.Stream != 0) continue;
            var sz = TypeSize(e.Type);
            if (sz <= 0) return false;
            stride = Math.Max(stride, e.Offset + sz);
        }
        return stride is > 0 and <= 1024;
    }

    private static void DecodeVertexD3D(List<D3dElem> elems, ReadOnlySpan<byte> vb, int i, int stride,
        out Vector3 pos, out Vector3 nrm, out Vector2 uv)
    {
        pos = Vector3.Zero;
        nrm = new Vector3(0, 0, 1);
        uv = Vector2.Zero;
        var baseOff = i * stride;
        foreach (var e in elems)
        {
            if (e.Stream == 0xFF) break;
            if (e.Stream != 0) continue;
            var at = baseOff + e.Offset;
            if (at < 0 || at + 4 > vb.Length) continue;
            if (e.Usage == 0 && e.Type == 2)
                pos = new Vector3(ReadF32(vb, at), ReadF32(vb, at + 4), ReadF32(vb, at + 8));
            else if (e.Usage == 3 && e.Type == 2)
                nrm = new Vector3(ReadF32(vb, at), ReadF32(vb, at + 4), ReadF32(vb, at + 8));
            else if (e.Usage == 5 && e.UsageIndex == 0 && e.Type == 1 && at + 8 <= vb.Length)
                uv = new Vector2(ReadF32(vb, at), ReadF32(vb, at + 4));
        }
    }

    // --- tree ---

    private static int ParseTreeEnd(byte[] data, int start)
    {
        var r = new ByteReader(data) { Position = start };
        void Node()
        {
            _ = r.ReadCString();
            r.Skip(0x40);
            var c = r.ReadU32();
            if (c == 1) Node();
            var s = r.ReadU32();
            if (s == 1) Node();
        }
        Node();
        return r.Position;
    }

    // --- byte helpers ---

}


