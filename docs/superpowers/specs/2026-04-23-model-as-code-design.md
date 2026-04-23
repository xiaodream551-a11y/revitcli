# Model-as-Code Design Spec

## Overview

为 revitcli 增加一组以 **snapshot 为基础的"版本化"能力**,把 Revit 模型的语义状态按 category 序列化成可 diff 的 JSON,并在此之上构建两个高价值上层应用:

1. **`revitcli snapshot` + `revitcli diff`** — 取模型当前语义快照,与历史快照对比差量(周会汇报 / PR 描述 / 回溯)。
2. **`revitcli publish --since`** — 基于快照差量决定哪些图纸需要重出(增量 publish,避免 50 张图全量重跑)。
3. **`revitcli import FILE.csv`** — 从 CSV 批量写回参数,匹配规则声明式(Excel ↔ Revit 双向同步)。

产品定位:把 "BIMOps runner" 升级到 "**Git for Revit Models**",填上竞品(Dynamo / pyRevit / Ideate)没覆盖的 CLI + 版本化生态位。

**设计原则**

- 三个能力分期交付,每期独立可用、独立发版;不一锅端。
- 所有新命令复用现有 CLI / Addin / shared 分层和 `RevitBridge` 主线程机制,不引入新的架构层。
- snapshot 是**纯只读**快照(不修改模型),import 走现有 `set` 的 transaction 路径。
- DTO 用 `schemaVersion` 字段版本化,跨版本显式报错而不是瞎对。

---

## Goals / Non-goals

### Goals

- 取全模型或指定 category 的语义快照,stable hash 可 diff。
- diff 输出支持 table / json / markdown 三种格式,markdown 可直接贴 PR。
- publish 能基于 snapshot 差量决定 sheet 重出范围,默认传递到 view 内容级。
- CSV 批量写参数,支持 dry-run 预览,支持自动编码探测(UTF-8/GBK)。
- 保持向后兼容:现有 profile / 命令 / 返回格式不变;新能力都是增量追加。

### Non-goals(v1 不做)

- 几何级 hash(形状 diff、位置 diff)— 通过参数已能侧面反映。
- 模型 binary diff(.rvt)— 无意义,用 snapshot JSON 代替。
- Web UI / Dashboard — 留到独立 spec。
- 跨模型协调(建筑/结构/机电多 .rvt 联合 diff)— 单模型做稳了再谈。
- Revit Link 模型内容下钻 — 只 snapshot 主模型。
- Workshare / Cloud sync — 需要 BIM 360/ACC 对接,另行设计。
- Auto-fix playbooks — 独立 spec。

---

## 关键决策(已拍板)

| 决策 | 选项 | 定论 | 理由 |
|---|---|---|---|
| snapshot 数据结构 | 平铺 / 按 category 分组 / 事件日志 | **按 category 分组** | diff 算法天然 group-by;`--summary-only` 可秒级 |
| hash 覆盖范围 | 参数 / 参数+几何 / 参数+几何+族定义 | **只 hash 参数 + 基础字段** | 几何变动必反映到"长度/面积"参数 |
| 命令 surface | 三合一 `version *` / 各自 top-level | **snapshot/diff/import top-level + publish 扩展 `--since` flag** | snapshot/diff 是通用基础设施;publish 是已有扩展;import 是输入操作 |
| 增量 publish 粒度 | sheet 元数据 / sheet + view 内容 | **默认 content 模式;`--since-mode meta` 退回轻量** | 实际改动多在元素/view,sheet title 改动少 |
| CSV match 策略 | ElementId / 任意参数 | **`--match-by <param>`(典型 Mark)** | ElementId 协作模型里不稳定,Mark 是业务键 |
| baseline 文件管理 | 自动更新 / 显式更新 | **profile `incremental: true` 时自动;CLI `--update-baseline` 显式** | 失败的 publish 不得静默写坏 baseline |
| 失败语义 | 中止 / 继续 | **import 继续,最后汇总;publish 失败保留旧 baseline** | 批处理语义 |
| CSV encoding | 强制 / 自动探测 | **BOM 探测 → UTF-8 → GBK 回退** | 国内 Excel 存 CSV 默认 GBK |
| schema 版本化 | 无 / `schemaVersion` | **`schemaVersion: 1` 必填** | diff 跨版本报错 |
| 分期 | 一锅端 / 分 phase | **三 phase 三 PR(v1.1 / v1.2 / v1.3)** | 每 phase 独立可用 |

