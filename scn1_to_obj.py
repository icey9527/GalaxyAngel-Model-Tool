#!/usr/bin/env python3
# -*- coding: utf-8 -*-

from __future__ import annotations

import argparse
import re
import struct
import shutil
from dataclasses import dataclass
from pathlib import Path
from typing import Dict, Iterable, List, Optional, Tuple

import imageio.v2 as imageio
from PIL import Image


class Reader:
    def __init__(self, data: bytes, base: int = 0):
        self.data = data
        self.ofs = base

    def tell(self) -> int:
        return self.ofs

    def seek(self, ofs: int) -> None:
        if not (0 <= ofs <= len(self.data)):
            raise ValueError(f"seek out of range: {ofs:#x}")
        self.ofs = ofs

    def skip(self, n: int) -> None:
        self.seek(self.ofs + n)

    def u8(self) -> int:
        v = self.data[self.ofs]
        self.ofs += 1
        return v

    def u16(self) -> int:
        v = struct.unpack_from("<H", self.data, self.ofs)[0]
        self.ofs += 2
        return v

    def u32(self) -> int:
        v = struct.unpack_from("<I", self.data, self.ofs)[0]
        self.ofs += 4
        return v

    def f32(self) -> float:
        v = struct.unpack_from("<f", self.data, self.ofs)[0]
        self.ofs += 4
        return v

    def bytes(self, n: int) -> bytes:
        b = self.data[self.ofs : self.ofs + n]
        if len(b) != n:
            raise ValueError("unexpected EOF")
        self.ofs += n
        return b

    def cstr(self) -> str:
        end = self.data.find(b"\x00", self.ofs)
        if end < 0:
            raise ValueError("unterminated cstr")
        s = self.data[self.ofs : end].decode("utf-8", "replace")
        self.ofs = end + 1
        return s


def half_to_float(h: int) -> float:
    # IEEE 754 half -> float32
    s = (h >> 15) & 1
    e = (h >> 10) & 0x1F
    f = h & 0x3FF
    if e == 0:
        if f == 0:
            return -0.0 if s else 0.0
        # subnormal
        return (-1.0 if s else 1.0) * (f / 1024.0) * (2.0 ** (-14))
    if e == 31:
        if f == 0:
            return float("-inf") if s else float("inf")
        return float("nan")
    return (-1.0 if s else 1.0) * (1.0 + f / 1024.0) * (2.0 ** (e - 15))


def vertex_stride_from_decl(decl: int) -> int:
    # mirror of sub_10090BDA
    base_sel = decl & 0x400E
    base = {
        0x0002: 12,
        0x0004: 16,
        0x0006: 16,
        0x0008: 20,
        0x000A: 24,
        0x000C: 28,
        0x000E: 32,
    }.get(base_sel, 0)

    stride = base
    if decl & 0x10:
        stride += 12
    if decl & 0x20:
        stride += 4
    if decl & 0x40:
        stride += 4
    if decl & 0x80:
        stride += 4

    uv_count = (decl >> 8) & 0xF
    uv_fmt_bits = (decl >> 16) & 0xFFFF
    if uv_fmt_bits:
        for _ in range(uv_count):
            fmt = uv_fmt_bits & 3
            if fmt == 0:
                stride += 8
            elif fmt == 1:
                stride += 12
            elif fmt == 2:
                stride += 16
            else:
                stride += 4
            uv_fmt_bits >>= 2
    else:
        stride += 8 * uv_count
    return stride


def scan_scn0_stride32_mesh_blocks(payload: bytes, *, start: int) -> List[Dict[str, int]]:
    """
    SCN0 has an additional packed layout used by some files (observed in sc06/ou06A.scn):

      u32 vcount
      vb[vcount * 32]  # pos(3f), nrm(3f), uv(2f)
      u32 tag          # 101 or 102
      u32 ib_bytes     # u16 indices, triangles
      ib[ib_bytes]

    Old debug scripts find these by bruteforce. Here we do a format-based scan starting from
    the scene-tree end, 4-byte aligned, within a bounded window.
    """

    STRIDE = 32
    out: List[Dict[str, int]] = []
    n = len(payload)
    # Scan the remainder of the file from tree end. This stays format-based and avoids missing
    # the high LOD when it sits later in the container.
    scan_end = n
    # Offsets are not guaranteed to be aligned (strings can precede), so scan bytewise within the window.
    for off in range(max(0, start), max(0, scan_end - 16)):
        if off + 4 > n:
            break
        vcount = struct.unpack_from("<I", payload, off)[0]
        if vcount < 3 or vcount > 2_000_000:
            continue
        vb_off = off + 4
        vb_size = vcount * STRIDE
        tag_off = vb_off + vb_size
        if tag_off + 8 > n:
            continue
        tag = struct.unpack_from("<I", payload, tag_off)[0]
        if tag not in (101, 102):
            continue
        ib_bytes = struct.unpack_from("<I", payload, tag_off + 4)[0]
        if ib_bytes <= 0 or ib_bytes > 500_000_000 or (ib_bytes % 2) != 0:
            continue
        ib_off = tag_off + 8
        ib_end = ib_off + ib_bytes
        if ib_end > n:
            continue
        idx_count = ib_bytes // 2
        if idx_count < 3 or (idx_count % 3) != 0:
            continue
        # Cheap validity checks:
        # - first vertex position floats should look sane (avoid false positives).
        x, y, z = struct.unpack_from("<3f", payload, vb_off)
        if not (abs(x) <= 200000.0 and abs(y) <= 200000.0 and abs(z) <= 200000.0):
            continue
        # - indices should not exceed vcount (sample a few).
        sample = min(10, idx_count)
        ok = True
        for i in range(sample):
            idx = struct.unpack_from("<H", payload, ib_off + i * 2)[0]
            if idx >= vcount:
                ok = False
                break
        if not ok:
            continue
        out.append(
            {
                "off": off,
                "vcount": vcount,
                "vb_off": vb_off,
                "vb_size": vb_size,
                "tag": tag,
                "ib_bytes": ib_bytes,
                "ib_off": ib_off,
                "end_off": ib_end,
                "stride": STRIDE,
            }
        )
    return out


def decode_scn0_stride32_mesh_block(
    payload: bytes, block: Dict[str, int], *, flip_v: bool, swap_yz: bool
) -> Mesh:
    STRIDE = 32
    vcount = int(block["vcount"])
    vb_off = int(block["vb_off"])
    ib_off = int(block["ib_off"])
    ib_bytes = int(block["ib_bytes"])

    vertices: List[Tuple[float, float, float]] = []
    normals: List[Optional[Tuple[float, float, float]]] = []
    uvs: List[Optional[Tuple[float, float]]] = []

    for i in range(vcount):
        base = vb_off + i * STRIDE
        x, y, z = struct.unpack_from("<3f", payload, base)
        nx, ny, nz = struct.unpack_from("<3f", payload, base + 12)
        u, v = struct.unpack_from("<2f", payload, base + 24)
        if swap_yz:
            y, z = z, y
            ny, nz = nz, ny
        if flip_v:
            v = 1.0 - v
        vertices.append((x, y, z))
        normals.append((nx, ny, nz))
        uvs.append((u, v))

    idx_count = ib_bytes // 2
    indices = list(struct.unpack_from("<" + "H" * idx_count, payload, ib_off))
    faces: List[Tuple[int, int, int]] = []
    for i in range(0, len(indices), 3):
        a, b, c = indices[i : i + 3]
        faces.append((a, b, c))

    return Mesh(
        name=f"SCN0_mesh_{block['off']:x}",
        decl=0,
        vertices=vertices,
        normals=normals,
        uvs=uvs,
        faces=faces,
        maps={},
        subsets=[],
        material_sets=[],
    )

