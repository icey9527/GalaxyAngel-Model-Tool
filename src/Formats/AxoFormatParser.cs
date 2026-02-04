using System;
using System.Collections.Generic;
using System.Linq;

namespace ScnViewer;

sealed class AxoFormatParser : IModelFormatParser
{
    public string Name => "AXO";

    public bool CanParse(string path, byte[] data, out string magic)
    {
        magic = "";
        if (!AxoParser.TryParseHeader(data, out var hdr)) return false;
        magic = $"AXO(v{hdr.Version})";
        return true;
    }

    public ModelLoader.LoadResult Load(string path, byte[] data, string magic)
    {
        var chunks = AxoParser.ParseTopLevelChunks(data);
        var geog = chunks.Find(c => c.Tag4CC == "GEOG");
        if (geog == null) return new ModelLoader.LoadResult(magic, new List<ScnModel>(), null, null);

        var texById = new Dictionary<uint, string>();
        {
            var texChunk = chunks.Find(c => c.Tag4CC == "TEX ");
            if (texChunk != null)
            {
                foreach (var t in AxoParser.ParseTextures(data, texChunk.Offset))
                    texById[t.Id] = t.Name;
            }
        }

        var materials = new List<AxoParser.MaterialEntry>();
        {
            var mtrlChunk = chunks.Find(c => c.Tag4CC == "MTRL");
            if (mtrlChunk != null)
                materials = AxoParser.ParseMaterials(data, mtrlChunk.Offset);
        }
        var knownMaterialKeys = materials.Count > 0
            ? new HashSet<uint>(materials.Select(m => m.MaterialKey))
            : new HashSet<uint>();

        var atomByGeomIndex = new Dictionary<int, (uint mtrlKey, uint frameIdx)>();
        {
            var atom = chunks.Find(c => c.Tag4CC == "ATOM");
            if (atom != null)
            {
                // From IDA: ATOM payload is a list of records, each record size is `unkC` bytes.
                // Each record is a sequence of (tag,u32) pairs (8 bytes each).
                var recordSize = (int)atom.UnkC;
                var recordCount = (int)atom.Count;
                if (recordSize > 0 && recordCount > 0 && recordSize % 8 == 0)
                {
                    var pairsPerRec = recordSize / 8;
                    var baseOff = atom.Offset + 16;
                    for (var ai = 0; ai < recordCount; ai++)
                    {
                        var recOff = baseOff + ai * recordSize;
                        if (recOff < 0 || recOff + recordSize > data.Length) break;

                        int? geomIdx = null;
                        uint? mtrlKey = null;
                        uint? frameIdx = null;
                        for (var pi = 0; pi < pairsPerRec; pi++)
                        {
                            var o = recOff + pi * 8;
                            var tag = BitConverter.ToUInt32(data, o + 0);
                            var val = BitConverter.ToUInt32(data, o + 4);
                            if (tag == 0x4D4F4547) geomIdx = unchecked((int)val); // "GEOM"
                            else if (tag == 0x4C52544D) mtrlKey = val; // "MTRL"
                            else if (tag == 0x4D415246) frameIdx = val; // "FRAM"
                        }
                        if (geomIdx is not null)
                            atomByGeomIndex[geomIdx.Value] = (mtrlKey ?? 0, frameIdx ?? 0);
                    }
                }
            }
        }

        var models = new List<ScnModel>();
        var kids = AxoParser.ParseGeogChildren(data, geog.Offset);
        // IMPORTANT: ATOM's "GEOM" value refers to the GEOG child index, not "the Nth successfully decoded mesh".
        // So we must keep an index that advances for every GEOM child, even if we skip decoding that child.
        var geogIndex = -1;
        foreach (var kc in kids)
        {
            if (kc.Tag4CC != "GEOM") continue;
            geogIndex++;
            if (!AxoParser.TryParseGeomHeader(data, kc.Offset, out var gh)) continue;

            var payload = kc.Offset + 16;
            var streamOff = payload + 0x20;
            var streamBytes = checked((int)gh.Unk0C * 4);
            if (streamOff < 0 || streamOff + streamBytes > data.Length) continue;

            var stream = data.AsSpan(streamOff, streamBytes);
            var res = AxoVifDecoder.DecodeGeomStream(stream);
            if (res.Positions.Count == 0) continue;

            var indices = BuildIndicesFromTailAndBatches(data, kc.Offset, res);

            var mesh = new ScnMesh
            {
                Positions = res.Positions.ToArray(),
                Normals = res.Normals.ToArray(),
                UVs = res.UVs.ToArray(),
                Indices = indices,
            };

            var modelName = $"GEOM_{geogIndex}";

            // Texture mapping from IDA flow:
            // - ATOM record chooses which GEOM index this atomic uses, and includes a "MTRL" value.
            // - The renderer uses that "MTRL" value as a key (compares against the first dword of each MTRL record),
            //   and TEX chunk maps texture id -> texture name.
            if (atomByGeomIndex.TryGetValue(geogIndex, out var atomInfo))
            {
                var key = atomInfo.mtrlKey;
                if (knownMaterialKeys.Count == 0 || knownMaterialKeys.Contains(key))
                {
                    var mat = materials.FirstOrDefault(m => m.MaterialKey == key);
                    var texId = mat?.TextureId ?? key;
                    if (texById.TryGetValue(texId, out var texName) && !string.IsNullOrWhiteSpace(texName))
                    {
                        mesh.MaterialSets[0] = new ScnMaterialSet
                        {
                            ColorMap = texName + ".agi.png",
                        };
                        modelName = $"{modelName} [{texName}]";
                    }
                }
            }

            models.Add(new ScnModel(modelName, mesh));
        }

        return new ModelLoader.LoadResult(magic, models, null, null);
    }