---

## 架构

```
┌────────────────────────────────────────────────┐
│           revitcli (CLI, net8.0)                │
│                                                 │
│  snapshot  diff  publish --since   import       │
│      │      │        │               │          │
│      │      └─ 纯 CLI ──────┐        │          │
│      ▼                       ▼        ▼          │
│  ModelSnapshot         SnapshotDiffer  CsvMapper │
│   (shared DTO)          (pure C#)     (CLI only) │
│                                       │          │
│                                       └──→ 复用现有 set endpoint
└──────┼───────────────────────────────────────────┘
       │ HTTP  POST /api/snapshot
       ▼
┌────────────────────────────────────────────────┐
│       revitcli.addin (in Revit process)         │
│                                                 │
│  SnapshotController  → IRevitOperations         │
│                       → RealRevitOperations     │
│                         .CaptureSnapshotAsync   │
│                       (via RevitBridge → Revit) │
└────────────────────────────────────────────────┘
```

### 文件改动清单

```
新增  shared/RevitCli.Shared/ModelSnapshot.cs
新增  shared/RevitCli.Shared/SnapshotRequest.cs
新增  shared/RevitCli.Shared/SnapshotDiff.cs
改    shared/RevitCli.Shared/IRevitOperations.cs       + Task<ModelSnapshot> CaptureSnapshotAsync(SnapshotRequest)

新增  src/RevitCli.Addin/Handlers/SnapshotController.cs
改    src/RevitCli.Addin/Server/ApiServer.cs           + controller 注册
改    src/RevitCli.Addin/Services/RealRevitOperations.cs     + CaptureSnapshotAsync 实现
改    src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs + 测试桩

改    src/RevitCli/Client/RevitClient.cs               + CaptureSnapshotAsync HTTP 方法

新增  src/RevitCli/Commands/SnapshotCommand.cs
新增  src/RevitCli/Commands/DiffCommand.cs
新增  src/RevitCli/Commands/ImportCommand.cs
改    src/RevitCli/Commands/PublishCommand.cs          + --since 逻辑
改    src/RevitCli/Commands/CliCommandCatalog.cs       + 3 个新命令注册
改    src/RevitCli/Commands/CompletionsCommand.cs      + 补全

改    src/RevitCli/Profile/ProjectProfile.cs           + PublishPipeline.Incremental / BaselinePath / SinceMode
改    src/RevitCli/Profile/ProfileLoader.cs            (如果继承合并逻辑需要调整)

新增  src/RevitCli/Output/SnapshotDiffer.cs            (pure C#,算 diff)
新增  src/RevitCli/Output/CsvParser.cs                 (BOM + GBK 探测)
新增  src/RevitCli/Output/DiffRenderer.cs              (table/json/markdown)

tests/RevitCli.Tests/Commands/SnapshotCommandTests.cs        (新,~15 个)
tests/RevitCli.Tests/Commands/DiffCommandTests.cs            (新,~10 个)
tests/RevitCli.Tests/Commands/ImportCommandTests.cs          (新,~12 个)
tests/RevitCli.Tests/Commands/PublishCommandTests.cs         (新或扩 existing,+5 --since 场景)
tests/RevitCli.Tests/Output/SnapshotDifferTests.cs           (新,~10 个)
tests/RevitCli.Tests/Output/CsvParserTests.cs                (新,~8 个)
tests/RevitCli.Addin.Tests/Integration/ProtocolTests.cs      (改,+ snapshot endpoint 测试)
```

---

## 数据模型

### ModelSnapshot(shared DTO,schema v1)

