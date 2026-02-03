# Reverse Engineering Status (SCN0/SCN1)

This file is a running log to avoid re-doing IDA work and to track what is *confirmed* vs *speculative*.

## Project goals
- Parse SCN0/SCN1 **by structure**, not by guessing/scanning.
- Viewer: show full model assembled from multiple parts + switch LOD/parts via a tree.
- Converter: SCN0/SCN1 → OBJ/MTL + textures (PNG), matching in-game material intent.

## Current implementation status (repo)
- Viewer renders correctly (centered via CPU pre-transform; UV V flipped).
- Anti-crash texture pipeline: caps texture size (default 2048), no mipmaps by default, optional env overrides.
- Viewer can render **multiple meshes at once** (assembled view) and toggle visibility via TreeView checkboxes.
- TreeView group/container → mesh mapping is now **by container index** (from the SCN1 map table), not by name-prefix heuristics.
- Viewer now parses SCN1 subset ranges reliably by anchoring the range table to the D3D decl block (matches `sub_10007860` layout).

## Confirmed observations (from decomp)

### MCP usage note (important)
- `lookup_funcs` expects **one function name or address per call** in this environment.
- Passing a newline-separated string like `"sub_...\\nsub_..."` will be treated as **one query** and return `Not found`.

### `sub_10014450(lpString2, a2)` (dispatcher)
- Loads file into memory (CMem/LoadToCMem).
- Chooses parser by first dword of file:
  - `810435411` → SCN1 path → `sub_10014580(...)`
  - `827212627` → SCN0 path → `sub_10014F20(...)`

### `sub_10014580(a1, a2, a3)` (SCN1 load path)
High-level behavior:
- Reads a structured blob from in-memory SCN buffer.
- Builds a list of records / “auto blocks”:
  - Outer count `v7` and nested count `v20` (float used as count in decomp; likely u32 in file).
  - Inner entry contains:
    - a name string (copied with `strlen`/`qmemcpy`)
    - a flag at `+68` (bool)
    - several variable-length arrays with fixed element sizes (16/20/16/68 bytes) matching “auto blocks”.
- Calls `sub_10015AC0(a1, a3, &v86, v70)` later, which appears to load meshes by name + per-mesh payload.
- Sets technique strings like `_LuminosityMap`, `_Specularity` and iterates render list at `(a1+228)` to bind materials/resources.

### `sub_10015AC0(a1, a2, a3, a4, a5)` (mesh table traversal / loader)
Confirmed loop shape:
- `a5` groups; each group is **16 bytes** (loop variable `v32 += 16`).
- For each group:
  - Reads `v8 = *(u32*)a1` (mesh entry count for that group), advances `a1 += 4`.
  - For each entry (repeat `v8` times):
    - `v27 = a1` points at a **NUL-terminated name string**.
    - `v11 = &a1[strlen(name) + 5]` points to a following payload header/body.
    - `*a4 += strlen(name) + 5` suggests there is 1 byte + 4 byte field after the string (exact meaning TBD).
    - Calls `CDCMgr::LoadMesh(..., v11, a3, &v29, ...)` and later advances:
      - `v23 = v29; *a4 += v29; a1 = &v11[v23];`
    - So `v29` is a **payload byte length** returned/filled by loader, and the file stores that payload inline after `v11`.
- It also stores `(nameId, containerId)` pairs into per-group vectors and refcounts resources.

### Name strings can be empty (important)
- `sub_10015430` and `sub_100155C0` both use `strlen()`/`strcpy()` on node/record name pointers without validating length > 0.
- `sub_10015AC0` likewise uses `strlen(a1)` to advance to payload (`v11 = &a1[strlen(name)+5]`), meaning an empty string is structurally valid.
- Therefore, an empty name is not automatically a parsing failure; it can represent an anonymous/root/group node.

## Unconfirmed / TODO
- **Tree header** (the recursive node structure at the file start):
  - Current code only uses it to find `treeEnd` (skip), but the `0x40` bytes likely contain IDs/offsets/LOD grouping.
  - Need to identify fields used to associate nodes with mesh-table groups/entries.
- SCN1 strict: how the mesh-table from `sub_10015AC0` maps to the later “record blocks” we parse in C# today.
- SCN0 strict: current implementation still relies on locating the biggest stride32 VB/IB block; we need the real SCN0 table (likely analogous to SCN1’s).

## SCN0 loader notes (MCP decompile)

### `sub_10014F20(a1, a2, a3)` (SCN0 load path)
High-level behavior (confirmed via decompile):
- Rejects file if header indicates unsupported variant (`*(u32)(mem+4) != 0`).
- Uses `sub_10015430` then `sub_100155C0` to advance a cursor from the in-memory buffer to a later section.
- Reads a list of pairs `(u32,u32)` and stores them into `(a1+240)` as 8-byte entries.
- The count `v7` advances the cursor by **3 dwords per entry** (12 bytes). The code uses `v9[1]` and `(v9+1)[1]` and then moves `v9 += 3`.
- Reads 3 dwords into `(a1+43..45)`.
- Reads `v19` containers via `CDCMgr::LoadMesh(..., v21, a3, &len, ...)`, storing resulting handles into a vector (`v57..v59`).
- Reads a table of entries: each entry has `(groupIndex, containerIndex, nameString)` and inserts `(nameId, containerHandle)` into per-group vectors (each group is 16 bytes: begin/end/cap pointers).
- Deletes containers at end (`CDCMgr::DeleteContainer` loop).
- Reads another list `v47` and loads additional meshes with `flag=1` (likely collision / simplified / extra resources).

Where “tree” likely lives for SCN0:
- The cursor-advancing functions `sub_10015430` / `sub_100155C0` are strong candidates for parsing the initial node tree and returning the end offset.

## Practical notes
- Name strings may be **Shift-JIS/CP932** (JP game). Viewer now decodes names as CP932.
- Some SCN1 records can legitimately have an **empty name**; viewer labels these as `<no-name>#N` without altering the underlying decoded bytes.
- For stability, viewer caps textures and disables mipmaps by default:
  - `SCN_TEX_MAX=4096` to raise cap
  - `SCN_TEX_MIPMAP=1` to enable mipmaps

## Next steps (recommended order)
1) Use IDA/MCP to locate the real “tree node” parser and document the `0x40` layout.
2) Implement SCN1: parse mesh table like `sub_10015AC0` and expose it as TreeView (LOD/group → entries).
3) Implement SCN0: find equivalent table-driven structure; remove stride-scan fallback.

## Implementation note (repo)
- `src/ScnParser.cs` now has an initial strict SCN1 path (`ParseScn1AllStrict`) that follows `sub_10014F20` structure:
  - parse tree via hasChild/hasSibling (matches `sub_10015430`)
  - skip auto-block table (`sub_100155C0` shape) for cursor alignment
  - parse pairs (12 bytes each), 3 dwords, container blocks (size-prefixed), and the (group,idx,name) mapping table
  - container display names come from the container header cstring at offset 8 (not from guessed record offsets)
  - material sets are parsed per-container by scanning `auto\0` blocks inside that container
    - IMPORTANT (SCN1): material IDs are kept as **auto-block indices** (no dedupe) to match subset/material ID usage in containers
  - subset ranges:
    - primary parse: subset table immediately before `decl520` (`u32 count` + `count*20` bytes), anchored to decl offset
    - fallback: wider scan and optional per-face attribute buffer (experimental)