def decode_vertex(decl: int, vb: bytes, i: int, *, flip_v: bool, swap_yz: bool) -> Tuple[Tuple[float, float, float], Optional[Tuple[float, float, float]], Optional[Tuple[float, float]]]:
    stride = vertex_stride_from_decl(decl)
    off = i * stride

    base_sel = decl & 0x400E
    if base_sel == 0x0002:
        x, y, z = struct.unpack_from("<3f", vb, off)
        off += 12
    elif base_sel in (0x0004, 0x0006):
        x, y, z, _w = struct.unpack_from("<4f", vb, off)
        off += 16
    elif base_sel == 0x0008:
        x, y, z, _w, _t = struct.unpack_from("<5f", vb, off)
        off += 20
    elif base_sel == 0x000A:
        x, y, z, _w, _t, _u = struct.unpack_from("<6f", vb, off)
        off += 24
    elif base_sel == 0x000C:
        x, y, z, _w, _t, _u, _v = struct.unpack_from("<7f", vb, off)
        off += 28
    elif base_sel == 0x000E:
        x, y, z, _w, _t, _u, _v, _q = struct.unpack_from("<8f", vb, off)
        off += 32
    else:
        # Unknown layout; still try first 12 bytes as position.
        x, y, z = struct.unpack_from("<3f", vb, off)
        off += 12

    if swap_yz:
        y, z = z, y
    pos = (x, y, z)

    nrm: Optional[Tuple[float, float, float]] = None
    if decl & 0x10:
        nx, ny, nz = struct.unpack_from("<3f", vb, off)
        off += 12
        if swap_yz:
            ny, nz = nz, ny
        nrm = (nx, ny, nz)

    # skip packed fields (unknown semantics)
    if decl & 0x20:
        off += 4
    if decl & 0x40:
        off += 4
    if decl & 0x80:
        off += 4

    uv: Optional[Tuple[float, float]] = None
    uv_count = (decl >> 8) & 0xF
    if uv_count:
        uv_fmt_bits = (decl >> 16) & 0xFFFF
        fmt = (uv_fmt_bits & 3) if uv_fmt_bits else 0
        if fmt == 0:
            u, v = struct.unpack_from("<2f", vb, off)
            off += 8
        elif fmt == 1:
            u, v, _w = struct.unpack_from("<3f", vb, off)
            off += 12
        elif fmt == 2:
            u, v, _w, _q = struct.unpack_from("<4f", vb, off)
            off += 16
        else:
            hu = struct.unpack_from("<H", vb, off)[0]
            hv = struct.unpack_from("<H", vb, off + 2)[0]
            u, v = half_to_float(hu), half_to_float(hv)
            off += 4

        if flip_v:
            v = 1.0 - v
        uv = (u, v)

    return pos, nrm, uv


def parse_scn_tree(data: bytes, start: int) -> int:
    """
    Reimplements the length accounting of sub_10015430 enough to advance cursor.

    Layout:
      node := name_cstr + blob[0x40] + u32 flag1 + (flag1? node) + u32 flag2 + (flag2? node)
    """

    r = Reader(data, start)

    def parse_node() -> None:
        _name = r.cstr()
        r.skip(0x40)
        flag1 = r.u32()
        if flag1 == 1:
            parse_node()
        flag2 = r.u32()
        if flag2 == 1:
            parse_node()

    parse_node()
    return r.tell()


@dataclass
class Mesh:
    name: str
    decl: int
    vertices: List[Tuple[float, float, float]]
    normals: List[Optional[Tuple[float, float, float]]]
    uvs: List[Optional[Tuple[float, float]]]
    faces: List[Tuple[int, int, int]]  # 0-based indices
    maps: Dict[str, str]
    subsets: List[Dict[str, int]]
    material_sets: List[Dict[str, str]]


def replace_last_suffix_with_png(name: str) -> str:
    """
    Replace only the last suffix with .png.
    Examples:
      a.dds -> a.png
      a.bmp -> a.png
      a.bmp.bmp -> a.bmp.png
    """
    p = Path(name)
    if not p.suffixes:
        return p.name + ".png"
    return p.with_suffix(".png").name


def load_image_any(src: Path) -> Optional[Image.Image]:
    """
    Load image for many formats, including DDS via imageio, and return a PIL Image.
    """
    try:
        arr = imageio.imread(src)
        if arr is None:
            return None
        if getattr(arr, "ndim", 0) == 2:
            return Image.fromarray(arr)
        if getattr(arr, "ndim", 0) == 3:
            return Image.fromarray(arr)
        return None
    except Exception:
        try:
            return Image.open(src)
        except Exception:
            return None


def prepare_textures_to_png(
    scn_dir: Path,
    out_dir: Path,
    *,
    maps: Dict[str, str],
    material_sets: List[Dict[str, str]],
) -> Tuple[Dict[str, str], List[Dict[str, str]]]:
    """
    Copy/convert referenced textures into out_dir and rewrite references to .png filenames.
    Does not scan folders by extension; only processes textures explicitly referenced by the mesh.
    """

    def remap_name(tex: str) -> str:
        return replace_last_suffix_with_png(Path(tex).name)

    def ensure_one(tex: str) -> Optional[str]:
        name = Path(tex).name
        src = scn_dir / name
        if not src.exists() or not src.is_file():
            return None
        dst_name = remap_name(name)
        dst = out_dir / dst_name
        if dst.exists():
            return dst_name
        img = load_image_any(src)
        if img is None:
            return None
        out_dir.mkdir(parents=True, exist_ok=True)
        try:
            img.save(dst, format="PNG")
            return dst_name
        except Exception:
            return None

    new_maps: Dict[str, str] = {}
    for k, v in (maps or {}).items():
        if not v:
            continue
        mapped = ensure_one(v)
        if mapped:
            new_maps[k] = mapped

    new_sets: List[Dict[str, str]] = []
    for mset in material_sets or []:
        out: Dict[str, str] = {}
        for k, v in mset.items():
            if not v:
                continue
            mapped = ensure_one(v)
            if mapped:
                out[k] = mapped
        new_sets.append(out)

    return new_maps, new_sets


