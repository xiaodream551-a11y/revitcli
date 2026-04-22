# Schedule Command Design Spec

## Overview

为 revitcli 新增 `schedule` 命令族，让 AI agent 能按需从 Revit 模型提取结构化表格数据（CSV/JSON），也能在 Revit 里创建持久化明细表视图（ViewSchedule），可选放到图纸上。

**设计原则：**

- 声明式命令行为主，profile 模板为辅
- `export` 只读不修改模型，`create` 在事务内写入模型
- 第一版不做分组/汇总/公式列，agent 拿原始数据自己聚合
- 复用现有基础设施（ElementFilter 语法、MatchesPattern、类别映射）

## CLI 命令接口

### schedule list

列出模型中已有的 ViewSchedule。

```bash
revitcli schedule list
revitcli schedule list --output json
```

### schedule export

提取明细表数据，不修改模型。两种模式：

```bash
# 模式一：临时组装（按类别 + 字段）
revitcli schedule export --category Doors \
  --fields "Fire Rating,Width,Height,Level" \
  --filter "Fire Rating != ''" \
  --sort "Level" \
  --output csv

revitcli schedule export --category Walls --fields all --output json

# 模式二：导出已有明细表
revitcli schedule export --name "既有门明细表" --output csv

# 模式三：使用 profile 模板
revitcli schedule export --template door-schedule --output csv

# 模板 + 命令行覆盖（命令行参数优先）
revitcli schedule export --template door-schedule --fields "Fire Rating,Width" --output csv
```

### schedule create

在 Revit 里创建 ViewSchedule，可选放到图纸。

```bash
revitcli schedule create --category Doors \
  --fields "Fire Rating,Width,Height,Level" \
  --name "Door Fire Rating Schedule"

# 创建 + 放图纸
revitcli schedule create --category Rooms \
  --fields "Number,Name,Area,Level" \
  --name "Room Schedule" \
  --place-on-sheet "A5*"

# 使用模板
revitcli schedule create --template door-schedule
```

### 参数说明

| 参数               | 适用命令       | 说明                                               |
| ------------------ | -------------- | -------------------------------------------------- |
| `--category`       | export, create | 元素类别（Doors, Walls, Rooms 等）                 |
| `--fields`         | export, create | 逗号分隔的参数名列表，`all` 表示全部               |
| `--filter`         | export, create | 过滤表达式，复用现有 ElementFilter 语法            |
| `--sort`           | export, create | 排序字段名                                         |
| `--sort-desc`      | export, create | 降序排序                                           |
| `--name`           | export, create | export: 已有 ViewSchedule 名称; create: 新建视图名 |
| `--template`       | export, create | 从 .revitcli.yml 的 schedules 节读取模板           |
| `--output`         | list, export   | 输出格式: table, json, csv（默认 table）           |
| `--place-on-sheet` | create         | 图纸匹配模式（支持通配符），不传则不放             |

### 互斥规则

- `export`: `--category` 和 `--name` 互斥（临时组装 vs 导出已有）
- `--template` 提供基础值，命令行参数可覆盖
- `create` 必须提供 `--name`（ViewSchedule 显示名）

## API 端点

```
GET  /api/schedules                    → ScheduleInfo[]
POST /api/schedules/export             → ScheduleData
POST /api/schedules/create             → ScheduleCreateResult
```

所有响应使用现有 `ApiResponse<T>` 统一包装。

## DTO 设计

### 请求

```csharp
public class ScheduleExportRequest
{
    // 模式一：临时组装
    public string? Category { get; set; }
    public List<string>? Fields { get; set; }
    public string? Filter { get; set; }
    public string? Sort { get; set; }
    public bool SortDescending { get; set; }

    // 模式二：导出已有明细表
    public string? ExistingName { get; set; }
}

public class ScheduleCreateRequest
{
    public string Category { get; set; }
    public List<string>? Fields { get; set; }
    public string? Filter { get; set; }
    public string? Sort { get; set; }
    public bool SortDescending { get; set; }
    public string Name { get; set; }
    public string? PlaceOnSheet { get; set; }
}
```

