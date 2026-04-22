# Schedule Command Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add `schedule list/export/create` commands so AI agents can extract tabular data and create ViewSchedule views in Revit.

**Architecture:** New `schedule` command with 3 subcommands backed by 3 new API endpoints. DTOs in Shared, controller in Addin, client methods in CLI. Profile templates reuse the existing `.revitcli.yml` pattern.

**Tech Stack:** System.CommandLine, EmbedIO WebApi, System.Text.Json, Revit API (ViewSchedule, SchedulableField, ScheduleSheetInstance), xUnit

---

### Task 1: Shared DTOs

**Files:**

- Create: `shared/RevitCli.Shared/ScheduleInfo.cs`
- Create: `shared/RevitCli.Shared/ScheduleData.cs`
- Create: `shared/RevitCli.Shared/ScheduleExportRequest.cs`
- Create: `shared/RevitCli.Shared/ScheduleCreateRequest.cs`
- Create: `shared/RevitCli.Shared/ScheduleCreateResult.cs`

- [ ] **Step 1: Create ScheduleInfo.cs**

```csharp
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class ScheduleInfo
{
    [JsonPropertyName("id")]
    public long Id { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("fieldCount")]
    public int FieldCount { get; set; }

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }
}
```

- [ ] **Step 2: Create ScheduleData.cs**

```csharp
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class ScheduleData
{
    [JsonPropertyName("columns")]
    public List<string> Columns { get; set; } = new();

    [JsonPropertyName("rows")]
    public List<Dictionary<string, string>> Rows { get; set; } = new();

    [JsonPropertyName("totalRows")]
    public int TotalRows { get; set; }
}
```

- [ ] **Step 3: Create ScheduleExportRequest.cs**

```csharp
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class ScheduleExportRequest
{
    [JsonPropertyName("category")]
    public string? Category { get; set; }

    [JsonPropertyName("fields")]
    public List<string>? Fields { get; set; }

    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    [JsonPropertyName("sort")]
    public string? Sort { get; set; }

    [JsonPropertyName("sortDescending")]
    public bool SortDescending { get; set; }

    [JsonPropertyName("existingName")]
    public string? ExistingName { get; set; }
}
```

- [ ] **Step 4: Create ScheduleCreateRequest.cs**

```csharp
using System.Collections.Generic;
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class ScheduleCreateRequest
{
    [JsonPropertyName("category")]
    public string Category { get; set; } = "";

    [JsonPropertyName("fields")]
    public List<string>? Fields { get; set; }

    [JsonPropertyName("filter")]
    public string? Filter { get; set; }

    [JsonPropertyName("sort")]
    public string? Sort { get; set; }

    [JsonPropertyName("sortDescending")]
    public bool SortDescending { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("placeOnSheet")]
    public string? PlaceOnSheet { get; set; }
}
```

- [ ] **Step 5: Create ScheduleCreateResult.cs**

```csharp
using System.Text.Json.Serialization;

namespace RevitCli.Shared;

public class ScheduleCreateResult
{
    [JsonPropertyName("viewId")]
    public long ViewId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("fieldCount")]
    public int FieldCount { get; set; }

    [JsonPropertyName("rowCount")]
    public int RowCount { get; set; }

    [JsonPropertyName("placedOnSheet")]
    public string? PlacedOnSheet { get; set; }
}
```

- [ ] **Step 6: Commit**

```bash
git add shared/RevitCli.Shared/ScheduleInfo.cs shared/RevitCli.Shared/ScheduleData.cs shared/RevitCli.Shared/ScheduleExportRequest.cs shared/RevitCli.Shared/ScheduleCreateRequest.cs shared/RevitCli.Shared/ScheduleCreateResult.cs
git commit -m "feat(shared): add schedule DTOs"
```

---

### Task 2: IRevitOperations Interface + Placeholder

**Files:**

- Modify: `shared/RevitCli.Shared/IRevitOperations.cs`
- Modify: `src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs`

- [ ] **Step 1: Add 3 methods to IRevitOperations**

Add these lines after the existing `RunAuditAsync` method in `shared/RevitCli.Shared/IRevitOperations.cs`:

```csharp
    Task<ScheduleInfo[]> ListSchedulesAsync();
    Task<ScheduleData> ExportScheduleAsync(ScheduleExportRequest request);
    Task<ScheduleCreateResult> CreateScheduleAsync(ScheduleCreateRequest request);
```

- [ ] **Step 2: Add placeholder implementations**

Add these methods to the end of `PlaceholderRevitOperations` class in `src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs`:

```csharp
    public Task<ScheduleInfo[]> ListSchedulesAsync()
    {
        return Task.FromResult(new[]
        {
            new ScheduleInfo { Id = 1001, Name = "Door Schedule", Category = "Doors", FieldCount = 5, RowCount = 12 },
            new ScheduleInfo { Id = 1002, Name = "Room Schedule", Category = "Rooms", FieldCount = 4, RowCount = 8 }
        });
    }

    public Task<ScheduleData> ExportScheduleAsync(ScheduleExportRequest request)
    {
        return Task.FromResult(new ScheduleData
        {
            Columns = new List<string> { "Name", "Level", "Type" },
            Rows = new List<Dictionary<string, string>>(),
            TotalRows = 0
        });
    }

    public Task<ScheduleCreateResult> CreateScheduleAsync(ScheduleCreateRequest request)
    {
        return Task.FromResult(new ScheduleCreateResult
        {
            ViewId = 2001,
            Name = request.Name,
            FieldCount = request.Fields?.Count ?? 0,
            RowCount = 0,
            PlacedOnSheet = null
        });
    }
```

- [ ] **Step 3: Commit**

```bash
git add shared/RevitCli.Shared/IRevitOperations.cs src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs
git commit -m "feat(shared): add schedule methods to IRevitOperations + placeholder"
```

---

### Task 3: RevitClient — 3 New HTTP Methods

**Files:**

- Modify: `src/RevitCli/Client/RevitClient.cs`
- Test: `tests/RevitCli.Tests/Client/RevitClientTests.cs`

- [ ] **Step 1: Write failing tests**

Add to `tests/RevitCli.Tests/Client/RevitClientTests.cs`:

