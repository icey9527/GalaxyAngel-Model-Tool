using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using OpenTK.Mathematics;

namespace ScnViewer;

static class ScnParser
{
    private static Encoding ScnEncoding
    {
        get
        {
            // Many Japanese titles store names in Shift-JIS/CP932.
            try { return Encoding.GetEncoding(932); }
            catch { return Encoding.UTF8; }
        }
    }

    public static List<ScnModel> ParseScn1All(string path, byte[] data)
    {
        try
        {
            return ParseScn1AllStrict(data);
        }
        catch
        {
            // If strict parsing fails due to unexpected version/offset, fall back to the older heuristic parser.
            // This should be removable once SCN1 is fully table-driven in our implementation.
            return ParseScn1AllLegacy(data);
        }
    }

    public sealed record Scn1Index(
        ScnTreeNodeInfo? Tree,
        List<ScnContainerInfo> Containers,
        List<ScnGroupEntry> Groups,
        List<ScnModel> Models,
        Scn1MeshTable? MeshTable = null,
        Scn1MeshTableScan? MeshTableScan = null);

    public sealed record Scn1MeshTable(int StartOffset, List<Scn1MeshGroup> Groups);
    public sealed record Scn1MeshGroup(int GroupIndex, List<Scn1MeshEntry> Entries);
    public sealed record Scn1MeshEntry(string Name, int Flag, ReadOnlyMemory<byte> Payload);
    public sealed record Scn1MeshTableScan(int PreferredStart, int ScanStart, int ScanEnd, int Candidates, int BestScore);

    public static Scn1Index ParseScn1Index(byte[] data)
    {
        var r = new ByteReader(data);
        if (r.ReadAscii(4) != "SCN1") return new(null, new(), new(), new());
        _ = r.ReadU32();

        var (tree, treeBytes) = ParseTreeStrictInfo(data, r.Position);
        r.Position += treeBytes;

        var autoBytes = SkipAutoBlockTable(data, r.Position);
        r.Position += autoBytes;
        var afterAuto = r.Position;

        var pairCount = (int)r.ReadU32();
        r.Skip(pairCount * 12);

        r.Skip(12);

        var containerCount = (int)r.ReadU32();
        var containers = new List<ReadOnlyMemory<byte>>(containerCount);
        var containerInfo = new List<ScnContainerInfo>(containerCount);
        for (var i = 0; i < containerCount; i++)
        {
            if (r.Remaining < 4) break;
            var sz = (int)r.ReadU32();
            if (sz <= 0 || sz > r.Remaining + 4) break;
            var start = r.Position - 4;
            r.Skip(sz - 4);
            var mem = data.AsMemory(start, sz);
            containers.Add(mem);

            // Container name at offset 8.
            var span = mem.Span;
            var nameStart = 8;
            var nameEnd = nameStart < span.Length ? span.Slice(nameStart).IndexOf((byte)0) : -1;
            if (nameEnd < 0) nameEnd = Math.Max(0, span.Length - nameStart);
            var nm = nameStart < span.Length ? ScnEncoding.GetString(span.Slice(nameStart, nameEnd)) : "";
            containerInfo.Add(new ScnContainerInfo(i, nm));
        }

        var mapCount = (int)r.ReadU32();
        var groups = new List<ScnGroupEntry>(mapCount);
        for (var i = 0; i < mapCount && r.Remaining > 0; i++)
        {
            var group = r.ReadI32();
            var idx = r.ReadI32();
            var name = r.ReadCString();
            if (group == -1) break;
            groups.Add(new ScnGroupEntry(group, idx, name));
        }

        // Optional: Try to parse the "mesh table" used by `sub_10015AC0`.
        //
        // The user request is to avoid "scan for patterns" approaches. So we do NOT brute-force scan offsets here.
        // Instead we try the known structural candidates in order:
        //   1) immediately after the auto table (most consistent with `sub_10014580` consumption)
        //   2) our current cursor (after the map table) for variants
        //
        // If parsing fails, fall back to container-scan extraction (kept temporarily for compatibility).
        var (meshTable, scan) = TryParseScn1MeshTableFromKnownStarts(data, afterAuto, r.Position);

        var models = meshTable != null
            ? BuildModelsFromMeshTable(meshTable)
            : BuildModelsFromContainers(containers, groups);

        return new Scn1Index(tree, containerInfo, groups, models, meshTable, scan);
    }

