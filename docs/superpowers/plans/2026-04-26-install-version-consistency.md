# Revit 2026 Install Version Consistency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make RevitCli prove that the CLI, installed Revit 2026 Add-in, and live loaded Add-in share the expected version before a live smoke run is trusted.

**Architecture:** Keep the public CLI surface unchanged. Add small CLI-side diagnostics helpers for version parsing and manifest assembly inspection, wire them into `doctor`, keep `status` concise, and make Add-in/protocol tests use isolated `server.json` paths. Harden `scripts/install.ps1` so the source-tree developer path can publish and install the Revit 2026 Add-in consistently.

**Tech Stack:** C#/.NET 8 CLI, Revit Add-in multi-targeting (`net48`, `net8.0-windows`), xUnit, PowerShell 5.1 scripts, Autodesk Revit 2026 API DLLs from `D:\revit2026\Revit 2026`.

---

## PR Breakdown

- PR 1: CLI diagnostics core and `doctor` version gate.
- PR 2: Add-in live version source and protocol test isolation.
- PR 3: Revit 2026 installer and smoke report version metadata.
- PR 4: Final verification polish, docs, and live Revit evidence.

The current branch already contains the previous Revit 2026 real-slice hardening. Do not revert those changes. Build these tasks on top of the current worktree and keep commits scoped.

## File Structure

- Create `src/RevitCli/Diagnostics/ComponentVersion.cs`
  - Parse assembly/informational version strings.
  - Compare major/minor compatibility.
  - Render stable diagnostic text.
- Create `src/RevitCli/Diagnostics/AssemblyVersionReader.cs`
  - Read installed assembly version from a DLL path without loading it into the process.
  - Prefer parseable `FileVersionInfo.ProductVersion`, preserve SemVer build metadata, convert four-part Windows versions to `major.minor.patch`, and fall back to `AssemblyName.GetAssemblyName`.
- Modify `src/RevitCli/Commands/DoctorCommand.cs`
  - Report CLI version, installed Add-in version, live Add-in version.
  - Fail on major/minor mismatch, warn on patch/build mismatch.
  - Preserve existing Revit API, manifest, server info, and connection checks.
- Modify `src/RevitCli/Commands/StatusCommand.cs`
  - Keep output shape stable; ensure empty Add-in version is treated as a visible warning only through `doctor`.
- Modify `src/RevitCli.Addin/Services/RealRevitOperations.cs`
  - Centralize live Add-in version retrieval through `AddinVersionProvider`.
- Modify `src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs`
  - Keep placeholder protocol tests explicit; use a non-production version string that tests expect.
- Modify `src/RevitCli.Addin/Server/ApiServer.cs`
  - Keep injectable `serverInfoPath` support from the previous slice.
- Modify `tests/RevitCli.Tests/Commands/DoctorCommandTests.cs`
  - Add installed/live version success, warning, and failure tests.
- Create `tests/RevitCli.Tests/Diagnostics/ComponentVersionTests.cs`
  - Unit-test parser and comparison behavior.
- Create `tests/RevitCli.Tests/Diagnostics/AssemblyVersionReaderTests.cs`
  - Unit-test version reader against the test assembly itself and missing files.
- Modify `tests/RevitCli.Addin.Tests/Integration/ProtocolTests.cs`
  - Assert temp `server.json` path is used and real user `server.json` is untouched.
- Create `tests/RevitCli.Addin.Tests/Services/AddinVersionProviderTests.cs`
  - Assert the Add-in assembly version provider returns a parseable version.
- Modify `scripts/install.ps1`
  - Add a repo/developer mode that can publish and install Revit 2026 from the source tree.
  - Keep existing packaged ZIP mode working.
- Modify `scripts/smoke-revit2026.ps1`
  - Record CLI version, manifest path, manifest assembly path, installed Add-in version, and live Add-in version in the JSON report.

## Task 1: Add CLI Component Version Helper

**Files:**
- Create: `src/RevitCli/Diagnostics/ComponentVersion.cs`
- Create: `tests/RevitCli.Tests/Diagnostics/ComponentVersionTests.cs`

- [ ] **Step 1: Write failing parser and comparison tests**

Create `tests/RevitCli.Tests/Diagnostics/ComponentVersionTests.cs`:

