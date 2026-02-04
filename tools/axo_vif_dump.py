#!/usr/bin/env python3
# AXO GEOM VIF stream dumper (PS2-style VIFcode).
# This is a research tool: it does NOT guess file structure; it expects a valid AXO with GEOG/GEOM.

from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path


def u32le(b: bytes, off: int) -> int:
    return struct.unpack_from("<I", b, off)[0]


def fourcc(u: int) -> str:
    return struct.pack("<I", u).decode("ascii", "replace")


TAG_GEOG = 0x474F4547  # "GEOG"
TAG_GEOM = 0x4D4F4547  # "GEOM"


@dataclass(frozen=True)
class Chunk:
    off: int
    tag: int
    size: int
    count: int
    unk_c: int

    @property
    def tag4(self) -> str:
        return fourcc(self.tag)


def parse_chunk_at(buf: bytes, off: int) -> Chunk:
    tag = u32le(buf, off + 0)
    size = u32le(buf, off + 4)
    count = u32le(buf, off + 8)
    unk_c = u32le(buf, off + 12)
    return Chunk(off, tag, size, count, unk_c)


def walk_top(buf: bytes) -> list[Chunk]:
    out: list[Chunk] = []
    off = 0
    while off + 16 <= len(buf):
        c = parse_chunk_at(buf, off)
        out.append(c)
        if c.tag4 == "END ":
            break
        off = off + 16 + c.size
    return out


def parse_geog_children(buf: bytes, geog_off: int) -> list[Chunk]:
    if u32le(buf, geog_off) != TAG_GEOG:
        return []
    count = u32le(buf, geog_off + 8)
    out: list[Chunk] = []
    off = geog_off + 16
    for _ in range(count):
        if off + 16 > len(buf):
            break
        c = parse_chunk_at(buf, off)
        out.append(c)
        off = off + 16 + c.size
    return out


def cmd_name(cmd: int) -> str:
    base = cmd & 0x7F
    if base == 0x00:
        return "NOP"
    if base == 0x01:
        return "STCYCL"
    if base == 0x02:
        return "OFFSET"
    if base == 0x03:
        return "BASE"
    if base == 0x04:
        return "ITOP"
    if base == 0x05:
        return "STMOD"
    if base == 0x06:
        return "MSKPATH3"
    if base == 0x07:
        return "MARK"
    if base == 0x14:
        return "MSCAL"
    if base == 0x10:
        return "FLUSHE"
    if base == 0x11:
        return "FLUSH"
    if base == 0x13:
        return "FLUSHA"
    if base == 0x15:
        return "MSCALF"
    if base == 0x17:
        return "MSCNT"
    if base == 0x20:
        return "STMASK"
    if base == 0x30:
        return "STROW"
    if base == 0x31:
        return "STCOL"
    if base == 0x50:
        return "DIRECT"
    if base == 0x51:
        return "DIRECTHL"
    if base == 0x20:
        return "STMASK"
    if base == 0x30:
        return "STROW"
    if base == 0x31:
        return "STCOL"
    if (cmd & 0x60) == 0x60:
        # UNPACK family
        return "UNPACK"
    return f"CMD_{base:02X}"

def unpack_kind(cmd: int) -> str:
    # VIF UNPACK: cmd 0x60..0x7F; lower nibble encodes vn/vl.
    # We only label it; we do not attempt to compute payload size here.
    if (cmd & 0x60) != 0x60:
        return ""
    vnvl = cmd & 0x0F
    vn = (vnvl >> 2) & 3
    vl = vnvl & 3
    comps = vn + 1
    bits = {0: 32, 1: 16, 2: 8, 3: 5}[vl]
    return f"vnvl=0x{vnvl:X} v{comps} bits={bits}"

def unpack_imm_info(imm: int) -> str:
    # For UNPACK, imm encodes address + flags. We only decode the common bits.
    addr = imm & 0x03FF
    usn = (imm >> 14) & 1
    msk = (imm >> 15) & 1
    extra = imm & ~0xC3FF
    s = f"addr=0x{addr:X} usn={usn} msk={msk}"
    if extra:
        s += f" extra=0x{extra:X}"
    return s

