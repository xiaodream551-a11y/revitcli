# RevitCli 最短落地路线图

## 目标

把项目从“可编译、可演示的占位原型”推进到“在真实 Revit 环境里可用的最小工具”。

首版只追求一条真实可回归的链路：

`doctor -> status -> query --id -> query <category> --filter -> set --dry-run -> set`

不要以“命令数量更多”作为进度标准，要以“真实 Revit 环境里能跑通且失败路径可控”作为进度标准。

## 首版完成定义

满足以下条件即可认为 `v0.1` 可落地：

- `revitcli doctor` 能明确区分配置问题、Add-in 未加载、Revit 未启动、无文档、连接失败。
- `revitcli status` 返回真实 `RevitVersion`、文档名、文档路径或“无文档”状态。
- `revitcli query --id <id>` 能返回真实元素，而不是占位数据。
- `revitcli query <category> --filter "<expr>"` 至少支持最小过滤子集，并对非法表达式稳定报错。
- `revitcli set ... --dry-run` 能返回真实受影响元素和旧值/新值预览。
- `revitcli set ...` 在事务内真实修改参数，失败时不会留下半改状态。
- `Create()` 和 `ExecuteAsync()` 在参数校验、错误码、错误信息上行为一致。

## 明确砍掉的范围

以下内容不进入最短落地路径：

- `audit` 的真实实现
- 复杂查询语言
- 多 Revit 版本同时兼容
- 一次性支持全部参数类型
- 一次性支持全部导出格式
- 任何仅提升“演示观感”但不提升真实可用性的工作

## 实施顺序

### 1. 让 Add-in 真正运行在 Revit 里

先解决“真实宿主环境可运行”问题，不要继续依赖当前的开发态占位桥接。

需要修改的重点文件：

- `src/RevitCli.Addin/RevitCli.Addin.csproj`
- `src/RevitCli.Addin/RevitCliApp.cs`
- `src/RevitCli.Addin/Bridge/RevitBridge.cs`
- `src/RevitCli.Addin/RevitCli.addin`

要完成的事情：

- 把 Add-in 项目改成真实可用的目标框架方案，不再只停留在 `net8.0` 开发态。
- 引入 Revit API 引用，并把 `RevitCliApp` 接到真实的 `IExternalApplication` 生命周期。
- 把 `RevitBridge` 从“直接执行回调”改成真正通过 `ExternalEvent` 回到 Revit 主线程。
- 启动时写入真实 `server.json`，退出时清理。
- 先只支持一个明确版本，建议先锁 `Revit 2025`，不要一开始追 `2024/2025/2026` 全覆盖。

完成标准：

- Revit 启动后 Add-in 自动加载。
- CLI 能通过 `server.json` 找到真实端口。
- Revit 关闭后 CLI 不会误判为在线。

### 2. 打通真实 `status`

这是第一条必须替换掉占位实现的接口。

需要修改的重点文件：

- `src/RevitCli.Addin/Handlers/StatusController.cs`
- `src/RevitCli/Client/RevitClient.cs`
- `src/RevitCli/Commands/StatusCommand.cs`
- `src/RevitCli/Commands/DoctorCommand.cs`

要完成的事情：

- 用真实 Revit API 返回版本、当前文档名、文档路径。
- 无文档时返回明确状态，不要伪造数据。
- Revit 未运行、Add-in 未加载、连接超时、端口文件陈旧时都要稳定报错。
- `doctor` 输出应能帮助定位是“客户端问题”还是“Add-in 端问题”。

完成标准：

- `status` 在有文档和无文档两个场景都能正确返回。
- Revit 未启动时返回稳定错误，不挂起，不抛未处理异常。
- `doctor` 可以解释失败原因，而不是只说连接失败。

### 3. 打通真实 `query --id`

不要一上来做复杂查询，先做最确定的一条路径。

需要修改的重点文件：

- `src/RevitCli.Addin/Handlers/ElementsController.cs`
- `shared/RevitCli.Shared/ElementInfo.cs`
- `src/RevitCli/Commands/QueryCommand.cs`
- `src/RevitCli/Output/OutputFormatter.cs`

要完成的事情：

- 用 `doc.GetElement(new ElementId(id))` 查询单个元素。
- 明确元素不存在、ID 非法、文档为空时的返回行为。
- 返回稳定 DTO，不要把 Revit 内部类型直接泄露到 CLI。
- 对输出格式保持一致：table/json/csv 三种都要稳定。

完成标准：

- `query --id` 可以查到真实元素。
- 非法 ID 不崩溃。
- 元素不存在时返回可脚本处理的失败信息。

### 4. 再做 `query <category> --filter`

把查询范围扩展到最小可用集合，但不要过度设计。

需要修改的重点文件：

- `src/RevitCli.Addin/Handlers/ElementsController.cs`
- `shared/RevitCli.Shared/ElementFilter.cs`
- `shared/RevitCli.Shared/ElementInfo.cs`
- `tests/RevitCli.Tests/ElementFilterTests.cs`
- `tests/RevitCli.Tests/Commands/QueryCommandTests.cs`