```csharp
public class ModelSnapshot
{
    public int SchemaVersion { get; set; } = 1;
    public string TakenAt { get; set; } = "";      // ISO 8601 UTC, e.g. "2026-04-23T10:00:00Z"
    public SnapshotRevit Revit { get; set; } = new();
    public SnapshotModel Model { get; set; } = new();
    public Dictionary<string, List<SnapshotElement>> Categories { get; set; } = new();
    public List<SnapshotSheet> Sheets { get; set; } = new();
    public List<SnapshotSchedule> Schedules { get; set; } = new();
    public SnapshotSummary Summary { get; set; } = new();
}

public class SnapshotRevit
{
    public string Version = "";         // e.g. "2026"
    public string Document = "";        // e.g. "revit_cli"
    public string DocumentPath = "";    // e.g. "D:\桌面\revit_cli.rvt"
}

public class SnapshotModel
{
    public long SizeBytes;              // 0 if unavailable
    public string FileHash = "";        // SHA256 of .rvt file bytes if accessible, "" otherwise
}

public class SnapshotElement
{
    public long Id;
    public string Name = "";
    public string TypeName = "";
    public Dictionary<string, string> Parameters = new();
    public string Hash = "";
}

public class SnapshotSheet
{
    public string Number = "";
    public string Name = "";
    public long ViewId;
    public List<long> PlacedViewIds = new();
    public Dictionary<string, string> Parameters = new();
    public string MetaHash = "";        // sheet 本身(不含 placed views). 填充于 P1.
    public string ContentHash = "";     // MetaHash + 所有 PlacedViewIds 上可见元素的 hash 聚合.
                                         // 填充于 P2(publish --since content mode 需要). P1 阶段保留空字符串.
}

public class SnapshotSchedule
{
    public long Id;
    public string Name = "";
    public string Category = "";        // 明细表所属类别(从 ScheduleData.Columns 或 definition 取)
    public int RowCount;
    public string Hash = "";            // schedule definition + stable-sorted rows hash
}

public class SnapshotSummary
{
    public Dictionary<string, int> ElementCounts = new();  // per category
    public int SheetCount;
    public int ScheduleCount;
}
```

### Hash 函数

所有 hash 统一用 SHA256,十六进制小写,**截断前 16 字符**(存储压缩 + 对日常 diff 够用,冲突概率 2^-64)。

**Element hash**(`SnapshotElement.Hash`):

```
content = string.Join("\n", new[] {
    $"id={Id}",
    $"name={Name}",
    $"typeName={TypeName}",
    // 参数按 key 排序后 "key=value" 连接,key 和 value 的换行符转义为 \\n
    string.Join("\n", Parameters.OrderBy(k => k.Key, StringComparer.Ordinal)
                                 .Select(kv => $"{EscapeNewlines(kv.Key)}={EscapeNewlines(kv.Value)}"))
});
hash = SHA256(content).ToHex().Substring(0, 16);
```

**Sheet MetaHash**:同上公式,用 `Number + Name + ViewId + 排序后 Parameters`。

**Sheet ContentHash**:
```
content = MetaHash + "\n" + string.Join("\n",
    allPlacedViews.SelectMany(v => v.visibleElementHashes).OrderBy(h => h, StringComparer.Ordinal));
hash = SHA256(content).ToHex().Substring(0, 16);
```

"可见元素"定义:`FilteredElementCollector(doc, viewId).WhereElementIsNotElementType()`,过滤出在此 view 作用域内的非类型元素。

**Schedule hash**:
```
content = $"category={Category}\nname={Name}" + "\n" + string.Join("\n", columns) 
          + "\n" + string.Join("\n", rows.Select(r => string.Join("|", columns.Select(c => r.GetValueOrDefault(c, "")))));
hash = SHA256(content).ToHex().Substring(0, 16);
```

rows 不重新排序(明细表本身有 sort rule,改变 rule 应被识别为变动)。

### SnapshotRequest(CLI → addin)

```csharp
public class SnapshotRequest
{
    // null = 使用默认集合;空列表 = 完全不 snapshot 元素(只 sheets/schedules)
    public List<string>? IncludeCategories { get; set; }
    public bool IncludeSheets { get; set; } = true;
    public bool IncludeSchedules { get; set; } = true;
    public bool SummaryOnly { get; set; } = false;
}
```

默认 `IncludeCategories` 集合:`walls, doors, windows, rooms, floors, roofs, stairs, columns, structural_columns, ceilings, furniture, levels`(与 `CategoryAliases` 一致 minus 重复项)。

### SnapshotDiff

