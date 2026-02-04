# 开发文档：新增模型格式解析器（通用骨架）

本仓库目前把“文件识别/分发”与“具体格式解析”分离。以后新增任何新格式，都按同一套路接入，避免改 UI/导出/批处理代码。

## 1. 总体结构

- 统一入口：`src/ModelLoader.cs`
  - 负责按 `IModelFormatParser` 列表匹配并加载
  - 产出统一 `ModelLoader.LoadResult`（`Magic + Models + 可选 Index`）
- 格式插件接口：`src/Formats/IModelFormatParser.cs`
- 现有实现：
  - `src/Formats/Scn0FormatParser.cs`
  - `src/Formats/Scn1FormatParser.cs`

> UI（`ViewerForm`）与导出（`Converter`）只依赖 `ModelLoader.Load(...)` 的返回结果，不应再直接调用某个具体格式的解析入口。

## 2. 新增一个新格式：最小步骤

### 2.1 新建解析器类

在 `src/Formats/` 下新建文件，例如：

- `src/Formats/AbcFormatParser.cs`

实现接口 `IModelFormatParser`：

- `Name`：格式名（用于调试/日志）
- `CanParse(path, data, out magic)`：**快速识别**（尽量只看头部，不做扫描）
- `Load(path, data, magic)`：调用你的解析逻辑，返回 `ModelLoader.LoadResult`

### 2.2 注册到 `ModelLoader`

在 `src/ModelLoader.cs` 的 `_parsers` 数组里加入新解析器：

- 把“识别特征最明确”的格式放在靠前位置
- 如果多个格式共享相同 magic/头部，`CanParse` 要更严格（例如检查版本字段/固定表结构）

## 3. 统一输出数据模型

解析器最终要输出 `List<ScnModel>`，每个 `ScnModel` 持有一个 `ScnMesh`：

- `ScnMesh.Positions` / `Normals` / `UVs`
- `ScnMesh.Indices`
- `ScnMesh.Subsets`（可为空；为空时渲染端会按一个整体 draw）
- `ScnMesh.MaterialSets`（可为空；为空时会用默认材质/白贴图）

建议：
- “几何正确”优先；材质/贴图可以后补，但不要用猜测覆盖几何。

## 4. 逆向与严谨性要求（建议流程）

> 目标：解析必须以 IDA 反汇编/伪代码为准，禁止全文件扫描/猜字段；hex 仅用于验证汇编结论。

建议流程：

1) **先锁定 loader 入口**
   - 找到打开文件/读取流的函数
   - 确认“读取顺序”（严格按 `readU32/readCString/memcpy/skip` 的消费）

2) **把“消费顺序”落地成结构化 parser**
   - 每段都有明确 `count` / `size` / `offset`
   - 任何未知字段：照汇编读取/跳过以保证 cursor 对齐，但不要推测语义

3) **mesh blob 单独模块化**
   - 先 VB/IB/subset ranges 做到严格一致
   - 再逐步补 vertex layout（每一种 layout 都要有汇编证据）

4) **删除/禁止启发式兜底**
   - 避免“扫字符串找贴图名”“扫全文件找 VB/IB”等捷径
   - 如确实需要“工程便利”的兜底，必须做成明确的可选开关，且默认关闭（并在文档写清楚它不是严谨路径）

## 5. 调试建议

- 优先用 `tools/ScnCli` 打印结构/统计（顶点数、面数、subset 区间等）
- 需要定位渲染问题时：
  - 可用环境变量：
    - `SCN_WIREFRAME=1`：线框（只看几何）
    - `SCN_NOTEX=1`：禁用贴图采样（排除贴图影响）

## 6. 新格式窗口提示词模板（复制即可）

```
你在仓库 e:\\汉化\\项目\\GalaxyAngel Model Tool（PowerShell）里工作。
我们要研究一种“新模型格式”（我会提供样本文件/已知信息/IDA 线索）。

目标：
1) 严格按 IDA 反汇编/伪代码实现解析，禁止全文件扫描/猜字段；hex 仅用于验证汇编结论。
2) 以通用骨架接入：实现一个 IModelFormatParser（放在 src/Formats/），并注册到 src/ModelLoader.cs。
3) 输出统一的 List<ScnModel>/ScnMesh；先保证几何正确，再逐步补材质/贴图。

我将提供：
- 样本文件路径：
- magic/头部信息：
- IDA 函数名/地址与关键反编译片段：
```