```csharp
    [Fact]
    public async Task ListSchedulesAsync_ReturnsSchedules()
    {
        var schedules = new[] { new ScheduleInfo { Id = 1, Name = "Door Schedule", Category = "Doors" } };
        var response = ApiResponse<ScheduleInfo[]>.Ok(schedules);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });

        var result = await client.ListSchedulesAsync();

        Assert.True(result.Success);
        Assert.Single(result.Data!);
        Assert.Equal("Door Schedule", result.Data![0].Name);
    }

    [Fact]
    public async Task ExportScheduleAsync_ReturnsData()
    {
        var data = new ScheduleData
        {
            Columns = new List<string> { "Name", "Level" },
            Rows = new List<Dictionary<string, string>>
            {
                new() { ["Name"] = "Door-01", ["Level"] = "Level 1" }
            },
            TotalRows = 1
        };
        var response = ApiResponse<ScheduleData>.Ok(data);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });

        var result = await client.ExportScheduleAsync(new ScheduleExportRequest { Category = "Doors" });

        Assert.True(result.Success);
        Assert.Equal(2, result.Data!.Columns.Count);
        Assert.Single(result.Data.Rows);
    }

    [Fact]
    public async Task CreateScheduleAsync_ReturnsResult()
    {
        var createResult = new ScheduleCreateResult { ViewId = 100, Name = "Test", FieldCount = 3, RowCount = 5 };
        var response = ApiResponse<ScheduleCreateResult>.Ok(createResult);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });

        var result = await client.CreateScheduleAsync(new ScheduleCreateRequest { Category = "Doors", Name = "Test" });

        Assert.True(result.Success);
        Assert.Equal(100, result.Data!.ViewId);
    }
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RevitCli.Tests --filter "FullyQualifiedName~RevitClientTests.ListSchedules|FullyQualifiedName~RevitClientTests.ExportSchedule|FullyQualifiedName~RevitClientTests.CreateSchedule" -v n`

Expected: Build error — `ListSchedulesAsync`, `ExportScheduleAsync`, `CreateScheduleAsync` don't exist on `RevitClient`.

- [ ] **Step 3: Implement 3 client methods**

Add to `src/RevitCli/Client/RevitClient.cs` after the existing `AuditAsync` method:

```csharp
    public async Task<ApiResponse<ScheduleInfo[]>> ListSchedulesAsync()
    {
        try
        {
            var url = "/api/schedules";
            var response = await _http.GetAsync(url);
            var json = await SendAndRead(response, "GET", url);
            return JsonSerializer.Deserialize<ApiResponse<ScheduleInfo[]>>(json, JsonOptions)!;
        }
        catch (HttpRequestException ex)
        {
            if (Verbose) Console.Error.WriteLine($"[HTTP] Connection failed: {ex.Message}");
            return ApiResponse<ScheduleInfo[]>.Fail("Revit is not running or plugin is not loaded.");
        }
        catch (Exception ex) when (ex is TaskCanceledException or JsonException)
        {
            return ApiResponse<ScheduleInfo[]>.Fail($"Communication error: {ex.Message}");
        }
    }

    public async Task<ApiResponse<ScheduleData>> ExportScheduleAsync(ScheduleExportRequest request)
    {
        try
        {
            var url = "/api/schedules/export";
            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");
            var response = await _http.PostAsync(url, content);
            var json = await SendAndRead(response, "POST", url);
            return JsonSerializer.Deserialize<ApiResponse<ScheduleData>>(json, JsonOptions)!;
        }
        catch (HttpRequestException ex)
        {
            if (Verbose) Console.Error.WriteLine($"[HTTP] Connection failed: {ex.Message}");
            return ApiResponse<ScheduleData>.Fail("Revit is not running or plugin is not loaded.");
        }
        catch (Exception ex) when (ex is TaskCanceledException or JsonException)
        {
            return ApiResponse<ScheduleData>.Fail($"Communication error: {ex.Message}");
        }
    }

    public async Task<ApiResponse<ScheduleCreateResult>> CreateScheduleAsync(ScheduleCreateRequest request)
    {
        try
        {
            var url = "/api/schedules/create";
            var content = new StringContent(
                JsonSerializer.Serialize(request),
                Encoding.UTF8,
                "application/json");
            var response = await _http.PostAsync(url, content);
            var json = await SendAndRead(response, "POST", url);
            return JsonSerializer.Deserialize<ApiResponse<ScheduleCreateResult>>(json, JsonOptions)!;
        }
        catch (HttpRequestException ex)
        {
            if (Verbose) Console.Error.WriteLine($"[HTTP] Connection failed: {ex.Message}");
            return ApiResponse<ScheduleCreateResult>.Fail("Revit is not running or plugin is not loaded.");
        }
        catch (Exception ex) when (ex is TaskCanceledException or JsonException)
        {
            return ApiResponse<ScheduleCreateResult>.Fail($"Communication error: {ex.Message}");
        }
    }
```

- [ ] **Step 4: Run tests to verify they pass**

Run: `dotnet test tests/RevitCli.Tests --filter "FullyQualifiedName~RevitClientTests.ListSchedules|FullyQualifiedName~RevitClientTests.ExportSchedule|FullyQualifiedName~RevitClientTests.CreateSchedule" -v n`

Expected: All 3 tests PASS.

- [ ] **Step 5: Commit**

```bash
git add src/RevitCli/Client/RevitClient.cs tests/RevitCli.Tests/Client/RevitClientTests.cs
git commit -m "feat(cli): add schedule HTTP client methods + tests"
```

---

### Task 4: Profile — ScheduleTemplate + Merge

**Files:**

- Modify: `src/RevitCli/Profile/ProjectProfile.cs`
- Modify: `src/RevitCli/Profile/ProfileLoader.cs`

- [ ] **Step 1: Add ScheduleTemplate class and Schedules dictionary to ProjectProfile.cs**

Add to `src/RevitCli/Profile/ProjectProfile.cs` after the existing `PublishPipeline` class:

```csharp
public class ScheduleTemplate
{
    [YamlMember(Alias = "category")]
    public string Category { get; set; } = "";

    [YamlMember(Alias = "fields")]
    public List<string>? Fields { get; set; }

    [YamlMember(Alias = "filter")]
    public string? Filter { get; set; }

    [YamlMember(Alias = "sort")]
    public string? Sort { get; set; }

    [YamlMember(Alias = "sortDescending")]
    public bool SortDescending { get; set; }

    [YamlMember(Alias = "name")]
    public string? Name { get; set; }
}
```

Add this property to the `ProjectProfile` class, after the `Publish` property:

```csharp
    [YamlMember(Alias = "schedules")]
    public Dictionary<string, ScheduleTemplate> Schedules { get; set; } = new();
```

- [ ] **Step 2: Add schedules merge to ProfileLoader.Merge**

In `src/RevitCli/Profile/ProfileLoader.cs`, inside the `Merge` method, add after the "Merge publish by name" block:

```csharp
        // Merge schedules by name
        foreach (var kvp in baseProfile.Schedules)
            merged.Schedules[kvp.Key] = kvp.Value;
        foreach (var kvp in child.Schedules)
            merged.Schedules[kvp.Key] = kvp.Value;
```

- [ ] **Step 3: Commit**

```bash
git add src/RevitCli/Profile/ProjectProfile.cs src/RevitCli/Profile/ProfileLoader.cs
git commit -m "feat(profile): add ScheduleTemplate and schedules merge support"
```

---

### Task 5: ScheduleCommand — CLI Command with Tests

**Files:**

- Create: `src/RevitCli/Commands/ScheduleCommand.cs`
- Create: `tests/RevitCli.Tests/Commands/ScheduleCommandTests.cs`
- Modify: `src/RevitCli/Commands/CliCommandCatalog.cs`

- [ ] **Step 1: Write failing tests for all 3 subcommands**

Create `tests/RevitCli.Tests/Commands/ScheduleCommandTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.Shared;
using RevitCli.Tests.Client;
using Xunit;

namespace RevitCli.Tests.Commands;

public class ScheduleCommandTests
{
    [Fact]
    public async Task List_ReturnsSchedules_PrintsTable()
    {
        var schedules = new[]
        {
            new ScheduleInfo { Id = 1, Name = "Door Schedule", Category = "Doors", FieldCount = 5, RowCount = 12 }
        };
        var response = ApiResponse<ScheduleInfo[]>.Ok(schedules);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await ScheduleCommand.ExecuteListAsync(client, "table", writer);

        Assert.Equal(0, exitCode);
        Assert.Contains("Door Schedule", writer.ToString());
    }

    [Fact]
    public async Task List_OutputJson_PrintsJson()
    {
        var schedules = new[] { new ScheduleInfo { Id = 1, Name = "Test", Category = "Walls" } };
        var response = ApiResponse<ScheduleInfo[]>.Ok(schedules);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await ScheduleCommand.ExecuteListAsync(client, "json", writer);

        Assert.Equal(0, exitCode);
        var output = writer.ToString();
        Assert.Contains("\"name\"", output.ToLower());
    }

    [Fact]
    public async Task Export_ByCategoryAndFields_ReturnsData()
    {
        var data = new ScheduleData
        {
            Columns = new List<string> { "Fire Rating", "Width" },
            Rows = new List<Dictionary<string, string>>
            {
                new() { ["Fire Rating"] = "60min", ["Width"] = "900" }
            },
            TotalRows = 1
        };
        var response = ApiResponse<ScheduleData>.Ok(data);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await ScheduleCommand.ExecuteExportAsync(
            client, "Doors", null, "Fire Rating,Width", null, null, false, "csv", null, writer);

        Assert.Equal(0, exitCode);
        Assert.Contains("Fire Rating", writer.ToString());
    }

    [Fact]
    public async Task Export_ByExistingName_ReturnsData()
    {
        var data = new ScheduleData
        {
            Columns = new List<string> { "Name" },
            Rows = new List<Dictionary<string, string>> { new() { ["Name"] = "Room A" } },
            TotalRows = 1
        };
        var response = ApiResponse<ScheduleData>.Ok(data);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await ScheduleCommand.ExecuteExportAsync(
            client, null, "Room Schedule", null, null, null, false, "json", null, writer);

        Assert.Equal(0, exitCode);
        Assert.Contains("Room A", writer.ToString());
    }

    [Fact]
    public async Task Export_NoCategoryOrName_PrintsError()
    {
        var handler = new FakeHttpHandler("{}");
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await ScheduleCommand.ExecuteExportAsync(
            client, null, null, null, null, null, false, "table", null, writer);

        Assert.Equal(1, exitCode);
        Assert.Contains("--category", writer.ToString().ToLower());
    }

    [Fact]
    public async Task Export_BothCategoryAndName_PrintsError()
    {
        var handler = new FakeHttpHandler("{}");
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await ScheduleCommand.ExecuteExportAsync(
            client, "Doors", "Existing Schedule", null, null, null, false, "table", null, writer);

        Assert.Equal(1, exitCode);
        Assert.Contains("mutually exclusive", writer.ToString().ToLower());
    }

    [Fact]
    public async Task Create_ValidRequest_ReturnsResult()
    {
        var result = new ScheduleCreateResult { ViewId = 100, Name = "Door Schedule", FieldCount = 3, RowCount = 10 };
        var response = ApiResponse<ScheduleCreateResult>.Ok(result);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await ScheduleCommand.ExecuteCreateAsync(
            client, "Doors", "Fire Rating,Width,Height", null, null, false, "Door Schedule", null, null, writer);

        Assert.Equal(0, exitCode);
        Assert.Contains("Door Schedule", writer.ToString());
    }

    [Fact]
    public async Task Create_MissingName_PrintsError()
    {
        var handler = new FakeHttpHandler("{}");
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await ScheduleCommand.ExecuteCreateAsync(
            client, "Doors", "Width", null, null, false, null!, null, null, writer);

        Assert.Equal(1, exitCode);
        Assert.Contains("--name", writer.ToString().ToLower());
    }

    [Fact]
    public async Task Create_MissingCategory_PrintsError()
    {
        var handler = new FakeHttpHandler("{}");
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await ScheduleCommand.ExecuteCreateAsync(
            client, null!, "Width", null, null, false, "Test", null, null, writer);

        Assert.Equal(1, exitCode);
        Assert.Contains("--category", writer.ToString().ToLower());
    }

    [Fact]
    public async Task Export_ServerDown_PrintsError()
    {
        var handler = new FakeHttpHandler(throwException: true);
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await ScheduleCommand.ExecuteExportAsync(
            client, "Doors", null, "Width", null, null, false, "csv", null, writer);

        Assert.Equal(1, exitCode);
        Assert.Contains("not running", writer.ToString().ToLower());
    }
}
```