### 响应

```csharp
public class ScheduleInfo
{
    public long Id { get; set; }
    public string Name { get; set; }
    public string Category { get; set; }
    public int FieldCount { get; set; }
    public int RowCount { get; set; }
}

public class ScheduleData
{
    public List<string> Columns { get; set; }
    public List<Dictionary<string, string>> Rows { get; set; }
    public int TotalRows { get; set; }
}

public class ScheduleCreateResult
{
    public long ViewId { get; set; }
    public string Name { get; set; }
    public int FieldCount { get; set; }
    public int RowCount { get; set; }
    public string? PlacedOnSheet { get; set; }
}
```

### DTO 设计要点

- `ScheduleData.Rows` 用 `Dictionary<string, string>` 而非 `string[]`，agent 按列名取值不依赖顺序
- 两种请求模式互斥：`ExistingName` 有值则忽略 `Category/Fields`
- `ScheduleCreateResult.PlacedOnSheet` 返回实际放置的图纸编号，null 表示未放

## Addin 端实现

### IRevitOperations 新增接口

```csharp
Task<ScheduleInfo[]> ListSchedulesAsync();
Task<ScheduleData> ExportScheduleAsync(ScheduleExportRequest request);
Task<ScheduleCreateResult> CreateScheduleAsync(ScheduleCreateRequest request);
```

### ListSchedules

```
FilteredElementCollector → OfClass(ViewSchedule)
→ 过滤掉 IsTemplate 和内部明细表（如 <Revision Schedule>）
→ 映射为 ScheduleInfo
```

### ExportSchedule

**路径 A — 导出已有明细表：**

```
按 ExistingName 找到 ViewSchedule
→ ViewSchedule.GetTableData().GetSectionData(SectionType.Body)
→ 遍历行列，GetCellText(row, col) 逐格取值
→ 返回 ScheduleData
```

**路径 B — 临时组装（不创建视图）：**

```
按 Category 解析 BuiltInCategory（复用现有类别映射）
→ FilteredElementCollector 查元素
→ 按 Fields 读取每个元素的指定参数（复用现有 ReadVisibleParameters 逻辑）
→ 按 Filter 过滤（复用现有 ElementFilter）
→ 按 Sort 排序
→ 限制 MaxRows（2000）防止内存溢出
→ 组装为 ScheduleData
```

路径 B 纯内存操作，不创建 ViewSchedule，不修改模型。

### CreateSchedule

```
Transaction("RevitCLI Create Schedule") {
  1. ViewSchedule.CreateSchedule(doc, categoryId)
  2. ViewSchedule.Definition.GetSchedulableFields() 获取可用字段
  3. 遍历 request.Fields:
     - 按名称匹配 SchedulableField
     - 匹配不到 → 抛异常，事务 rollback，返回明确错误
     - schedule.Definition.AddField(field)
  4. 若有 Filter:
     - 解析为 ScheduleFilter
     - schedule.Definition.AddFilter(filter)
  5. 若有 Sort:
     - 找到对应 FieldId
     - schedule.Definition.AddSortGroupField(new ScheduleSortGroupField(fieldId))
  6. 若有 PlaceOnSheet:
     - 找到匹配的 Sheet（复用现有 MatchesPattern）
     - ScheduleSheetInstance.Create(doc, sheetId, scheduleId, XYZ location)
     - 位置：固定偏移（左上角 + margin），第一版不做智能排版
  tx.Commit()
}
→ 返回 ScheduleCreateResult
```

### 实现要点

