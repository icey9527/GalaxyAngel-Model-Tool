# SCN0 Notes

## SCN0（旧格式）

### 0) 现状结论（重要，已落地到 C#）

SCN0 现在在 C#（Viewer/Converter）里已经可以 **按结构严格解析**，并且在 UI 里能看到 **Groups(LOD/sets)**，可切换高/低模（不同 group / 不同 container）。

当前实现的关键点（都来自 IDA 反编译/汇编行为 + 样本 hex 对齐验证，不靠扫描全文件）：

- tree：按 `sub_10014AE0` 的递归结构解析（`name_cstr + 0x40 + hasChild + hasSibling`）。
- group/entry 表：按 `sub_10014C50`（groupCount = pairCount+1）结构消费字节流，得到每个 entry 的 payload（container block）。
- container 内 mesh blob：按 `sub_10004700` 结构解析：`decl_bitfield + vcount + VB + tag + ib_bytes + IB + subsetCount + subsetRecords`。
- stride：严格使用 `sub_1002A207(decl_bitfield)` 的计算规则。
- subset ranges：在 subset record 的 `+0x44` 位置（5*u32）读取 `(matId,startTri,triCount,baseV,vCnt)`。
- 贴图名：在 subset record 的 `+0x58` 位置读取 **16 字节内联字符串**（NUL 终止；有些文件为空）。

### 1) SCN0 文件头 & 场景树

- `char[4] magic` = `"SCN0"`
- 紧接场景树：tree 从 offset `+4` 开始
- tree 节点结构（与 SCN1 相同的“hasChild/hasSibling”递归布局）：
  - `name_cstr`
  - `u8[0x40]`（blob，语义未完全解释，但按长度消费）
  - `u32 hasChild`（==1 递归）
  - `u32 hasSibling`（==1 递归）

实现位置：`src/ScnParser.Scn1.cs` 的 `ParseTreeStrictInfo`（SCN0/SCN1 共用同一套节点解析）。

### 2) tree 之后的顶层区段（按 loader 消费顺序）

根据 `sub_100143E0`（SCN0 loader）和它调用的 `sub_10014C50/sub_10014E40`，tree 后面是按 count 串起来的区段：

- `auto block table`（shape 与 SCN1 的 auto table 一致；目前主要用于 cursor 对齐）
- 固定的若干 `u32` 配置字段（当前 viewer 不依赖其语义，仅按汇编消费以对齐 cursor）
- `u32 pairCount` + `pairCount * (u32,u32)`
- `u32[3]`（3 个 dword）
- mesh table：`groupCount = pairCount + 1`
  - 对每个 group：`u32 entryCount`
  - 对每个 entry：`name_cstr` + `u32 flag` + `payload(size-prefixed container block)`
- extra table（`sub_10014E40`）：`u32 count` + `count * (name_cstr + u32 flag + payload)`（目前不用于生成模型）

### 3) container block（旧 SCN0 容器头）

SCN0 的 container 头是固定布局（这解释了为什么名字里会直接出现 `E/U` 这样的变体）：

- `u32 size`（包含自身）
- `u32 id/hash`
- `char name[0x20]`：NUL 终止的固定缓冲（后面 padding=0）
- 紧接 mesh blob（即 offset = `0x28`）

> 注意：mesh table entry 的 `name_cstr` 可能在多个 container 上相同（例如都叫 `ou01`）。
> 区分 `ou01E/ou01U` 等变体的真实名字来自 container header 的 `name[0x20]`。

### 4) container 内 mesh blob（老布局，已解析）

由 `sub_10004700`（vtable 方法）严格读取：

- `u32 decl_bitfield`
- `u32 vcount`
- `VB[vcount * stride]`
  - `stride = sub_1002A207(decl_bitfield)`
- `u32 tag`：已观察到 `101` / `102`
- `u32 ib_bytes`
- `IB[ib_bytes]`：`u16` 索引（三角形列表）
- `u32 subsetCount`
- `subsetCount * 0x68` 字节的记录：
  - `record+0x44`：`u32 matId, startTri, triCount, baseV, vCnt`
  - `record+0x58`：`char texName[16]`（NUL 终止；可能为空）

#### 已验证的 decl 布局（当前 C# 已实现）

- `decl=0x112, stride=32`（`sc06/ou06A.scn`）：`pos(3f)@0 + nrm(3f)@12 + uv(2f)@24`
- `decl=0x52, stride=28`（`ob101/ob101.scn`）：`pos(3f)@0 + nrm(3f)@12 + uv(u16,u16)@24`（归一化到 `[0,1]`）

其余 `decl_bitfield` 组合仍需要继续按 vtable 的解析逻辑/渲染输入要求逐个落地（不允许“猜一个 offset 看起来对就算”）。

### 5) 仍待补齐/继续逆向的点

- 完整覆盖更多 `decl_bitfield` → vertex layout（目前只实现了已验证的两种）。
- subset record 中贴图名为空时，如何从其它表（auto table / extra table / 资源表）恢复材质名/贴图绑定。
- `auto block table` 的语义字段（目前只按结构跳过用于对齐；如果后续需要与 SCN1 一样完整材质集，可继续落地）。