    private static List<ScnModel> BuildModelsFromContainers(List<ReadOnlyMemory<byte>> containers, List<ScnGroupEntry> groups)
    {
        // Build models from containers using container-local auto blocks.
        var models = new List<ScnModel>();

        // Reverse index container -> groupIndex (best-effort; a container can appear in multiple groups).
        var containerToGroup = groups
            .GroupBy(g => g.ContainerIndex)
            .ToDictionary(g => g.Key, g => g.Select(x => x.GroupIndex).DefaultIfEmpty(-1).Min());

        for (var ci = 0; ci < containers.Count; ci++)
        {
            var rec = containers[ci].ToArray();
            var nameStart = 8;
            var nameEnd = nameStart < rec.Length ? Array.IndexOf(rec, (byte)0, nameStart) : -1;
            if (nameEnd < 0) nameEnd = rec.Length;
            var containerName = nameStart < rec.Length ? ScnEncoding.GetString(rec, nameStart, Math.Max(0, nameEnd - nameStart)) : $"container{ci}";
            var afterName = Math.Min(rec.Length, nameEnd + 1);
            var payloadOff = afterName;

            var materialSets = ExtractMaterialSetsFromAutoBlocksScn1(rec);
            var embedded = ExtractD3DMeshBlocks(rec.AsSpan(payloadOff), materialSets);
            for (var mi = 0; mi < embedded.Count; mi++)
            {
                var m = embedded[mi];
                var nm = embedded.Count == 1 ? containerName : $"{containerName}#{mi}";
                var gi = containerToGroup.TryGetValue(ci, out var gix) ? gix : -1;
                models.Add(new ScnModel(nm, m, ci, mi, gi));
            }
        }
        return models;
    }

    private static List<ScnModel> BuildModelsFromMeshTable(Scn1MeshTable meshTable)
    {
        var models = new List<ScnModel>();
        for (var gi = 0; gi < meshTable.Groups.Count; gi++)
        {
            var g = meshTable.Groups[gi];
            for (var ei = 0; ei < g.Entries.Count; ei++)
            {
                var e = g.Entries[ei];
                var record = e.Payload.ToArray();
                var materialSets = ExtractMaterialSetsFromAutoBlocksScn1(record);
                var embedded = ExtractD3DMeshBlocks(record, materialSets);
                for (var mi = 0; mi < embedded.Count; mi++)
                {
                    var m = embedded[mi];
                    var nm = embedded.Count == 1 ? e.Name : $"{e.Name}#{mi}";
                    models.Add(new ScnModel(nm, m, ContainerIndex: -1, EmbeddedIndex: mi, GroupIndex: g.GroupIndex));
                }
            }
        }
        return models;
    }

    private static (Scn1MeshTable? table, Scn1MeshTableScan scan) TryParseScn1MeshTableFromKnownStarts(byte[] data, params int[] starts)
    {
        // Try to parse the `sub_10015AC0` input stream.
        // Expected (best-effort based on decomp):
        //   u32 groupCount
        //   repeat groupCount:
        //     u32 entryCount
        //     repeat entryCount:
        //       name_cstr
        //       u32 flag
        //       payload: starts with u32 size (container block), consume `size` bytes total
        //
        // Notes:
        // - Decomp shows `v11 = &a1[strlen(name) + 5]`, meaning "cstring + 4 bytes (flag) + payloadStart".
        // - The payload length is not read directly by `sub_10015AC0`; it is returned by `CDCMgr::LoadMesh`,
        //   which in practice reads a size prefix from the payload itself (u32 size at payloadStart).
        // - We keep this conservative and reject on inconsistencies.

        if (starts == null || starts.Length == 0)
            starts = new[] { 0 };

        static int Align4(int x) => (x + 3) & ~3;

        foreach (var raw in starts)
        {
            var start = Align4(Math.Clamp(raw, 0, Math.Max(0, data.Length - 4)));
            var table = TryParseScn1MeshTableAt(data, start);
            var scan = new Scn1MeshTableScan(raw, start, start, table != null ? 1 : 0, table != null ? 1 : -1);
            if (table != null) return (table, scan);
        }

        // Not found at the expected anchors.
        return (null, new Scn1MeshTableScan(starts[0], Align4(starts[0]), Align4(starts[0]), 0, -1));
    }

