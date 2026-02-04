using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using OpenTK.Mathematics;

namespace ScnViewer;

static class AxoVifDecoder
{
    public sealed record Batch(int StartVertex, int VertexCount);
    public sealed record Result(List<Vector3> Positions, List<Vector3> Normals, List<Vector2> UVs, List<Batch> Batches);

    public static Result DecodeGeomStream(ReadOnlySpan<byte> stream)
    {
        var positions = new List<Vector3>();
        var normals = new List<Vector3>();
        var uvs = new List<Vector2>();
        var batches = new List<Batch>();
        var batchStart = 0;
        var batchAdded = 0;

        var off = 0;
        while (off + 4 <= stream.Length)
        {
            var code = ReadU32(stream, off);
            off += 4;

            var imm = (ushort)(code & 0xFFFF);
            var num = (byte)((code >> 16) & 0xFF);
            var cmd = (byte)((code >> 24) & 0xFF);

            var baseCmd = (byte)(cmd & 0x7F);

            // Packet boundary markers. We use these to segment vertex batches for topology reconstruction.
            // In samples, packets are structured as:
            //   UNPACK header + UNPACK position/attr + MSCAL/MSCNT
            if (baseCmd is 0x14 or 0x17) // MSCAL / MSCNT
            {
                if (batchAdded >= 3)
                    batches.Add(new Batch(batchStart, batchAdded));
                batchStart = positions.Count;
                batchAdded = 0;
                continue;
            }

            if ((cmd & 0x60) == 0x60)
            {
                var addr = imm & 0x03FF;
                var vnvl = (byte)(cmd & 0x0F);
                var vn = (vnvl >> 2) & 3;
                var vl = vnvl & 3;
                var usn = ((imm >> 14) & 1) != 0;

                var comps = vn + 1;
                var bits = vl switch
                {
                    0 => 32,
                    1 => 16,
                    2 => 8,
                    3 => 5,
                    _ => 32,
                };

                var n = num == 0 ? 256 : num;
                var wordsPerVec = (comps * bits + 31) / 32;
                var payloadBytes = checked(n * wordsPerVec * 4);

                if (off + payloadBytes > stream.Length)
                    break;

                var payload = stream.Slice(off, payloadBytes);
                off += payloadBytes;

                // Observed in samples:
                // - addr=1 UNPACK v4 32-bit: positions (x,y,z,w)
                // - addr=1 UNPACK v3 32-bit: positions (x,y,z)
                // - addr=2 UNPACK v4 16-bit: normals as signed 16-bit fixed point (w is 0x1000)
                // - addr=3 UNPACK v2 32-bit: UV (u,v)
                if (bits == 32 && addr == 1 && comps == 4)
                {
                    var floats = MemoryMarshal.Cast<byte, float>(payload);
                    for (var i = 0; i < n && i * 4 + 3 < floats.Length; i++)
                    {
                        var x = floats[i * 4 + 0];
                        var y = floats[i * 4 + 1];
                        var z = floats[i * 4 + 2];
                        // Viewer convention is Y-up. AXO VIF data is Y-down in observed samples.
                        positions.Add(new Vector3(x, -y, z));
                    }
                    batchAdded += n;
                }
                else if (bits == 32 && addr == 1 && comps == 3)
                {
                    var floats = MemoryMarshal.Cast<byte, float>(payload);
                    for (var i = 0; i < n && i * 3 + 2 < floats.Length; i++)
                    {
                        var x = floats[i * 3 + 0];
                        var y = floats[i * 3 + 1];
                        var z = floats[i * 3 + 2];
                        positions.Add(new Vector3(x, -y, z));
                    }
                    batchAdded += n;
                }
                else if (bits == 16 && addr == 2 && comps == 4 && !usn)
                {
                    // NRM is typically stored as s16 with scale 1/0x1000, and already unit-length in samples.
                    var s16 = MemoryMarshal.Cast<byte, short>(payload);
                    for (var i = 0; i < n && i * 4 + 3 < s16.Length; i++)
                    {
                        // Observed: w is consistently 0x1000. If it doesn't match, don't treat this stream as normals.
                        if (s16[i * 4 + 3] != 0x1000)
                            continue;
                        var x = s16[i * 4 + 0] / 4096.0f;
                        var y = s16[i * 4 + 1] / 4096.0f;
                        var z = s16[i * 4 + 2] / 4096.0f;
                        normals.Add(new Vector3(x, -y, z));
                    }
                }
                else if (bits == 32 && addr == 3 && comps == 2)
                {
                    var floats = MemoryMarshal.Cast<byte, float>(payload);
                    for (var i = 0; i < n && i * 2 + 1 < floats.Length; i++)
                    {
                        var u = floats[i * 2 + 0];
                        var v = floats[i * 2 + 1];
                        // AXO UVs are vertically inverted relative to what the renderer expects.
                        // The renderer shader flips V, so we pre-flip here to cancel out and keep the on-screen result correct.
                        uvs.Add(new Vector2(u, 1f - v));
                    }
                }

                continue;
            }

            // Known immediate-data commands in VIF stream.
            if (baseCmd is 0x20 or 0x30 or 0x31)
            {
                // STMASK/STROW/STCOL: 4 words
                off += 16;
                continue;
            }

            if (baseCmd is 0x50 or 0x51)
            {
                // DIRECT/DIRECTHL: imm is qword count (16 bytes each)
                off += imm * 16;
                continue;
            }

            // Other commands do not carry immediate payload beyond the code word.
        }

        // Align UV count to position count if needed (missing UVs default to 0).
        if (uvs.Count < positions.Count)
        {
            for (var i = uvs.Count; i < positions.Count; i++)
                uvs.Add(Vector2.Zero);
        }
        // Align normals count to position count if needed (missing normals default to +Z).
        if (normals.Count < positions.Count)
        {
            for (var i = normals.Count; i < positions.Count; i++)
                normals.Add(Vector3.UnitZ);
        }

        if (batchAdded >= 3)
            batches.Add(new Batch(batchStart, batchAdded));

        return new Result(positions, normals, uvs, batches);
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, int off) =>
        BitConverter.ToUInt32(data.Slice(off, 4));
}
