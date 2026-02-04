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
    public sealed record Scn0Index(
        ScnTreeNodeInfo? Tree,
        List<ScnContainerInfo> Containers,
        List<ScnGroupEntry> Groups,
        List<ScnModel> Models,
        List<Scn0MeshTableGroup> MeshTable,
        List<Scn0MeshTableEntry> ExtraTable);

    public static List<ScnModel> ParseScn0All(string path, byte[] data)
    {
        // SCN0 must be parsed by structure (per decomp), not by scanning.
        return ParseScn0Index(path, data).Models;
    }

    public static Scn0Index ParseScn0Index(string path, byte[] data)
    {
        // Verified by IDA (sub_100143E0 + sub_10014AE0 + sub_10014C50 + sub_10014E40):
        //   "SCN0"
        //   tree (node parser sub_10014AE0 consumes name_cstr + 0x40 + hasChild/hasSibling recursion)
        //   u32 outerAutoCount + auto table (same element-size layout as SCN1 auto table)
        //   a sequence of config dwords/bools
        //   u32 pairCount + pairCount*(u32,u32)
        //   3*u32
        //   mesh table: groupCount=(pairCount+1); for each group: u32 entryCount; entries: cstring + u32 flag + size-prefixed payload
        //   extra table: u32 count; entries: cstring + u32 flag + size-prefixed payload
        //
        // NOTE: We intentionally avoid any "scan for VB/IB" or "scan for decl signatures" at the file level.

        var r = new ByteReader(data);
        if (r.ReadAscii(4) != "SCN0") return new(null, new(), new(), new(), new(), new());

        var (tree, treeBytes) = ParseTreeStrictInfo(data, r.Position);
        r.Position += treeBytes;

        // Auto table used by the loader (outer records / inner entries).
        // We parse it structurally (per loader) and keep the subset-like records, because some files store
        // texture bindings / subset ranges here instead of in the container's own subset records.
        var autoTable = ParseAutoBlockTable(data, r.Position, out var autoBytes);
        r.Position += autoBytes;

        // Post-auto scalar fields (order matches SCN0 loader's sequential reads).
        //
        // Observed in SCN0 samples (sc06/ou06A.scn, ob101/ob101.scn):
        //   u32[7] config values (some treated as bools/flags by the loader)
        //   u32 pairCount
        //
        // NOTE: Earlier drafts assumed an extra standalone feature flag here, but in-file alignment shows
        // the count immediately follows these 7 dwords for our current sample set.
        if (r.Remaining < 4 * 8) return new(tree, new(), new(), new(), new(), new());
        _ = r.ReadU32();
        _ = r.ReadU32();
        _ = r.ReadU32();
        _ = r.ReadU32();
        _ = r.ReadU32();
        _ = r.ReadU32();
        _ = r.ReadU32();

        var pairCount = (int)r.ReadU32();
        if (pairCount < 0 || pairCount > 200_000) return new(tree, new(), new(), new(), new(), new());
        if (r.Remaining < pairCount * 8) return new(tree, new(), new(), new(), new(), new());
        for (var i = 0; i < pairCount; i++)
        {
            _ = r.ReadU32();
            _ = r.ReadU32();
        }

        if (r.Remaining < 12) return new(tree, new(), new(), new(), new(), new());
        _ = r.ReadU32();
        _ = r.ReadU32();
        _ = r.ReadU32();

        // Mesh table: groupCount = pairCount + 1 (as per `inc ebx; push ebx` before calling sub_10014C50).
        var groupCount = pairCount + 1;
        var meshTable = ParseScn0MeshTable(data, r.Position, groupCount, out var meshTableBytes);
        r.Position += meshTableBytes;

        // Extra table parsed by sub_10014E40: u32 count then entries (name + flag + payload).
        var extraTable = ParseScn0ExtraTable(data, r.Position, out var extraBytes);
        r.Position += extraBytes;

        static Dictionary<string, List<Scn0AutoSubset>> BuildAutoSubsetMap(List<Scn0AutoOuter> t)
        {
            var map = new Dictionary<string, List<Scn0AutoSubset>>(StringComparer.OrdinalIgnoreCase);
            foreach (var o in t)
            {
                foreach (var e in o.Entries)
                {
                    if (string.IsNullOrWhiteSpace(e.Name)) continue;
                    if (!map.TryGetValue(e.Name, out var list))
                    {
                        list = new List<Scn0AutoSubset>();
                        map[e.Name] = list;
                    }
                    if (e.Subsets.Count > 0)
                        list.AddRange(e.Subsets);
                }
            }
            return map;
        }

        var autoSubsetsByName = BuildAutoSubsetMap(autoTable);

        // Build models from the mesh table payload blocks (each payload is a size-prefixed container block).
        // SCN0 containers use an older mesh format (decl_bitfield + VB/IB + subset records), see `sub_10004700`.
        var models = new List<ScnModel>();
        var containers = new List<ScnContainerInfo>();
        var groups = new List<ScnGroupEntry>();
        var containerIndex = 0;
        foreach (var g in meshTable)
        {
            foreach (var e in g.Entries)
            {
                var container = e.Payload.ToArray();
                var cname = ReadFixedName20(container);
                containers.Add(new ScnContainerInfo(containerIndex, cname));
                groups.Add(new ScnGroupEntry(g.GroupIndex, containerIndex, e.Name));

                List<Scn0AutoSubset>? autoSubsets = null;
                if (autoSubsetsByName.TryGetValue(cname, out var byContainer))
                    autoSubsets = byContainer;
                else if (autoSubsetsByName.TryGetValue(e.Name, out var byEntry))
                    autoSubsets = byEntry;

                var embedded = ExtractScn0MeshBlobsFromContainer(container, autoSubsets);
                for (var mi = 0; mi < embedded.Count; mi++)
                {
                    var m = embedded[mi];
                    // Prefer the container header name as the model name (e.g. "ou01E"/"ou01U").
                    // Do not fall back to the mesh-table entry name here; if a container has no name,
                    // the UI will display it as <no-name>#N.
                    var nm = embedded.Count == 1 ? cname : $"{cname}#{mi}";
                    models.Add(new ScnModel(nm, m, ContainerIndex: containerIndex, EmbeddedIndex: mi, GroupIndex: g.GroupIndex));
                }
                containerIndex++;
            }
        }

        // Extra table (`sub_10014E40`) payloads are loaded by the original code as additional resources.
        // Some files place additional mesh containers here; decode them only when they match the SCN0 container
        // shape strictly (size-prefixed header + plausible mesh blob).
        const int extraGroupIndex = -2;
        for (var i = 0; i < extraTable.Count; i++)
        {
            var e = extraTable[i];
            var container = e.Payload.ToArray();
            if (!LooksLikeScn0ContainerBlock(container)) continue;

            var cname = ReadFixedName20(container);
            containers.Add(new ScnContainerInfo(containerIndex, cname));
            groups.Add(new ScnGroupEntry(extraGroupIndex, containerIndex, e.Name));

            List<Scn0AutoSubset>? autoSubsets = null;
            if (autoSubsetsByName.TryGetValue(cname, out var byContainer))
                autoSubsets = byContainer;
            else if (autoSubsetsByName.TryGetValue(e.Name, out var byEntry))
                autoSubsets = byEntry;

            var embedded = ExtractScn0MeshBlobsFromContainer(container, autoSubsets);
            if (embedded.Count == 0)
            {
                // Not a mesh container => do not add a placeholder entry.
                containers.RemoveAt(containers.Count - 1);
                groups.RemoveAt(groups.Count - 1);
                continue;
            }

            for (var mi = 0; mi < embedded.Count; mi++)
            {
                var m = embedded[mi];
                var nm = embedded.Count == 1 ? cname : $"{cname}#{mi}";
                models.Add(new ScnModel(nm, m, ContainerIndex: containerIndex, EmbeddedIndex: mi, GroupIndex: extraGroupIndex));
            }
            containerIndex++;
        }

        return new Scn0Index(tree, containers, groups, models, meshTable, extraTable);
    }

    private static bool LooksLikeScn0ContainerBlock(ReadOnlySpan<byte> container)
    {
        const int nameBufSize = 0x20;
        const int headerSize = 8 + nameBufSize; // 0x28
        if (container.Length < headerSize + 8) return false;

        var size = (int)ReadU32(container, 0);
        if (size != container.Length) return false;

        var nameSpan = container.Slice(8, nameBufSize);
        var nul = nameSpan.IndexOf((byte)0);
        if (nul == 0) return false;

        var decl = ReadU32(container, headerSize + 0);
        var vcount = (int)ReadU32(container, headerSize + 4);
        if (vcount <= 0 || vcount > 5_000_000) return false;
        var stride = Scn0StrideFromDeclBitfield(decl);
        if (stride <= 0 || stride > 1024) return false;
        var vbBytes = (long)vcount * stride;
        return vbBytes > 0 && headerSize + 8 + vbBytes <= container.Length;
    }

    private static string ReadFixedName20(byte[] container)
    {
        // SCN0 container header stores name in a fixed 0x20 buffer at offset 8.
        if (container.Length < 8 + 0x20) return "";
        var span = container.AsSpan(8, 0x20);
        var nul = span.IndexOf((byte)0);
        if (nul < 0) nul = span.Length;
        return ScnEncoding.GetString(span.Slice(0, nul));
    }

    private sealed record Scn0AutoOuter(string Name, List<Scn0AutoInner> Entries);
    private sealed record Scn0AutoSubset(int MaterialId, int StartTri, int TriCount, int BaseV, int VertexCount, string TextureName);
    private sealed record Scn0AutoInner(string Name, uint Flag, List<Scn0AutoSubset> Subsets);

    private static List<Scn0AutoOuter> ParseAutoBlockTable(byte[] data, int start, out int bytesConsumed)
    {
        // This matches the serialized shape consumed by the loader loop in sub_100143E0:
        //   u32 outerCount
        //   repeat outerCount:
        //     outerName_cstr
        //     u32 innerCount
        //     repeat innerCount:
        //       innerName_cstr
        //       u32 flag
        //       u32 c1; skip 16*c1
        //       u32 c2; skip 20*c2
        //       u32 c3; skip 16*c3
        //       u32 c4; read c4 * 0x68 records (same size as SCN0 subset records)
        //
        // We keep the c4 records because they can contain subset ranges and texture names for some files.
        // This is still strict: the loader consumes these records structurally (no scanning).
        var pos = start;
        bytesConsumed = 0;
        if (pos + 4 > data.Length) return new();
        var outer = (int)ReadU32(data, pos);
        pos += 4;
        var outList = new List<Scn0AutoOuter>(Math.Clamp(outer, 0, 2048));
        if (outer < 0 || outer > 1_000_000) { bytesConsumed = 0; return new(); }

        for (var i = 0; i < outer && pos < data.Length; i++)
        {
            var outerName = ReadCString(data, ref pos);
            if (pos + 4 > data.Length) break;
            var inner = (int)ReadU32(data, pos);
            pos += 4;
            if (inner < 0 || inner > 1_000_000) break;

            var entries = new List<Scn0AutoInner>(Math.Clamp(inner, 0, 4096));
            for (var j = 0; j < inner && pos < data.Length; j++)
            {
                var name = ReadCString(data, ref pos);
                if (pos + 4 > data.Length) break;
                var flag = ReadU32(data, pos);
                pos += 4;
                if (pos + 4 > data.Length) break;
                var c1 = ReadU32(data, pos); pos += 4; pos += 16 * (int)c1;
                if (pos + 4 > data.Length) break;
                var c2 = ReadU32(data, pos); pos += 4; pos += 20 * (int)c2;
                if (pos + 4 > data.Length) break;
                var c3 = ReadU32(data, pos); pos += 4; pos += 16 * (int)c3;
                if (pos + 4 > data.Length) break;
                var c4 = (int)ReadU32(data, pos); pos += 4;
                if (c4 < 0 || c4 > 1_000_000) break;

                var subsets = new List<Scn0AutoSubset>(Math.Clamp(c4, 0, 4096));
                for (var k = 0; k < c4; k++)
                {
                    if (pos + 0x68 > data.Length) break;
                    var rec = data.AsSpan(pos, 0x68);
                    pos += 0x68;

                    // Subset-ish fields match the SCN0 subset record layout (same size 0x68):
                    // +0x44: matId,startTri,triCount,baseV,vCnt
                    // +0x58: texName[16]
                    var matId = (int)ReadU32(rec, 0x44);
                    var startTri = (int)ReadU32(rec, 0x48);
                    var triCount = (int)ReadU32(rec, 0x4C);
                    var baseV = (int)ReadU32(rec, 0x50);
                    var vCnt = (int)ReadU32(rec, 0x54);
                    var texNameSpan = rec.Slice(0x58, 16);
                    var end = texNameSpan.IndexOf((byte)0);
                    if (end < 0) end = 16;
                    var tex = Encoding.ASCII.GetString(texNameSpan.Slice(0, end));

                    // Keep even empty names; consumers may only need ranges.
                    subsets.Add(new Scn0AutoSubset(matId, startTri, triCount, baseV, vCnt, tex));
                }

                entries.Add(new Scn0AutoInner(name, flag, subsets));
            }

            outList.Add(new Scn0AutoOuter(outerName, entries));
        }

        bytesConsumed = Math.Max(0, pos - start);
        return outList;
    }

    public sealed record Scn0MeshTableGroup(int GroupIndex, List<Scn0MeshTableEntry> Entries);
    public sealed record Scn0MeshTableEntry(string Name, int Flag, ReadOnlyMemory<byte> Payload);

    private static List<Scn0MeshTableGroup> ParseScn0MeshTable(byte[] data, int start, int groupCount, out int bytesConsumed)
    {
        // Matches `sub_10014C50`'s stream consumption:
        // - groupCount is *not* stored in the file; it is passed in from caller as (pairCount + 1).
        // - For each group:
        //   u32 entryCount
        //   repeat entryCount:
        //     name_cstr
        //     u32 flag
        //     payload: size-prefixed block (u32 size includes itself)
        var pos = start;
        var groups = new List<Scn0MeshTableGroup>(Math.Clamp(groupCount, 0, 8192));
        for (var gi = 0; gi < groupCount; gi++)
        {
            if (pos + 4 > data.Length) break;
            var entryCount = (int)ReadU32(data, pos);
            pos += 4;
            if (entryCount < 0 || entryCount > 200_000) break;

            var entries = new List<Scn0MeshTableEntry>(Math.Clamp(entryCount, 0, 4096));
            for (var ei = 0; ei < entryCount; ei++)
            {
                var name = ReadCString(data, ref pos);
                if (pos + 4 > data.Length) { bytesConsumed = Math.Max(0, pos - start); return groups; }
                var flag = (int)ReadU32(data, pos);
                pos += 4;

                if (pos + 4 > data.Length) { bytesConsumed = Math.Max(0, pos - start); return groups; }
                var len = (int)ReadU32(data, pos);
                if (len < 8 || pos + len > data.Length) { bytesConsumed = Math.Max(0, pos - start); return groups; }
                var payload = data.AsMemory(pos, len);
                pos += len;
                entries.Add(new Scn0MeshTableEntry(name, flag, payload));
            }

            groups.Add(new Scn0MeshTableGroup(gi, entries));
        }

        bytesConsumed = Math.Max(0, pos - start);
        return groups;
    }

    private static List<Scn0MeshTableEntry> ParseScn0ExtraTable(byte[] data, int start, out int bytesConsumed)
    {
        // Matches `sub_10014E40` stream consumption:
        //   u32 count
        //   repeat:
        //     name_cstr
        //     u32 flag
        //     payload: size-prefixed block (u32 size includes itself)
        var pos = start;
        bytesConsumed = 0;
        if (pos + 4 > data.Length) return new();
        var count = (int)ReadU32(data, pos);
        pos += 4;
        if (count < 0 || count > 500_000) return new();

        var outList = new List<Scn0MeshTableEntry>(Math.Clamp(count, 0, 4096));
        for (var i = 0; i < count; i++)
        {
            var name = ReadCString(data, ref pos);
            if (pos + 4 > data.Length) break;
            var flag = (int)ReadU32(data, pos);
            pos += 4;
            if (pos + 4 > data.Length) break;
            var len = (int)ReadU32(data, pos);
            if (len < 8 || pos + len > data.Length) break;
            var payload = data.AsMemory(pos, len);
            pos += len;
            outList.Add(new Scn0MeshTableEntry(name, flag, payload));
        }
        bytesConsumed = Math.Max(0, pos - start);
        return outList;
    }
    public static ScnMesh? ParseScn0High(string path, byte[] data)
    {
        var models = ParseScn0All(path, data);
        if (models.Count == 0) return null;
        return models
            .Select(x => x.Mesh)
            .OrderByDescending(m => m.Positions.Length)
            .ThenByDescending(m => m.Indices.Length)
            .FirstOrDefault();
    }

    // --- SCN0 container mesh blob (verified by IDA: sub_10004700 + sub_1002A207) ---

    private static List<ScnMesh> ExtractScn0MeshBlobsFromContainer(byte[] container, List<Scn0AutoSubset>? autoSubsets)
    {
        // Container is size-prefixed. Observed SCN0SCEN containers store:
        //   u32 size
        //   u32 id/hash
        //   char name[0x20] at offset 8 (NUL-terminated within the fixed buffer)
        //   mesh blob begins immediately after the fixed header (0x28 bytes total)
        //
        // The mesh blob structure itself is exact (from `sub_10004700`):
        //   u32 decl_bitfield
        //   u32 vcount
        //   u8  VB[vcount * stride(decl_bitfield)]
        //   u32 tag (seen 101/102)
        //   u32 ib_bytes
        //   u8  IB[ib_bytes] (u16 indices)
        //   u32 subsetCount
        //   repeat subsetCount:
        //     u8  record[0x68]
        //     at record+0x44: u32 matId, startTri, triCount, baseV, vCnt
        //     at record+0x58: char[16] textureName (NUL terminated)
        var meshes = new List<ScnMesh>();
        const int nameBufSize = 0x20;
        const int headerSize = 8 + nameBufSize; // 0x28
        if (container.Length < headerSize + 8) return meshes;

        // Parse fixed container header (no scanning):
        // - name is within a fixed-size buffer; remaining bytes are padding.
        // This yields a deterministic mesh blob offset.
        var nameSpan = container.AsSpan(8, nameBufSize);
        var nul = nameSpan.IndexOf((byte)0);
        if (nul < 0) nul = nameBufSize;
        _ = ScnEncoding.GetString(nameSpan.Slice(0, nul)); // currently not used for decoding; kept for future mapping/debug.

        // NOTE: Earlier drafts enforced that unused bytes in the fixed name buffer are 0. The original loader
        // code path we matched in IDA reads a C-string and does not validate padding, so enforcing padding here
        // can incorrectly drop containers from some files.

        var meshOff = headerSize;
        var decl = ReadU32(container, meshOff + 0);
        var vcount = (int)ReadU32(container, meshOff + 4);
        if (vcount <= 0 || vcount > 5_000_000) return meshes;
        var stride = Scn0StrideFromDeclBitfield(decl);
        if (stride <= 0 || stride > 1024) return meshes;

        var vbOff = meshOff + 8;
        var vbBytes = (long)vcount * stride;
        if (vbBytes <= 0 || vbOff + vbBytes > container.Length) return meshes;

        var idxHdrOff = vbOff + (int)vbBytes;
        if (idxHdrOff + 8 > container.Length) return meshes;
        var tag = ReadU32(container, idxHdrOff + 0);
        var ibBytes = (int)ReadU32(container, idxHdrOff + 4);
        if (ibBytes <= 0 || (ibBytes % 2) != 0) return meshes;
        var ibOff = idxHdrOff + 8;
        if ((long)ibOff + ibBytes > container.Length) return meshes;
        var idxCount = ibBytes / 2;
        if (idxCount < 3 || (idxCount % 3) != 0) return meshes;

        var afterIb = ibOff + ibBytes;
        if (afterIb + 4 > container.Length) return meshes;
        var subsetCount = (int)ReadU32(container, afterIb);
        if (subsetCount < 0 || subsetCount > 10_000) return meshes;

        var subsetBase = afterIb + 4;
        var subsetBytes = subsetCount * 0x68;
        if (subsetBase + subsetBytes > container.Length) return meshes;

        // Decode vertices using the D3D8 FVF rules implied by `decl_bitfield` (see `sub_1002A207` port).
        if (!TryDecodeScn0Vertices(container.AsSpan(vbOff, (int)vbBytes), vcount, stride, decl, out var pos, out var nrm, out var uv))
            throw new NotSupportedException($"SCN0 vertex decl unsupported: decl=0x{decl:X} stride={stride} vcount={vcount}");

        for (var i = 0; i < vcount; i++)
            uv[i].Y = 1f - uv[i].Y;

        var indices = new uint[idxCount];
        var maxIndex = 0u;
        for (var i = 0; i < idxCount; i++)
        {
            var idx = BinaryPrimitives.ReadUInt16LittleEndian(container.AsSpan(ibOff + i * 2, 2));
            if (idx > maxIndex) maxIndex = idx;
            indices[i] = idx;
        }
        if (maxIndex >= (uint)vcount)
            throw new InvalidDataException($"SCN0 indices out of range: maxIndex={maxIndex} vcount={vcount}");

        var subsets = new List<ScnSubset>();
        var materialSets = new Dictionary<int, ScnMaterialSet>();

        void AddSubset(int matId, int startTri, int triCount, int baseV, int vCnt, string? tex)
        {
            if (matId < 0 || matId > 4096) return;
            if (triCount <= 0 || startTri < 0) return;
            if (baseV < 0 || vCnt <= 0) return;
            if ((long)baseV + vCnt > vcount) return;
            if ((long)startTri + triCount > (idxCount / 3)) return;
            subsets.Add(new ScnSubset(matId, startTri, triCount, baseV, vCnt));
            if (!string.IsNullOrWhiteSpace(tex))
                materialSets[matId] = new ScnMaterialSet { ColorMap = tex };
        }

        if (subsetCount > 0)
        {
            for (var i = 0; i < subsetCount; i++)
            {
                var recOff = subsetBase + i * 0x68;
                var matId = (int)ReadU32(container, recOff + 0x44);
                var startTri = (int)ReadU32(container, recOff + 0x48);
                var triCount = (int)ReadU32(container, recOff + 0x4C);
                var baseV = (int)ReadU32(container, recOff + 0x50);
                var vCnt = (int)ReadU32(container, recOff + 0x54);

                // 16-byte inline texture name at 0x58.
                var texNameSpan = container.AsSpan(recOff + 0x58, 16);
                var end = texNameSpan.IndexOf((byte)0);
                if (end < 0) end = 16;
                var tex = Encoding.ASCII.GetString(texNameSpan.Slice(0, end));
                AddSubset(matId, startTri, triCount, baseV, vCnt, tex);
            }
        }
        else if (autoSubsets != null && autoSubsets.Count > 0)
        {
            // Strict fallback: use loader-consumed auto-table records when the container has no subset records.
            foreach (var s in autoSubsets)
                AddSubset(s.MaterialId, s.StartTri, s.TriCount, s.BaseV, s.VertexCount, s.TextureName);
        }

        if (subsets.Count == 0)
        {
            // Some meshes (collision/helpers) have no subsets/materials.
            materialSets[0] = new ScnMaterialSet();
        }

        subsets = subsets.OrderBy(s => s.StartTri).ThenBy(s => s.MaterialId).ToList();

        meshes.Add(new ScnMesh
        {
            Positions = pos,
            Normals = nrm,
            UVs = uv,
            Indices = indices,
            Subsets = subsets,
            MaterialSets = materialSets,
        });

        _ = tag; // kept for debugging/version checks if needed (101/102 observed).
        return meshes;
    }

    private static int Scn0StrideFromDeclBitfield(uint a1)
    {
        // Exact port of `sub_1002A207`.
        var result = (a1 & 0xE) switch
        {
            2 => 12,
            4 or 6 => 16,
            8 => 20,
            0xA => 24,
            0xC => 28,
            0xE => 32,
            _ => 0,
        };

        if ((a1 & 0x10) != 0) result += 12;
        if ((a1 & 0x20) != 0) result += 4;
        if ((a1 & 0x40) != 0) result += 4;
        if ((a1 & 0x80) != 0) result += 4;

        var v2 = (int)((a1 >> 8) & 0xF);
        var v3 = (a1 >> 16) & 0xFFFF;
        if (v3 != 0)
        {
            for (; v2 > 0; v2--)
            {
                var t = (int)(v3 & 3);
                result += t switch { 0 => 8, 1 => 12, 2 => 16, 3 => 4, _ => 0 };
                v3 >>= 2;
            }
        }
        else
        {
            result += 8 * v2;
        }

        return result;
    }

    private static bool TryDecodeScn0Vertices(ReadOnlySpan<byte> vb, int vcount, int stride, uint decl,
        out Vector3[] pos, out Vector3[] nrm, out Vector2[] uv)
    {
        pos = Array.Empty<Vector3>();
        nrm = Array.Empty<Vector3>();
        uv = Array.Empty<Vector2>();

        // decl_bitfield is passed to D3D8 as an FVF (SetVertexShader), and stride is computed by `sub_1002A207`
        // which matches the D3DFVF stride rules (position mask + NORMAL/PSIZE/DIFFUSE/SPECULAR + TEXCOORDSIZE).
        //
        // We decode strictly by those rules, extracting only what the tool currently uses (pos/nrm/uv0).

        var fvfStride = Scn0StrideFromDeclBitfield(decl);
        if (fvfStride != stride) return false;
        if (stride <= 0 || vcount <= 0) return false;
        if (vb.Length < (long)vcount * stride) return false;

        if (!TryGetFvfOffsets(decl, out var posOff, out var posKind, out var nrmOff, out var uv0Off, out var uv0Dim))
            return false;

        pos = new Vector3[vcount];
        nrm = new Vector3[vcount];
        uv = new Vector2[vcount];

        for (var i = 0; i < vcount; i++)
        {
            var o = i * stride;

            pos[i] = posOff >= 0 ? ReadFvfPosition(vb, o + posOff, posKind) : Vector3.Zero;
            nrm[i] = nrmOff >= 0
                ? new Vector3(ReadF32(vb, o + nrmOff + 0), ReadF32(vb, o + nrmOff + 4), ReadF32(vb, o + nrmOff + 8))
                : new Vector3(0, 0, 1);

            if (uv0Off >= 0 && uv0Dim >= 1)
            {
                var u = ReadF32(vb, o + uv0Off + 0);
                var v = uv0Dim >= 2 ? ReadF32(vb, o + uv0Off + 4) : 0f;
                uv[i] = new Vector2(u, v);
            }
            else
            {
                uv[i] = Vector2.Zero;
            }
        }

        return true;
    }

    private enum FvfPositionKind
    {
        None = 0,
        Xyz = 1,
        Xyzrhw = 2,
        Xyzb1 = 3,
        Xyzb2 = 4,
        Xyzb3 = 5,
        Xyzb4 = 6,
        Xyzb5 = 7,
    }

    private static Vector3 ReadFvfPosition(ReadOnlySpan<byte> vb, int at, FvfPositionKind kind)
    {
        // For XYZRHW / XYZB*, the first 3 floats are XYZ.
        return kind switch
        {
            FvfPositionKind.Xyz or FvfPositionKind.Xyzrhw or
            FvfPositionKind.Xyzb1 or FvfPositionKind.Xyzb2 or FvfPositionKind.Xyzb3 or FvfPositionKind.Xyzb4 or FvfPositionKind.Xyzb5
                => new Vector3(ReadF32(vb, at + 0), ReadF32(vb, at + 4), ReadF32(vb, at + 8)),
            _ => Vector3.Zero
        };
    }

    private static bool TryGetFvfOffsets(uint decl,
        out int posOff, out FvfPositionKind posKind,
        out int nrmOff,
        out int uv0Off, out int uv0Dim)
    {
        posOff = -1;
        posKind = FvfPositionKind.None;
        nrmOff = -1;
        uv0Off = -1;
        uv0Dim = 0;

        var cursor = 0;

        switch (decl & 0xEu)
        {
            case 0x2: // XYZ
                posOff = cursor;
                posKind = FvfPositionKind.Xyz;
                cursor += 12;
                break;
            case 0x4: // XYZRHW
                posOff = cursor;
                posKind = FvfPositionKind.Xyzrhw;
                cursor += 16;
                break;
            case 0x6: // XYZB1
                posOff = cursor;
                posKind = FvfPositionKind.Xyzb1;
                cursor += 16;
                break;
            case 0x8: // XYZB2
                posOff = cursor;
                posKind = FvfPositionKind.Xyzb2;
                cursor += 20;
                break;
            case 0xA: // XYZB3
                posOff = cursor;
                posKind = FvfPositionKind.Xyzb3;
                cursor += 24;
                break;
            case 0xC: // XYZB4
                posOff = cursor;
                posKind = FvfPositionKind.Xyzb4;
                cursor += 28;
                break;
            case 0xE: // XYZB5
                posOff = cursor;
                posKind = FvfPositionKind.Xyzb5;
                cursor += 32;
                break;
            default:
                return false;
        }

        if ((decl & 0x10u) != 0)
        {
            nrmOff = cursor;
            cursor += 12;
        }

        if ((decl & 0x20u) != 0) cursor += 4; // PSIZE
        if ((decl & 0x40u) != 0) cursor += 4; // DIFFUSE
        if ((decl & 0x80u) != 0) cursor += 4; // SPECULAR

        var texCount = (int)((decl >> 8) & 0xFu);
        var texSizeBits = (uint)(decl >> 16); // HIWORD(decl), 2 bits per stage

        for (var i = 0; i < texCount; i++)
        {
            var dim = 2;
            if (texSizeBits != 0)
            {
                var t = (int)(texSizeBits & 3u);
                dim = t switch { 0 => 2, 1 => 3, 2 => 4, 3 => 1, _ => 2 };
                texSizeBits >>= 2;
            }
            else
            {
                dim = 2;
            }

            if (i == 0)
            {
                uv0Off = cursor;
                uv0Dim = dim;
            }

            cursor += dim * 4;
        }

        return true;
    }


    // --- SCN0 textures/subsets ---

    private sealed record AutoBlock(int Offset, ScnMaterialSet Set);

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


