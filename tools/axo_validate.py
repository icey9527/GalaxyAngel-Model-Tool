#!/usr/bin/env python3
# Batch validator for AXO parsing assumptions.
# Strict: uses chunk headers and record sizes; no scanning for "likely" data.

from __future__ import annotations

import argparse
import struct
from dataclasses import dataclass
from pathlib import Path


def u32le(b: bytes, off: int) -> int:
    return struct.unpack_from("<I", b, off)[0]


def fourcc(u: int) -> str:
    return struct.pack("<I", u).decode("ascii", "replace")


TAG_INFO = 0x4F464E49  # "INFO"
TAG_AXO_ = 0x5F4F5841  # "AXO_"
TAG_END_ = 0x20444E45  # "END "
TAG_GEOG = 0x474F4547  # "GEOG"
TAG_GEOM = 0x4D4F4547  # "GEOM"
TAG_TEX_ = 0x20584554  # "TEX "
TAG_MTRL = 0x4C52544D  # "MTRL"
TAG_ATOM = 0x4D4F5441  # "ATOM"


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


def parse_header(b: bytes) -> bool:
    return len(b) >= 0x20 and u32le(b, 0x00) == TAG_INFO and u32le(b, 0x10) == TAG_AXO_


def parse_chunk_at(b: bytes, off: int) -> Chunk:
    return Chunk(off, u32le(b, off), u32le(b, off + 4), u32le(b, off + 8), u32le(b, off + 12))


def walk_top(b: bytes) -> list[Chunk]:
    out: list[Chunk] = []
    off = 0
    while off + 16 <= len(b):
        c = parse_chunk_at(b, off)
        out.append(c)
        if c.tag == TAG_END_:
            break
        off = off + 16 + c.size
    return out


def parse_tex(b: bytes, tex: Chunk) -> dict[int, str]:
    if tex.tag != TAG_TEX_:
        return {}
    out: dict[int, str] = {}
    off = tex.off + 16
    for _ in range(tex.count):
        if off + 36 > len(b):
            break
        tid = u32le(b, off)
        name_raw = b[off + 4 : off + 36]
        name = name_raw.split(b"\x00", 1)[0].decode("ascii", "replace")
        out[tid] = name
        off += 36
    return out


def parse_mtrl_keys(b: bytes, mtrl: Chunk) -> set[int]:
    if mtrl.tag != TAG_MTRL:
        return set()
    out: set[int] = set()
    off = mtrl.off + 16
    for _ in range(mtrl.count):
        if off + 68 > len(b):
            break
        out.add(u32le(b, off + 0))
        off += 68
    return out


def parse_mtrl_key_to_texid(b: bytes, mtrl: Chunk) -> dict[int, int]:
    if mtrl.tag != TAG_MTRL:
        return {}
    out: dict[int, int] = {}
    off = mtrl.off + 16
    for _ in range(mtrl.count):
        if off + 68 > len(b):
            break
        key = u32le(b, off + 0)
        tex_id = u32le(b, off + 15 * 4)
        out[key] = tex_id
        off += 68
    return out


def parse_atom_pairs(b: bytes, atom: Chunk) -> list[dict[str, int]]:
    if atom.tag != TAG_ATOM:
        return []
    rec_size = atom.unk_c
    if rec_size <= 0 or (rec_size % 8) != 0:
        return []
    pairs = rec_size // 8
    base = atom.off + 16
    out: list[dict[str, int]] = []
    for i in range(atom.count):
        off = base + i * rec_size
        if off + rec_size > len(b):
            break
        rec: dict[str, int] = {}
        for p in range(pairs):
            tag = u32le(b, off + p * 8 + 0)
            val = u32le(b, off + p * 8 + 4)
            rec[fourcc(tag)] = val
        out.append(rec)
    return out


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("root", type=Path, nargs="?", default=Path("in"))
    args = ap.parse_args()

    bad = 0
    total = 0
    for p in args.root.rglob("*.axo"):
        total += 1
        b = p.read_bytes()
        if not parse_header(b):
            continue
        chunks = walk_top(b)

        tex_chunk = next((c for c in chunks if c.tag == TAG_TEX_), None)
        mtrl_chunk = next((c for c in chunks if c.tag == TAG_MTRL), None)
        atom_chunk = next((c for c in chunks if c.tag == TAG_ATOM), None)

        tex = parse_tex(b, tex_chunk) if tex_chunk else {}
        mtrl_keys = parse_mtrl_keys(b, mtrl_chunk) if mtrl_chunk else set()
        mtrl_key_to_texid = parse_mtrl_key_to_texid(b, mtrl_chunk) if mtrl_chunk else {}
        atoms = parse_atom_pairs(b, atom_chunk) if atom_chunk else []

        # Validate ATOM->MTRL key and ATOM->TEX name existence.
        for ai, rec in enumerate(atoms):
            geom = rec.get("GEOM")
            mtrl = rec.get("MTRL")
            if geom is None or mtrl is None:
                continue
            if mtrl_keys and mtrl not in mtrl_keys:
                bad += 1
                print(f"[bad] {p} atom[{ai}] GEOM={geom} MTRL(key)={mtrl} not found in MTRL table")
            if tex and mtrl_key_to_texid:
                tid = mtrl_key_to_texid.get(mtrl)
                if tid is not None and tid not in tex:
                    bad += 1
                    print(f"[bad] {p} atom[{ai}] GEOM={geom} TEX(id)={tid} not found in TEX table")
                if tid is None:
                    continue
                name = tex.get(tid, "")
            elif tex:
                name = ""

            if name:
                tex_path = p.parent / (name + ".agi.png")
                if not tex_path.exists():
                    bad += 1
                    print(f"[bad] {p} atom[{ai}] texture file missing: {tex_path}")

    print(f"[axo_validate] checked={total} bad={bad}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