```csharp
public class SnapshotDiff
{
    public int SchemaVersion { get; set; } = 1;
    public string From { get; set; } = "";        // "a.json" 或来源描述
    public string To { get; set; } = "";
    public Dictionary<string, CategoryDiff> Categories { get; set; } = new();
    public CategoryDiff Sheets { get; set; } = new();
    public CategoryDiff Schedules { get; set; } = new();
    public DiffSummary Summary { get; set; } = new();
    public List<string> Warnings { get; set; } = new();  // e.g. "DocumentPath mismatch"
}

public class CategoryDiff
{
    public List<AddedItem> Added = new();        // { Id/Number, Name, Category }
    public List<RemovedItem> Removed = new();
    public List<ModifiedItem> Modified = new();
}

public class ModifiedItem
{
    public long Id;                              // 或 Sheet.Number 作为 stable key
    public string Key = "";                      // 人类可读:category:name 或 sheet:number
    public Dictionary<string, ParamChange> Changed = new();
    public string? OldHash;
    public string? NewHash;
}

public class ParamChange
{
    public string From = "";
    public string To = "";
}

public class DiffSummary
{
    public Dictionary<string, CategoryCount> PerCategory = new();  // { "walls": { Added:1, Removed:0, Modified:3 } }
    public CategoryCount Sheets = new();
    public CategoryCount Schedules = new();
}

public class CategoryCount { public int Added, Removed, Modified; }
```

### Diff 算法(伪代码)

```
function diff(A: ModelSnapshot, B: ModelSnapshot) -> SnapshotDiff:
    assert A.SchemaVersion == B.SchemaVersion, "schema mismatch"
    if A.Revit.DocumentPath != B.Revit.DocumentPath:
        warnings.add("baseline is a different document")
    
    result = new SnapshotDiff()
    
    # Element-level
    for category in union(A.Categories.keys, B.Categories.keys):
        aById = dict((e.Id, e) for e in A.Categories.get(category, []))
        bById = dict((e.Id, e) for e in B.Categories.get(category, []))
        catDiff = new CategoryDiff()
        
        for id in bById.keys - aById.keys:
            catDiff.Added.append({Id: id, Name: bById[id].Name, ...})
        for id in aById.keys - bById.keys:
            catDiff.Removed.append({Id: id, Name: aById[id].Name, ...})
        for id in aById.keys & bById.keys:
            if aById[id].Hash != bById[id].Hash:
                catDiff.Modified.append(computeParamDiff(aById[id], bById[id]))
        
        result.Categories[category] = catDiff
    
    # Sheets — similar, key on Number (not Id,因为 sheets 可能 recreate)
    result.Sheets = diffSheets(A.Sheets, B.Sheets)
    
    # Schedules — key on Id
    result.Schedules = diffSchedules(A.Schedules, B.Schedules)
    
    result.Summary = buildSummary(result)
    return result
```

---

## CLI Surface

### `revitcli snapshot`

```
revitcli snapshot [OPTIONS]

Options:
  --output FILE              Write JSON to file (default: stdout)
  --categories LIST          Comma-separated category list (default: built-in set)
  --no-sheets                Skip sheets section
  --no-schedules             Skip schedules section
  --summary-only             Only output Summary section (fast path)
  --verbose                  Print progress to stderr
  --help
```

返回码:0 = ok;1 = Revit 未连接 / 无活动文档 / 写文件失败。

### `revitcli diff`

```
revitcli diff FROM TO [OPTIONS]

  FROM, TO                  Snapshot JSON file paths. Use "-" for stdin (one of them).

Options:
  --output table|json|markdown     Default: table
  --categories LIST                Limit diff to these categories
  --severity added|removed|modified|all   Default: all
  --report FILE                    Write to file (format inferred from extension .md/.json)
  --max-rows N                     Table/markdown: rows per category (default: 20)
  --help
```

返回码:0 = 有或无差异都是 0(这是查询型命令);1 = 文件读不了 / schema mismatch。

### `revitcli publish` — 新增 flags

```
revitcli publish [NAME] [OPTIONS]

Added options:
  --since FILE               Incremental: only re-export sheets whose content changed since this snapshot
  --since-mode content|meta  How to detect change (default: content)
                              - content: sheet meta + placed views' element hashes
                              - meta:    only sheet own parameters
  --update-baseline          After successful publish, write new snapshot to --since path
  (existing options preserved)
```