def extract_d3d_mesh_blocks(
    payload: bytes,
    *,
    flip_v: bool,
    swap_yz: bool,
    name_prefix: str,
    maps: Optional[Dict[str, str]] = None,
    material_sets: Optional[List[Dict[str, str]]] = None,
) -> List[Mesh]:
    """
    Scan a payload for embedded D3D9-like (decl520 + vcount + VB + idxhdr + IB) blocks.
    This happens in some SCN1 records (e.g. ou01U) and in the extra mesh records.
    """

    meshes: List[Mesh] = []
    maps = dict(maps or {})
    material_sets = list(material_sets or [])
    if len(payload) < 520 + 4 + 8:
        return meshes

    seen: set[int] = set()
    # Index header variants observed:
    # - Variant A (a7==1 paths): u32 idx_fmt(0=U16,1=U32) + u32 idx_count
    # - Variant B (a7==0 paths): u32 idx_type + u32 idx_count, where idx_type maps to bytes-per-index.
    idx_bpi = {0: 2, 1: 4, 2: 2, 3: 2, 4: 4}

    # Offsets are not guaranteed to be 4-byte aligned (due to c-strings earlier in the record),
    # so scan on 2-byte alignment.
    for off in range(0, len(payload) - (520 + 4 + 8), 2):
        # quick reject: end element often at element #3 for simple decls, but don't rely on it exclusively
        maybe = parse_d3d_decl_520(payload[off : off + 520])
        if maybe is None:
            continue
        stride, elems = maybe
        # Layout after the 520-byte decl isn't stable; observed patterns:
        # - u32 vcount, then VB
        # - u32 0, u32 vcount, then VB
        # - u32 0, u32 0, u32 vcount, then VB
        v0 = struct.unpack_from("<I", payload, off + 520)[0]
        v1 = struct.unpack_from("<I", payload, off + 524)[0]
        v2 = struct.unpack_from("<I", payload, off + 528)[0]
        if 0 < v0 <= 5_000_000:
            vcount, vb_off = v0, off + 524
        elif 0 < v1 <= 5_000_000:
            vcount, vb_off = v1, off + 528
        else:
            vcount, vb_off = v2, off + 532
        if vcount == 0 or vcount > 5_000_000:
            continue

        vb_size = stride * vcount
        idx_hdr = vb_off + vb_size
        if idx_hdr + 8 > len(payload):
            continue
        h0, h1 = struct.unpack_from("<II", payload, idx_hdr)

        # Try Variant A first.
        idx_fmt: Optional[int] = None
        idx_count: Optional[int] = None
        idx_size: Optional[int] = None
        if h0 in (0, 1) and h1 != 0 and h1 <= 50_000_000:
            idx_fmt, idx_count = h0, h1
            idx_size = 2 if idx_fmt == 0 else 4
        else:
            # Try Variant B.
            bpi = idx_bpi.get(h0)
            if bpi is None or h1 == 0 or h1 > 100_000_000:
                continue
            idx_fmt, idx_count, idx_size = (0 if bpi == 2 else 1), h1, bpi

        ib_off = idx_hdr + 8
        ib_size = idx_count * idx_size
        end = ib_off + ib_size
        if end > len(payload):
            continue

        # avoid duplicates if scanning overlaps
        key = (off << 1) ^ (vcount & 0xFFFF) ^ (idx_count << 3)
        if key in seen:
            continue
        seen.add(key)

        vb = payload[vb_off : vb_off + vb_size]
        ib = payload[ib_off:end]
        if idx_fmt == 0:
            indices = list(struct.unpack_from("<" + "H" * idx_count, ib, 0))
        else:
            indices = list(struct.unpack_from("<" + "I" * idx_count, ib, 0))

        verts: List[Tuple[float, float, float]] = []
        nrms: List[Optional[Tuple[float, float, float]]] = []
        uvs: List[Optional[Tuple[float, float]]] = []
        for i in range(vcount):
            pos, nrm, uv = decode_vertex_d3d(elems, vb, i, stride, flip_v=flip_v, swap_yz=swap_yz)
            verts.append(pos)
            nrms.append(nrm)
            uvs.append(uv)

        faces: List[Tuple[int, int, int]] = []
        if len(indices) % 3 == 0:
            for i in range(0, len(indices), 3):
                a, b, c = indices[i : i + 3]
                faces.append((a, b, c))
        else:
            for i in range(2, len(indices)):
                a, b, c = indices[i - 2], indices[i - 1], indices[i]
                if a == b or b == c or a == c:
                    continue
                if i & 1:
                    faces.append((b, a, c))
                else:
                    faces.append((a, b, c))

        subsets: List[Dict[str, int]] = []
        if verts and faces:
            # For subset lookup, use the effective decl offset derived from vb_off.
            # This avoids false-positive decl matches that overlap the preceding subset table.
            decl_off_eff = vb_off - 524
            subsets = find_subset_table(payload, decl_off=decl_off_eff, vcount=vcount, face_count=len(faces))
        # Avoid ugly "_part0" suffix. If a record contains multiple embedded mesh blocks,
        # keep the first as the node name, and suffix subsequent ones with _1, _2...
        mesh_name = name_prefix if len(meshes) == 0 else f"{name_prefix}_{len(meshes)}"
        meshes.append(
            Mesh(
                name=mesh_name,
                decl=0,
                vertices=verts,
                normals=nrms,
                uvs=uvs,
                faces=faces,
                maps=maps,
                subsets=subsets,
                material_sets=material_sets,
            )
        )

    return meshes


def find_vb_block(payload: bytes) -> Optional[Tuple[int, int, int, int, int]]:
    """
    Heuristic: locate the 520-byte vertex-desc block within a mesh record.

    Returns (desc_off, decl, vcount, ib_off, end_off) where:
      - desc_off is offset of vertex-desc in payload
      - decl is vertex decl flags (assumed payload[desc_off:desc_off+4])
      - vcount is vertex count (payload[desc_off+520:desc_off+524])
      - ib_off is offset of indices header (format,count,data) within payload
      - end_off is end of index data within payload
    """

    if len(payload) < 520 + 4 + 4 + 8:
        return None

    for desc_off in range(0, len(payload) - (520 + 4 + 8), 4):
        decl = struct.unpack_from("<I", payload, desc_off)[0]
        stride = vertex_stride_from_decl(decl)
        if not (12 <= stride <= 256):
            continue

        vcount = struct.unpack_from("<I", payload, desc_off + 520)[0]
        if vcount == 0 or vcount > 10_000_000:
            continue

        vb_off = desc_off + 524
        vb_size = stride * vcount
        ib_hdr_off = vb_off + vb_size
        if ib_hdr_off + 8 > len(payload):
            continue

        idx_fmt = struct.unpack_from("<I", payload, ib_hdr_off)[0]
        idx_count = struct.unpack_from("<I", payload, ib_hdr_off + 4)[0]
        if idx_count == 0 or idx_count > 100_000_000:
            continue

        if idx_fmt == 0:
            idx_size = 2
        elif idx_fmt == 1:
            idx_size = 4
        else:
            continue

        end_off = ib_hdr_off + 8 + idx_count * idx_size
        if end_off > len(payload):
            continue

        # allow small trailing bytes (alignment/extra fields)
        if len(payload) - end_off > 64:
            continue

        return desc_off, decl, vcount, ib_hdr_off, end_off

    return None


def parse_d3d_decl_520(block: bytes) -> Optional[Tuple[int, List[Tuple[int, int, int, int, int, int]]]]:
    """
    Parse a 520-byte D3D9-like vertex declaration block:
      65 * D3DVERTEXELEMENT9 (8 bytes each)

    Returns (stride, elements) or None.
    """

    if len(block) != 520:
        return None

    elems: List[Tuple[int, int, int, int, int, int]] = []
    end_found = False
    for i in range(65):
        off = i * 8
        stream, offset = struct.unpack_from("<HH", block, off)
        typ, method, usage, usage_idx = struct.unpack_from("<BBBB", block, off + 4)
        elems.append((stream, offset, typ, method, usage, usage_idx))
        if stream == 0xFF:
            end_found = True
            break

    if not end_found:
        return None

    # D3DDECLTYPE sizes (D3D9)
    type_size = {
        0: 4,   # FLOAT1
        1: 8,   # FLOAT2
        2: 12,  # FLOAT3
        3: 16,  # FLOAT4
        4: 4,   # D3DCOLOR
        5: 4,   # UBYTE4
        6: 4,   # SHORT2
        7: 4,   # SHORT4
        8: 8,   # UBYTE4N
        9: 8,   # SHORT2N
        10: 16, # SHORT4N
        11: 4,  # USHORT2N
        12: 4,  # USHORT4N
        13: 4,  # UDEC3
        14: 4,  # DEC3N
        15: 8,  # FLOAT16_2
        16: 8,  # FLOAT16_4 (often 8? Some impl treat as 8/16; keep conservative)
        17: 8,  # UNUSED/other
    }

    stride = 0
    for stream, offset, typ, _method, _usage, _usage_idx in elems:
        if stream == 0xFF:
            break
        sz = type_size.get(typ)
        if sz is None:
            return None
        # We only support stream 0 for export.
        if stream != 0:
            continue
        stride = max(stride, offset + sz)

    if stride <= 0 or stride > 1024:
        return None
    return stride, elems


def decode_vertex_d3d(elems: List[Tuple[int, int, int, int, int, int]], vb: bytes, i: int, stride: int, *, flip_v: bool, swap_yz: bool) -> Tuple[Tuple[float, float, float], Optional[Tuple[float, float, float]], Optional[Tuple[float, float]]]:
    """
    Minimal D3D9 vertex decoder for common usage:
      Usage 0: POSITION (FLOAT3)
      Usage 3: NORMAL (FLOAT3)
      Usage 5: TEXCOORD0 (FLOAT2)
    """

    base = i * stride
    pos: Optional[Tuple[float, float, float]] = None
    nrm: Optional[Tuple[float, float, float]] = None
    uv: Optional[Tuple[float, float]] = None

    for stream, offset, typ, _method, usage, usage_idx in elems:
        if stream == 0xFF:
            break
        if stream != 0:
            continue
        at = base + offset
        if usage == 0 and pos is None:
            # POSITION
            if typ == 2:
                x, y, z = struct.unpack_from("<3f", vb, at)
                if swap_yz:
                    y, z = z, y
                pos = (x, y, z)
        elif usage == 3 and nrm is None:
            # NORMAL
            if typ == 2:
                nx, ny, nz = struct.unpack_from("<3f", vb, at)
                if swap_yz:
                    ny, nz = nz, ny
                nrm = (nx, ny, nz)
        elif usage == 5 and usage_idx == 0 and uv is None:
            # TEXCOORD0
            if typ == 1:
                u, v = struct.unpack_from("<2f", vb, at)
                if flip_v:
                    v = 1.0 - v
                uv = (u, v)

    if pos is None:
        pos = (0.0, 0.0, 0.0)
    return pos, nrm, uv


