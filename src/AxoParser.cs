using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Text;

namespace ScnViewer;

static class AxoParser
{
    // From IDA:
    // - First chunk must be "INFO"
    // - At +0x10, must be "AXO_"
    // - Version is a u32 at +0x14
    public sealed record Header(uint Version, uint Unk24, uint Unk28);

    private const uint Tag_INFO = 0x4F464E49; // "INFO" as u32le
    private const uint Tag_AXO_ = 0x5F4F5841; // "AXO_" as u32le
    private const uint Tag_END_ = 0x20444E45; // "END " as u32le
    private const uint Tag_TEX_ = 0x20584554; // "TEX " as u32le
    private const uint Tag_MTRL = 0x4C52544D; // "MTRL" as u32le

    public sealed record Chunk(int Offset, uint Tag, uint Size, uint Count, uint UnkC)
    {
        public string Tag4CC => ToFourCC(Tag);
    }

    public sealed record TextureEntry(uint Id, string Name);

    // Material record size is 68 bytes (17 dwords) per IDA: axl_axo.c::AxlAxoGetMaterialParam (sub_2635E0).
    // We keep raw values and surface:
    // - MaterialKey: dword[0] (used as the key matched by ATOM's "MTRL" value in sub_25E2F8)
    // - TextureId: dword[15] (returned as the 9th output parameter in sub_2635E0)
    public sealed record MaterialEntry(
        uint MaterialKey,
        int Unknown4,
        uint TextureId,
        float[] RawFloats,
        uint[] RawU32);

    public sealed record GeomHeader(
        uint Unk00,
        uint Unk04,
        uint Unk08,
        uint Unk0C,
        uint Unk10,
        uint Unk14,
        uint Unk18,
        uint Unk1C);

    public static bool TryParseHeader(ReadOnlySpan<byte> data, out Header header)
    {
        header = new Header(0, 0, 0);
        if (data.Length < 0x20) return false;
        if (ReadU32(data, 0x00) != Tag_INFO) return false;
        if (ReadU32(data, 0x10) != Tag_AXO_) return false;
        header = new Header(
            Version: ReadU32(data, 0x14),
            Unk24: ReadU32(data, 0x18),
            Unk28: ReadU32(data, 0x1C));
        return true;
    }

    public static List<Chunk> ParseTopLevelChunks(ReadOnlySpan<byte> data)
    {
        var chunks = new List<Chunk>();
        var off = 0;
        while (off + 16 <= data.Length)
        {
            var tag = ReadU32(data, off + 0);
            var size = ReadU32(data, off + 4);
            var count = ReadU32(data, off + 8);
            var unkC = ReadU32(data, off + 12);
            chunks.Add(new Chunk(off, tag, size, count, unkC));

            if (tag == Tag_END_) break;

            var next = off + 16 + checked((int)size);
            if (next <= off) break;
            off = next;
        }
        return chunks;
    }

    // From IDA: GEOG payload is a list of chunk records, with count stored at header+8.
    public static List<Chunk> ParseGeogChildren(ReadOnlySpan<byte> data, int geogOffset)
    {
        if (geogOffset < 0 || geogOffset + 16 > data.Length) return new List<Chunk>();
        if (ReadU32(data, geogOffset + 0) != 0x474F4547) return new List<Chunk>(); // "GEOG"

        var count = (int)ReadU32(data, geogOffset + 8);
        if (count < 0 || count > 1_000_000) return new List<Chunk>();

        var chunks = new List<Chunk>(count);
        var off = geogOffset + 16;
        for (var i = 0; i < count && off + 16 <= data.Length; i++)
        {
            var tag = ReadU32(data, off + 0);
            var size = ReadU32(data, off + 4);
            var c = ReadU32(data, off + 8);
            var u = ReadU32(data, off + 12);
            chunks.Add(new Chunk(off, tag, size, c, u));

            off = off + 16 + checked((int)size);
        }
        return chunks;
    }

