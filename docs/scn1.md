# SCN1 Notes

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
