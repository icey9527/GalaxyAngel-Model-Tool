# AXO（.axo）逆向记录（进行中）

目标：**严格按 IDA 反汇编/伪代码**实现解析；不做全文件扫描/启发式兜底。本文只记录已经从样本与 IDA 证实的结论。

## 1. 工作流与入口

- 仓库入口：`src/ModelLoader.cs`
- AXO 识别/结构打印：`src/AxoParser.cs` + `src/Formats/AxoFormatParser.cs`
- CLI 打印：`tools/ScnCli/Program.cs`
- UI（主程序）加载：`src/ViewerForm.cs`（通过 `ModelLoader.Load(...)`）
- 研究脚本：
  - `tools/axo_dump.py`：结构化 dump（chunks/TEX/MTRL/GEOG->GEOM）
  - `tools/axo_vif_dump.py`：打印 `GEOM` 内 VIFcode 流（用于还原几何）

当前状态：已能识别 `INFO/AXO_`、遍历 top-level chunks，并能解析 `GEOG` 下的 `GEOM`（包含几何与贴图引用的基础链路）。

当前实现进度（仓库侧）：

- 已实现：从 `GEOM` 的 VIF 流中严格提取 `position/uv`，并按尾部表给出的 `PRIM` 模式生成索引（目前样本主要为 `triangle strip` / `triangle fan`）
- 已实现：按 `ATOM -> MTRL -> TEX` 把贴图名映射到 `ScnMaterialSet.ColorMap`，并按工程约定拼接为 `*.agi.png`
- 备注：渲染器仅在 `Indices` 为空时回退到点云（`GL_POINTS`）绘制，用于调试

## 2. 顶层 chunk 结构（已验证）

### 2.1 顶层 chunk header

在样本 `in/mes_0100/si_mes_0101.axo` 中，文件从 `INFO` 开始，chunk 统一为 16 字节 header：

- `tag`：u32 little-endian 的 FourCC（例如 `"INFO"`）
- `size`：u32，payload 字节数
- `count`：u32（对部分 chunk 有意义，如 `MTRL/TEX/GEOG`）
- `unkC`：u32（含义未定，但在样本里与记录大小/size 有强相关）
- payload 紧随其后：`offset + 0x10`
- 下一个 chunk：`offset + 0x10 + size`

该“按 tag/size(+0x10) 走链”的逻辑与 IDA 中的 chunk-walk 函数一致：

- `sub_261250`：从某个 chunk 起顺着 `next = cur + 16 + cur.size` 迭代查找指定 tag；遇到 `"END "` 提前退出。

### 2.2 头部校验（IDA 证据）

IDA（`axl_axo.c`）中校验逻辑：

- `sub_263268`（AxlAxoCheckAxoModel）：要求
  - `*(u32)model == "INFO"`
  - `*(u32)(model+0x10) == "AXO_"`
  - 读取：
    - `version`：`*(u32)(model+0x14)`
    - `unk24`：`*(u32)(model+0x18)`
    - `unk28`：`*(u32)(model+0x1C)`

仓库里对应实现：`src/AxoParser.cs` 的 `TryParseHeader(...)`。

## 3. 已识别的常见 tag（样本观测）

样本 `si_mes_0101.axo` 的 top-level chunks：

- `INFO`
- `ATOM`
- `FRAM`
- `MTRL`
- `TEX `
- `CBBX`
- `GBBX`
- `ALIN`
- `GEOG`（包含若干 `GEOM`）
- `END `

其中：

- `GEOG`：其 payload 内部是一个“chunk 列表”，每个子块仍是 16 字节 header（在样本中为两个 `GEOM`）。

仓库里对应实现：

- `src/AxoParser.cs` 的 `ParseGeogChildren(...)`。

## 4. TEX 贴图表（已验证）

### 4.1 IDA 证据

IDA（`axl_axo.c`）相关函数：

- `sub_2637A8`：AxlAxoGetTextureNum（返回 `TEX ` chunk 的 `count`）
- `sub_263818`：AxlAxoGetTextureName（按 index 取 `TEX ` entry 指针）
- `sub_2638B0`：AxlAxoGetTextureNameByTextureId（按 textureId 搜索 entry，返回 entry 内部指针）

`sub_263818` 返回：`texChunk + 36*idx + 20`，说明 entry 大小为 **36 字节**，并且 `+20` 指向 entry 内部偏移 4（跳过 id）。

`sub_2638B0` 以 `int*(texChunk+16)` 为起点，每次 `v6 += 9`（9 个 int = 36 字节），并用第一个 int 与 `textureId` 比较。

### 4.2 从样本推导出的 entry 布局（与 IDA 一致）

在样本中，每条 TEX entry 的布局可直接观察为：