```csharp
using RevitCli.Diagnostics;

namespace RevitCli.Tests.Diagnostics;

public class ComponentVersionTests
{
    [Theory]
    [InlineData("1.3.0", 1, 3, 0, "")]
    [InlineData("1.3.0+local", 1, 3, 0, "local")]
    [InlineData("1.3.0-beta.1+sha", 1, 3, 0, "sha")]
    [InlineData("v1.3.2", 1, 3, 2, "")]
    public void TryParse_AcceptsAssemblyAndInformationalVersions(
        string input, int major, int minor, int patch, string metadata)
    {
        Assert.True(ComponentVersion.TryParse(input, out var parsed));
        Assert.Equal(major, parsed.Major);
        Assert.Equal(minor, parsed.Minor);
        Assert.Equal(patch, parsed.Patch);
        Assert.Equal(metadata, parsed.Metadata);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1")]
    [InlineData("1.x.0")]
    public void TryParse_RejectsUnusableVersions(string input)
    {
        Assert.False(ComponentVersion.TryParse(input, out _));
    }

    [Theory]
    [InlineData("1.3.0", "1.3.1", VersionCompatibility.PatchMismatch)]
    [InlineData("1.3.0+local", "1.3.0", VersionCompatibility.MetadataMismatch)]
    [InlineData("1.3.0", "1.0.0", VersionCompatibility.MajorMinorMismatch)]
    [InlineData("2.0.0", "1.3.0", VersionCompatibility.MajorMinorMismatch)]
    [InlineData("1.3.0", "1.3.0", VersionCompatibility.Compatible)]
    public void Compare_UsesMajorMinorAsFailureBoundary(
        string left, string right, VersionCompatibility expected)
    {
        Assert.True(ComponentVersion.TryParse(left, out var a));
        Assert.True(ComponentVersion.TryParse(right, out var b));

        Assert.Equal(expected, ComponentVersion.Compare(a, b));
    }
}
```

- [ ] **Step 2: Run the new tests and verify they fail**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~ComponentVersionTests --verbosity minimal
```

Expected: compile failure because `RevitCli.Diagnostics.ComponentVersion` does not exist.

- [ ] **Step 3: Implement the helper**

Create `src/RevitCli/Diagnostics/ComponentVersion.cs`:

```csharp
using System;
using System.Text.RegularExpressions;

namespace RevitCli.Diagnostics;

internal enum VersionCompatibility
{
    Compatible,
    MetadataMismatch,
    PatchMismatch,
    MajorMinorMismatch
}