- [ ] **Step 2: Run tests to verify they fail**

Run: `dotnet test tests/RevitCli.Tests --filter "FullyQualifiedName~ScheduleCommandTests" -v n`

Expected: Build error — `ScheduleCommand` class doesn't exist.

- [ ] **Step 3: Implement ScheduleCommand.cs**

Create `src/RevitCli/Commands/ScheduleCommand.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Output;
using RevitCli.Profile;
using RevitCli.Shared;
using Spectre.Console;

namespace RevitCli.Commands;

public static class ScheduleCommand
{
    private static readonly JsonSerializerOptions PrettyJson = new() { WriteIndented = true };

    public static Command Create(RevitClient client)
    {
        var command = new Command("schedule", "Manage and export Revit schedules");
        command.AddCommand(CreateListCommand(client));
        command.AddCommand(CreateExportCommand(client));
        command.AddCommand(CreateCreateCommand(client));
        return command;
    }

    private static Command CreateListCommand(RevitClient client)
    {
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json");
        var cmd = new Command("list", "List existing schedules in the Revit model") { outputOpt };
        cmd.SetHandler(async (output) =>
        {
            if (!ConsoleHelper.IsInteractive)
            {
                Environment.ExitCode = await ExecuteListAsync(client, output, Console.Out);
                return;
            }
            Environment.ExitCode = await ExecuteListAsync(client, output, Console.Out);
        }, outputOpt);
        return cmd;
    }

    private static Command CreateExportCommand(RevitClient client)
    {
        var categoryOpt = new Option<string?>("--category", "Element category (Doors, Walls, Rooms, etc.)");
        var nameOpt = new Option<string?>("--name", "Name of existing schedule to export");
        var fieldsOpt = new Option<string?>("--fields", "Comma-separated field names, or 'all'");
        var filterOpt = new Option<string?>("--filter", "Filter expression");
        var sortOpt = new Option<string?>("--sort", "Sort by field name");
        var sortDescOpt = new Option<bool>("--sort-desc", () => false, "Sort descending");
        var outputOpt = new Option<string>("--output", () => "table", "Output format: table, json, csv");
        var templateOpt = new Option<string?>("--template", "Schedule template name from .revitcli.yml");

        var cmd = new Command("export", "Export schedule data from the Revit model")
        {
            categoryOpt, nameOpt, fieldsOpt, filterOpt, sortOpt, sortDescOpt, outputOpt, templateOpt
        };

        cmd.SetHandler(async (category, name, fields, filter, sort, sortDesc, output, template) =>
        {
            Environment.ExitCode = await ExecuteExportAsync(
                client, category, name, fields, filter, sort, sortDesc, output, template, Console.Out);
        }, categoryOpt, nameOpt, fieldsOpt, filterOpt, sortOpt, sortDescOpt, outputOpt, templateOpt);

        return cmd;
    }

    private static Command CreateCreateCommand(RevitClient client)
    {
        var categoryOpt = new Option<string?>("--category", "Element category (Doors, Walls, Rooms, etc.)");
        var fieldsOpt = new Option<string?>("--fields", "Comma-separated field names, or 'all'");
        var filterOpt = new Option<string?>("--filter", "Filter expression");
        var sortOpt = new Option<string?>("--sort", "Sort by field name");
        var sortDescOpt = new Option<bool>("--sort-desc", () => false, "Sort descending");
        var nameOpt = new Option<string?>("--name", "Name for the new ViewSchedule");
        var placeOpt = new Option<string?>("--place-on-sheet", "Sheet pattern to place schedule on");
        var templateOpt = new Option<string?>("--template", "Schedule template name from .revitcli.yml");

        var cmd = new Command("create", "Create a new ViewSchedule in the Revit model")
        {
            categoryOpt, fieldsOpt, filterOpt, sortOpt, sortDescOpt, nameOpt, placeOpt, templateOpt
        };

        cmd.SetHandler(async (category, fields, filter, sort, sortDesc, name, placeOnSheet, template) =>
        {
            Environment.ExitCode = await ExecuteCreateAsync(
                client, category, fields, filter, sort, sortDesc, name, placeOnSheet, template, Console.Out);
        }, categoryOpt, fieldsOpt, filterOpt, sortOpt, sortDescOpt, nameOpt, placeOpt, templateOpt);

        return cmd;
    }

    internal static async Task<int> ExecuteListAsync(RevitClient client, string outputFormat, TextWriter output)
    {
        var result = await client.ListSchedulesAsync();
        if (!result.Success)
        {
            await output.WriteLineAsync($"Error: {result.Error}");
            return 1;
        }

        var schedules = result.Data!;
        if (schedules.Length == 0)
        {
            await output.WriteLineAsync("No schedules found in the model.");
            return 0;
        }

        if (outputFormat.Equals("json", StringComparison.OrdinalIgnoreCase))
        {
            await output.WriteLineAsync(JsonSerializer.Serialize(schedules, PrettyJson));
        }
        else
        {
            await output.WriteLineAsync($"{"Name",-30} {"Category",-15} {"Fields",-8} {"Rows",-8} {"Id",-10}");
            await output.WriteLineAsync(new string('-', 71));
            foreach (var s in schedules)
                await output.WriteLineAsync($"{s.Name,-30} {s.Category,-15} {s.FieldCount,-8} {s.RowCount,-8} {s.Id,-10}");
        }

        return 0;
    }

    internal static async Task<int> ExecuteExportAsync(
        RevitClient client, string? category, string? existingName,
        string? fields, string? filter, string? sort, bool sortDesc,
        string outputFormat, string? templateName, TextWriter output)
    {
        var request = new ScheduleExportRequest { SortDescending = sortDesc };

        if (templateName != null)
        {
            var template = LoadTemplate(templateName);
            if (template == null)
            {
                await output.WriteLineAsync($"Error: schedule template '{templateName}' not found in .revitcli.yml.");
                return 1;
            }
            request.Category = category ?? template.Category;
            request.Fields = ParseFields(fields) ?? template.Fields;
            request.Filter = filter ?? template.Filter;
            request.Sort = sort ?? template.Sort;
            request.ExistingName = existingName;
        }
        else
        {
            request.Category = category;
            request.Fields = ParseFields(fields);
            request.Filter = filter;
            request.Sort = sort;
            request.ExistingName = existingName;
        }

        if (request.Category == null && request.ExistingName == null)
        {
            await output.WriteLineAsync("Error: provide --category or --name (or --template).");
            return 1;
        }

        if (request.Category != null && request.ExistingName != null)
        {
            await output.WriteLineAsync("Error: --category and --name are mutually exclusive.");
            return 1;
        }

        var result = await client.ExportScheduleAsync(request);
        if (!result.Success)
        {
            await output.WriteLineAsync($"Error: {result.Error}");
            return 1;
        }

        var data = result.Data!;
        await output.WriteLineAsync(FormatScheduleData(data, outputFormat));

        if (data.TotalRows > data.Rows.Count)
            await output.WriteLineAsync($"Warning: showing {data.Rows.Count} of {data.TotalRows} total rows (truncated).");

        return 0;
    }

    internal static async Task<int> ExecuteCreateAsync(
        RevitClient client, string? category, string? fields,
        string? filter, string? sort, bool sortDesc,
        string? name, string? placeOnSheet, string? templateName, TextWriter output)
    {
        var request = new ScheduleCreateRequest { SortDescending = sortDesc };

        if (templateName != null)
        {
            var template = LoadTemplate(templateName);
            if (template == null)
            {
                await output.WriteLineAsync($"Error: schedule template '{templateName}' not found in .revitcli.yml.");
                return 1;
            }
            request.Category = category ?? template.Category;
            request.Fields = ParseFields(fields) ?? template.Fields;
            request.Filter = filter ?? template.Filter;
            request.Sort = sort ?? template.Sort;
            request.Name = name ?? template.Name ?? templateName;
            request.PlaceOnSheet = placeOnSheet;
        }
        else
        {
            request.Category = category ?? "";
            request.Fields = ParseFields(fields);
            request.Filter = filter;
            request.Sort = sort;
            request.Name = name ?? "";
            request.PlaceOnSheet = placeOnSheet;
        }

        if (string.IsNullOrWhiteSpace(request.Category))
        {
            await output.WriteLineAsync("Error: --category is required (or use --template).");
            return 1;
        }

        if (string.IsNullOrWhiteSpace(request.Name))
        {
            await output.WriteLineAsync("Error: --name is required (or use --template with a name defined).");
            return 1;
        }

        var result = await client.CreateScheduleAsync(request);
        if (!result.Success)
        {
            await output.WriteLineAsync($"Error: {result.Error}");
            return 1;
        }

        var r = result.Data!;
        await output.WriteLineAsync($"Schedule '{r.Name}' created (ViewId: {r.ViewId}, {r.FieldCount} fields, {r.RowCount} rows).");
        if (r.PlacedOnSheet != null)
            await output.WriteLineAsync($"Placed on sheet: {r.PlacedOnSheet}");

        return 0;
    }

    private static List<string>? ParseFields(string? fields)
    {
        if (string.IsNullOrWhiteSpace(fields))
            return null;
        if (fields.Equals("all", StringComparison.OrdinalIgnoreCase))
            return new List<string> { "all" };
        return fields.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static ScheduleTemplate? LoadTemplate(string name)
    {
        var profile = ProfileLoader.DiscoverAndLoad();
        if (profile == null)
            return null;
        return profile.Schedules.TryGetValue(name, out var template) ? template : null;
    }

    private static string FormatScheduleData(ScheduleData data, string format)
    {
        if (data.Rows.Count == 0)
            return "No data.";

        return format.ToLowerInvariant() switch
        {
            "json" => JsonSerializer.Serialize(data, PrettyJson),
            "csv" => FormatCsv(data),
            _ => FormatTable(data),
        };
    }

    private static string FormatCsv(ScheduleData data)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", data.Columns.Select(EscapeCsvField)));
        foreach (var row in data.Rows)
        {
            var values = data.Columns.Select(c => row.TryGetValue(c, out var v) ? v : "");
            sb.AppendLine(string.Join(",", values.Select(EscapeCsvField)));
        }
        return sb.ToString().TrimEnd();
    }

    private static string EscapeCsvField(string field)
    {
        if (field.Contains(',') || field.Contains('"') || field.Contains('\n'))
            return $"\"{field.Replace("\"", "\"\"")}\"";
        return field;
    }

    private static string FormatTable(ScheduleData data)
    {
        var sb = new System.Text.StringBuilder();
        var widths = data.Columns.Select(c =>
            Math.Max(c.Length, data.Rows.Max(r => r.TryGetValue(c, out var v) ? v.Length : 0))
        ).ToArray();

        for (int i = 0; i < data.Columns.Count; i++)
            sb.Append(data.Columns[i].PadRight(widths[i] + 2));
        sb.AppendLine();
        sb.AppendLine(new string('-', widths.Sum() + widths.Length * 2));

        foreach (var row in data.Rows)
        {
            for (int i = 0; i < data.Columns.Count; i++)
            {
                var val = row.TryGetValue(data.Columns[i], out var v) ? v : "";
                sb.Append(val.PadRight(widths[i] + 2));
            }
            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }
}
```