def parse_mesh_record(record: bytes, *, flip_v: bool, swap_yz: bool) -> Optional[Mesh]:
    """
    SCN1 mesh record (as passed to CDCMgr::LoadMesh with a3==1):
      u32 size
      u32 unk
      cstr name (starts at offset 8)
      ... payload ...

    We only rely on (size, name) and then a heuristic to locate:
      vertex_desc[520] + u32 vertex_count + vertex_data + (u32 idx_fmt,u32 idx_count) + index_data
    """

    if len(record) < 12:
        return None
    if record[:4] != struct.pack("<I", len(record)):
        # some files store "size excluding the first dword"; tolerate but keep going
        pass

    name_off = 8
    nul = record.find(b"\x00", name_off)
    if nul < 0:
        return None
    name = record[name_off:nul].decode("utf-8", "replace") or "mesh"
    payload = record[nul + 1 :]

    # Path A (SCN1 extra meshes / a7==1): 520-byte D3D decl + u32 vcount + vb + (u32 fmt,u32 count)+ib
    if len(payload) >= 520 + 4 + 8:
        maybe = parse_d3d_decl_520(payload[:520])
        if maybe is not None:
            stride, elems = maybe
            v0 = struct.unpack_from("<I", payload, 520)[0]
            v1 = struct.unpack_from("<I", payload, 524)[0] if len(payload) >= 520 + 8 else 0
            v2 = struct.unpack_from("<I", payload, 528)[0] if len(payload) >= 520 + 12 else 0
            if 0 < v0 <= 5_000_000:
                vcount, vb_off = v0, 524
            elif 0 < v1 <= 5_000_000:
                vcount, vb_off = v1, 528
            else:
                vcount, vb_off = v2, 532
            vb_size = stride * vcount
            if vb_off + vb_size + 8 <= len(payload):
                idx_fmt, idx_count = struct.unpack_from("<II", payload, vb_off + vb_size)
                if idx_fmt in (0, 1):
                    idx_size = 2 if idx_fmt == 0 else 4
                    ib_off = vb_off + vb_size + 8
                    ib_size = idx_count * idx_size
                    if ib_off + ib_size <= len(payload):
                        vb = payload[vb_off : vb_off + vb_size]
                        ib = payload[ib_off : ib_off + ib_size]
                        if idx_fmt == 0:
                            indices = list(struct.unpack_from("<" + "H" * idx_count, ib, 0))
                        else:
                            indices = list(struct.unpack_from("<" + "I" * idx_count, ib, 0))

                        verts: List[Tuple[float, float, float]] = []
                        nrms: List[Optional[Tuple[float, float, float]]] = []
                        uvs: List[Optional[Tuple[float, float]]] = []
                        for i in range(vcount):
                            pos, nrm, uv = decode_vertex_d3d(elems, vb, i, stride, flip_v=flip_v, swap_yz=swap_yz)
                            verts.append(pos)
                            nrms.append(nrm)
                            uvs.append(uv)

                        faces: List[Tuple[int, int, int]] = []
                        if len(indices) % 3 == 0:
                            for i in range(0, len(indices), 3):
                                a, b, c = indices[i : i + 3]
                                faces.append((a, b, c))
                        else:
                            for i in range(2, len(indices)):
                                a, b, c = indices[i - 2], indices[i - 1], indices[i]
                                if a == b or b == c or a == c:
                                    continue
                                if i & 1:
                                    faces.append((b, a, c))
                                else:
                                    faces.append((a, b, c))

                        return Mesh(
                            name=name,
                            decl=0,
                            vertices=verts,
                            normals=nrms,
                            uvs=uvs,
                            faces=faces,
                            maps={},
                            subsets=[],
                            material_sets=[],
                        )

    # Path A2: Some records embed the mesh block at a non-zero offset (e.g. ou01U).
    embedded = extract_d3d_mesh_blocks(payload, flip_v=flip_v, swap_yz=swap_yz, name_prefix=name, maps=None)
    if embedded:
        # return the first; higher-level code can rescan for multiple if needed
        return embedded[0]

    hit = find_vb_block(payload)
    if not hit:
        return None
    desc_off, decl, vcount, ib_hdr_off, end_off = hit

    stride = vertex_stride_from_decl(decl)
    vb_off = desc_off + 524
    vb = payload[vb_off : vb_off + stride * vcount]

    idx_fmt = struct.unpack_from("<I", payload, ib_hdr_off)[0]
    idx_count = struct.unpack_from("<I", payload, ib_hdr_off + 4)[0]
    idx_data = payload[ib_hdr_off + 8 : end_off]
    if idx_fmt == 0:
        indices = list(struct.unpack_from("<" + "H" * idx_count, idx_data, 0))
    else:
        indices = list(struct.unpack_from("<" + "I" * idx_count, idx_data, 0))

    verts: List[Tuple[float, float, float]] = []
    nrms: List[Optional[Tuple[float, float, float]]] = []
    uvs: List[Optional[Tuple[float, float]]] = []
    for i in range(vcount):
        pos, nrm, uv = decode_vertex(decl, vb, i, flip_v=flip_v, swap_yz=swap_yz)
        verts.append(pos)
        nrms.append(nrm)
        uvs.append(uv)

    faces: List[Tuple[int, int, int]] = []
    if len(indices) % 3 == 0:
        for i in range(0, len(indices), 3):
            a, b, c = indices[i : i + 3]
            faces.append((a, b, c))
    else:
        # Fallback: treat as triangle strip.
        for i in range(2, len(indices)):
            a, b, c = indices[i - 2], indices[i - 1], indices[i]
            if a == b or b == c or a == c:
                continue
            if i & 1:
                faces.append((b, a, c))
            else:
                faces.append((a, b, c))

    return Mesh(
        name=name,
        decl=decl,
        vertices=verts,
        normals=nrms,
        uvs=uvs,
        faces=faces,
        maps={},
        subsets=find_subset_table(payload, decl_off=desc_off, vcount=vcount, face_count=len(faces)) if verts and faces else [],
        material_sets=[],
    )


def sanitize_mtl_name(s: str) -> str:
    s = s.strip().replace("\\", "/")
    s = re.sub(r"[^0-9A-Za-z_.:/+-]+", "_", s)
    if not s:
        return "mat"
    return s


def extract_texture_maps(record_bytes: bytes) -> Dict[str, str]:
    """
    Pull texture filenames from a SCN record's raw bytes.

    We key by semantic name found in the file:
      ColorMap -> diffuse (map_Kd)
      NormalMap -> normal/bump
      LuminosityMap -> emissive-ish
      ReflectionMap -> reflection-ish
    """

    keys = ["ColorMap", "NormalMap", "LuminosityMap", "ReflectionMap"]
    out: Dict[str, str] = {}
    for key in keys:
        needle = (key + "\x00").encode("ascii")
        pos = 0
        while True:
            idx = record_bytes.find(needle, pos)
            if idx < 0:
                break
            s_start = idx + len(needle)
            s_end = record_bytes.find(b"\x00", s_start)
            if s_end > s_start:
                # Keep this purely as a string extractor. Do not validate by extension here.
                # (Binding/copying uses the parsed material blocks instead.)
                val = record_bytes[s_start:s_end].decode("utf-8", "replace")
                if 1 <= len(val) <= 260 and all((c == "\t" or 32 <= ord(c) < 127) for c in val):
                    out.setdefault(key, val)
            pos = idx + 1
    return out