internal readonly record struct ComponentVersion(
    int Major,
    int Minor,
    int Patch,
    string Metadata,
    string Original)
{
    private static readonly Regex VersionPattern = new(
        @"^v?(?<major>\d+)\.(?<minor>\d+)\.(?<patch>\d+)(?:-[^+]+)?(?:\+(?<metadata>.+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    public static bool TryParse(string? value, out ComponentVersion version)
    {
        version = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        var match = VersionPattern.Match(trimmed);
        if (!match.Success)
            return false;

        version = new ComponentVersion(
            int.Parse(match.Groups["major"].Value),
            int.Parse(match.Groups["minor"].Value),
            int.Parse(match.Groups["patch"].Value),
            match.Groups["metadata"].Success ? match.Groups["metadata"].Value : "",
            trimmed);
        return true;
    }

    public static VersionCompatibility Compare(ComponentVersion expected, ComponentVersion actual)
    {
        if (expected.Major != actual.Major || expected.Minor != actual.Minor)
            return VersionCompatibility.MajorMinorMismatch;

        if (expected.Patch != actual.Patch)
            return VersionCompatibility.PatchMismatch;

        if (!string.Equals(expected.Metadata, actual.Metadata, StringComparison.Ordinal))
            return VersionCompatibility.MetadataMismatch;

        return VersionCompatibility.Compatible;
    }

    public override string ToString() => string.IsNullOrWhiteSpace(Original)
        ? $"{Major}.{Minor}.{Patch}"
        : Original;
}
```

- [ ] **Step 4: Run tests and verify pass**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~ComponentVersionTests --verbosity minimal
```

Expected: all `ComponentVersionTests` pass.

- [ ] **Step 5: Commit**

```powershell
git add src\RevitCli\Diagnostics\ComponentVersion.cs tests\RevitCli.Tests\Diagnostics\ComponentVersionTests.cs
git commit -m "test: add component version comparison"
```

## Task 2: Read CLI and Installed Add-in Assembly Versions

**Files:**
- Create: `src/RevitCli/Diagnostics/AssemblyVersionReader.cs`
- Create: `tests/RevitCli.Tests/Diagnostics/AssemblyVersionReaderTests.cs`
- Modify: `src/RevitCli/Commands/DoctorCommand.cs`

- [ ] **Step 1: Write failing assembly reader tests**

Create `tests/RevitCli.Tests/Diagnostics/AssemblyVersionReaderTests.cs`:

```csharp
using System.Reflection;
using RevitCli.Diagnostics;

namespace RevitCli.Tests.Diagnostics;

public class AssemblyVersionReaderTests
{
    [Fact]
    public void TryRead_ReturnsVersionForManagedAssembly()
    {
        var path = typeof(AssemblyVersionReaderTests).Assembly.Location;

        Assert.True(AssemblyVersionReader.TryRead(path, out var version, out var error));
        Assert.True(ComponentVersion.TryParse(version, out _));
        Assert.Null(error);
    }

    [Fact]
    public void TryRead_ReturnsErrorForMissingFile()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"), "missing.dll");

        Assert.False(AssemblyVersionReader.TryRead(path, out var version, out var error));
        Assert.Equal("", version);
        Assert.Contains("does not exist", error);
    }

    [Fact]
    public void CurrentCliVersion_IsParseable()
    {
        var version = AssemblyVersionReader.CurrentCliVersion();

        Assert.True(ComponentVersion.TryParse(version, out _));
    }
}
```

- [ ] **Step 2: Run the tests and verify they fail**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~AssemblyVersionReaderTests --verbosity minimal
```

Expected: compile failure because `AssemblyVersionReader` does not exist.

- [ ] **Step 3: Implement assembly version reader**

Create `src/RevitCli/Diagnostics/AssemblyVersionReader.cs`:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace RevitCli.Diagnostics;

internal static class AssemblyVersionReader
{
    public static string CurrentCliVersion()
    {
        var assembly = typeof(AssemblyVersionReader).Assembly;
        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
    }

    public static bool TryRead(string path, out string version, out string? error)
    {
        version = "";
        error = null;

        if (!File.Exists(path))
        {
            error = $"Assembly does not exist: {path}";
            return false;
        }

        try
        {
            var fileInfo = FileVersionInfo.GetVersionInfo(path);
            if (TryNormalizeVersion(fileInfo.ProductVersion, out var productVersion))
            {
                version = productVersion;
                return true;
            }

            var assemblyName = AssemblyName.GetAssemblyName(path);
            version = assemblyName.Version?.ToString(3) ?? "";
            if (string.IsNullOrWhiteSpace(version))
            {
                error = $"Assembly has no readable version: {path}";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException or UnauthorizedAccessException)
        {
            error = $"Assembly version cannot be read: {ex.Message}";
            return false;
        }
    }

    internal static bool TryNormalizeVersion(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (ComponentVersion.TryParse(trimmed, out _))
        {
            normalized = trimmed;
            return true;
        }

        var metadata = "";
        var plusIndex = trimmed.IndexOf('+');
        var core = trimmed;
        if (plusIndex >= 0)
        {
            metadata = trimmed[plusIndex..];
            core = trimmed[..plusIndex];
        }

        var parts = core.Split('.');
        if (parts.Length == 4
            && int.TryParse(parts[0], out var major)
            && int.TryParse(parts[1], out var minor)
            && int.TryParse(parts[2], out var patch)
            && int.TryParse(parts[3], out _))
        {
            var candidate = $"{major}.{minor}.{patch}{metadata}";
            if (ComponentVersion.TryParse(candidate, out _))
            {
                normalized = candidate;
                return true;
            }
        }

        return false;
    }
}
```

- [ ] **Step 4: Add `DoctorEnvironment` CLI version seam**

Modify `src/RevitCli/Commands/DoctorCommand.cs` inside `DoctorEnvironment`:

```csharp
public string CliVersion { get; init; } = AssemblyVersionReader.CurrentCliVersion();
```

Add this using at the top:

```csharp
using RevitCli.Diagnostics;
```

- [ ] **Step 5: Run tests and verify pass**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter "FullyQualifiedName~AssemblyVersionReaderTests|FullyQualifiedName~ComponentVersionTests" --verbosity minimal
```

Expected: all diagnostics tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src\RevitCli\Diagnostics\AssemblyVersionReader.cs tests\RevitCli.Tests\Diagnostics\AssemblyVersionReaderTests.cs src\RevitCli\Commands\DoctorCommand.cs
git commit -m "feat: read cli and addin assembly versions"
```

## Task 3: Gate Installed Add-in Version in Doctor

**Files:**
- Modify: `src/RevitCli/Commands/DoctorCommand.cs`
- Modify: `tests/RevitCli.Tests/Commands/DoctorCommandTests.cs`

- [ ] **Step 1: Write failing doctor tests for installed version checks**

Add these helpers to `DoctorCommandTests`:

```csharp
private static void WriteAddinManifest(DoctorEnvironment environment, string assemblyPath)
{
    var addins = Path.Combine(environment.AppData, "Autodesk", "Revit", "Addins", "2026");
    Directory.CreateDirectory(addins);
    File.WriteAllText(Path.Combine(addins, "RevitCli.addin"),
        $@"<?xml version=""1.0"" encoding=""utf-8""?>
<RevitAddIns>
  <AddIn Type=""Application"">
    <Assembly>{assemblyPath}</Assembly>
  </AddIn>
</RevitAddIns>");
}

private static string CurrentCliAssemblyPath() =>
    typeof(RevitCli.ProgramExit).Assembly.Location;

private static DoctorEnvironment WithCliVersion(DoctorEnvironment environment, string cliVersion)
{
    return new DoctorEnvironment
    {
        UserProfile = environment.UserProfile,
        AppData = environment.AppData,
        Revit2026InstallDir = environment.Revit2026InstallDir,
        CliVersion = cliVersion
    };
}
```

Add tests:

```csharp
[Fact]
public async Task Execute_InstalledAddinMajorMinorMismatch_ReturnsFailure()
{
    var environment = WithCliVersion(CreateDoctorEnvironment(), "9.9.0");
    WriteAddinManifest(environment, CurrentCliAssemblyPath());
    var status = new StatusInfo { RevitVersion = "2026", RevitYear = 2026, AddinVersion = "9.9.0" };
    var handler = new FakeHttpHandler(JsonSerializer.Serialize(ApiResponse<StatusInfo>.Ok(status)));
    var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
    var writer = new StringWriter();

    var exitCode = await DoctorCommand.ExecuteAsync(client, new CliConfig(), writer, environment);

    Assert.Equal(1, exitCode);
    Assert.Contains("Installed Add-in version", writer.ToString());
    Assert.Contains("does not match CLI", writer.ToString());
}

[Fact]
public async Task Execute_PrintsCliAndInstalledAddinVersions_WhenManifestAssemblyExists()
{
    var environment = CreateDoctorEnvironment();
    WriteAddinManifest(environment, CurrentCliAssemblyPath());
    var status = new StatusInfo { RevitVersion = "2026", RevitYear = 2026, AddinVersion = environment.CliVersion };
    var handler = new FakeHttpHandler(JsonSerializer.Serialize(ApiResponse<StatusInfo>.Ok(status)));
    var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
    var writer = new StringWriter();

    await DoctorCommand.ExecuteAsync(client, new CliConfig(), writer, environment);

    var output = writer.ToString();
    Assert.Contains("CLI version", output);
    Assert.Contains("Installed Add-in version", output);
}
```

- [ ] **Step 2: Run doctor tests and verify they fail**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~DoctorCommandTests --verbosity minimal
```

Expected: version check assertions fail because `doctor` does not compare versions yet.

- [ ] **Step 3: Implement version check output**

In `DoctorCommand.ExecuteAsync`, after config output and before Revit API checks, add:

```csharp
var cliVersion = environment.CliVersion;
await output.WriteLineAsync($"OK: CLI version: {cliVersion}");
var expectedVersion = ParseExpectedVersion(output, cliVersion, ref hasFailure);
```

Add private methods to `DoctorCommand`:

```csharp
private static ComponentVersion? ParseExpectedVersion(TextWriter output, string cliVersion, ref bool hasFailure)
{
    if (ComponentVersion.TryParse(cliVersion, out var parsed))
        return parsed;

    output.WriteLine($"FAIL: CLI version cannot be parsed: {cliVersion}");
    hasFailure = true;
    return null;
}

private static async Task<bool> WriteVersionCompatibility(
    TextWriter output,
    string label,
    ComponentVersion expected,
    string actualVersion)
{
    if (!ComponentVersion.TryParse(actualVersion, out var actual))
    {
        await output.WriteLineAsync($"FAIL: {label} version cannot be parsed: {actualVersion}");
        return false;
    }

    var compatibility = ComponentVersion.Compare(expected, actual);
    return compatibility switch
    {
        VersionCompatibility.Compatible => await WriteOk(output, $"{label} version: {actual}"),
        VersionCompatibility.MetadataMismatch => await WriteWarn(output, $"{label} version differs by metadata only: CLI={expected}, {label}={actual}"),
        VersionCompatibility.PatchMismatch => await WriteWarn(output, $"{label} version differs by patch only: CLI={expected}, {label}={actual}"),
        _ => await WriteFail(output, $"{label} version {actual} does not match CLI version {expected} by major/minor.")
    };
}

private static async Task<bool> WriteOk(TextWriter output, string message)
{
    await output.WriteLineAsync($"OK: {message}");
    return true;
}

private static async Task<bool> WriteWarn(TextWriter output, string message)
{
    await output.WriteLineAsync($"WARN: {message}");
    return true;
}

private static async Task<bool> WriteFail(TextWriter output, string message)
{
    await output.WriteLineAsync($"FAIL: {message}");
    return false;
}
```

Change `WriteAddinManifestCheck` to return the resolved assembly path and version. Use this shape:

```csharp
private sealed record AddinInstallInfo(string ManifestPath, string AssemblyPath, string Version);

private static async Task<AddinInstallInfo?> WriteAddinManifestCheck(
    TextWriter output,
    DoctorEnvironment environment,
    ComponentVersion? expectedCliVersion)
```

Inside the method, after assembly path exists:

```csharp
if (!AssemblyVersionReader.TryRead(assemblyPath, out var installedVersion, out var versionError))
{
    await output.WriteLineAsync($"FAIL: Installed Add-in version cannot be read ({assemblyPath}): {versionError}");
    return null;
}

await output.WriteLineAsync($"OK: Add-in manifest: {manifestPath}");
await output.WriteLineAsync($"OK: Add-in assembly: {assemblyPath}");
if (expectedCliVersion.HasValue)
{
    var compatible = await WriteVersionCompatibility(
        output,
        "Installed Add-in",
        expectedCliVersion.Value,
        installedVersion);
    if (!compatible)
        return null;
}
else
{
    await output.WriteLineAsync($"OK: Installed Add-in version: {installedVersion}");
}
return new AddinInstallInfo(manifestPath, assemblyPath, installedVersion);
```

Update `ExecuteAsync`:

```csharp
hasFailure |= !await WriteRevit2026ApiCheck(output, environment);
var installedAddin = await WriteAddinManifestCheck(output, environment, expectedVersion);
hasFailure |= installedAddin == null;
```

- [ ] **Step 4: Run doctor tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~DoctorCommandTests --verbosity minimal
```

Expected: doctor tests pass.

- [ ] **Step 5: Run full CLI tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --verbosity minimal
```

Expected: all CLI tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src\RevitCli\Commands\DoctorCommand.cs tests\RevitCli.Tests\Commands\DoctorCommandTests.cs
git commit -m "feat: gate installed addin version in doctor"
```

## Task 4: Gate Live Add-in Version in Doctor

**Files:**
- Modify: `src/RevitCli/Commands/DoctorCommand.cs`
- Modify: `tests/RevitCli.Tests/Commands/DoctorCommandTests.cs`

- [ ] **Step 1: Write failing live version mismatch tests**

Add tests to `DoctorCommandTests`:

```csharp
[Fact]
public async Task Execute_LiveAddinMajorMinorMismatch_ReturnsFailure()
{
    var environment = CreateDoctorEnvironment();
    WriteAddinManifest(environment, CurrentCliAssemblyPath());
    var status = new StatusInfo
    {
        RevitVersion = "2026",
        RevitYear = 2026,
        AddinVersion = "1.0.0",
        DocumentName = "Test.rvt"
    };
    var handler = new FakeHttpHandler(JsonSerializer.Serialize(ApiResponse<StatusInfo>.Ok(status)));
    var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
    var writer = new StringWriter();

    var exitCode = await DoctorCommand.ExecuteAsync(client, new CliConfig(), writer, environment);

    Assert.Equal(1, exitCode);
    Assert.Contains("Live Add-in version", writer.ToString());
    Assert.Contains("does not match CLI", writer.ToString());
}

[Fact]
public async Task Execute_LiveAddinPatchMismatch_WarnsButDoesNotFail()
{
    var environment = WithCliVersion(CreateDoctorEnvironment(), "1.3.0");
    WriteAddinManifest(environment, CurrentCliAssemblyPath());
    var status = new StatusInfo
    {
        RevitVersion = "2026",
        RevitYear = 2026,
        AddinVersion = "1.3.1",
        DocumentName = "Test.rvt"
    };
    var handler = new FakeHttpHandler(JsonSerializer.Serialize(ApiResponse<StatusInfo>.Ok(status)));
    var client = new RevitClient(new HttpClient(handler) { BaseAddress = new Uri("http://localhost:17839") });
    var writer = new StringWriter();

    await DoctorCommand.ExecuteAsync(client, new CliConfig(), writer, environment);

    Assert.Contains("WARN: Live Add-in version differs by patch only", writer.ToString());
}
```

The patch warning test intentionally does not assert the final exit code. The installed assembly check may fail independently when `CurrentCliAssemblyPath()` differs from the injected CLI version; this test's responsibility is only to prove the live Add-in patch mismatch is classified as `WARN`.

- [ ] **Step 2: Run doctor tests and verify failure**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~DoctorCommandTests --verbosity minimal
```

Expected: live mismatch test fails because `doctor` does not compare `status.Data.AddinVersion` yet.

- [ ] **Step 3: Implement live version check**

In the successful `status` branch of `DoctorCommand.ExecuteAsync`, after Revit version check:

```csharp
if (expectedVersion.HasValue)
{
    var liveCompatible = await WriteVersionCompatibility(
        output,
        "Live Add-in",
        expectedVersion.Value,
        status.Data.AddinVersion);
    if (!liveCompatible)
    {
        await output.WriteLineAsync(
            "HINT: Close Revit, reinstall the Revit 2026 add-in, restart Revit, and rerun doctor.");
        hasFailure = true;
    }
}
else if (!string.IsNullOrWhiteSpace(status.Data.AddinVersion))
{
    await output.WriteLineAsync($"OK: Live Add-in version: {status.Data.AddinVersion}");
}
else
{
    await output.WriteLineAsync("FAIL: Live Add-in version is missing from status.");
    hasFailure = true;
}
```

- [ ] **Step 4: Run doctor tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --filter FullyQualifiedName~DoctorCommandTests --verbosity minimal
```

Expected: doctor tests pass.

- [ ] **Step 5: Run full CLI tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --verbosity minimal
```

Expected: all CLI tests pass.

- [ ] **Step 6: Commit**

```powershell
git add src\RevitCli\Commands\DoctorCommand.cs tests\RevitCli.Tests\Commands\DoctorCommandTests.cs
git commit -m "feat: gate live addin version in doctor"
```

## Task 5: Make Live Status Version Explicit and Testable

**Files:**
- Create: `src/RevitCli.Addin/Services/AddinVersionProvider.cs`
- Modify: `src/RevitCli.Addin/Services/RealRevitOperations.cs`
- Modify: `src/RevitCli.Addin/Services/PlaceholderRevitOperations.cs`
- Create: `tests/RevitCli.Addin.Tests/Services/AddinVersionProviderTests.cs`
- Modify: `tests/RevitCli.Addin.Tests/Integration/ProtocolTests.cs`

- [ ] **Step 1: Write failing Add-in version provider test**

Create `tests/RevitCli.Addin.Tests/Services/AddinVersionProviderTests.cs`:

```csharp
using RevitCli.Addin.Services;

namespace RevitCli.Addin.Tests.Services;

public class AddinVersionProviderTests
{
    [Fact]
    public void Current_ReturnsParseableAssemblyVersion()
    {
        var version = AddinVersionProvider.Current();

        Assert.Matches(@"^\d+\.\d+\.\d+", version);
    }
}
```

- [ ] **Step 2: Run test and verify failure**

Run:

```powershell
dotnet test tests\RevitCli.Addin.Tests\RevitCli.Addin.Tests.csproj -p:RevitInstallDir="D:\revit2026\Revit 2026" --filter FullyQualifiedName~AddinVersionProviderTests --verbosity minimal
```

Expected: compile failure because `AddinVersionProvider` does not exist.

- [ ] **Step 3: Implement provider**

Create `src/RevitCli.Addin/Services/AddinVersionProvider.cs`:

```csharp
using System.Reflection;

namespace RevitCli.Addin.Services;

internal static class AddinVersionProvider
{
    public static string Current()
    {
        var assembly = typeof(AddinVersionProvider).Assembly;
        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
    }
}
```

- [ ] **Step 4: Wire provider into real operations**

In `RealRevitOperations.cs`, replace:

```csharp
private static readonly string AddinVersionString =
    typeof(RealRevitOperations).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
```

with:

```csharp
private static readonly string AddinVersionString = AddinVersionProvider.Current();
```

- [ ] **Step 5: Keep placeholder status intentionally non-production**

In `PlaceholderRevitOperations.GetStatusAsync`, keep:

```csharp
AddinVersion = "0.0.0",
```

Add a comment:

```csharp
// Placeholder protocol tests must not masquerade as a production Add-in.
```

- [ ] **Step 6: Add protocol assertion for isolated server info path**

In `ProtocolTests`, add a field:

```csharp
private readonly string _realUserServerInfoPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
    ".revitcli",
    "server.json");