- [ ] **Step 4: Register schedule command in CliCommandCatalog**

In `src/RevitCli/Commands/CliCommandCatalog.cs`, add to the `TopLevelCommands` array after `("coverage", ...)`:

```csharp
        ("schedule", "Manage and export Revit schedules"),
```

Add to `InteractiveHelpEntries` after `("coverage", ...)`:

```csharp
        ("schedule list", "List existing schedules in the model"),
        ("schedule export", "Export schedule data (--category, --name, --fields, --output)"),
        ("schedule create", "Create a ViewSchedule (--category, --fields, --name)"),
```

In `CreateRootCommand`, add before the `if (includeBatchCommand)` line:

```csharp
        root.AddCommand(ScheduleCommand.Create(client));
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test tests/RevitCli.Tests --filter "FullyQualifiedName~ScheduleCommandTests" -v n`

Expected: All 10 tests PASS.

- [ ] **Step 6: Run full test suite to check for regressions**

Run: `dotnet test tests/RevitCli.Tests -v n`

Expected: All existing tests still pass.

- [ ] **Step 7: Commit**

```bash
git add src/RevitCli/Commands/ScheduleCommand.cs tests/RevitCli.Tests/Commands/ScheduleCommandTests.cs src/RevitCli/Commands/CliCommandCatalog.cs
git commit -m "feat(cli): add schedule list/export/create commands + tests"
```

