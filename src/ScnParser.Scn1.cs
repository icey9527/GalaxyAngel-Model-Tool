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

}