def extract_auto_material_blocks(record_bytes: bytes) -> List[Dict[str, object]]:
    """
    Parse repeated "auto" material blocks seen in SCN1 records.

    Observed pattern in samples (little-endian):
      "auto\\0" + u32 entry_count +
        entry_count * ( key_cstr + value_cstr + u32 flag1 + u32 flag2 ) +
      followed by floats/other data we ignore.

    Returns a list of blocks:
      { "off": int, "entry_count": int, "entries": [{"key": str, "value": str, "flag1": int, "flag2": int}, ...] }
    """

    blocks: List[Dict[str, object]] = []
    needle = b"auto\x00"
    start = 0

    def read_cstr(buf: bytes, ofs: int) -> Tuple[str, int]:
        end = buf.find(b"\x00", ofs)
        if end < 0:
            raise ValueError("unterminated cstr")
        return buf[ofs:end].decode("utf-8", "replace"), end + 1

    while True:
        idx = record_bytes.find(needle, start)
        if idx < 0:
            break
        # require room for entry_count
        if idx + 5 + 4 > len(record_bytes):
            break
        try:
            entry_count = struct.unpack_from("<I", record_bytes, idx + 5)[0]
            # sanity: typical values are small (3~4)
            if entry_count == 0 or entry_count > 64:
                start = idx + 1
                continue
            ofs = idx + 9
            entries: List[Dict[str, object]] = []
            for _ in range(entry_count):
                key, ofs = read_cstr(record_bytes, ofs)
                val, ofs = read_cstr(record_bytes, ofs)
                flag1 = struct.unpack_from("<I", record_bytes, ofs)[0]
                flag2 = struct.unpack_from("<I", record_bytes, ofs + 4)[0]
                ofs += 8
                entries.append({"key": key, "value": val, "flag1": flag1, "flag2": flag2})
            blocks.append({"off": idx, "entry_count": entry_count, "entries": entries})
            start = idx + 1
        except Exception:
            # If parsing fails, keep scanning.
            start = idx + 1

    return blocks


def auto_blocks_to_material_sets(blocks: List[Dict[str, object]]) -> List[Dict[str, str]]:
    out: List[Dict[str, str]] = []
    for b in blocks:
        m: Dict[str, str] = {}
        for e in b.get("entries", []):
            key = str(e.get("key") or "")
            val = str(e.get("value") or "")
            if key and val:
                m[key] = val
        out.append(m)
    return out


def find_subset_table(payload: bytes, decl_off: int, vcount: int, face_count: Optional[int] = None) -> List[Dict[str, int]]:
    """
    Find subset/material table right before a D3D mesh block.

    Observed layout (little-endian):
      u32 subset_count
      subset_count * (u32 material_id, u32 startTri, u32 triCount, u32 baseVertex, u32 vertexCount)

    Where vertexCount == vcount and baseVertex is often 0.
    """

    best: List[Dict[str, int]] = []
    # Some files keep a larger gap between the subset table and the vertex declaration block.
    search_start = max(0, decl_off - 0x4000)
    search_end = max(0, decl_off - 4)
    if search_end <= search_start:
        return best

    for off in range(search_start, search_end, 4):
        subset_count = struct.unpack_from("<I", payload, off)[0]
        if subset_count == 0 or subset_count > 256:
            continue
        for entry_size in (20, 16):
            table_bytes = 4 + subset_count * entry_size
            if off + table_bytes > decl_off:
                continue

            ok = True
            entries: List[Dict[str, int]] = []
            max_end = 0
            sum_tris = 0
            for i in range(subset_count):
                if entry_size == 20:
                    m_id, start_tri, tri_count, base_v, vcnt = struct.unpack_from("<5I", payload, off + 4 + i * 20)
                    if vcnt != vcount:
                        ok = False
                        break
                else:
                    m_id, start_tri, tri_count, base_v = struct.unpack_from("<4I", payload, off + 4 + i * 16)

                if base_v > vcount:
                    ok = False
                    break
                if start_tri > 100_000_000 or tri_count > 100_000_000 or tri_count == 0:
                    ok = False
                    break
                if face_count is not None and (start_tri + tri_count) > face_count:
                    ok = False
                    break

                max_end = max(max_end, start_tri + tri_count)
                sum_tris += tri_count
                entries.append(
                    {
                        "material_id": int(m_id),
                        "start_tri": int(start_tri),
                        "tri_count": int(tri_count),
                        "base_vertex": int(base_v),
                    }
                )

            if not ok:
                continue

            # Heuristic: table matches if it covers a reasonable number of triangles (non-zero), and sits close to decl.
            if sum_tris == 0 or max_end == 0:
                continue
            # Prefer the closest table to decl_off.
            if not best or (decl_off - (off + table_bytes)) < (decl_off - (best[0]["_end_off"])):  # type: ignore[index]
                if entries:
                    entries[0]["_end_off"] = off + table_bytes  # type: ignore[typeddict-item]
                best = entries

    # strip helper key
    if best and "_end_off" in best[0]:
        best[0].pop("_end_off", None)  # type: ignore[arg-type]
    return best


def write_obj(out_dir: Path, base_name: str, meshes: List[Mesh], mesh_to_tex: Dict[int, str]) -> None:
    out_dir.mkdir(parents=True, exist_ok=True)
    obj_path = out_dir / f"{base_name}.obj"
    mtl_path = out_dir / f"{base_name}.mtl"

    # Materials: one per mesh OR multiple per mesh if subsets exist.
    mtl_names: List[str] = []
    per_mesh_material_map: List[Optional[Dict[int, str]]] = []
    with mtl_path.open("w", encoding="utf-8", newline="\n") as f:
        for i, mesh in enumerate(meshes):
            if mesh.subsets and mesh.material_sets:
                # Per-subset materials
                # Build a stable list of used material ids for this mesh.
                used_ids = sorted({s["material_id"] for s in mesh.subsets})
                local_names: Dict[int, str] = {}
                for mid in used_ids:
                    mtl = sanitize_mtl_name(f"{base_name}_{i}_{mesh.name}_mat{mid}")
                    local_names[mid] = mtl
                    mset = mesh.material_sets[mid] if mid < len(mesh.material_sets) else {}
                    tex = mset.get("ColorMap")
                    f.write(f"newmtl {mtl}\n")
                    f.write("Kd 1.000000 1.000000 1.000000\n")
                    if tex:
                        f.write(f"map_Kd {Path(tex).name}\n")
                    nrm = mset.get("NormalMap")
                    if nrm:
                        f.write(f"map_Bump {Path(nrm).name}\n")
                    lum = mset.get("LuminosityMap")
                    if lum:
                        f.write(f"map_Ke {Path(lum).name}\n")
                    ref = mset.get("ReflectionMap")
                    if ref:
                        f.write(f"# ReflectionMap {Path(ref).name}\n")
                    f.write("\n")
                mtl_names.append("")  # placeholder; actual mapping stored in per_mesh_material_map
                per_mesh_material_map.append(local_names)
            else:
                tex = mesh_to_tex.get(i) or mesh.maps.get("ColorMap")
                mtl = sanitize_mtl_name(f"{base_name}_{i}_{mesh.name}")
                mtl_names.append(mtl)
                per_mesh_material_map.append(None)
                f.write(f"newmtl {mtl}\n")
                f.write("Kd 1.000000 1.000000 1.000000\n")
                if tex:
                    tex_norm = Path(tex).name.replace("\\", "/")
                    f.write(f"map_Kd {tex_norm}\n")
                nrm = mesh.maps.get("NormalMap")
                if nrm:
                    f.write(f"map_Bump {Path(nrm).name.replace('\\\\','/')}\n")
                lum = mesh.maps.get("LuminosityMap")
                if lum:
                    f.write(f"map_Ke {Path(lum).name.replace('\\\\','/')}\n")
                ref = mesh.maps.get("ReflectionMap")
                if ref:
                    f.write(f"# ReflectionMap {Path(ref).name.replace('\\\\','/')}\n")
                f.write("\n")

    # OBJ: global index space
    v_base = 1
    vt_base = 1
    vn_base = 1

    with obj_path.open("w", encoding="utf-8", newline="\n") as f:
        f.write(f"mtllib {mtl_path.name}\n")
        for mi, mesh in enumerate(meshes):
            f.write(f"o {sanitize_mtl_name(mesh.name)}\n")
            local_map = per_mesh_material_map[mi]
            if not local_map:
                f.write(f"usemtl {mtl_names[mi]}\n")
            for (x, y, z) in mesh.vertices:
                f.write(f"v {x:.6f} {y:.6f} {z:.6f}\n")
            for uv in mesh.uvs:
                if uv is None:
                    f.write("vt 0.000000 0.000000\n")
                else:
                    u, v = uv
                    f.write(f"vt {u:.6f} {v:.6f}\n")
            for nrm in mesh.normals:
                if nrm is None:
                    f.write("vn 0.000000 0.000000 1.000000\n")
                else:
                    nx, ny, nz = nrm
                    f.write(f"vn {nx:.6f} {ny:.6f} {nz:.6f}\n")

            if mesh.subsets and local_map:
                for s in mesh.subsets:
                    mid = s["material_id"]
                    start_tri = s["start_tri"]
                    tri_count = s["tri_count"]
                    mtl = local_map.get(mid)
                    if mtl:
                        f.write(f"usemtl {mtl}\n")
                    for a, b, c in mesh.faces[start_tri : start_tri + tri_count]:
                        fa = v_base + a
                        fb = v_base + b
                        fc = v_base + c
                        fta = vt_base + a
                        ftb = vt_base + b
                        ftc = vt_base + c
                        fna = vn_base + a
                        fnb = vn_base + b
                        fnc = vn_base + c
                        f.write(f"f {fa}/{fta}/{fna} {fb}/{ftb}/{fnb} {fc}/{ftc}/{fnc}\n")
            else:
                for a, b, c in mesh.faces:
                    fa = v_base + a
                    fb = v_base + b
                    fc = v_base + c
                    fta = vt_base + a
                    ftb = vt_base + b
                    ftc = vt_base + c
                    fna = vn_base + a
                    fnb = vn_base + b
                    fnc = vn_base + c
                    f.write(f"f {fa}/{fta}/{fna} {fb}/{ftb}/{fnb} {fc}/{ftc}/{fnc}\n")

            v_base += len(mesh.vertices)
            vt_base += len(mesh.vertices)
            vn_base += len(mesh.vertices)