    public static string DumpTopLevel(ReadOnlySpan<byte> data, int max = 64)
    {
        var sb = new StringBuilder();
        if (!TryParseHeader(data, out var hdr))
        {
            sb.AppendLine("[axo] not an AXO file");
            return sb.ToString();
        }

        sb.AppendLine($"[axo] version={hdr.Version} unk24=0x{hdr.Unk24:X8} unk28=0x{hdr.Unk28:X8}");
        var chunks = ParseTopLevelChunks(data);
        for (var i = 0; i < chunks.Count && i < max; i++)
        {
            var c = chunks[i];
            sb.AppendLine($"[axo] chunk[{i}] off=0x{c.Offset:X} tag='{c.Tag4CC}' size=0x{c.Size:X} count={c.Count} unkC=0x{c.UnkC:X}");

            if (c.Tag == Tag_TEX_)
            {
                var tex = ParseTextures(data, c.Offset);
                for (var t = 0; t < tex.Count; t++)
                {
                    sb.AppendLine($"[axo]   tex[{t}] id={tex[t].Id} name='{tex[t].Name}'");
                }
            }
            else if (c.Tag == Tag_MTRL)
            {
                var mats = ParseMaterials(data, c.Offset);
                for (var m = 0; m < mats.Count; m++)
                {
                    sb.AppendLine($"[axo]   mtrl[{m}] texId={mats[m].TextureId} unk4={mats[m].Unknown4}");
                }
            }
            if (c.Tag == 0x474F4547) // "GEOG"
            {
                var kids = ParseGeogChildren(data, c.Offset);
                for (var k = 0; k < kids.Count; k++)
                {
                    var kc = kids[k];
                    sb.AppendLine($"[axo]   geog[{k}] off=0x{kc.Offset:X} tag='{kc.Tag4CC}' size=0x{kc.Size:X} count={kc.Count} unkC=0x{kc.UnkC:X}");

                    if (kc.Tag == 0x4D4F4547) // "GEOM"
                    {
                        if (TryParseGeomHeader(data, kc.Offset, out var gh))
                        {
                            sb.AppendLine($"[axo]     geom.hdr unk00=0x{gh.Unk00:X} unk04=0x{gh.Unk04:X} unk08=0x{gh.Unk08:X} unk0C=0x{gh.Unk0C:X}");
                            sb.AppendLine($"[axo]     geom.hdr unk10=0x{gh.Unk10:X} unk14=0x{gh.Unk14:X} unk18=0x{gh.Unk18:X} unk1C=0x{gh.Unk1C:X}");
                        }
                    }
                }
            }
        }
        if (chunks.Count > max) sb.AppendLine($"[axo] ... ({chunks.Count - max} more)");
        return sb.ToString();
    }

    // Observed in samples: GEOM payload starts with 0x20 bytes of header-like fields,
    // and VIF/GIF packet stream begins at payload+0x20.
    public static bool TryParseGeomHeader(ReadOnlySpan<byte> data, int geomChunkOffset, out GeomHeader header)
    {
        header = new GeomHeader(0, 0, 0, 0, 0, 0, 0, 0);
        if (geomChunkOffset < 0 || geomChunkOffset + 16 + 0x20 > data.Length) return false;
        if (ReadU32(data, geomChunkOffset + 0) != 0x4D4F4547) return false; // "GEOM"
        var payload = geomChunkOffset + 16;
        header = new GeomHeader(
            Unk00: ReadU32(data, payload + 0x00),
            Unk04: ReadU32(data, payload + 0x04),
            Unk08: ReadU32(data, payload + 0x08),
            Unk0C: ReadU32(data, payload + 0x0C),
            Unk10: ReadU32(data, payload + 0x10),
            Unk14: ReadU32(data, payload + 0x14),
            Unk18: ReadU32(data, payload + 0x18),
            Unk1C: ReadU32(data, payload + 0x1C));
        return true;
    }

    // From IDA: axl_axo.c::AxlAxoGetTextureName / AxlAxoGetTextureNameByTextureId (sub_263818/sub_2638B0).
    // TEX chunk: header(16) + entries at +16, each entry is 36 bytes:
    //   u32 id; char name[32] (ASCII, NUL-terminated).
    public static List<TextureEntry> ParseTextures(ReadOnlySpan<byte> data, int texOffset)
    {
        var list = new List<TextureEntry>();
        if (texOffset < 0 || texOffset + 16 > data.Length) return list;
        if (ReadU32(data, texOffset + 0) != Tag_TEX_) return list;

        var count = (int)ReadU32(data, texOffset + 8);
        if (count < 0 || count > 100_000) return list;

        var off = texOffset + 16;
        for (var i = 0; i < count; i++)
        {
            if (off + 36 > data.Length) break;
            var id = ReadU32(data, off + 0);
            var nameBytes = data.Slice(off + 4, 32);
            var end = nameBytes.IndexOf((byte)0);
            if (end < 0) end = nameBytes.Length;
            var name = Encoding.ASCII.GetString(nameBytes.Slice(0, end));
            list.Add(new TextureEntry(id, name));
            off += 36;
        }
        return list;
    }

    // From IDA: axl_axo.c::AxlAxoGetMaterialParam (sub_2635E0).
    // MTRL chunk: header(16) + records at +16, each record is 68 bytes (17 dwords).
    public static List<MaterialEntry> ParseMaterials(ReadOnlySpan<byte> data, int mtrlOffset)
    {
        var list = new List<MaterialEntry>();
        if (mtrlOffset < 0 || mtrlOffset + 16 > data.Length) return list;
        if (ReadU32(data, mtrlOffset + 0) != Tag_MTRL) return list;

        var count = (int)ReadU32(data, mtrlOffset + 8);
        if (count < 0 || count > 100_000) return list;

        var off = mtrlOffset + 16;
        for (var i = 0; i < count; i++)
        {
            if (off + 68 > data.Length) break;

            var rawU32 = new uint[17];
            var rawFloats = new float[17];
            for (var k = 0; k < 17; k++)
            {
                var u = ReadU32(data, off + 4 * k);
                rawU32[k] = u;
                rawFloats[k] = BitConverter.Int32BitsToSingle(unchecked((int)u));
            }

            var key = rawU32[0];
            var unk4 = unchecked((int)rawU32[1]);
            var texId = rawU32[15];
            list.Add(new MaterialEntry(key, unk4, texId, rawFloats, rawU32));
            off += 68;
        }
        return list;
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int off) =>
        BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(off, 4));

    private static string ToFourCC(uint tag)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, tag);
        return Encoding.ASCII.GetString(b);
    }
}