    private static Scn1MeshTable? TryParseScn1MeshTableAt(byte[] data, int start)
    {
        if (start < 0 || start + 4 > data.Length) return null;
        var r = new ByteReader(data) { Position = start };
        var groupCount = (int)r.ReadU32();
        if (groupCount <= 0 || groupCount > 512) return null;

        var groups = new List<Scn1MeshGroup>(groupCount);
        for (var gi = 0; gi < groupCount; gi++)
        {
            if (r.Remaining < 4) return null;
            var entryCount = (int)r.ReadU32();
            if (entryCount < 0 || entryCount > 5000) return null;

            var entries = new List<Scn1MeshEntry>(entryCount);
            for (var ei = 0; ei < entryCount; ei++)
            {
                if (r.Remaining < 2) return null;
                var name = r.ReadCString();
                if (name.Length > 260) return null;
                if (r.Remaining < 4) return null;
                var flag = (int)r.ReadU32();

                // Payload starts with u32 size (container block); consume that many bytes.
                if (r.Remaining < 4) return null;
                var len = (int)r.ReadU32();
                // size includes itself (consistent with our container blocks): we already consumed the size dword,
                // so we need to take (len-4) remaining bytes from the stream.
                if (len < 8) return null;
                var remaining = len - 4;
                if (remaining > r.Remaining) return null;
                var payload = data.AsMemory(r.Position - 4, len);
                r.Skip(remaining);
                entries.Add(new Scn1MeshEntry(name, flag, payload));
            }

            groups.Add(new Scn1MeshGroup(gi, entries));
        }

        // Sanity: require at least one entry with a plausible container name at offset 8 (like our other container blocks).
        // This is cheap and more "structural" than scanning for D3D decl patterns.
        var anyContainerName = false;
        foreach (var g in groups)
        {
            foreach (var e in g.Entries)
            {
                var span = e.Payload.Span;
                if (span.Length < 16) continue;
                var nameStart = 8;
                var nameEnd = span.Slice(nameStart).IndexOf((byte)0);
                if (nameEnd <= 0) continue;
                // accept short-ish ascii-ish names (we decode later as CP932 anyway)
                if (nameEnd > 1 && nameEnd < 64) { anyContainerName = true; break; }
            }
            if (anyContainerName) break;
        }
        if (!anyContainerName) return null;

        return new Scn1MeshTable(start, groups);
    }

    // Strict SCN1 parser guided by `sub_10014F20` / `sub_10015430` / `sub_100155C0`.
    //
    // Layout (high level):
    //   "SCN1" + u32(0)
    //   tree (hasChild + hasSibling recursion)
    //   auto-block table (`sub_100155C0`) -> counted outer records / inner entries
    //   u32 pairCount; pairCount * (u32,u32)
    //   3 * u32
    //   u32 containerCount; containers are size-prefixed blocks (u32 size + bytes)
    //   u32 mapCount; mapCount * (i32 groupIndex, i32 containerIndex, cstring name) (groupIndex can be -1 sentinel)
    //   (more sections we currently ignore, e.g. collision/supplementary loads)
    private static List<ScnModel> ParseScn1AllStrict(byte[] data)
    {
        // For now, strict parsing returns the per-container models list.
        return ParseScn1Index(data).Models;
    }