def infer_scn0_material_color_maps(data: bytes, *, base_hint: Optional[str] = None) -> Dict[int, str]:
    """
    SCN0SCEN often contains multiple texture families for the same asset (e.g. ...U_*.dds vs ...E_*.dds),
    and the engine may bind the final material set after mesh loading (sub_10014580's post-load pass).

    We keep this deterministic and file-driven:
    - Extract NUL-terminated ASCII strings.
    - Identify the best texture family prefix (e.g. 'ou06E') using base_hint if present.
    - Return {index -> filename} for entries like '{prefix}_{index}.ext'.

    Note: This does not scan folders or rely on fixed extensions; it uses strings embedded in the .scn itself.
    """

    def iter_cstrings(max_len: int = 260) -> Iterable[str]:
        i = 0
        n = len(data)
        while i < n:
            b = data[i]
            if 32 <= b < 127:
                j = i
                while j < n and data[j] != 0 and (32 <= data[j] < 127) and (j - i) < max_len:
                    j += 1
                if j < n and data[j] == 0 and j - i >= 4:
                    s = data[i:j].decode("ascii", "ignore")
                    # Keep only "filename-like" strings (cheap filter).
                    if any(c.isalpha() for c in s) and ("." in s) and ("/" not in s) and ("\\" not in s):
                        yield s
                    i = j + 1
                    continue
            i += 1

    # 1) Gather candidate texture filenames by pattern: PREFIX_<int>.EXT
    tex_pat = re.compile(r"^([A-Za-z0-9]+)_([0-9]+)\.([A-Za-z0-9]{2,5})$")
    by_prefix: Dict[str, Dict[int, str]] = {}
    for s in iter_cstrings():
        m = tex_pat.match(s)
        if not m:
            continue
        prefix, idx_s, _ext = m.group(1), m.group(2), m.group(3)
        try:
            idx = int(idx_s)
        except ValueError:
            continue
        if idx < 0 or idx > 999:
            continue
        by_prefix.setdefault(prefix, {})[idx] = s

    if not by_prefix:
        return {}

    # 2) Prefer a family driven by base_hint, if possible.
    base = None
    if base_hint:
        # Example: base_hint='ou06' -> prefer 'ou06E', then 'ou06U', then exact 'ou06'.
        for suffix in ("E", "U", ""):
            cand = f"{base_hint}{suffix}"
            if cand in by_prefix:
                base = cand
                break

    # 3) Fallback: choose the richest family (most indices), stable tie-break by prefix name.
    if base is None:
        base = max(sorted(by_prefix), key=lambda k: (len(by_prefix[k]), k))

    return dict(by_prefix.get(base, {}))


def color_maps_to_material_sets(color_maps: Dict[int, str]) -> List[Dict[str, str]]:
    if not color_maps:
        return []
    need = max(color_maps) + 1
    out: List[Dict[str, str]] = [{} for _ in range(need)]
    for idx, fname in color_maps.items():
        out[idx]["ColorMap"] = fname
    return out


def choose_scn0_material_sets(data: bytes, *, base_hint: Optional[str]) -> List[Dict[str, str]]:
    """
    Prefer SCN0's in-file 'auto\\0' material blocks (same concept as SCN1),
    then fall back to string-based ColorMap family inference if 'auto' blocks are absent.
    """

    auto_blocks = extract_auto_material_blocks(data)
    auto_sets = [m for m in auto_blocks_to_material_sets(auto_blocks) if m.get("ColorMap")]
    if auto_sets:
        def score(m: Dict[str, str]) -> Tuple[int, int]:
            keys = ("ColorMap", "NormalMap", "LuminosityMap", "ReflectionMap")
            return (sum(1 for k in keys if m.get(k)), len(m))

        # Prefer base_hint+'E' family if present, else base_hint+'U', else keep all.
        if base_hint:
            e_fam = f"{base_hint}E"
            u_fam = f"{base_hint}U"
            e = [m for m in auto_sets if Path(m.get("ColorMap", "")).name.lower().startswith(e_fam.lower())]
            if e:
                auto_sets = e
            else:
                u = [m for m in auto_sets if Path(m.get("ColorMap", "")).name.lower().startswith(u_fam.lower())]
                if u:
                    auto_sets = u

        # Deduplicate by ColorMap filename, keep the richest variant.
        best: Dict[str, Dict[str, str]] = {}
        for m in auto_sets:
            cm = Path(m.get("ColorMap", "")).name
            if not cm:
                continue
            cur = best.get(cm)
            if cur is None or score(m) > score(cur):
                best[cm] = m

        def trailing_idx(name: str) -> Tuple[int, str]:
            mm = re.search(r"_([0-9]+)\.", name)
            if not mm:
                return (10**9, name.lower())
            return (int(mm.group(1)), name.lower())

        return [best[k] for k in sorted(best.keys(), key=trailing_idx)]

    color_maps = infer_scn0_material_color_maps(data, base_hint=base_hint)
    return list(color_maps_to_material_sets(color_maps))


def infer_scn0_subset_requirements_from_texture_blocks(
    data: bytes, *, color_maps: Dict[int, str]
) -> Tuple[int, int]:
    """
    From each chosen texture name, backscan for:
      u32 start_tri, u32 tri_count, u32 base_vertex, u32 vertex_count
    without requiring vcount/face_count. Used to pick the correct (usually highest) mesh block.
    """

    req_faces = 0
    req_verts = 0
    for _mid, tex in sorted(color_maps.items()):
        needle = Path(tex).name.encode("ascii", "ignore") + b"\x00"
        pos = data.find(needle)
        if pos < 0:
            continue
        best: Optional[Tuple[int, int, int, int]] = None
        best_score = (-1, -1)
        back_start = max(0, pos - 64)
        for ofs in range(pos - 16, back_start - 1, -4):
            if ofs < 0 or ofs + 16 > len(data):
                continue
            start_tri, tri_count, base_v, v_cnt = struct.unpack_from("<4I", data, ofs)
            if tri_count <= 0 or v_cnt <= 0:
                continue
            score = (tri_count, v_cnt)
            if score > best_score:
                best_score = score
                best = (start_tri, tri_count, base_v, v_cnt)
        if best:
            start_tri, tri_count, base_v, v_cnt = best
            req_faces = max(req_faces, int(start_tri + tri_count))
            req_verts = max(req_verts, int(base_v + v_cnt))
    return req_faces, req_verts