    private static uint[] BuildIndicesFromTailAndBatches(byte[] data, int geomChunkOffset, AxoVifDecoder.Result res)
    {
        if (!AxoParser.TryParseGeomHeader(data, geomChunkOffset, out var gh)) return Array.Empty<uint>();

        var payload = geomChunkOffset + 16;
        var tailOff = checked(payload + 0x20 + (int)gh.Unk0C * 4);
        if (tailOff < 0 || tailOff + 8 > data.Length) return Array.Empty<uint>();

        var q0 = BitConverter.ToUInt64(data, tailOff + 0);
        var prim = (int)((q0 >> 47) & 0x7FF);
        var primKind = prim & 7;

        // Observed across AXO samples in this repo: primKind is consistently 4 (triangle strip).
        // We use this value from the GEOM tail qword as the authoritative topology mode.
        if (primKind != 4 && primKind != 5) return Array.Empty<uint>();

        var idx = new List<uint>();
        foreach (var b in res.Batches)
        {
            if (b.VertexCount < 3) continue;
            if (primKind == 4)
            {
                // Triangle strip.
                for (var i = 0; i < b.VertexCount - 2; i++)
                {
                    var a = b.StartVertex + i;
                    var b0 = b.StartVertex + i + 1;
                    var c = b.StartVertex + i + 2;
                    if ((i & 1) == 0)
                    {
                        idx.Add((uint)a);
                        idx.Add((uint)b0);
                        idx.Add((uint)c);
                    }
                    else
                    {
                        idx.Add((uint)b0);
                        idx.Add((uint)a);
                        idx.Add((uint)c);
                    }
                }
            }
            else
            {
                // Triangle fan.
                var center = b.StartVertex;
                for (var i = 1; i < b.VertexCount - 1; i++)
                {
                    idx.Add((uint)center);
                    idx.Add((uint)(b.StartVertex + i));
                    idx.Add((uint)(b.StartVertex + i + 1));
                }
            }
        }

        return idx.ToArray();
    }
}