    // Legacy (heuristic) SCN1 parser kept for compatibility while strict parsing is rolled out.
    private static List<ScnModel> ParseScn1AllLegacy(byte[] data)
    {
        var r = new ByteReader(data);
        if (r.ReadAscii(4) != "SCN1") return new();
        _ = r.ReadU32();

        var treeEnd = ParseTreeEnd(data, r.Position);
        r.Position = treeEnd;

        // Old skip logic (may be wrong for some versions)
        var countA = r.ReadU32();
        for (var i = 0; i < countA; i++)
        {
            _ = r.ReadCString();
            var countB = r.ReadU32();
            for (var j = 0; j < countB; j++)
            {
                _ = r.ReadCString();
                _ = r.ReadU32();
                var c1 = r.ReadU32(); r.Skip(16 * (int)c1);
                var c2 = r.ReadU32(); r.Skip(20 * (int)c2);
                var c3 = r.ReadU32(); r.Skip(16 * (int)c3);
                var c4 = r.ReadU32(); r.Skip(68 * (int)c4);
            }
        }

        var pairCount = r.ReadU32();
        r.Skip((int)pairCount * 12);
        r.Skip(12);

        var meshCount = (int)r.ReadU32();
        var models = new List<ScnModel>();
        for (var i = 0; i < meshCount; i++)
        {
            var recSize = (int)r.ReadU32();
            if (recSize <= 8 || r.Remaining < recSize - 4) break;
            var rec = new byte[recSize];
            BinaryPrimitives.WriteUInt32LittleEndian(rec.AsSpan(0, 4), (uint)recSize);
            r.ReadInto(rec.AsSpan(4, recSize - 4));

            var nameEnd = Array.IndexOf(rec, (byte)0, 4);
            var payloadOff = nameEnd >= 4 ? nameEnd + 1 : 0;
            if (payloadOff <= 0 || payloadOff >= rec.Length) continue;
            var recName = ScnEncoding.GetString(rec, 4, nameEnd - 4);
            var payload = rec.AsSpan(payloadOff);

            var materialSets = ExtractMaterialSetsFromAutoBlocksScn1(rec);
            var embedded = ExtractD3DMeshBlocks(payload, materialSets);
            for (var mi = 0; mi < embedded.Count; mi++)
            {
                var m = embedded[mi];
                var nm = embedded.Count == 1 ? recName : $"{recName}#{mi}";
                models.Add(new ScnModel(nm, m, i, mi));
            }
        }
        return models;
    }

    // Strict node parser matching `sub_10015430` semantics (hasChild + hasSibling).
    private static (ScnTreeNodeInfo? root, int bytesConsumed) ParseTreeStrictInfo(byte[] data, int start)
    {
        var pos = start;
        var rootPtr = ParseNodePtr(data, ref pos);
        var root = ToInfo(rootPtr);
        return (root, pos - start);

        static NodePtr? ParseNodePtr(byte[] data, ref int pos)
        {
            var startOff = pos;
            if (pos < 0 || pos >= data.Length) return null;
            var name = ReadCString(data, ref pos);
            if (pos + 0x40 + 8 > data.Length) return null;
            var blob = data.AsSpan(pos, 0x40).ToArray();
            pos += 0x40;
            var hasChild = (int)ReadU32(data, pos); pos += 4;
            NodePtr? child = null;
            if (hasChild == 1) child = ParseNodePtr(data, ref pos);
            var hasSibling = (int)ReadU32(data, pos); pos += 4;
            NodePtr? sib = null;
            if (hasSibling == 1) sib = ParseNodePtr(data, ref pos);
            return new NodePtr(name, startOff, blob, hasChild, hasSibling, child, sib);
        }

        static ScnTreeNodeInfo? ToInfo(NodePtr? n)
        {
            if (n == null) return null;
            var children = new List<ScnTreeNodeInfo>();
            // first child, then its sibling chain
            for (var c = n.Child; c != null; c = c.Sibling)
            {
                var ci = ToInfo(c);
                if (ci != null) children.Add(ci);
            }
            return new ScnTreeNodeInfo(n.Name, n.StartOffset, n.Blob40, n.HasChild, n.HasSibling, children);
        }
    }

    private sealed record NodePtr(
        string Name,
        int StartOffset,
        byte[] Blob40,
        int HasChild,
        int HasSibling,
        NodePtr? Child,
        NodePtr? Sibling);