---

### Task 6: ScheduleController — Addin HTTP Endpoint

**Files:**

- Create: `src/RevitCli.Addin/Handlers/ScheduleController.cs`
- Modify: `src/RevitCli.Addin/Server/ApiServer.cs`

- [ ] **Step 1: Create ScheduleController.cs**

Create `src/RevitCli.Addin/Handlers/ScheduleController.cs`:

```csharp
using System;
using System.Text.Json;
using System.Threading.Tasks;
using EmbedIO;
using EmbedIO.Routing;
using EmbedIO.WebApi;
using RevitCli.Shared;

namespace RevitCli.Addin.Handlers;

public class ScheduleController : WebApiController
{
    private readonly IRevitOperations _operations;

    public ScheduleController(IRevitOperations operations)
    {
        _operations = operations;
    }

    [Route(HttpVerbs.Get, "/schedules")]
    public async Task ListSchedules()
    {
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();

        try
        {
            var schedules = await _operations.ListSchedulesAsync();
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleInfo[]>.Ok(schedules)));
        }
        catch (InvalidOperationException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleInfo[]>.Fail(ex.Message)));
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleInfo[]>.Fail(ex.Message)));
        }
    }

    [Route(HttpVerbs.Post, "/schedules/export")]
    public async Task ExportSchedule()
    {
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();

        ScheduleExportRequest? request;
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync();
            request = JsonSerializer.Deserialize<ScheduleExportRequest>(body);
        }
        catch (JsonException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleData>.Fail($"Invalid JSON: {ex.Message}")));
            return;
        }

        if (request == null)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleData>.Fail("Request body is required")));
            return;
        }

        try
        {
            var data = await _operations.ExportScheduleAsync(request);
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleData>.Ok(data)));
        }
        catch (ArgumentException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleData>.Fail(ex.Message)));
        }
        catch (InvalidOperationException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleData>.Fail(ex.Message)));
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleData>.Fail(ex.Message)));
        }
    }

    [Route(HttpVerbs.Post, "/schedules/create")]
    public async Task CreateSchedule()
    {
        HttpContext.Response.ContentType = "application/json";
        await using var writer = HttpContext.OpenResponseText();

        ScheduleCreateRequest? request;
        try
        {
            var body = await HttpContext.GetRequestBodyAsStringAsync();
            request = JsonSerializer.Deserialize<ScheduleCreateRequest>(body);
        }
        catch (JsonException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleCreateResult>.Fail($"Invalid JSON: {ex.Message}")));
            return;
        }

        if (request == null)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleCreateResult>.Fail("Request body is required")));
            return;
        }

        try
        {
            var result = await _operations.CreateScheduleAsync(request);
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleCreateResult>.Ok(result)));
        }
        catch (ArgumentException ex)
        {
            HttpContext.Response.StatusCode = 400;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleCreateResult>.Fail(ex.Message)));
        }
        catch (InvalidOperationException ex)
        {
            HttpContext.Response.StatusCode = 409;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleCreateResult>.Fail(ex.Message)));
        }
        catch (Exception ex)
        {
            HttpContext.Response.StatusCode = 500;
            await writer.WriteAsync(JsonSerializer.Serialize(
                ApiResponse<ScheduleCreateResult>.Fail(ex.Message)));
        }
    }
}
```

- [ ] **Step 2: Register ScheduleController in ApiServer**

In `src/RevitCli.Addin/Server/ApiServer.cs`, inside `CreateServer`, add the new controller after the `AuditController` line:

```csharp
                .WithController(() => new ScheduleController(_operations)))
```

Replace the existing closing `)` on the AuditController line. The full WebApi section should look like:

```csharp
            .WithWebApi("/api", m => m
                .WithController(() => new StatusController(_operations))
                .WithController(() => new ElementsController(_operations))
                .WithController(() => new ExportController(_operations))
                .WithController(() => new SetController(_operations))
                .WithController(() => new AuditController(_operations))
                .WithController(() => new ScheduleController(_operations)))
```

- [ ] **Step 3: Commit**

```bash
git add src/RevitCli.Addin/Handlers/ScheduleController.cs src/RevitCli.Addin/Server/ApiServer.cs
git commit -m "feat(addin): add ScheduleController with 3 endpoints"
```

---

### Task 7: RealRevitOperations — Revit API Implementation

**Files:**

- Modify: `src/RevitCli.Addin/Services/RealRevitOperations.cs`

- [ ] **Step 1: Add ListSchedulesAsync**

Add this method to `RealRevitOperations` after the existing `GetExportProgressAsync`:

```csharp
    // ═══════════════════════════════════════════════════════════════
    //  Schedule operations
    // ═══════════════════════════════════════════════════════════════

    public Task<ScheduleInfo[]> ListSchedulesAsync()
    {
        return _bridge.InvokeAsync(app =>
        {
            var doc = RequireActiveDocument(app);
            var schedules = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Where(vs => !vs.IsTemplate && !vs.IsTitleblockRevisionSchedule)
                .Where(vs => !vs.Name.StartsWith("<"))
                .Select(vs =>
                {
                    var body = vs.GetTableData()?.GetSectionData(SectionType.Body);
                    return new ScheduleInfo
                    {
                        Id = ToCliElementId(vs.Id),
                        Name = vs.Name,
                        Category = vs.Definition.CategoryId != ElementId.InvalidElementId
                            ? (Category.GetCategory(doc, vs.Definition.CategoryId)?.Name ?? "")
                            : "",
                        FieldCount = vs.Definition.GetFieldCount(),
                        RowCount = body?.NumberOfRows ?? 0
                    };
                })
                .ToArray();

            return schedules;
        });
    }
```