- `u32 id`
- `char name[32]`（ASCII，NUL 结尾，剩余填 0）

仓库里对应实现：

- `src/AxoParser.cs` 的 `ParseTextures(...)`

示例输出（来自 `tools/ScnCli`）会打印：

- `tex[0] id=0 name='enm01'`
- `tex[1] id=1 name='si_mes_0100'`

注意：你提到的 `.agi.png` 后缀处理，属于导出/贴图加载层面的映射规则；AXO 内部这里提供的是纹理名/标识本身。

## 5. MTRL 材质表（部分验证）

### 5.1 IDA 证据

IDA（`axl_axo.c`）：

- `sub_263570`：AxlAxoGetMaterialNum（返回 `MTRL` chunk 的 `count`）
- `sub_2635E0`：AxlAxoGetMaterialParam（每条记录大小 **68 字节** = 17 dwords）

仓库里对应实现：

- `src/AxoParser.cs` 的 `ParseMaterials(...)`（保留 raw dword/float，不推测字段语义）。

### 5.2 与 TEX 的关联（样本观测）

在样本里，`MTRL` 里每条记录的第一个 dword 恰好是 `0/1`，与 `TEX ` 的 id 对应；因此 `TextureId` 字段在代码里单独暴露为 `MaterialEntry.TextureId`。

## 6. ATOM/MTRL/TEX：贴图关联（已验证）

这部分的目标是：把 AXO 内的贴图引用转换为工具链使用的贴图路径（你已把 `*.agi` 转成 `*.agi.png`）。

- `TEX `：提供 `textureId -> name`（见上文）
- `MTRL`：每条记录 68 字节（17 dwords），第一个 dword 作为 `textureId`
- `ATOM`：每条记录内含一组 `(tag,u32)` 对，其中：
  - `"GEOM"`：选择对应的 `GEOM` index
  - `"MTRL"`：选择对应的 `MTRL` index
  - `"FRAM"`：存在但目前未用于静态网格输出（动画相关）

仓库对应实现：

- `src/Formats/AxoFormatParser.cs`：按 `MTRL[index].TextureId -> TEX[textureId].Name -> Name + ".agi.png"` 填充 `mesh.MaterialSets[0].ColorMap`
- `tools/axo_dump.py`：会打印 `atom[*]` 并展示解析到的 `*.agi.png` 名称（用于验证映射关系）

## 7. GEOM：流与尾部表（已验证）

这是目前几何方向最关键、且已经能用 **IDA 代码路径**证明的结构结论：

- `GEOM` 子块的 payload：
  - `+0x00..+0x1F`：8 个 `u32` 头部字段（暂不解释语义）
  - `+0x20` 开始：一段 VIFcode/数据流（用于喂给 VIF1/VU1）
  - 末尾固定 `0x30` 字节：6 个 `qword`（48 字节）的“尾部表”

### 6.1 IDA 证据：尾部表定位方式

在渲染路径中，几何指针最终会进入：

- `sub_25DFF8`：从 `geom + 0x20 + 4 * *(u32)(geom+0x0C)` 取一个 `_QWORD*`，并调用插件回调 `(*(v4+0x78))(pkt, qwordPtr)`。
- 该插件回调在默认插件表中为 `sub_265D60`，它会从 `qwordPtr` 连续读取 6 个 qword 写入 packet。

因此：

- `u32 header[3]`（即 `*(u32)(geom+0x0C)`）是从 `geom+0x20` 起算的 **dword 计数**，
- 该位置后紧跟 6 个 qword，即 `0x30` 字节尾部表。

样本验证（`si_mes_0101.axo` 两个 GEOM）也满足：

- `streamBytes = header[3] * 4`
- `payloadBytesAfter0x20 = streamBytes + 0x30`

对应的研究脚本：`tools/axo_vif_dump.py` 会打印 `stream dwords/len` 和 6 个 `tail.qword[*]`。

### 6.2 VIF 流的“包”边界（已验证）

`GEOM` 的 VIF 流里存在大量重复的指令组，并且以 `MSCAL (0x14)` 或 `MSCNT (0x17)` 作为边界；这在样本中非常明显，且与 VIF/GIF 常见的“喂数据 → 触发 VU microprogram”流程一致。

`tools/axo_vif_dump.py --summary` 可以把流按 `MSCAL/MSCNT` 分组并统计每组内的 `UNPACK` 计数；在 `si_mes_0101.axo` 的 `GEOM[0]` 中，常见模式是对 VU 地址 `0..4` 的 `UNPACK`（其中 `addr=1` 是 `v4 bits=32` 的 float 向量，`addr=3` 是 `v2 bits=32` 的 float 向量，推测分别对应位置/UV，但语义仍需以 IDA/微码为准）。