```

Add a test:

```csharp
[Fact]
public void ProtocolServer_UsesTemporaryServerInfoPath()
{
    Assert.True(File.Exists(_serverInfoPath));
    Assert.NotEqual(_realUserServerInfoPath, _serverInfoPath);
}
```

- [ ] **Step 7: Run Add-in tests**

Run:

```powershell
dotnet test tests\RevitCli.Addin.Tests\RevitCli.Addin.Tests.csproj -p:RevitInstallDir="D:\revit2026\Revit 2026" --verbosity minimal
```

Expected: all Add-in tests pass.

- [ ] **Step 8: Commit**

```powershell
git add src\RevitCli.Addin\Services\AddinVersionProvider.cs src\RevitCli.Addin\Services\RealRevitOperations.cs src\RevitCli.Addin\Services\PlaceholderRevitOperations.cs tests\RevitCli.Addin.Tests\Services\AddinVersionProviderTests.cs tests\RevitCli.Addin.Tests\Integration\ProtocolTests.cs
git commit -m "feat: expose live addin assembly version"
```

## Task 6: Harden Source-Tree Revit 2026 Install Script

**Files:**
- Modify: `scripts/install.ps1`

- [ ] **Step 1: Add script parameters**

At the top of `scripts/install.ps1`, change the param block to:

```powershell
param(
    [string]$RevitYears = "2026",
    [string]$Configuration = "Release",
    [string]$RevitInstallDir = "",
    [switch]$SkipBuild,
    [switch]$Force
)
```

- [ ] **Step 2: Add helper functions for package mode vs source-tree mode**

After `$SrcBin = Join-Path $ScriptDir "bin"`, add:

```powershell
$RepoRoot = Split-Path -Parent $ScriptDir
$ArtifactsRoot = Join-Path $RepoRoot ".artifacts\install"
$SourceTreeMode = Test-Path (Join-Path $RepoRoot "revitcli.sln")

