# SCN0 / SCN1 模型格式笔记（GAscn）

> 目标：把 `.scn`（SCN0/SCN1）解析成 `.obj/.mtl`，贴图绑定尽量与游戏一致；避免靠“扫描后缀/猜测”做绑定。
>
> 本文基于：仓库内 `scn1_to_obj.py` 的实现 + 逆向（IDA）函数行为 + `sc06/ou06A.scn` 实测。

---

## SCN1（新格式）

### 1) 文件头

- `char[4] magic` = `"SCN1"`
- `u32` = `0`
- 紧接一棵**场景树**（与 SCN0 结构一致，见下）

### 2) 场景树（tree）

每个 node 的结构（只用来“跳过长度定位后续结构”，不解释矩阵语义）：

- `name_cstr`：NUL 结尾字符串
- `u8[0x40]`：通常是矩阵/变换相关数据
- `u32 hasChild`：`==1` 则递归读取一个 node
- `u32 hasSibling`：`==1` 则递归读取一个 node

脚本实现：`parse_scn_tree()`

### 3) tree 之后：按 count 串起来的区段（按汇编对齐）

tree 后面会出现多个 “`u32 count` + 列表/块” 的区段；**这一段最容易因为“跳过字节数不对”导致全体错位**。

根据 `sub_10014F20`（反编译）实测（如 `ou01/ou01a.scn`），顺序更接近：

- `auto table`（见下；某些文件 outer=0）
- `u32 pairCount`
  - `pairCount * 12` 字节（每条 3 个 dword；不是 8/不是 16）
- `u32[3]`：3 个 dword（用途未完全确认）
- `u32 containerCount`
  - 循环 `containerCount` 次：
    - `u32 blockSize`
    - `u8[blockSize-4] blockBytes`
- `u32 mapCount`
  - 循环 `mapCount` 次：
    - `i32 groupIndex`（遇到 `-1` 作为 sentinel 终止）
    - `i32 containerIndex`
    - `name_cstr`

> 备注：我们现在在 C#（viewer）里优先按这条“表驱动路径”解析 SCN1，避免靠猜 offset。

#### map table 的用途（实践）

- 它把 “groupIndex（类似 LOD/组）→ containerIndex（容器块）→ 名字” 串起来。
- Viewer 右侧 TreeView 的 “Groups” 视图，就是按这张表来组织的（**不再用容器名/贴图名做猜测绑定**）。

### 4) container block：名字与 payload 起点（关键）

对 `ou01/ou01a.scn` 等文件，container block 头部可观察到：

- `u32 blockSize`
- `u32 header/flags`（不确定）
- `name_cstr`：从 **offset=8** 开始；例如 `SB_ou01E` / `ou01U`
- `u32 ???`：名字后紧跟 4 字节（`strlen(name)+5` 的现象）
- 后续才是 D3D mesh / auto 材质等 payload

### 5) 几何：container payload 内嵌 “D3D9 decl(520 bytes) + VB/IB” mesh block

#### A. record 内嵌 “D3D9 decl(520 bytes) + VB/IB” mesh block（高模/完整材质常见）

在 `rec_payload` 内会出现一段可识别的 **D3D9 Vertex Declaration**（固定 520 字节）：

- `decl520`：多条 element（`<stream, offset, type, method, usage, usageIndex>`），以 END 标记结束
- decl 后面紧跟 `vcount + VB`
  - `vcount` 位置存在变体：`v0` / `0,v1` / `0,0,v2`，脚本做兼容
- VB 后面：`idx_hdr + IB`
  - `idx_hdr` 也有变体（脚本用启发式判断 idx fmt / bytes-per-index）

**关键：subset table（决定分段贴图/分段材质）**

高模的正确贴图往往依赖 subset table（每段用不同的 material_id）：

- subset 典型字段（按三角形范围切）：  
  - `material_id`
  - `start_tri`
  - `tri_count`
  - `base_vertex`
  - `vertex_count`

**（按汇编更严格的定位）**