def infer_scn0_subsets_from_texture_blocks(
    data: bytes,
    *,
    vcount: int,
    face_count: int,
    color_maps: Dict[int, str],
) -> List[Dict[str, int]]:
    """
    SCN0 在贴图字符串附近常携带分段范围（与 SCN0SCEN mesh blob 尾部类似）：

      ... u32 start_tri, u32 tri_count, u32 base_vertex, u32 vertex_count, "tex.dds\\0"

    但在部分样本中这 4 个 u32 之前还夹杂一些 float/u32（例如一个 1 标志或 HB=50.0f）。

    这里不做“全文件暴扫”，只针对已选中的贴图族（如 ou06E_0.dds/ou06E_1.dds），
    在每个贴图名出现位置附近（向前 64 字节）寻找一组满足约束的 4*u32 作为 subset ranges。
    """

    if not color_maps or vcount <= 0 or face_count <= 0:
        return []

    def candidate_ok(start_tri: int, tri_count: int, base_v: int, v_cnt: int) -> bool:
        if tri_count <= 0 or tri_count > face_count:
            return False
        if start_tri >= face_count or start_tri + tri_count > face_count:
            return False
        if v_cnt <= 0 or base_v >= vcount or base_v + v_cnt > vcount:
            return False
        return True

    subsets: List[Dict[str, int]] = []
    for material_id, tex in sorted(color_maps.items()):
        needle = tex.encode("ascii", "ignore") + b"\x00"
        pos = data.find(needle)
        if pos < 0:
            continue
        best: Optional[Tuple[int, int, int, int]] = None
        best_score = (-1, -1)  # (tri_count, v_cnt)
        back_start = max(0, pos - 64)
        for ofs in range(pos - 16, back_start - 1, -4):
            if ofs < 0 or ofs + 16 > len(data):
                continue
            start_tri, tri_count, base_v, v_cnt = struct.unpack_from("<4I", data, ofs)
            if not candidate_ok(start_tri, tri_count, base_v, v_cnt):
                continue
            score = (tri_count, v_cnt)
            if score > best_score:
                best_score = score
                best = (start_tri, tri_count, base_v, v_cnt)
        if best:
            start_tri, tri_count, base_v, v_cnt = best
            subsets.append(
                {
                    "material_id": int(material_id),
                    "start_tri": int(start_tri),
                    "tri_count": int(tri_count),
                    "base_vertex": int(base_v),
                    "vertex_count": int(v_cnt),
                }
            )

    subsets.sort(key=lambda s: (s["start_tri"], s["material_id"]))
    return subsets



def write_mesh_package(out_dir: Path, scn_dir: Path, base_name: str, mesh: Mesh) -> None:
    """
    Write a single mesh as its own OBJ/MTL pair into out_dir.

    Notes:
    - If the mesh provides subset -> material_id mapping, we emit one MTL entry per used material_id.
    - If the mesh has multiple material sets but no subset table (common for mid/low LODs),
      we emit one OBJ/MTL per material-set variant (geometry duplicated, but binding stays faithful).
    """

    out_dir.mkdir(parents=True, exist_ok=True)
    safe_base = sanitize_mtl_name(base_name)

    # Convert/copy textures to PNG and rewrite references.
    maps, material_sets = prepare_textures_to_png(
        scn_dir, out_dir, maps=dict(mesh.maps or {}), material_sets=list(mesh.material_sets or [])
    )

    obj_path = out_dir / f"{safe_base}.obj"
    mtl_path = out_dir / f"{safe_base}.mtl"

    per_mesh_material_map: Dict[int, str] = {}
    with mtl_path.open('w', encoding='utf-8', newline='\n') as f:
        if mesh.subsets and material_sets:
            used_ids = sorted({s['material_id'] for s in mesh.subsets})
            for mid in used_ids:
                mtl = sanitize_mtl_name(f"{safe_base}_mat{mid}")
                per_mesh_material_map[mid] = mtl
                mset = material_sets[mid] if mid < len(material_sets) else {}
                f.write(f"newmtl {mtl}\n")
                f.write('Kd 1.000000 1.000000 1.000000\n')
                if (tex := mset.get('ColorMap')):
                    f.write(f"map_Kd {Path(tex).name}\n")
                if (nrm := mset.get('NormalMap')):
                    f.write(f"map_Bump {Path(nrm).name}\n")
                if (lum := mset.get('LuminosityMap')):
                    f.write(f"map_Ke {Path(lum).name}\n")
                if (ref := mset.get('ReflectionMap')):
                    f.write(f"# ReflectionMap {Path(ref).name}\n")
                f.write('\n')
        else:
            mtl = sanitize_mtl_name(f"{safe_base}_mat0")
            per_mesh_material_map[0] = mtl
            f.write(f"newmtl {mtl}\n")
            f.write('Kd 1.000000 1.000000 1.000000\n')
            if (tex := maps.get('ColorMap')):
                f.write(f"map_Kd {Path(tex).name}\n")
            if (nrm := maps.get('NormalMap')):
                f.write(f"map_Bump {Path(nrm).name}\n")
            if (lum := maps.get('LuminosityMap')):
                f.write(f"map_Ke {Path(lum).name}\n")
            if (ref := maps.get('ReflectionMap')):
                f.write(f"# ReflectionMap {Path(ref).name}\n")
            f.write('\n')

        with obj_path.open('w', encoding='utf-8', newline='\n') as f:
            f.write(f"mtllib {mtl_path.name}\n")
            f.write(f"o {safe_base}\n")
            for (x, y, z) in mesh.vertices:
                f.write(f"v {x:.6f} {y:.6f} {z:.6f}\n")
            for uv in mesh.uvs:
                if uv is None:
                    f.write('vt 0.000000 0.000000\n')
                else:
                    u, v = uv
                    f.write(f"vt {u:.6f} {v:.6f}\n")
            for nrm in mesh.normals:
                if nrm is None:
                    f.write('vn 0.000000 0.000000 1.000000\n')
                else:
                    nx, ny, nz = nrm
                    f.write(f"vn {nx:.6f} {ny:.6f} {nz:.6f}\n")

            if mesh.subsets and per_mesh_material_map:
                for s in mesh.subsets:
                    mid = s['material_id']
                    if (mtl := per_mesh_material_map.get(mid)):
                        f.write(f"usemtl {mtl}\n")
                    start_tri = s['start_tri']
                    tri_count = s['tri_count']
                    for a, b, c in mesh.faces[start_tri : start_tri + tri_count]:
                        fa, fb, fc = a + 1, b + 1, c + 1
                        f.write(f"f {fa}/{fa}/{fa} {fb}/{fb}/{fb} {fc}/{fc}/{fc}\n")
            else:
                if (mtl := per_mesh_material_map.get(0)):
                    f.write(f"usemtl {mtl}\n")
                for a, b, c in mesh.faces:
                    fa, fb, fc = a + 1, b + 1, c + 1
                    f.write(f"f {fa}/{fa}/{fa} {fb}/{fb}/{fb} {fc}/{fc}/{fc}\n")