要完成的事情：

- 支持最小过滤子集：`=`, `!=`, `>`, `<`, `>=`, `<=`。
- 过滤表达式非法时直接报错，不要静默返回空集合。
- 做最小类别映射，例如 `walls`、`doors` 这样的常见类别别名。
- 参数缺失、参数类型不匹配、不可比较值都要有稳定处理。

完成标准：

- 合法过滤表达式返回真实结果。
- 垃圾表达式、空表达式、未知类别都不崩。
- `Create()` 和 `ExecuteAsync()` 的参数校验一致。

### 5. 先做 `set --dry-run`

不要先碰真实写入，先把“命中哪些元素、将改成什么”做准。

需要修改的重点文件：

- `src/RevitCli.Addin/Handlers/SetController.cs`
- `shared/RevitCli.Shared/SetRequest.cs`
- `shared/RevitCli.Shared/SetResult.cs`
- `src/RevitCli/Commands/SetCommand.cs`
- `tests/RevitCli.Tests/Commands/SetCommandTests.cs`

要完成的事情：

- 复用真实查询逻辑找出目标元素。
- 对参数不存在、参数只读、值类型不匹配、目标为空做稳定处理。
- 返回可读的 preview，包括元素 ID、名称、旧值、新值。
- 对大批量修改预留保护阈值。

完成标准：

- `--dry-run` 返回真实 preview。
- 参数不存在或不可写时不会误报成功。
- 非法输入不会直接进入真实写入路径。

### 6. 最后做真实 `set`

真实修改必须以“可回滚、可失败、可解释”为前提。

需要修改的重点文件：

- `src/RevitCli.Addin/Handlers/SetController.cs`
- `src/RevitCli/Commands/SetCommand.cs`
- `src/RevitCli/Client/RevitClient.cs`
- `tests/RevitCli.Addin.Tests/Integration/EndToEndTests.cs`

要完成的事情：

- 所有真实修改都放进事务。
- 对单个元素失败和整批失败的策略要明确，优先保证一致性。
- 失败时返回清晰原因，不要吞异常。
- 只读参数、类型转换失败、文档状态变化、事务冲突都要测试。

完成标准：

- 修改成功时受影响数量准确。
- 修改失败时不会留下半改状态。
- 断连、异常、事务失败时 CLI 错误码和提示稳定。

### 7. 只有在前面稳定后，才考虑 `export`

`export` 不应该进入第一阶段，除非前面的查询和修改链路已经稳定。

如果要做：

- 先只做一种格式
- 先只做同步或最小异步模型
- 先把错误路径和进度查询做稳

如果前面链路还不稳定，就把 `export` 明确推迟到 `v0.2`。

## 测试策略

不要再把“测试全绿”当成真实可用的证据。当前很多测试在验证 placeholder 行为，价值有限。

应该补的测试分层：

- CLI 单元测试：参数校验、输出格式、错误码、交互和非交互行为一致性。
- Add-in 逻辑测试：过滤、参数读取、参数写入、事务失败、异常传播。
- Windows + Revit 集成回归：至少有一套可以重复执行的 smoke 流程。

必须覆盖的失败路径：

- `null`
- 空字符串
- 非法 ID
- 未知类别
- 非法过滤表达式
- 网络断开
- Add-in 未加载
- Revit 未启动
- 无文档
- 参数不存在
- 参数只读
- 类型转换失败
- 事务失败
- `Create()` 和 `ExecuteAsync()` 行为不一致

## 文档和发布收口

在真实功能落地前，不要让文档继续超前。

需要处理的事情：

- 重写 `README.md`，删掉尚未真实支持的演示输出。
- 更新 `.gitignore`，补 `.DS_Store`。
- CI 只验证当前真实支持的平台和能力，不要营造“全平台可用”的假象。
- 写最小安装文档，明确只支持哪个 Revit 版本、哪个系统、有哪些已知限制。

## Claude Code 执行约束

如果把这份文档交给 Claude Code，应该明确要求它遵守以下约束：

- 不要扩 scope，不要顺手做 `audit`。
- 不要先做漂亮输出，先做真实接口。
- 每做一步都先检查失败路径，不要只实现 happy path。
- 每改一个命令，都要比对 `Create()` 和 `ExecuteAsync()`。
- 每接一个真实接口，都要先删掉对应 placeholder 假设。
- 如果真实 Revit 环境还没跑通，不要继续往上层命令堆功能。

## 回归脚本

只有下面这组命令在真实环境里可重复通过，才能进入下一阶段：

```bash
revitcli doctor
revitcli status
revitcli query --id 12345
revitcli query walls --filter "Mark = W-01"
revitcli set walls --filter "Mark = W-01" --param "Comments" --value "test" --dry-run
revitcli set walls --filter "Mark = W-01" --param "Comments" --value "test"
```

## 一句话原则

先把一个真实纵向切片做通，再谈功能扩展；先把失败路径做稳，再谈用户体验；先收窄支持范围，再谈多版本和高级能力。