function Invoke-DotNet {
    param([string[]]$Arguments)
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $($Arguments -join ' ') failed with exit code $LASTEXITCODE"
    }
}

function Publish-SourceTreePackage {
    param([string[]]$Years)

    if ($SkipBuild) {
        return
    }

    if (Test-Path -LiteralPath $ArtifactsRoot) {
        Remove-Item -LiteralPath $ArtifactsRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $ArtifactsRoot -Force | Out-Null

    $script:SrcBin = Join-Path $ArtifactsRoot "bin"
    Invoke-DotNet @("publish", (Join-Path $RepoRoot "src\RevitCli\RevitCli.csproj"), "-c", $Configuration, "-o", $script:SrcBin)

    foreach ($year in $Years) {
        $addinOut = Join-Path $ArtifactsRoot "addin\$year"
        $args = @(
            "publish",
            (Join-Path $RepoRoot "src\RevitCli.Addin\RevitCli.Addin.csproj"),
            "-c", $Configuration,
            "-p:RevitYear=$year",
            "-o", $addinOut
        )
        if ($RevitInstallDir -and $year -eq "2026") {
            $args += "-p:RevitInstallDir=$RevitInstallDir"
        }
        Invoke-DotNet $args
    }
}
```

- [ ] **Step 3: Call source-tree publish before source directory validation**

After target year calculation and validation of supported year values, add:

```powershell
if ($SourceTreeMode) {
    Publish-SourceTreePackage -Years $targetYears
}
```

Then make source validation use `$SrcBin` and `$ArtifactsRoot\addin\$year` in source-tree mode:

```powershell
function Get-SourceAddinDir {
    param([string]$Year)
    if ($SourceTreeMode) {
        return Join-Path $ArtifactsRoot "addin\$Year"
    }
    return Join-Path $ScriptDir "addin\$Year"
}
```

Replace existing `Join-Path $ScriptDir "addin\$year"` source references with `Get-SourceAddinDir $year`.

- [ ] **Step 4: Write install metadata version from CLI executable**

Before metadata creation, add:

```powershell
$installedCliVersion = ""
$installedCliExe = Join-Path $BinDir "RevitCli.exe"
if (Test-Path -LiteralPath $installedCliExe) {
    $installedCliVersion = (& $installedCliExe --version 2>$null) -replace '^revitcli\s+', ''
}
```

Change metadata:

```powershell
$metadata = @{
    version      = $installedCliVersion
    revitYears   = $installedYears
    binDir       = $BinDir
    installRoot  = $InstallRoot
    timestamp    = (Get-Date -Format "o")
} | ConvertTo-Json
```

- [ ] **Step 5: Manually dry-run syntax by invoking help-free install with forced source tree**

Run this only after closing Revit if it is running:

```powershell
scripts\install.ps1 -RevitYears 2026 -Configuration Debug -RevitInstallDir "D:\revit2026\Revit 2026" -Force
```

Expected:

- CLI copied to `%LOCALAPPDATA%\RevitCli\bin`.
- Add-in copied to `%LOCALAPPDATA%\RevitCli\addin\2026`.
- Manifest written to `%APPDATA%\Autodesk\Revit\Addins\2026\RevitCli.addin`.
- Manifest `<Assembly>` points to `%LOCALAPPDATA%\RevitCli\addin\2026\RevitCli.Addin.dll`.

- [ ] **Step 6: Commit**

```powershell
git add scripts\install.ps1
git commit -m "feat: install revit 2026 addin from source tree"
```

## Task 7: Add Version Metadata to Smoke Report

**Files:**
- Modify: `scripts/smoke-revit2026.ps1`

- [ ] **Step 1: Add assembly version reader function to script**

In `scripts/smoke-revit2026.ps1`, after `Assert-FileExists`, add:

```powershell
function Get-AssemblyVersion {
    param([string]$Path)
    if (-not (Test-Path -LiteralPath $Path)) { return "" }
    try {
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($Path)
        if ($info.ProductVersion) {
            return $info.ProductVersion.Trim()
        }
        return ""
    } catch {
        return ""
    }
}

function Resolve-ManifestAssemblyPath {
    param([string]$ManifestPath)
    [xml]$manifest = Get-Content -Raw -LiteralPath $ManifestPath
    $assembly = [string]$manifest.RevitAddIns.AddIn.Assembly
    if ([System.IO.Path]::IsPathRooted($assembly)) { return $assembly }
    return [System.IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $ManifestPath) $assembly))
}
```

- [ ] **Step 2: Capture CLI and installed Add-in versions**

After manifest and `server.json` prechecks, add:

```powershell
$manifestAssemblyPath = Resolve-ManifestAssemblyPath $manifestPath
Assert-FileExists $manifestAssemblyPath "RevitCli add-in assembly"
$installedAddinVersion = Get-AssemblyVersion $manifestAssemblyPath
$cliVersionOutput = & $RevitCli "--version" 2>&1
$cliVersion = (($cliVersionOutput | Out-String).Trim() -replace '^revitcli\s+', '')
```

- [ ] **Step 3: Parse live Add-in version from status output**

After:

```powershell
Invoke-RevitCliSmoke @("status") | Out-Null
```

replace it with:

```powershell
$statusText = Invoke-RevitCliSmoke @("status")
$liveAddinVersion = ""
foreach ($line in ($statusText -split "`r?`n")) {
    if ($line -match '^Add-in:\s+v?(.+)$') {
        $liveAddinVersion = $Matches[1].Trim()
    }
}
```

- [ ] **Step 4: Add metadata to report**

In `$report = [ordered]@{ ... }`, add:

```powershell
cliVersion = $cliVersion
manifestAssemblyPath = $manifestAssemblyPath
installedAddinVersion = $installedAddinVersion
liveAddinVersion = $liveAddinVersion
```

- [ ] **Step 5: Run smoke after Revit is open**

Run:

```powershell
$env:REVITCLI_REVIT2026_INSTALL_DIR = "D:\revit2026\Revit 2026"
scripts\smoke-revit2026.ps1 `
  -RevitCli "$env:LOCALAPPDATA\RevitCli\bin\RevitCli.exe" `
  -ElementId 337596 `
  -Category walls `
  -Filter "标记 = TEST" `
  -Param "标记" `
  -Value "TEST-CODEX-20260426" `
  -Apply
```