def parse_scn1(path: Path, *, flip_v: bool, swap_yz: bool) -> Tuple[List[Mesh], Dict[int, str]]:
    data = path.read_bytes()
    r = Reader(data, 0)
    magic = r.bytes(4)
    if magic != b"SCN1":
        raise ValueError(f"not SCN1: {magic!r}")
    v = r.u32()
    if v != 0:
        raise ValueError(f"unexpected SCN1 header dword: {v}")

    # 1) tree header (sub_10015430)
    after_tree = parse_scn_tree(data, r.tell())
    r.seek(after_tree)

    # 2) materials/library blob (sub_100155C0) - we only advance cursor
    #    It starts with u32 countA and contains lots of cstr/arrays.
    #    We'll parse minimally to land at the next section.
    start_lib = r.tell()
    count_a = r.u32()
    for _ in range(count_a):
        _lib_name = r.cstr()
        count_b = r.u32()
        for _ in range(count_b):
            _entry_name = r.cstr()
            _flag = r.u32()  # 1/0
            c1 = r.u32()
            r.skip(16 * c1)
            c2 = r.u32()
            r.skip(20 * c2)
            c3 = r.u32()
            r.skip(16 * c3)
            c4 = r.u32()
            r.skip(68 * c4)
    _lib_size = r.tell() - start_lib

    # 3) pairs list
    pair_count = r.u32()
    # SCN1: each entry advances by 3 dwords in sub_10014F20 (12 bytes).
    r.skip(pair_count * 12)

    # 4) 3 dwords (unknown; often floats in practice)
    _d0 = r.u32()
    _d1 = r.u32()
    _d2 = r.u32()

    # 5) mesh list
    # Note: sub_10014F20 reads v18 = *v16; v19 = *v16 (same dword, no increment), then v20 = v16 + 1.
    # So the file provides a single u32 here; treat it as mesh_count.
    mesh_count = r.u32()
    meshes: List[Mesh] = []
    for _ in range(mesh_count):
        rec_size = r.u32()
        rec = struct.pack("<I", rec_size) + r.bytes(rec_size - 4)
        # Some main records can contain multiple embedded mesh blocks (LOD/high-low etc.).
        name_end = rec.find(b"\x00", 8)
        rec_name = rec[8:name_end].decode("utf-8", "replace") if name_end > 8 else f"rec_{len(meshes)}"
        payload = rec[name_end + 1 :] if name_end > 0 else b""
        maps = extract_texture_maps(rec)
        material_sets = auto_blocks_to_material_sets(extract_auto_material_blocks(rec))
        embedded = extract_d3d_mesh_blocks(
            payload,
            flip_v=flip_v,
            swap_yz=swap_yz,
            name_prefix=rec_name,
            maps=maps,
            material_sets=material_sets,
        )
        if embedded:
            meshes.extend(embedded)
        else:
            mesh = parse_mesh_record(rec, flip_v=flip_v, swap_yz=swap_yz)
            if mesh is not None and mesh.vertices and mesh.faces:
                mesh.maps = maps
                mesh.material_sets = material_sets
                meshes.append(mesh)

    # 6) mapping block: (mesh_index, container_index, cstr texture), sentinel -1
    mapping_count = r.u32()
    mesh_to_tex: Dict[int, str] = {}
    for _ in range(mapping_count):
        mesh_index = r.u32()
        container_index = r.u32()
        tex_name = r.cstr()
        # This section is not used for material/texture binding in our exporter.
        # We keep reading it only to maintain correct file offsets.
        _ = container_index  # currently unused

    # 7) extra mesh list: record + trailing cstr per entry
    extra_count = r.u32()
    for _ in range(extra_count):
        rec_size = r.u32()
        rec = struct.pack("<I", rec_size) + r.bytes(rec_size - 4)
        extra_mesh = parse_mesh_record(rec, flip_v=flip_v, swap_yz=swap_yz)
        _extra_name = r.cstr()
        if extra_mesh is not None and extra_mesh.vertices and extra_mesh.faces:
            extra_mesh.maps = extract_texture_maps(rec)
            extra_mesh.material_sets = auto_blocks_to_material_sets(extract_auto_material_blocks(rec))
            meshes.append(extra_mesh)

    return meshes, mesh_to_tex


def main() -> int:
    ap = argparse.ArgumentParser(description="Convert SCN0/SCN1 (*.scn) to OBJ+MTL (high LOD).")
    ap.add_argument("input_dir", type=Path, help="Input folder (recursively scans .scn files)")
    ap.add_argument("output_dir", type=Path, help="Output folder")
    args = ap.parse_args()

    in_dir: Path = args.input_dir
    out_root: Path = args.output_dir
    if not in_dir.exists() or not in_dir.is_dir():
        raise SystemExit(f"input_dir is not a folder: {in_dir}")
    out_root.mkdir(parents=True, exist_ok=True)

    scn_paths = sorted(in_dir.rglob("*.scn"))
    for scn_path in scn_paths:
        try:
            data = scn_path.read_bytes()
            head = data[:4]
            if head == b"SCN1":
                meshes, _mesh_to_tex = parse_scn1(scn_path, flip_v=True, swap_yz=False)
                # Prefer the structurally complete high LOD (segmented materials + multiple ColorMaps),
                # then fall back to largest geometry.
                hi = [m for m in meshes if (m.subsets and m.material_sets)]
                rich = []
                for m in hi:
                    color_maps = {ms.get("ColorMap") for ms in m.material_sets if ms.get("ColorMap")}
                    rich.append((len(color_maps), len(m.subsets), len(m.vertices), len(m.faces), m))
                if rich:
                    # Prefer more distinct ColorMaps (e.g. E_0 + E_1), then more subsets.
                    mesh = max(rich, key=lambda t: (t[0], t[1], t[2], t[3]))[-1]
                else:
                    mesh = max(meshes, key=lambda m: (len(m.vertices), len(m.faces)))
                out_dir = out_root / "scn1" / scn_path.stem
                if out_dir.exists():
                    shutil.rmtree(out_dir, ignore_errors=True)
                out_dir.mkdir(parents=True, exist_ok=True)
                write_mesh_package(out_dir, scn_path.parent, scn_path.stem, mesh)
            elif head == b"SCN0":
                tree_end = parse_scn_tree(data, 4)
                # No fallback/guesses: only use the known high-LOD block format (stride32 + tag 101/102).
                stride32_blocks = scan_scn0_stride32_mesh_blocks(data, start=tree_end)
                if not stride32_blocks:
                    continue

                m0 = re.match(r"^([A-Za-z]{2}\d{2})", scn_path.stem)
                base_hint = m0.group(1) if m0 else None
                material_sets = choose_scn0_material_sets(data, base_hint=base_hint)
                inferred_name = scn_path.stem

                cmaps = {
                    i: mset.get("ColorMap", "")
                    for i, mset in enumerate(material_sets)
                    if mset.get("ColorMap")
                }
                req_faces, req_verts = infer_scn0_subset_requirements_from_texture_blocks(data, color_maps=cmaps)

                def blk_face_count(b: Dict[str, int]) -> int:
                    return int(b.get("ib_bytes", 0)) // 2 // 3

                candidates = [
                    b
                    for b in stride32_blocks
                    if int(b.get("vcount", 0)) >= req_verts and blk_face_count(b) >= req_faces
                ]
                chosen_block = max(
                    candidates or stride32_blocks,
                    key=lambda b: (blk_face_count(b), int(b.get("vcount", 0))),
                )
                mesh = decode_scn0_stride32_mesh_block(data, chosen_block, flip_v=True, swap_yz=False)
                mesh.name = inferred_name

                if material_sets:
                    mesh.material_sets = list(material_sets)
                    if not mesh.subsets and len(mesh.material_sets) > 1 and cmaps:
                        mesh.subsets = infer_scn0_subsets_from_texture_blocks(
                            data,
                            vcount=len(mesh.vertices),
                            face_count=len(mesh.faces),
                            color_maps=cmaps,
                        )
                    if not mesh.subsets and len(mesh.material_sets) > 1:
                        mesh.maps = dict(mesh.material_sets[0])
                        mesh.material_sets = []

                out_dir = out_root / "scn0" / scn_path.stem
                if out_dir.exists():
                    shutil.rmtree(out_dir, ignore_errors=True)
                out_dir.mkdir(parents=True, exist_ok=True)
                write_mesh_package(out_dir, scn_path.parent, scn_path.stem, mesh)
            else:
                continue
        except Exception as e:
            print(f"[!] Failed: {scn_path} ({e})")

    print(f"Wrote: {out_root}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
