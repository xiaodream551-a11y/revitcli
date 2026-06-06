using System.Text.Json;
using RevitCli.Commands;

namespace RevitCli.Tests.Commands;

public sealed class EnvCommandTests : IDisposable
{
    private readonly string _root;

    public EnvCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "revitcli-env-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public async Task Baseline_Json_RecordsInstallAddinsContentAndRuntimeEvidence()
    {
        var installDir = Path.Combine(_root, "Revit 2026");
        Directory.CreateDirectory(installDir);
        WriteFile(Path.Combine(installDir, "Revit.exe"));
        WriteFile(Path.Combine(installDir, "RevitAPI.dll"));
        WriteFile(Path.Combine(installDir, "RevitAPIUI.dll"));

        var contentRoot = Path.Combine(_root, "content");
        WriteFile(Path.Combine(contentRoot, "Libraries", "US Imperial", "Doors", "Door.rfa"));
        WriteFile(Path.Combine(contentRoot, "Family Templates", "English_I", "Door.rft"));

        var allUsersRoot = Path.Combine(_root, "all-users");
        var allUsers2026 = Path.Combine(allUsersRoot, "2026");
        Directory.CreateDirectory(allUsers2026);
        var addinAssembly = WriteFile(Path.Combine(_root, "OfficeAddin.dll"));
        WriteAddin(Path.Combine(allUsers2026, "Office.addin"), addinAssembly);

        var output = new StringWriter();
        var exitCode = await EnvCommand.ExecuteBaselineAsync(
            "2026",
            "ENU",
            contentRoot,
            revitIni: Path.Combine(_root, "missing.ini"),
            allUsersRoot: allUsersRoot,
            perUserRoot: Path.Combine(_root, "per-user"),
            outPath: null,
            "json",
            output,
            new Dictionary<int, string> { [2026] = installDir },
            new DateTimeOffset(2026, 6, 4, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("revit-env-baseline.v1", root.GetProperty("schemaVersion").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.False(root.GetProperty("requiresRevit").GetBoolean());
        Assert.Equal("ENU", root.GetProperty("locale").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("machine").GetProperty("frameworkDescription").GetString()));

        var install = root.GetProperty("revitInstallations").EnumerateArray().Single();
        Assert.Equal(2026, install.GetProperty("year").GetInt32());
        Assert.Equal(installDir, install.GetProperty("installDir").GetString());
        Assert.True(install.GetProperty("installDirExists").GetBoolean());
        Assert.True(install.GetProperty("revitApiDllExists").GetBoolean());
        Assert.True(install.GetProperty("revitApiUiDllExists").GetBoolean());
        Assert.Contains(install.GetProperty("build").GetProperty("status").GetString(), new[] { "known", "unknown" });

        var content = root.GetProperty("contentBaselines").EnumerateArray().Single();
        Assert.True(content.GetProperty("success").GetBoolean());
        Assert.Equal(1, content.GetProperty("summary").GetProperty("familyFileCount").GetInt32());
        Assert.Equal(1, content.GetProperty("summary").GetProperty("templateFileCount").GetInt32());

        var addins = root.GetProperty("addinSummary");
        Assert.True(addins.GetProperty("success").GetBoolean());
        Assert.Equal(1, addins.GetProperty("manifestCount").GetInt32());
        Assert.Equal(1, addins.GetProperty("addInCount").GetInt32());
        Assert.Equal(0, addins.GetProperty("issueCount").GetInt32());
    }

    [Fact]
    public async Task Baseline_MissingRevitAndContent_StillSucceedsWithUnknownReasons()
    {
        var missingInstall = Path.Combine(_root, "missing-revit");
        var missingContent = Path.Combine(_root, "missing-content");
        var output = new StringWriter();

        var exitCode = await EnvCommand.ExecuteBaselineAsync(
            "2026",
            "ENU",
            missingContent,
            revitIni: Path.Combine(_root, "missing.ini"),
            allUsersRoot: Path.Combine(_root, "all-users"),
            perUserRoot: Path.Combine(_root, "per-user"),
            outPath: null,
            "json",
            output,
            new Dictionary<int, string> { [2026] = missingInstall });

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        var install = root.GetProperty("revitInstallations").EnumerateArray().Single();
        Assert.False(install.GetProperty("installDirExists").GetBoolean());
        Assert.Equal("unknown", install.GetProperty("build").GetProperty("status").GetString());
        Assert.Contains("was not found", install.GetProperty("build").GetProperty("reason").GetString(), StringComparison.OrdinalIgnoreCase);
        var observations = root.GetProperty("observations").EnumerateArray().ToArray();
        Assert.Contains(observations, observation => observation.GetProperty("code").GetString() == "revit-install-dir-missing");
        Assert.Contains(observations, observation => observation.GetProperty("code").GetString() == "library-check-has-issues");
    }

    [Fact]
    public async Task Baseline_Out_WritesBaselineJson()
    {
        var outPath = Path.Combine(_root, "evidence", "env-baseline.json");
        var output = new StringWriter();

        var exitCode = await EnvCommand.ExecuteBaselineAsync(
            "2026",
            "ENU",
            contentRoot: Path.Combine(_root, "missing-content"),
            revitIni: Path.Combine(_root, "missing.ini"),
            allUsersRoot: Path.Combine(_root, "all-users"),
            perUserRoot: Path.Combine(_root, "per-user"),
            outPath,
            "table",
            output,
            new Dictionary<int, string> { [2026] = Path.Combine(_root, "missing-revit") });

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(outPath));
        using var json = JsonDocument.Parse(File.ReadAllText(outPath));
        Assert.Equal("revit-env-baseline.v1", json.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(outPath, json.RootElement.GetProperty("outputPath").GetString());
        Assert.Contains("Saved JSON", output.ToString());
    }

    [Fact]
    public async Task Baseline_InvalidOutput_ReturnsFailure()
    {
        var output = new StringWriter();

        var exitCode = await EnvCommand.ExecuteBaselineAsync(
            "2026",
            "ENU",
            contentRoot: null,
            revitIni: null,
            allUsersRoot: null,
            perUserRoot: null,
            outPath: null,
            "xml",
            output);

        Assert.Equal(1, exitCode);
        Assert.Contains("--output", output.ToString());
    }

    private string WriteFile(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "placeholder");
        return path;
    }

    private static void WriteAddin(string path, string assembly)
    {
        File.WriteAllText(path, $$"""
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Office Addin</Name>
    <Assembly>{{assembly}}</Assembly>
    <AddInId>{{Guid.NewGuid():D}}</AddInId>
    <VendorId>OFF</VendorId>
  </AddIn>
</RevitAddIns>
""");
    }
}
