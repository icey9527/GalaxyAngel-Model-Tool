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
    private static Encoding ScnEncoding
    {
        get
        {
            // Many Japanese titles store names in Shift-JIS/CP932.
            try { return Encoding.GetEncoding(932); }
            catch { return Encoding.UTF8; }
        }
    }

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
        public string ReadAscii(int n) { var s = Encoding.ASCII.GetString(_data, Position, n); Position += n; return s; }
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