- **字段匹配**：按 `SchedulableField.GetName(doc)` 匹配 agent 传入的字段名。匹配不到时返回明确错误（"Field 'Xxx' not found for category Doors. Available: ..."），不静默跳过
- **类别映射**：复用现有 `QueryElementsAsync` 的类别名 → `BuiltInCategory` 映射
- **图纸放置**：复用现有 `MatchesPattern` 做图纸通配符匹配
- **事务安全**：create 全部操作在单一事务内，任何步骤失败整体 rollback
- **MaxRows 保护**：export 路径 B 限制 2000 行，超出截断并在 ScheduleData 中标记 TotalRows（真实总数）

## Profile 模板

`.revitcli.yml` 新增 `schedules:` 节：

```yaml
schedules:
  door-schedule:
    category: Doors
    fields: [Fire Rating, Width, Height, Level, Mark]
    filter: "Fire Rating != ''"
    sort: Level
    name: "门防火等级明细表"

  room-schedule:
    category: Rooms
    fields: [Number, Name, Area, Level, Department]
    sort: Number
    name: "房间明细表"

  wall-quantities:
    category: Walls
    fields: [Type, Length, Area, Volume, Level]
    sort: Level
    name: "墙体工程量表"
```

**覆盖规则**：命令行参数 > 模板值 > 默认值。与现有 `checks:` 节同构。

`ProfileLoader.Merge` 需扩展以支持 `schedules:` 节的继承合并（按名称整体替换，与 checks/exports/publish 一致）。

## 新增文件清单

```
shared/RevitCli.Shared/
  ├── ScheduleExportRequest.cs
  ├── ScheduleCreateRequest.cs
  ├── ScheduleInfo.cs
  ├── ScheduleData.cs
  └── ScheduleCreateResult.cs

src/RevitCli/Commands/
  └── ScheduleCommand.cs

src/RevitCli.Addin/Handlers/
  └── ScheduleController.cs

tests/RevitCli.Tests/Commands/
  └── ScheduleCommandTests.cs
```

## 修改文件清单

```
shared/RevitCli.Shared/IRevitOperations.cs          — 加 3 个方法
src/RevitCli.Addin/Services/RealRevitOperations.cs   — 实现 3 个方法
src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs — placeholder 实现
src/RevitCli.Addin/Server/ApiServer.cs               — 注册 ScheduleController
src/RevitCli/Program.cs                              — 注册 schedule 命令
src/RevitCli/Profile/ProjectProfile.cs               — 加 Schedules 字典属性
src/RevitCli/Profile/ProfileLoader.cs                — Merge 扩展 schedules 节
src/RevitCli/Client/RevitClient.cs                   — 加 3 个 client 方法
```

## 错误处理

| 场景                     | 处理                                                                                      |
| ------------------------ | ----------------------------------------------------------------------------------------- |
| Category 不存在          | 400: "Unknown category 'Xxx'. Available: Doors, Walls, ..."                               |
| Field 不存在             | 400: "Field 'Xxx' not found for category Doors. Available: Fire Rating, Width, ..."       |
| ExistingName 找不到      | 400: "Schedule 'Xxx' not found. Use 'revitcli schedule list' to see available schedules." |
| 无活动文档               | 400: "No active document is open."                                                        |
| 同名 ViewSchedule 已存在 | 400: "A schedule named 'Xxx' already exists. Use a different --name."                     |
| Sheet 匹配不到           | 400: "No sheets matching pattern 'A5\*'."                                                 |
| 元素数超 MaxRows         | 正常返回截断数据，TotalRows 标记真实总数，CLI 输出警告                                    |
| 事务失败                 | 500: rollback + 返回 Revit 错误信息                                                       |
| Filter 语法错误          | 400: 复用现有 ElementFilter.Parse 的错误信息                                              |

## 不做的事（v1 scope out）

- 分组/小计/总计
- 公式列/计算字段
- 多类别合并明细表
- 智能图纸排版（自动找空位）
- 修改已有 ViewSchedule 的定义
- 导出为 Excel (.xlsx)
