# SCN0 Notes

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
