#!/usr/bin/env python3
# Minimal AXO dumper for reverse-engineering.
# Strictly parses documented headers; does not scan or guess.

from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path


def u32le(buf: bytes, off: int) -> int:
    return struct.unpack_from("<I", buf, off)[0]


def fourcc(u: int) -> str:
    return struct.pack("<I", u).decode("ascii", "replace")


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


TAG_INFO = 0x4F464E49  # "INFO"
TAG_AXO_ = 0x5F4F5841  # "AXO_"
TAG_END_ = 0x20444E45  # "END "
TAG_GEOG = 0x474F4547  # "GEOG"
TAG_GEOM = 0x4D4F4547  # "GEOM"
TAG_TEX_ = 0x20584554  # "TEX "
TAG_MTRL = 0x4C52544D  # "MTRL"
TAG_ATOM = 0x4D4F5441  # "ATOM"
TAG_FRAM = 0x4D415246  # "FRAM"


def parse_header(buf: bytes) -> tuple[int, int, int]:
    if len(buf) < 0x20:
        raise ValueError("file too small")
    if u32le(buf, 0x00) != TAG_INFO:
        raise ValueError("missing INFO")
    if u32le(buf, 0x10) != TAG_AXO_:
        raise ValueError("missing AXO_ at +0x10")
    version = u32le(buf, 0x14)
    unk24 = u32le(buf, 0x18)
    unk28 = u32le(buf, 0x1C)
    return version, unk24, unk28


def parse_chunk_at(buf: bytes, off: int) -> Chunk:
    if off + 16 > len(buf):
        raise ValueError("chunk header out of range")
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
        if c.tag == TAG_END_:
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


def parse_tex(buf: bytes, tex_off: int) -> list[tuple[int, str]]:
    if u32le(buf, tex_off) != TAG_TEX_:
        return []
    count = u32le(buf, tex_off + 8)
    out: list[tuple[int, str]] = []
    off = tex_off + 16
    for _ in range(count):
        if off + 36 > len(buf):
            break
        tid = u32le(buf, off)
        name_raw = buf[off + 4 : off + 36]
        name = name_raw.split(b"\x00", 1)[0].decode("ascii", "replace")
        out.append((tid, name))
        off += 36
    return out


def parse_mtrl(buf: bytes, mtrl_off: int) -> list[tuple[int, int, int]]:
    if u32le(buf, mtrl_off) != TAG_MTRL:
        return []
    count = u32le(buf, mtrl_off + 8)
    out: list[tuple[int, int, int]] = []
    off = mtrl_off + 16
    for _ in range(count):
        if off + 68 > len(buf):
            break
        key = u32le(buf, off + 0)
        unk4 = struct.unpack_from("<i", buf, off + 4)[0]
        tex_id = u32le(buf, off + 15 * 4)
        out.append((key, tex_id, unk4))
        off += 68
    return out


def parse_atom(buf: bytes, atom: Chunk) -> list[dict[str, int]]:
    if atom.tag != TAG_ATOM:
        return []
    rec_size = atom.unk_c
    rec_count = atom.count
    if rec_size <= 0 or rec_count <= 0 or (rec_size % 8) != 0:
        return []

    pairs_per_rec = rec_size // 8
    base = atom.off + 16
    out: list[dict[str, int]] = []
    for i in range(rec_count):
        off = base + i * rec_size
        if off + rec_size > len(buf):
            break
        rec: dict[str, int] = {}
        for p in range(pairs_per_rec):
            tag = u32le(buf, off + p * 8 + 0)
            val = u32le(buf, off + p * 8 + 4)
            rec[fourcc(tag)] = val
        out.append(rec)
    return out


def parse_geom_hdr(buf: bytes, geom_off: int) -> list[int]:
    if u32le(buf, geom_off) != TAG_GEOM:
        return []
    base = geom_off + 16
    if base + 0x20 > len(buf):
        return []
    return [u32le(buf, base + i * 4) for i in range(8)]


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("path", type=Path)
    args = ap.parse_args()

    buf = args.path.read_bytes()
    version, unk24, unk28 = parse_header(buf)
    print(f"[axo] version={version} unk24=0x{unk24:08X} unk28=0x{unk28:08X}")

    chunks = walk_top(buf)
    tex_chunk: Chunk | None = None
    mtrl_chunk: Chunk | None = None
    atom_chunk: Chunk | None = None
    for i, c in enumerate(chunks):
        print(
            f"[axo] chunk[{i}] off=0x{c.off:X} tag='{c.tag4}' size=0x{c.size:X} count={c.count} unkC=0x{c.unk_c:X}"
        )
        if c.tag == TAG_TEX_:
            tex_chunk = c
        if c.tag == TAG_MTRL:
            mtrl_chunk = c
        if c.tag == TAG_ATOM:
            atom_chunk = c
        if c.tag == TAG_MTRL:
            mats = parse_mtrl(buf, c.off)
            for mi, (key, tex_id, unk4) in enumerate(mats):
                print(f"[axo]   mtrl[{mi}] key={key} texId={tex_id} unk4={unk4}")
        if c.tag == TAG_TEX_:
            tex = parse_tex(buf, c.off)
            for ti, (tid, name) in enumerate(tex):
                print(f"[axo]   tex[{ti}] id={tid} name='{name}'")
        if c.tag == TAG_GEOG:
            kids = parse_geog_children(buf, c.off)
            for ki, kc in enumerate(kids):
                print(
                    f"[axo]   geog[{ki}] off=0x{kc.off:X} tag='{kc.tag4}' size=0x{kc.size:X} count={kc.count} unkC=0x{kc.unk_c:X}"
                )
                if kc.tag == TAG_GEOM:
                    h = parse_geom_hdr(buf, kc.off)
                    if h:
                        print(
                            "[axo]     geom.hdr "
                            + " ".join(
                                [
                                    f"unk{idx*4:02X}=0x{val:X}"
                                    for idx, val in enumerate(h)
                                ]
                            )
                        )

    if atom_chunk is not None:
        tex_by_id: dict[int, str] = {}
        if tex_chunk is not None:
            for tid, name in parse_tex(buf, tex_chunk.off):
                tex_by_id[tid] = name

        mtrl = []
        if mtrl_chunk is not None:
            mtrl = parse_mtrl(buf, mtrl_chunk.off)

        atoms = parse_atom(buf, atom_chunk)
        for ai, rec in enumerate(atoms):
            geom_i = rec.get("GEOM")
            mtrl_i = rec.get("MTRL")
            fram_i = rec.get("FRAM")
            extra = [k for k in rec.keys() if k not in {"GEOM", "MTRL", "FRAM"}]
            extra_s = f" extra={extra}" if extra else ""
            print(f"[axo] atom[{ai}] geom={geom_i} mtrl={mtrl_i} fram={fram_i}{extra_s}")

            if geom_i is None or mtrl_i is None:
                continue

            # In the game's loader, ATOM.MTRL is treated as a key and compared against the first dword of each MTRL record.
            # In samples here, that key equals the MTRL record index and also matches TEX ids.
            mtrl_match = next((idx for idx, (key, tex_id, _unk4) in enumerate(mtrl) if key == mtrl_i), None)
            if mtrl_match is not None:
                _key, tex_id, _unk4 = mtrl[mtrl_match]
                tex_name = tex_by_id.get(tex_id)
                if tex_name:
                    print(f"[axo]   -> mtrlIndex={mtrl_match} texId={tex_id} name='{tex_name}' file='{tex_name}.agi.png'")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