    // Skip the "auto block" table parsed by `sub_100155C0` and return its byte length.
    private static int SkipAutoBlockTable(byte[] data, int start)
    {
        var pos = start;
        if (pos + 4 > data.Length) return 0;
        var outer = (int)ReadU32(data, pos);
        pos += 4;
        for (var i = 0; i < outer && pos < data.Length; i++)
        {
            _ = ReadCString(data, ref pos);
            if (pos + 4 > data.Length) break;
            var inner = (int)ReadU32(data, pos);
            pos += 4;
            for (var j = 0; j < inner && pos < data.Length; j++)
            {
                _ = ReadCString(data, ref pos);
                if (pos + 4 > data.Length) break; // flag (u32)
                pos += 4;
                if (pos + 4 > data.Length) break;
                var c1 = (int)ReadU32(data, pos); pos += 4; pos += 16 * c1;
                if (pos + 4 > data.Length) break;
                var c2 = (int)ReadU32(data, pos); pos += 4; pos += 20 * c2;
                if (pos + 4 > data.Length) break;
                var c3 = (int)ReadU32(data, pos); pos += 4; pos += 16 * c3;
                if (pos + 4 > data.Length) break;
                var c4 = (int)ReadU32(data, pos); pos += 4; pos += 68 * c4;
            }
        }
        return Math.Max(0, pos - start);
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

    public static ScnMesh? ParseScn1High(string path, byte[] data)
    {
        var models = ParseScn1All(path, data);
        if (models.Count == 0) return null;
        return models
            .Select(x => x.Mesh)
            .OrderByDescending(m => m.Positions.Length)
            .ThenByDescending(m => m.Indices.Length)
            .FirstOrDefault();
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

    private static uint ReadU32(byte[] data, int off) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(off, 4));
    private static uint ReadU32(ReadOnlySpan<byte> data, int off) => BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off, 4));
    private static float ReadF32(byte[] data, int off) => BitConverter.Int32BitsToSingle((int)ReadU32(data, off));
    private static float ReadF32(ReadOnlySpan<byte> data, int off) => BitConverter.Int32BitsToSingle((int)ReadU32(data, off));

    private static int IndexOf(byte[] hay, byte[] needle, int start)
    {
        for (var i = start; i <= hay.Length - needle.Length; i++)
        {
            var ok = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (hay[i + j] != needle[j]) { ok = false; break; }
            }
            if (ok) return i;
        }
        return -1;
    }

    private static string ReadCString(byte[] data, ref int ofs)
    {
        var end = Array.IndexOf(data, (byte)0, ofs);
        if (end < 0) end = data.Length;
        var s = ScnEncoding.GetString(data, ofs, Math.Max(0, end - ofs));
        ofs = Math.Min(data.Length, end + 1);
        return s;
    }

    private sealed class ByteReader
    {
        private readonly byte[] _data;
        public int Position { get; set; }
        public int Remaining => _data.Length - Position;
        public ByteReader(byte[] data) => _data = data;
        public string ReadAscii(int n) { var s = System.Text.Encoding.ASCII.GetString(_data, Position, n); Position += n; return s; }
        public uint ReadU32() { var v = BinaryPrimitives.ReadUInt32LittleEndian(_data.AsSpan(Position, 4)); Position += 4; return v; }
        public byte ReadU8() { var v = _data[Position]; Position += 1; return v; }
        public int ReadI32() { var v = BinaryPrimitives.ReadInt32LittleEndian(_data.AsSpan(Position, 4)); Position += 4; return v; }
        public void Skip(int n) => Position += n;
        public string ReadCString()
        {
            var end = Array.IndexOf(_data, (byte)0, Position);
            if (end < 0) end = _data.Length;
            var s = ScnEncoding.GetString(_data, Position, end - Position);
            Position = Math.Min(_data.Length, end + 1);
            return s;
        }
        public void ReadInto(Span<byte> dst)
        {
            _data.AsSpan(Position, dst.Length).CopyTo(dst);
            Position += dst.Length;
        }
    }
}