行为:

- 如果 profile 里 `incremental: true` 且没传 `--since`,CLI 自动读 `.revitcli/last-publish.json`(或 profile 的 `baselinePath`)作为 baseline。
- 如果 profile 没 incremental,也没 `--since` 传入 → 行为与当前一样(全量 publish)。
- Publish 开始前先 snapshot 当前状态到临时文件,diff baseline,选择要 publish 的 sheets。
- 成功 publish 后,如果 `--update-baseline` 或 profile `incremental: true`:将临时 snapshot 写到 baseline 路径。
- **失败处理**:任一 sheet publish 失败 → 不更新 baseline,warning 告诉用户"baseline 保留在 XX,下次仍从此 baseline 出发"。

### `revitcli import`

```
revitcli import FILE.csv [OPTIONS]

Required:
  --category CAT             Revit category (walls, doors, 墙, 门, etc.)
  --match-by PARAM           Parameter name linking CSV row to Revit element (e.g. Mark)

Optional:
  --map "csv:param,..."      Explicit CSV-column → Revit-parameter mapping.
                              Default: CSV column names == Revit parameter names.
                              Example: --map "锁具型号:锁具,耐火:耐火等级"
  --dry-run                  Preview changes without committing
  --on-missing error|warn|skip   CSV row with no Revit match. Default: warn
  --on-duplicate error|first|all   Revit has multiple matches. Default: error
  --encoding utf-8|gbk|auto  Default: auto (BOM → UTF-8 → GBK fallback)
  --batch-size N             Commit transaction every N rows (default: 100)
  --help
```

返回码:0 = 全部成功或 dry-run;1 = 任一关键错误(CSV parse、category 不存在、--match-by 列缺失);2 = 部分行失败(dry-run 不触发此码)。

示例:

```bash
# doors.csv: Mark,锁具型号,耐火等级
revitcli import doors.csv --category doors --match-by Mark --dry-run
revitcli import doors.csv --category doors --match-by Mark
revitcli import doors.csv --category doors --match-by Mark \
  --map "耐火等级:Fire Rating" \
  --on-missing skip
```

---

## Profile Extensions

`.revitcli.yml` 扩展字段(全部新字段可选,缺省行为 = 现有行为):

```yaml
version: 1

publish:
  default:
    precheck: default
    incremental: true                          # 新字段,默认 false
    baselinePath: .revitcli/last-publish.json  # 新字段,默认即此路径
    sinceMode: content                          # 新字段,content|meta,默认 content
    presets: [publish-dwg]

snapshot:                                        # 新顶层字段,完全可选
  default:
    categories: [walls, doors, windows, sheets, schedules]
    includeSchedules: true
    includeSheets: true
```

`ProjectProfile.cs` 的 `PublishPipeline` 类加三个 nullable 字段;`ProjectProfile` 加 `Snapshot` dict(`Dictionary<string, SnapshotPreset>`)。

`extends:` 继承时,新字段遵循现有 "child replaces parent entry wholesale" 规则。

---

## 典型工作流

### A. 周会汇报

```bash
# 周一基线
revitcli snapshot --output snap-week20.json

# 周五看变动,贴进周报
revitcli snapshot --output snap-today.json
revitcli diff snap-week20.json snap-today.json --output markdown > week20-changes.md
```

### B. 增量出图

```bash
# 首次:profile 里 incremental: true
revitcli snapshot --output .revitcli/last-publish.json

# 之后每次出图都只出变动过的
revitcli publish           # 读 profile incremental → 自动比 baseline → 只出 diff 的 sheets
```

### C. Excel 批量填锁具

```csv
Mark,锁具型号,耐火等级
W01,YALE-500,甲级
W02,YALE-500,乙级
```

```bash
revitcli import doors.csv --category doors --match-by Mark --dry-run
revitcli import doors.csv --category doors --match-by Mark
```

---

## 边界与错误矩阵

