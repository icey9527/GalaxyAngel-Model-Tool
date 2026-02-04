# SCN0 IDA TODO（用于窗口迁移/继续逆向）

> 目的：把 SCN0 做到和 SCN1 一样“完全按汇编/伪代码验证”的解析与渲染输入解释；禁止用全文件扫描/猜字段。

## 0) 当前落地进度（已实现）

- 严格 SCN0 顶层结构解析（magic/tree/auto/config/pairs/mesh-table/extra-table）：
  - 代码：`src/ScnParser.Scn0.cs`
  - 文档：`docs/scn0.md`
- tree 递归结构（与 SCN1 同形）：`name_cstr + 0x40 + hasChild + hasSibling`
- mesh table：`groupCount = pairCount + 1`，每 entry：`name_cstr + u32 flag + payload(size-prefixed container)`
- container header：`u32 size + u32 hash + name[0x20]`，mesh blob 起始 offset=`0x28`
- mesh blob（旧格式）严格读取：
  - `u32 decl_bitfield`
  - `u32 vcount`
  - `VB[vcount * stride]`，其中 `stride = sub_1002A207(decl_bitfield)`（已精确移植）
  - `u32 tag`（观测到 101/102）
  - `u32 ib_bytes`
  - `IB[u16]`（严格检查 maxIndex < vcount）
  - `u32 subsetCount`
  - subset record：在 `+0x44` 读 5*u32（matId/startTri/triCount/baseV/vCnt）；在 `+0x58` 读 16 字节贴图名
- 已验证并实现的顶点布局（其余 decl 直接抛异常，避免拉伸/撕裂）：
  - `decl=0x112, stride=32`：pos(3f)@0 + nrm(3f)@12 + uv(2f)@24（`in/sc06/ou06A.scn`）
  - `decl=0x52, stride=28`：pos(3f)@0 + nrm(3f)@12 + uv(u16,u16)@24 归一化（`in/ob101/ob101.scn`）
- UI：可按 group/container 形成“树形 + LOD 切换”，并且模型名仅使用 containerName（`E/U` 变体）
  - 代码：`src/ViewerForm.cs`

## 1) 需要 IDA 继续钉死的结论（本文件的核心）

### 1.1 `decl_bitfield -> 具体顶点元素布局` 的完整映射

现状：只实现了两个 decl；遇到其他 decl 会抛 `NotSupportedException`，以防错误解码导致“拉伸/撕裂”。

需要在 IDA 里找出**除 stride 外**，程序如何根据 `decl_bitfield`：

- 决定 vertex elements（position/normal/uv/uv2/color/tangent/weights/indices 等）
- 每个元素的 offset、类型（float/half/ubyte/u16 等）、是否归一化
- 是否存在多个 UV / skinning / color

### 1.2 subset record 贴图名为空时：材质/贴图绑定来自哪里

现状：subset record `texName[16]` 可能为空；目前用稳定默认材质兜底（仅为保证导出可用）。

需要确认：

- 贴图名是否来自 extra table（`sub_10014E40`）或 auto block table（`sub_100143E0` 前半段读取的那张表）
- 或来自别的资源管理器/字典（例如 hash -> name 的映射）

## 2) 建议你在 IDA 里抓取并贴回来的信息（最少集合）

### 2.1 必看函数（按优先级）

1. `sub_10004700`：读取 SCN0 container 的 mesh blob（decl/vb/ib/subsets 的完整读取顺序）
2. `sub_1002A207`：stride 计算（已移植，但用于确认没有变体）
3. `sub_100143E0`：SCN0 顶层 loader（tree 后面区段读取顺序、pairCount/groupCount、extra/auto 的关联）
4. `sub_10014C50`：mesh table（entry 的 payload 读取方式：长度字段的位置/类型）
5. `sub_10014E40`：extra table（每条记录格式与用途）

### 2.2 需要你贴出来的内容格式（不要截图整屏，尽量贴文本）

- 每个函数的伪代码（F5）中：
  - 读 `decl_bitfield` 后**如何解释/分支**的那段（尤其是构建 vertex decl / 输入布局的循环）
  - 任何“位掩码 -> offset/type” 的 switch/case 或查表
  - 任何把 `texName` 为空时替换为别表字符串的逻辑
- 若伪代码不清晰：贴对应基本块的反汇编（包含比较常量/位操作/表索引）

## 3) 本地验证（辅助汇编，不作为主依据）

- 样本：
  - `in/sc06/ou06A.scn`（decl=0x112）
  - `in/ob101/ob101.scn`（decl=0x52）
- CLI（用于快速查看 decl/面数/材质数等）：
  - `dotnet run -c Release --project tools\\ScnCli\\ScnCli.csproj -- <file.scn>`

## 4) “窗口迁移”用的提示词（复制到新窗口即可）

把下面整段作为新窗口第一条消息发给模型（并附上你从 IDA 贴出的伪代码/反汇编片段）：

---

你在一个仓库 `e:\\汉化\\项目\\GalaxyAngel Model Tool` 里工作（PowerShell）。目标：把 SCN0 文件格式做到和 SCN1 一样严谨解析，必须以 IDA 反汇编/伪代码为准，禁止全文件扫描/猜测；本地 hex 只用于校验汇编结论。

当前进度记录在：
- `docs/scn0.md`（已落地的结构化结论）
- `docs/scn0_ida_todo.md`（待逆向的 TODO / 需要从 IDA 抽取的关键逻辑）

已实现：`src/ScnParser.Scn0.cs` 严格按结构解析 SCN0 顶层与 mesh blob；目前只支持两种 decl（0x112 stride32、0x52 stride28），其他 decl 会抛异常以避免几何拉伸。

你的任务：
1) 读取 `docs/scn0_ida_todo.md` 和 `docs/scn0.md`，理解当前已确定的 SCN0 结构。
2) 结合我接下来粘贴的 IDA 伪代码/反汇编片段，补齐 `decl_bitfield -> 顶点布局` 的完整映射，并实现到 `src/ScnParser.Scn0.cs`（新增支持的 decl 必须来自汇编证据）。
3) 确认 subset 贴图名为空时的材质/贴图绑定来源（extra table / auto table / 资源字典等），并同样按汇编落地，不要猜。
4) 用 `in/sc06` 与 `in/ob101` 的样本跑 `tools/ScnCli` 验证输出稳定；构建 `dotnet build -c Release` 通过。

下面是 IDA 片段（我会继续粘贴）：
---