- [ ] **Step 2: Add ExportScheduleAsync**

```csharp
    public Task<ScheduleData> ExportScheduleAsync(ScheduleExportRequest request)
    {
        if (!string.IsNullOrWhiteSpace(request.ExistingName))
            return ExportExistingSchedule(request.ExistingName);

        if (string.IsNullOrWhiteSpace(request.Category))
            throw new ArgumentException("Category is required when not exporting an existing schedule.");

        return ExportAdHocSchedule(request);
    }

    private Task<ScheduleData> ExportExistingSchedule(string scheduleName)
    {
        return _bridge.InvokeAsync(app =>
        {
            var doc = RequireActiveDocument(app);
            var schedule = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .FirstOrDefault(vs => vs.Name.Equals(scheduleName, StringComparison.OrdinalIgnoreCase));

            if (schedule == null)
                throw new ArgumentException(
                    $"Schedule '{scheduleName}' not found. Use 'revitcli schedule list' to see available schedules.");

            var tableData = schedule.GetTableData();
            var body = tableData.GetSectionData(SectionType.Body);
            var headerSection = tableData.GetSectionData(SectionType.Header);

            var columns = new List<string>();
            var fieldCount = schedule.Definition.GetFieldCount();
            for (int c = 0; c < fieldCount; c++)
            {
                var field = schedule.Definition.GetField(c);
                if (!field.IsHidden)
                    columns.Add(field.GetName());
            }

            var rows = new List<Dictionary<string, string>>();
            var visibleColIndex = 0;
            for (int r = 0; r < body.NumberOfRows; r++)
            {
                var row = new Dictionary<string, string>();
                var colIdx = 0;
                for (int c = 0; c < fieldCount; c++)
                {
                    var field = schedule.Definition.GetField(c);
                    if (field.IsHidden) continue;
                    row[columns[colIdx]] = body.GetCellText(r, c);
                    colIdx++;
                }
                if (row.Values.Any(v => !string.IsNullOrWhiteSpace(v)))
                    rows.Add(row);
            }

            return new ScheduleData
            {
                Columns = columns,
                Rows = rows,
                TotalRows = rows.Count
            };
        });
    }

    private const int ScheduleMaxRows = 2000;

    private Task<ScheduleData> ExportAdHocSchedule(ScheduleExportRequest request)
    {
        return _bridge.InvokeAsync(app =>
        {
            var doc = RequireActiveDocument(app);
            var elements = CollectElementsByCategory(doc, request.Category!);

            if (!string.IsNullOrWhiteSpace(request.Filter))
            {
                var filter = CliElementFilter.Parse(request.Filter);
                elements = elements.Where(e => MatchesFilter(e, filter)).ToList();
            }

            var fields = ResolveFields(elements, request.Fields);

            var rows = new List<Dictionary<string, string>>();
            foreach (var element in elements)
            {
                var row = new Dictionary<string, string>();
                var info = MapElement(doc, element);
                foreach (var field in fields)
                {
                    row[field] = info.Parameters.TryGetValue(field, out var v) ? v : "";
                }
                rows.Add(row);
            }

            if (!string.IsNullOrWhiteSpace(request.Sort))
            {
                var sortField = request.Sort;
                rows = request.SortDescending
                    ? rows.OrderByDescending(r => r.TryGetValue(sortField, out var v) ? v : "").ToList()
                    : rows.OrderBy(r => r.TryGetValue(sortField, out var v) ? v : "").ToList();
            }

            var totalRows = rows.Count;
            if (rows.Count > ScheduleMaxRows)
                rows = rows.Take(ScheduleMaxRows).ToList();

            return new ScheduleData
            {
                Columns = fields,
                Rows = rows,
                TotalRows = totalRows
            };
        });
    }

    private static List<Element> CollectElementsByCategory(Document doc, string categoryName)
    {
        var cat = ResolveCategoryName(categoryName);
        return new FilteredElementCollector(doc)
            .OfCategory(cat)
            .WhereElementIsNotElementType()
            .ToList();
    }

    private static List<string> ResolveFields(List<Element> elements, List<string>? requestedFields)
    {
        if (requestedFields == null || requestedFields.Count == 0)
            return new List<string> { "Name", "Level", "Type Name" };

        if (requestedFields.Count == 1 && requestedFields[0].Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            return elements
                .SelectMany(e => e.GetOrderedParameters().Select(p => p.Definition.Name))
                .Distinct()
                .OrderBy(n => n)
                .ToList();
        }

        return requestedFields;
    }

    private static bool MatchesFilter(Element element, CliElementFilter filter)
    {
        var param = element.LookupParameter(filter.Property);
        if (param == null || !param.HasValue)
            return false;

        var paramValue = param.AsValueString() ?? param.AsString() ?? "";
        return filter.Operator switch
        {
            "=" => string.Equals(paramValue, filter.Value, StringComparison.OrdinalIgnoreCase),
            "!=" => !string.Equals(paramValue, filter.Value, StringComparison.OrdinalIgnoreCase),
            ">" => double.TryParse(paramValue, out var pv) && double.TryParse(filter.Value, out var fv) && pv > fv,
            "<" => double.TryParse(paramValue, out var pv2) && double.TryParse(filter.Value, out var fv2) && pv2 < fv2,
            ">=" => double.TryParse(paramValue, out var pv3) && double.TryParse(filter.Value, out var fv3) && pv3 >= fv3,
            "<=" => double.TryParse(paramValue, out var pv4) && double.TryParse(filter.Value, out var fv4) && pv4 <= fv4,
            _ => false
        };
    }
```

Note: `ResolveCategoryName` should already exist in `RealRevitOperations` (used by `QueryElementsAsync`). If it's named differently, use the existing method name. Read the file to find the exact name.