| 场景 | 处理 | 退出码 |
|---|---|---|
| snapshot 时 Revit 未打开文档 | Error: "No active document" | 1 |
| snapshot 时 Revit 不可达 | Error: "Cannot reach Revit server. Run 'revitcli doctor'." | 1 |
| diff 两个不同 SchemaVersion | Error: "Schema mismatch: from=1, to=2. Regenerate snapshots." | 1 |
| diff 两个不同 DocumentPath | Warning(不阻塞),写入 SnapshotDiff.Warnings | 0 |
| diff 输入文件不是合法 JSON | Error: "Invalid snapshot JSON at <path>: <details>" | 1 |
| publish `--since FILE` 不存在 | Error: "Baseline not found: <path>. Run 'revitcli snapshot --output <path>' first." | 1 |
| publish 有 sheet 失败 | 完成其他 sheets,退出码 1,不更新 baseline,警告提示 | 1 |
| publish --update-baseline 但有失败 | baseline 不更新,warning 说明 | 1 |
| import CSV 文件不存在 | Error | 1 |
| import `--match-by X` 但 CSV 没有 X 列 | Error: 列出 CSV 实际列名 | 1 |
| import CSV 某行 match-by 在 Revit 没对应 | 按 `--on-missing`:error/warn/skip | 1/0/0 |
| import Revit 有多个 match | 按 `--on-duplicate`:error/first/all | 1/0/0 |
| import 某行 set 失败(参数只读/锁定/权限) | 记录,继续下一行,最后汇总 | 2 |
| import CSV 编码探测都失败 | Error,建议 `--encoding gbk` | 1 |
| 任一命令 Revit bridge timeout | Error: "Revit main thread did not respond in 60s." | 1 |

---

## 性能

- 目标:5k 元素 + 50 sheets + 16 schedules 的中型模型,snapshot < 10 秒。
- 关键瓶颈:`FilteredElementCollector` 遍历 + `element.GetOrderedParameters()` 每元素读所有参数。
- 优化手段(v1 包含):
  - `--summary-only` 不读参数,秒级。
  - 默认 category 集合限定(不扫 900+ BuiltInCategory)。
  - schedule 数据读取复用现有 `ExportScheduleAsync` 逻辑。
- **非目标**:并发(Revit API 主线程锁死),缓存(snapshot 本来就是缓存产物)。
- 首次实现后在你的实际项目跑一次,超过 30 秒再考虑分批 / 流式。

---

## Testing Strategy

### 单元测试(`tests/RevitCli.Tests/`,目标 +50 个)

| 文件 | 覆盖 | 用例数 |
|---|---|---|
| `Commands/SnapshotCommandTests.cs` | 构造 request、写文件、stdout 输出、错误 | ~15 |
| `Commands/DiffCommandTests.cs` | 两个手写 snapshot → added/removed/modified 断言 | ~10 |
| `Commands/ImportCommandTests.cs` | CSV parse + mapping + match + dry-run + 错误分支 | ~12 |
| `Commands/PublishCommandTests.cs`(扩现有) | `--since` sheet selection 逻辑 | +5 |
| `Output/SnapshotDifferTests.cs` | Diff 算法,hash 稳定性,category 过滤 | ~10 |
| `Output/CsvParserTests.cs` | BOM、转义、多行值、GBK、auto 探测 | ~8 |
| `Output/DiffRendererTests.cs` | table/json/markdown 输出快照比对 | ~5 |

所有测试遵循现有 pattern:`FakeHttpHandler` + `StringWriter`,不联网、不需要 Revit。

### Addin 协议测试

改 `tests/RevitCli.Addin.Tests/Integration/ProtocolTests.cs`:

- `PlaceholderRevitOperations.CaptureSnapshotAsync` 返回 hardcoded fixture。
- 加 `Snapshot_RoundTrip` 测试:POST `/api/snapshot` → 反序列化 `ModelSnapshot` → 断言 fixture 匹配。

### 手工 end-to-end(每 phase 结束)

- **Phase 1**: snapshot → 改某墙 Mark → snapshot again → diff 看到 modified 记录中文正常。
- **Phase 2**: snapshot → 改一张 sheet 的 title → publish --since → 只有那张 sheet 被导出。
- **Phase 3**: 写 doors.csv → import dry-run → 正式 import → query 确认参数写入。

---

## Phase Plan

三 phase 独立 PR / 独立发版。