根据 `CDCMgr::LoadMesh` 的 `a3==1` 分支（`sub_10007860`），subset table 常见直接紧贴在 decl520 之前：

- `u32 subsetCount`
- `subsetCount * 20` 字节（每条 5 个 dword）：`matId,startTri,triCount,baseV,vCnt`
- 然后立刻是 `decl520`（520 bytes）

这比“在 decl 前后扫一大片猜 table”更可靠，且能解决 E/U 某些容器 `Subsets(0)` 的问题。

脚本实现：

- `extract_d3d_mesh_blocks()`：从 record payload 扫描 decl520 mesh block 并解码
- `find_subset_table()`：从 decl 前附近推断 subset table 的位置
- OBJ 导出时按 subset 输出 `usemtl` 分段

#### B. record 本体就是一个 mesh_record（无 decl520 block）

当 record 不含 decl520 mesh block 时，走 `parse_mesh_record(rec)` 解析（仍是结构化读取）。

### 6) 材质/贴图：`"auto\\0"` 块（SCN1 的核心）

record 中常见多段以 `auto\0` 开头的材质块（你提供过一大段十六进制示例）：

- `auto\0`
- `u32 map_count`
- 然后按对出现：
  - `MapTypeName_cstr`（如 `ColorMap`/`NormalMap`/`LuminosityMap`/`ReflectionMap`）
  - `TextureFilename_cstr`（如 `ou06E_0.bmp` / `ou06E_0_bmp.bmp` 等）
- 中间夹杂一些 float 参数（脚本目前跳过，不影响贴图名提取）

脚本实现：

- `extract_auto_material_blocks()`：提取所有 auto 块
- `auto_blocks_to_material_sets()`：转成 `material_sets[material_id] -> {ColorMap/NormalMap/...}`

### 7) SCN1 “只导高模”的选择规则（不靠名字）

用户偏好：只导出高模。脚本采用“结构性”选择，而非 `E/U/L` 名字判断：

- 候选必须同时具备：
  - `subsets`（能按 `material_id` 分段）
  - `material_sets`（auto 块提供多贴图）
- 在候选中取最大（顶点/面/分段数量等）

脚本位置：`main()` 的 SCN1 分支

---

## SCN0（旧格式）

### 0) 现状结论（重要）

SCN0 现在“能导出高模”，并且能把多贴图（`E_0/E_1/...`）按三角形范围切出来；整体策略仍然是“按文件内结构走”，不靠扫描目录/后缀去猜。

- 高模几何：来自 **stride=32 + tag=101/102** 的 VB/IB 块（不依赖名字判断）。
- 材质/贴图名：优先读取文件内的 **`auto\0` 材质块**（与 SCN1 同源的概念），而不是靠“扫字符串猜贴图”。
- 分段范围：在 `auto` 块/贴图名附近可回溯到 `start_tri/tri_count/base_vertex/vertex_count`（用来导出 OBJ 的 `usemtl` 分段）。

### 1) SCN0 文件头 & 场景树

- `char[8]` 常见开头：`"SCN0SCEN"`（文件 `sc06/ou06A.scn` 如此）
- 场景树结构与 SCN1 相同；tree 从 offset `+4` 开始

脚本实现：`parse_scn_tree(data, 4)`

### 2) SCN0 已实现的几何来源（多路尝试）

#### A) “SCN0SCEN mesh blob”（带 decl bitfield 的老布局）

脚本函数：`parse_scn0_mesh_blob()`

它尝试解析：

- `u32 decl_bitfield`
- `u32 vcount`
- `VB[vcount * stride]`（stride 由 decl_bitfield 计算）
- `u32 idx_meta0`
- `u32 ib_bytes`
- `IB[ib_bytes]`
- 之后可能跟一段 material/texture 段（某些样本能提供 `start_tri/tri_count/...`）

对 `sc06/ou06A.scn` 来说，这条路目前更像“低模/某一容器”的几何（并非最高细节那块）。

#### B) “SCN0 stride=32 + tag(101/102) 的高模块”（关键：能导出更高模）

这块格式来自你旧脚本 `scn0.py` 的发现，但现在实现做了收敛：