Expected:

- Smoke exits `0`.
- Report has `cliVersion`, `installedAddinVersion`, `liveAddinVersion`.
- Final `query --id 337596` shows `标记=TEST`.

- [ ] **Step 6: Commit**

```powershell
git add scripts\smoke-revit2026.ps1
git commit -m "feat: record version metadata in revit smoke"
```

## Task 8: Final Verification and Documentation Notes

**Files:**
- Modify: `README.md`
- Modify: `docs/troubleshooting.html`
- Modify: `CHANGELOG.md`

- [ ] **Step 1: Update README Revit 2026 install verification**

In `README.md`, under Add-in install instructions, add:

```markdown
For a non-default Revit 2026 install directory:

```powershell
scripts\install.ps1 -RevitYears 2026 -RevitInstallDir "D:\revit2026\Revit 2026" -Force
$env:REVITCLI_REVIT2026_INSTALL_DIR = "D:\revit2026\Revit 2026"
revitcli doctor
```

`doctor` reports the CLI version, installed Add-in version, live Add-in version,
manifest path, and Revit API path. A CLI/Add-in major or minor version mismatch
is treated as a failed install.
```

- [ ] **Step 2: Update troubleshooting version mismatch section**

In `docs/troubleshooting.html`, add a section after the Add-in not loaded checklist:

```html
      <h2>"Live Add-in version ... does not match CLI version ..."</h2>

      <div class="alert alert-error">
        Revit is currently running an Add-in DLL from a different RevitCli
        major/minor version than the CLI.
      </div>

      <h3>Fix</h3>
      <pre><code># Close Revit first
scripts\install.ps1 -RevitYears 2026 -RevitInstallDir "D:\revit2026\Revit 2026" -Force

# Start Revit again, open the model, then verify
revitcli doctor
revitcli status</code></pre>
```

- [ ] **Step 3: Update changelog under Unreleased**

If `CHANGELOG.md` has no `Unreleased` section, add one above `1.3.0`:

```markdown
## [Unreleased]

### Added

- `doctor` now reports CLI, installed Add-in, and live Add-in versions for the Revit 2026 baseline.
- `scripts\smoke-revit2026.ps1` records version metadata in smoke reports.

### Changed

- Revit 2026 install verification now fails on CLI/Add-in major or minor version mismatch.
- Add-in protocol tests use an isolated `server.json` path and no longer touch the user's real RevitCli server discovery file.
```

- [ ] **Step 4: Run full automated tests**

Run:

```powershell
dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --verbosity minimal
dotnet test tests\RevitCli.Addin.Tests\RevitCli.Addin.Tests.csproj -p:RevitInstallDir="D:\revit2026\Revit 2026" --verbosity minimal
git diff --check
```

