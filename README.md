# GalaxyAngel Model Tool

UI viewer + OBJ exporter for:

- Galaxy Angel 1 series (Moonlit Lovers / Eternal Lover): `SCN0/SCN1` (`.scn`)
- Galaxy Angel 2 series (PS2): `AXO` (`.axo`)

## Getting the files

You must extract game resources first with:

- https://github.com/icey9527/Verviewer

Notes:

- When exporting AXO, enable image conversion to PNG (so textures become `*.agi.png`).

## Where models are

### GA1 (SCN)

- `gadat020.pak`
- Files starting with `ou` are unit models.
- Each unit has `a/b/c` LOD variants:
  - `a` is the highest LOD.
- Each SCN contains one high mesh + one low mesh.
- After export:
  - Subfolder ending with `e` (e.g. `ou01e`) is the high mesh
  - Subfolder ending with `u` (e.g. `ou01u`) is the low mesh

### GA2 (AXO)

- `slg.dat`
- Unit models: `slg/machines`
- Prefixes:
  - `si_mon*` = RA team
  - `si_moo*` = GA team

## Not supported

- GA1 Microsoft official `X` model format
- GA1 PS2 `sc0、ao3` model format

## Build (single EXE)

Release publish is configured as a single self-contained EXE (win-x64).

From the repo root:

`dotnet publish -p:PublishProfile=SingleFile-win-x64`