- [ ] **Step 3: Add CreateScheduleAsync**

```csharp
    public Task<ScheduleCreateResult> CreateScheduleAsync(ScheduleCreateRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Category))
            throw new ArgumentException("Category is required.");
        if (string.IsNullOrWhiteSpace(request.Name))
            throw new ArgumentException("Name is required.");

        return _bridge.InvokeAsync(app =>
        {
            var doc = RequireActiveDocument(app);
            var categoryId = ResolveCategoryId(doc, request.Category);

            var existing = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewSchedule))
                .Cast<ViewSchedule>()
                .Any(vs => vs.Name.Equals(request.Name, StringComparison.OrdinalIgnoreCase));

            if (existing)
                throw new InvalidOperationException(
                    $"A schedule named '{request.Name}' already exists. Use a different --name.");

            using var tx = new Transaction(doc, "RevitCLI Create Schedule");
            tx.Start();
            try
            {
                var schedule = ViewSchedule.CreateSchedule(doc, categoryId);
                schedule.Name = request.Name;

                var schedulableFields = schedule.Definition.GetSchedulableFields();
                var fieldMap = new Dictionary<string, SchedulableField>(StringComparer.OrdinalIgnoreCase);
                foreach (var sf in schedulableFields)
                    fieldMap[sf.GetName(doc)] = sf;

                var fieldsToAdd = request.Fields ?? new List<string>();
                if (fieldsToAdd.Count == 1 && fieldsToAdd[0].Equals("all", StringComparison.OrdinalIgnoreCase))
                    fieldsToAdd = fieldMap.Keys.OrderBy(k => k).ToList();

                ScheduleFieldId? sortFieldId = null;
                foreach (var fieldName in fieldsToAdd)
                {
                    if (!fieldMap.TryGetValue(fieldName, out var sf))
                    {
                        var available = string.Join(", ", fieldMap.Keys.OrderBy(k => k).Take(20));
                        throw new ArgumentException(
                            $"Field '{fieldName}' not found for category {request.Category}. Available: {available}");
                    }

                    var addedField = schedule.Definition.AddField(sf);
                    if (fieldName.Equals(request.Sort, StringComparison.OrdinalIgnoreCase))
                        sortFieldId = addedField.FieldId;
                }

                if (!string.IsNullOrWhiteSpace(request.Sort) && sortFieldId != null)
                {
                    var sortGroup = new ScheduleSortGroupField(sortFieldId);
                    if (request.SortDescending)
                        sortGroup.SortOrder = ScheduleSortOrder.Descending;
                    schedule.Definition.AddSortGroupField(sortGroup);
                }

                string? placedOnSheet = null;
                if (!string.IsNullOrWhiteSpace(request.PlaceOnSheet))
                {
                    var sheets = new FilteredElementCollector(doc)
                        .OfClass(typeof(ViewSheet))
                        .Cast<ViewSheet>()
                        .Where(s => MatchesPattern(s.SheetNumber, request.PlaceOnSheet)
                                 || MatchesPattern(s.Name, request.PlaceOnSheet)
                                 || MatchesPattern(s.SheetNumber + " - " + s.Name, request.PlaceOnSheet))
                        .ToList();

                    if (sheets.Count == 0)
                        throw new ArgumentException($"No sheets matching pattern '{request.PlaceOnSheet}'.");

                    var sheet = sheets[0];
                    var location = new XYZ(0.1, 0.9, 0);
                    ScheduleSheetInstance.Create(doc, sheet.Id, schedule.Id, location);
                    placedOnSheet = $"{sheet.SheetNumber} - {sheet.Name}";
                }

                var commitStatus = tx.Commit();
                if (commitStatus != TransactionStatus.Committed)
                    throw new InvalidOperationException($"Transaction failed with status: {commitStatus}.");

                var body = schedule.GetTableData()?.GetSectionData(SectionType.Body);
                return new ScheduleCreateResult
                {
                    ViewId = ToCliElementId(schedule.Id),
                    Name = schedule.Name,
                    FieldCount = schedule.Definition.GetFieldCount(),
                    RowCount = body?.NumberOfRows ?? 0,
                    PlacedOnSheet = placedOnSheet
                };
            }
            catch
            {
                if (tx.GetStatus() == TransactionStatus.Started)
                    tx.RollBack();
                throw;
            }
        });
    }

    private static ElementId ResolveCategoryId(Document doc, string categoryName)
    {
        var builtInCat = ResolveCategoryName(categoryName);
        return new ElementId(builtInCat);
    }
```

Note: `ResolveCategoryName` and `MatchesPattern` are existing methods in `RealRevitOperations`. Verify their exact signatures by reading the file before editing.

- [ ] **Step 4: Commit**

```bash
git add src/RevitCli.Addin/Services/RealRevitOperations.cs
git commit -m "feat(addin): implement schedule list/export/create with Revit API"
```

---

### Task 8: Final Integration — Build Check + Full Test

**Files:** None new — verification only.

- [ ] **Step 1: Build the full solution**

Run: `dotnet build revitcli.sln -c Debug`

Expected: Build succeeds with 0 errors. Fix any compilation issues if present (e.g., missing `using` statements, method name mismatches with existing `RealRevitOperations` methods).

- [ ] **Step 2: Run full test suite**

Run: `dotnet test tests/RevitCli.Tests -v n`

Expected: All tests pass, including new `ScheduleCommandTests` and `RevitClientTests`.

- [ ] **Step 3: Verify CLI help output**

Run: `dotnet run --project src/RevitCli -- schedule --help`

Expected: Shows `list`, `export`, `create` subcommands with descriptions.

Run: `dotnet run --project src/RevitCli -- schedule export --help`

Expected: Shows all options: `--category`, `--name`, `--fields`, `--filter`, `--sort`, `--sort-desc`, `--output`, `--template`.

- [ ] **Step 4: Final commit with all fixes**

If any fixes were needed in steps 1-3:

```bash
git add -A
git commit -m "fix: resolve schedule integration issues"
```

- [ ] **Step 5: Tag if desired**

```bash
git tag -a v1.1.0 -m "feat: schedule command — list, export, create"
```