Expected:

- CLI tests pass.
- Add-in tests pass.
- `git diff --check` exits `0`. LF/CRLF warnings are acceptable if no whitespace errors are reported.

- [ ] **Step 5: Run live Revit verification**

With Revit 2026 open and `D:\桌面\revit_cli.rvt` loaded:

```powershell
$env:REVITCLI_REVIT2026_INSTALL_DIR = "D:\revit2026\Revit 2026"
revitcli doctor
revitcli status
scripts\smoke-revit2026.ps1 `
  -ElementId 337596 `
  -Category walls `
  -Filter "标记 = TEST" `
  -Param "标记" `
  -Value "TEST-CODEX-20260426" `
  -Apply
```

Expected:

- `doctor` exits `0`.
- `doctor` shows CLI/Add-in major/minor consistency.
- `status` shows Revit `2026`, current document, and Add-in version.
- Smoke exits `0`.
- Final element `337596` still has `标记=TEST`.

- [ ] **Step 6: Commit docs and final verification notes**

```powershell
git add README.md docs\troubleshooting.html CHANGELOG.md
git commit -m "docs: document revit 2026 version checks"
```

## Final Checks Before PR

- [ ] Run `git status --short` and confirm only intended files are changed.
- [ ] Run `git log --oneline -n 10` and confirm commits are scoped.
- [ ] Capture the final verification commands and results in the PR description:
  - `dotnet test tests\RevitCli.Tests\RevitCli.Tests.csproj --verbosity minimal`
  - `dotnet test tests\RevitCli.Addin.Tests\RevitCli.Addin.Tests.csproj -p:RevitInstallDir="D:\revit2026\Revit 2026" --verbosity minimal`
  - `git diff --check`
  - `revitcli doctor`
  - `revitcli status`
  - `scripts\smoke-revit2026.ps1 ... -Apply`