布局：

- `u32 vcount`
- `VB[vcount * 32]`：`pos(3f) + nrm(3f) + uv(2f)`
- `u32 tag`：`101` 或 `102`
- `u32 ib_bytes`
- `IB[ib_bytes]`：`u16` 三角形索引

脚本实现：

- `scan_scn0_stride32_mesh_blocks(data, start=tree_end)`：只在 `tree_end` 后的有限窗口内按结构判定（不是全文件扫后缀/扫图片）
- `decode_scn0_stride32_mesh_block()`：解码成 Mesh

实测（`sc06/ou06A.scn`）：

- 高模块可识别于 `0x603`：`v=11078, f=10990`

### 3) SCN0 多贴图分段（已补齐到“治标更少、结果更接近”）

`sc06/ou06A.scn` 的高模对应多张贴图（`ou06E_0.dds / ou06E_1.dds`），并且分段范围就存放在 `auto` 块/贴图名附近，形态类似：

- `u32 start_tri`
- `u32 tri_count`
- `u32 base_vertex`
- `u32 vertex_count`
- `tex_name_cstr`（`ou06E_0.dds\0` 等）

实现要点：

- 贴图名来源：解析 `auto\0` 块的 `ColorMap/NormalMap/...` 字段（优先选择 `baseHint+"E"` 贴图族）。
- subset 回溯：从 `auto` 起始位置（失败则从 `ColorMap` 字符串位置）向前 64 字节扫描 `4*u32`，选出 `tri_count` 最大的一组作为该材质的范围。
- LOD 选择：在所有 stride32 VB/IB 块里，优先挑选能覆盖所有 subset 范围的那块（通常就是高模）。

> 这一步让 SCN0 高模也能正确使用多张贴图（至少达到与文件中存储的分段范围一致）。

#### C) “D3D decl520 mesh block”（某些 SCN0 文件可能包含）

脚本也会对 SCN0 整文件尝试 `extract_d3d_mesh_blocks()`，以便覆盖“旧容器里嵌新布局”的情况。

### 3) SCN0 贴图族（E/U）与命名

SCN0 中可能同时出现多套贴图族，例如：

- `ou06U_0.dds / ou06U_1.dds`
- `ou06E_0.dds / ou06E_1.dds`

当前脚本策略（不扫描目录、不枚举后缀来“猜贴图”）：

- `infer_scn0_material_color_maps()`：只从 `.scn` 文件内嵌字符串里提取 `PREFIX_<idx>.<ext>`，并优先选择 `base_hint+"E"` 贴图族
- `infer_scn0_root_name_from_texture_block()`：在贴图块附近寻找紧跟的短 root 名（如 `ou06\0`），用于输出目录/对象名（避免把 `Xou06L_c` 这种容器名当节点名）

### 4) SCN0 目前“不完整”的部分（需要继续逆向落地）

现在 SCN0 高模能导出，但“只有一张图”是结构信息缺失导致：

- stride32 高模块本身没有 subset/material ranges
- 文件里虽然能找到 `E_0/E_1/...`，但缺少 “每段三角形范围 -> 贴图/材质” 的表

要做到 SCN1 那种“分段贴图完全正确”，需要把 SCN0 的 container/group/material 结构按逆向函数真实解析出来：

- 解析 group/entry 列表：`name_cstr + u32 + (mesh_record or mesh_blob)`（而不是靠扫描 offset）
- 解析材质/资源映射表：把 `E_*` / 其它 maps 与具体 mesh/subset 绑定起来

对应逆向函数（你提供的新版链路，作为下一步对齐目标）：

- `sub_100143E0`：SCN0 顶层 loader（新版）
- `sub_10014C50`：看起来在建立/填充一张“名字 -> 资源(Handle)”列表，并且更新某些标志位
- `sub_10014E40`：另一张类似的资源表/列表

> 备注：SCN0 的“治本”目标，是把这些 count/record 的结构在 Python 中复刻到位，从而得到真正的 subset ranges/材质绑定，而不是仅仅找到一块能画出来的 VB/IB。