def unpack_payload_words(cmd: int, num: int) -> int:
    vnvl = cmd & 0x0F
    vn = (vnvl >> 2) & 3
    vl = vnvl & 3
    comps = vn + 1
    bits = {0: 32, 1: 16, 2: 8, 3: 5}[vl]
    n = 256 if num == 0 else num
    words_per_vec = (comps * bits + 31) // 32
    return n * words_per_vec

def unpack_preview_as_f32(cmd: int, payload: bytes, count: int) -> list[list[float]] | None:
    vnvl = cmd & 0x0F
    vn = (vnvl >> 2) & 3
    vl = vnvl & 3
    if vl != 0:
        return None
    comps = vn + 1
    words_per_vec = comps
    out: list[list[float]] = []
    for i in range(count):
        start = i * 4 * words_per_vec
        end = start + 4 * words_per_vec
        if end > len(payload):
            break
        vec = list(struct.unpack_from("<" + "f" * words_per_vec, payload, start))
        out.append(vec)
    return out

def unpack_preview_bits(cmd: int, payload: bytes, count_vec: int) -> list[str]:
    vnvl = cmd & 0x0F
    vn = (vnvl >> 2) & 3
    vl = vnvl & 3
    comps = vn + 1
    bits = {0: 32, 1: 16, 2: 8, 3: 5}[vl]
    n = count_vec
    if bits == 32:
        f32 = unpack_preview_as_f32(cmd, payload, n)
        if f32 is None:
            return []
        return [" ".join([f"{v:.6g}" for v in vec]) for vec in f32]
    if bits == 16:
        words_per_vec = (comps * bits + 31) // 32
        lines: list[str] = []
        for i in range(n):
            start = i * 4 * words_per_vec
            end = start + 4 * words_per_vec
            if end > len(payload):
                break
            words = struct.unpack_from("<" + "I" * words_per_vec, payload, start)
            halves: list[str] = []
            for w in words:
                halves.append(f"{w & 0xFFFF:04X}")
                halves.append(f"{(w >> 16) & 0xFFFF:04X}")
            # Only keep the components we nominally have.
            halves = halves[:comps]
            lines.append(" ".join(halves))
        return lines
    if bits == 8:
        words_per_vec = (comps * bits + 31) // 32
        lines = []
        for i in range(n):
            start = i * 4 * words_per_vec
            end = start + 4 * words_per_vec
            if end > len(payload):
                break
            words = struct.unpack_from("<" + "I" * words_per_vec, payload, start)
            bs: list[str] = []
            for w in words:
                bs.append(f"{w & 0xFF:02X}")
                bs.append(f"{(w >> 8) & 0xFF:02X}")
                bs.append(f"{(w >> 16) & 0xFF:02X}")
                bs.append(f"{(w >> 24) & 0xFF:02X}")
            bs = bs[:comps]
            lines.append(" ".join(bs))
        return lines
    # 5-bit (rare): show raw words.
    words = [u32le(payload, i * 4) for i in range(min(len(payload) // 4, 8))]
    return [" ".join([f"{w:08X}" for w in words])]


def dump_vif(buf: bytes, start: int, length: int, max_codes: int, preview: int) -> None:
    off = start
    end = min(len(buf), start + length)
    n = 0
    while off + 4 <= end and n < max_codes:
        code = u32le(buf, off)
        imm = code & 0xFFFF
        num = (code >> 16) & 0xFF
        cmd = (code >> 24) & 0xFF

        name = cmd_name(cmd)
        extra = ""
        if name == "UNPACK":
            extra = " " + unpack_kind(cmd) + " " + unpack_imm_info(imm)
        print(f"  +0x{off-start:04X} code=0x{code:08X} cmd=0x{cmd:02X} {name:7s} num={num:3d} imm=0x{imm:04X}{extra}")

        off += 4
        n += 1

        base = cmd & 0x7F

        # Skip embedded/immediate data for known commands.
        if base in (0x20, 0x30, 0x31):  # STMASK/STROW/STCOL: 4 words
            off += 16
        elif base in (0x50, 0x51):  # DIRECT/DIRECTHL: imm is qword count
            off += imm * 16
        elif (cmd & 0x60) == 0x60:
            # UNPACK: payload length is determined by VN/VL + num.
            payload_words = unpack_payload_words(cmd, num)
            payload_bytes = payload_words * 4
            payload = buf[off : min(end, off + payload_bytes)]
            if preview > 0:
                for i, line in enumerate(unpack_preview_bits(cmd, payload, preview)):
                    print(f"        data[{i}] {line}")
            off += payload_bytes

def summarize_vif(buf: bytes, start: int, length: int) -> None:
    off = start
    end = min(len(buf), start + length)

    pkt = 0
    counts: dict[tuple[int, int], int] = {}

    def flush(kind: str, at_off: int) -> None:
        nonlocal pkt
        if not counts:
            print(f"[vif] pkt[{pkt}] {kind} at +0x{at_off - start:04X} (no unpack)")
            pkt += 1
            return
        parts = []
        for (cmd, addr), n in sorted(counts.items(), key=lambda x: (x[0][0], x[0][1])):
            parts.append(f"{cmd_name(cmd)}@{addr}: {n}")
        print(f"[vif] pkt[{pkt}] {kind} at +0x{at_off - start:04X} " + ", ".join(parts))
        pkt += 1

    while off + 4 <= end:
        code = u32le(buf, off)
        imm = code & 0xFFFF
        num = (code >> 16) & 0xFF
        cmd = (code >> 24) & 0xFF
        base = cmd & 0x7F
        off += 4

        if base in (0x20, 0x30, 0x31):
            off += 16
            continue
        if base in (0x50, 0x51):
            off += imm * 16
            continue
        if (cmd & 0x60) == 0x60:
            n = 256 if num == 0 else num
            addr = imm & 0x03FF
            counts[(cmd, addr)] = counts.get((cmd, addr), 0) + n
            off += unpack_payload_words(cmd, num) * 4
            continue
        if base == 0x14:
            flush("MSCAL", off - 4)
            counts.clear()
            continue
        if base == 0x17:
            flush("MSCNT", off - 4)
            counts.clear()
            continue


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("path", type=Path)
    ap.add_argument("--geom", type=int, default=0, help="GEOM index within GEOG (default: 0)")
    ap.add_argument("--max", type=int, default=200, help="max VIF codes to print")
    ap.add_argument("--preview", type=int, default=0, help="preview N decoded vectors for UNPACK (default: 0)")
    ap.add_argument("--summary", action="store_true", help="print packet summary grouped by MSCAL/MSCNT")
    args = ap.parse_args()

    buf = args.path.read_bytes()
    top = walk_top(buf)
    geog = next((c for c in top if c.tag == TAG_GEOG), None)
    if geog is None:
        raise SystemExit("no GEOG chunk found")

    kids = parse_geog_children(buf, geog.off)
    geoms = [c for c in kids if c.tag == TAG_GEOM]
    if not geoms:
        raise SystemExit("no GEOM children found")
    if args.geom < 0 or args.geom >= len(geoms):
        raise SystemExit(f"--geom out of range, have {len(geoms)} GEOM")

    g = geoms[args.geom]
    payload = g.off + 16
    if payload + 0x20 > len(buf):
        raise SystemExit("GEOM payload too small")

    hdr = [u32le(buf, payload + i * 4) for i in range(8)]
    print(f"[geom] idx={args.geom} off=0x{g.off:X} size=0x{g.size:X} count={g.count} unkC=0x{g.unk_c:X}")
    print("[geom] hdr " + " ".join([f"u32[{i}]=0x{v:X}" for i, v in enumerate(hdr)]))

    stream_off = payload + 0x20
    # Observed via game render path (see repo docs): hdr[3] is a dword count from stream start.
    stream_dwords = hdr[3]
    stream_len = stream_dwords * 4
    if stream_off + stream_len > payload + g.size:
        stream_len = max(0, payload + g.size - stream_off)
    tail_off = stream_off + stream_len
    print(f"[geom] stream off=0x{stream_off:X} dwords=0x{stream_dwords:X} len=0x{stream_len:X}")
    print(f"[geom] tail  off=0x{tail_off:X} len=0x{(payload + g.size - tail_off):X}")

    # Last 0x30 bytes are treated as 6 qwords by the game's packet builder.
    if payload + g.size >= tail_off + 0x30:
        tail = buf[tail_off : tail_off + 0x30]
        q = struct.unpack_from("<" + "Q" * 6, tail, 0)
        for i, v in enumerate(q):
            print(f"[geom] tail.qword[{i}] = 0x{v:016X}")

    if args.summary:
        summarize_vif(buf, stream_off, stream_len)
    else:
        dump_vif(buf, stream_off, stream_len, args.max, args.preview)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