| Phase | 范围 | 预估 | 版本 |
|---|---|---|---|
| **P1** | snapshot + diff 基础设施(ModelSnapshot / SnapshotDiffer / SnapshotController / 元素 hash + sheet MetaHash / schedule hash)。SnapshotSheet.ContentHash 保留字段但留空 | 3-5 天 | v1.1 |
| **P2** | ContentHash 计算(view 展开元素聚合);publish `--since` + `--since-mode content\|meta` + baseline 管理 + profile incremental 字段 | 2-3 天(复用 P1) | v1.2 |
| **P3** | import csv(独立,无新 endpoint) | 3-4 天 | v1.3 |

每个 phase 结束条件:

- P1:`revitcli snapshot` + `revitcli diff` 能在真 Revit 上跑通;CLI 单测 + 协议测试齐全;CHANGELOG 更新;tag v1.1。
- P2:`revitcli publish --since` 在真 Revit 上跑通,baseline 自动更新验证;P1 测试不回归;tag v1.2。
- P3:`revitcli import doors.csv` 在真 Revit 上跑通,dry-run + 真跑 + 中文/英文参数都验证;tag v1.3。

**本 spec 先只为 P1 出实施计划**(writing-plans skill);P2 / P3 各自等 P1 落地后再开 spec + plan。

---

## Risks & Assumptions

### 假设

1. **Revit API `sheet.GetAllPlacedViews()`** 能准确返回 sheet 上所有 viewport 对应的 view id。**P2 开发前需验证**(P1 的 SnapshotSheet 只填 PlacedViewIds,不做聚合 hash;如果此 API 不存在或返回不完整,P2 需要改用 viewport 遍历)。
2. **`FilteredElementCollector(doc, viewId)` in view 作用域**能取到 view 里可见的非类型元素。**P2 开发前需要验证**。
3. **Schedule rows 的顺序在 Revit 内部稳定**(两次 `GetCellText` 返回同样序列)。**P1 测试必须包含 `Snapshot_Idempotent_OnSameModel`** 验证;如果不稳定,在 hash 前显式排序。
4. 中型模型(~5k 元素)snapshot 在 10 秒内完成。**P1 做完第一件事就拿实际项目跑性能**。

### 风险

| 风险 | 缓解 |
|---|---|
| Schedule hash 不 idempotent(同模型两次 snapshot hash 不同) | Phase 1 测试必须包含 `Snapshot_Idempotent_OnSameModel` |
| CSV 大文件(万行)性能差 | `--batch-size` 参数,每批一次 transaction;测试覆盖 1k / 5k 行 |
| import 在 workshare 模型里写参数,元素被别人锁定 | `--on-locked error|skip` flag(v1 先不做,observe 后再加) |
| hash 冲突(16 字符 = 64bit) | 项目规模 < 10 万元素,冲突概率 < 10^-10,忽略 |
| CLI snapshot + publish --since 构成竞态(snapshot 期间有人动模型) | Revit bridge 单线程,snapshot 内部是原子的;外部并发由用户规避 |

---

## Out of Scope

本 spec 不覆盖以下(留独立 spec 或未来版本):

- Auto-fix playbooks(规则驱动批量修值)
- Family management commands(`family ls / purge / validate`)
- 静态 HTML dashboard(脱 Revit 可看报告)
- 规则包从 URL/git 继承的 profile 扩展
- 跨模型(多 .rvt)联合 snapshot / diff
- .rvt 文件 binary diff(technically 不可行)
- 性能极限优化(流式、缓存层)

---

## Open Questions(实施时逐项解决)

- P1 做完后,snapshot JSON 的典型大小是多少?需要 gzip 吗?(目前设计:不 gzip,便于 `cat` 和 `jq`。)
- `baselinePath` 在 profile `extends:` 中被子 profile 覆盖时的语义?(跟现有 "wholesale replace" 一致,不破坏约定。)
- `--since` 给相对路径时,基于什么目录?(决定:基于 profile 所在目录,跟 `exports.outputDir` 一致。)
- P2 引入 ContentHash 时对已有 P1 产出的 schema=1 snapshot 如何兼容?(P2 前决策:bump schemaVersion → 2 并要求重建 baseline;或保留 schema=1 + 对空 ContentHash 退回 meta 模式。)
