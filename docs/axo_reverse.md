# AXO (.axo) reverse notes (strict, no scanning)

This repository parses models via `ModelLoader.Load(...)` using format plugins in `src/Formats/`.

## Status

- Loads `.axo` in UI and exporter via `ModelLoader` dispatch.
- Parses top-level chunks and `GEOG -> GEOM` children.
- Decodes `GEOM` VIF stream to positions/UVs and builds triangle indices from the GEOM tail `PRIM`.
- Maps textures via `ATOM -> (MTRL key) -> TEX name`, producing `name + ".agi.png"`.

## Proven chunk walking (IDA)

`sub_261250` walks a chunk list by `next = cur + 0x10 + cur.size` and stops at `"END "`.

Each chunk header is 16 bytes:

- `u32 tag` (FourCC in little-endian)
- `u32 size` (payload size in bytes)
- `u32 count`
- `u32 unkC` (often a record size)

## Header check (IDA)

`sub_263268` (`AxlAxoCheckAxoModel`):

- requires `*(u32) == "INFO"` and `*(u32)(+0x10) == "AXO_"`
- reads `version` at `+0x14`, and two unknown u32 at `+0x18/+0x1C`

Repo: `src/AxoParser.cs::TryParseHeader`.

## TEX table (IDA)

`sub_263818` / `sub_2638B0` (`AxlAxoGetTextureName*`) imply:

- entry size = 36 bytes
- layout: `u32 id; char name[32]` (NUL-terminated ASCII)

Repo: `src/AxoParser.cs::ParseTextures`.

## MTRL table (IDA)

`sub_2635E0` (`AxlAxoGetMaterialParam`):

- record size = 68 bytes (17 dwords)
- indexed by `a2` (0..count-1)
- dword[0] is used as a key by the loader (matched against ATOM's `"MTRL"` value in `sub_25E2F8`)
- dword[15] is returned via output parameter `a9` and is the texture id used for `TEX` lookup in several samples (e.g. `si_moo_0000.axo` swaps 0/1 here)

Repo: `src/AxoParser.cs::ParseMaterials`.

## ATOM records (IDA-backed parsing)

In `sub_25E2F8` (clump/atomic init):

- the `ATOM` chunk is located by `sub_261250(model, "ATOM")`
- record size is `unkC` and the code iterates `unkC >> 3` times (8 bytes per pair)
- each pair is `(u32 tag, u32 value)`

Observed tags handled in `sub_25E2F8`:

- `"PLGI"`: plugin id (used to find a render plugin)
- `"FRAM"`: frame id/key (matched against the first dword of each FRAM record)
- `"MTRL"`: material key (matched against the first dword of each MTRL-derived record)
- `"GEOM"`: chooses which `GEOM` entry is used
- `"BBOX"`: bounding box index/offset into the bbox table

Repo: `src/Formats/AxoFormatParser.cs` parses `(tag,value)` pairs with record size `unkC`.

## GEOM payload and topology

Within a `GEOM` child chunk:

- payload starts at `geom + 0x10`
- `+0x00..+0x1F`: 8 x `u32` header fields
- `+0x20..`: VIF stream, length = `hdr[3] * 4` bytes (dword count)
- after the VIF stream: a fixed tail table (48 bytes = 6 qwords) used for GS packet building

The first tail qword contains `PRIM`; the current parser uses `prim & 7`:

- `4` = triangle strip
- `5` = triangle fan

Repo:

- `src/AxoVifDecoder.cs` decodes positions/UVs and batch boundaries from `MSCAL/MSCNT`.
- `src/Formats/AxoFormatParser.cs::BuildIndicesFromTailAndBatches` builds indices per batch.

## Texture naming in this toolchain

This app expects textures as files; for AXO it sets:

- `ScnMaterialSet.ColorMap = <texName> + ".agi.png"`

So if the game references `enm01` the tool loads `enm01.agi.png`.

## UV + alpha conventions (toolchain)

- Renderer flips V in shader (`v = 1 - v`) because textures are loaded top-to-bottom.
- AXO VIF UVs are additionally pre-flipped during decode so the on-screen result is correct.
- AXO OBJ export flips V back so external viewers match the in-app render.
- Texture alpha is preserved by default. Set `SCN_TEX_FORCE_OPAQUE=1` to force alpha to 255 (discard transparency).

## Debug helpers (no UI changes)

- `tools/axo_dump.py`: dumps chunks and prints `ATOM` mappings to `*.agi.png`
- `tools/axo_vif_dump.py`: dumps VIF stream and groups it by `MSCAL/MSCNT`
