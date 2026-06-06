using System.Text.Json;
using RevitCli.Commands;

namespace RevitCli.Tests.Commands;

public sealed class AddinsCommandTests : IDisposable
{
    private readonly string _root;

    public AddinsCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "revitcli-addins-tests-" + Guid.NewGuid().ToString("N"));
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
    public async Task Audit_Json_ReportsMissingAssemblyDuplicateIdShadowingAndDevPath()
    {
        var allUsersRoot = Path.Combine(_root, "all-users");
        var perUserRoot = Path.Combine(_root, "per-user");
        var allUsers2026 = Path.Combine(allUsersRoot, "2026");
        var perUser2026 = Path.Combine(perUserRoot, "2026");
        Directory.CreateDirectory(allUsers2026);
        Directory.CreateDirectory(perUser2026);
        var devAssembly = Path.Combine(_root, "src", "MyAddin", "bin", "Debug", "MyAddin.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(devAssembly)!);
        File.WriteAllText(devAssembly, "dll");
        var duplicateId = Guid.NewGuid().ToString("D");
        WriteAddin(Path.Combine(allUsers2026, "Office.addin"), "Office A", duplicateId, devAssembly, vendorId: "OFF");
        WriteAddin(Path.Combine(perUser2026, "Office.addin"), "Office B", duplicateId, Path.Combine(_root, "missing.dll"), vendorId: "OFF");
        WriteAddin(Path.Combine(perUser2026, "Broken.addin"), "Broken", Guid.NewGuid().ToString("D"), "", vendorId: "");
        var output = new StringWriter();

        var exitCode = await AddinsCommand.ExecuteAuditAsync(
            "2026",
            allUsersRoot,
            perUserRoot,
            "json",
            output);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal("revit-addins-audit.v1", root.GetProperty("schemaVersion").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(3, root.GetProperty("manifestCount").GetInt32());
        Assert.Equal(3, root.GetProperty("addInCount").GetInt32());
        var issues = root.GetProperty("issues").EnumerateArray().ToArray();
        Assert.Contains(issues, issue => issue.GetProperty("code").GetString() == "missing-assembly");
        Assert.Contains(issues, issue => issue.GetProperty("code").GetString() == "duplicate-addin-id");
        Assert.Contains(issues, issue => issue.GetProperty("code").GetString() == "same-filename-shadowing");
        Assert.Contains(issues, issue => issue.GetProperty("code").GetString() == "dev-path-assembly");
    }

    [Fact]
    public async Task Audit_FindingsJson_EmitsBimOpsEnvelope()
    {
        var allUsersRoot = Path.Combine(_root, "all-users");
        var allUsers2026 = Path.Combine(allUsersRoot, "2026");
        Directory.CreateDirectory(allUsers2026);
        WriteAddin(Path.Combine(allUsers2026, "Broken.addin"), "Broken", Guid.NewGuid().ToString("D"), "", vendorId: "OFF");
        var output = new StringWriter();

        var exitCode = await AddinsCommand.ExecuteAuditAsync(
            "2026",
            allUsersRoot,
            perUserRoot: Path.Combine(_root, "per-user"),
            "json",
            output,
            findings: true);

        Assert.Equal(1, exitCode);
        using var document = JsonDocument.Parse(output.ToString());
        var root = document.RootElement;
        Assert.Equal("bimops-finding.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("addins.audit", root.GetProperty("source").GetString());
        Assert.False(root.GetProperty("requiresRevit").GetBoolean());
        Assert.True(root.GetProperty("summary").GetProperty("errors").GetInt32() >= 1);
        var finding = root.GetProperty("findings").EnumerateArray().Single();
        Assert.Equal("addins.missing.assembly", finding.GetProperty("ruleId").GetString());
        Assert.Equal("error", finding.GetProperty("severity").GetString());
        Assert.Equal("addin-manifest", finding.GetProperty("target").GetProperty("category").GetString());
    }

    [Fact]
    public async Task Audit_FindingsRequiresJsonOutput()
    {
        var output = new StringWriter();

        var exitCode = await AddinsCommand.ExecuteAuditAsync(
            "2026",
            Path.Combine(_root, "all-users"),
            Path.Combine(_root, "per-user"),
            "markdown",
            output,
            findings: true);

        Assert.Equal(1, exitCode);
        Assert.Contains("--findings currently requires '--output json'", output.ToString());
    }

    [Fact]
    public async Task Audit_InvalidYear_ReturnsError()
    {
        var output = new StringWriter();

        var exitCode = await AddinsCommand.ExecuteAuditAsync(
            "2026,not-a-year",
            Path.Combine(_root, "all-users"),
            Path.Combine(_root, "per-user"),
            "json",
            output);

        Assert.Equal(1, exitCode);
        Assert.Contains("Invalid Revit year", output.ToString());
    }

    [Fact]
    public async Task PlanDisable_CloudSafe_SkipsAutodeskAndPlansThirdParty()
    {
        var allUsersRoot = Path.Combine(_root, "all-users");
        var allUsers2026 = Path.Combine(allUsersRoot, "2026");
        Directory.CreateDirectory(allUsers2026);
        var autodeskAssembly = WriteFile("AutodeskCloud.dll");
        var thirdPartyAssembly = WriteFile("ThirdParty.dll");
        WriteAddin(Path.Combine(allUsers2026, "AutodeskCloud.addin"), "Autodesk Collaboration", Guid.NewGuid().ToString("D"), autodeskAssembly, vendorId: "ADSK");
        WriteAddin(Path.Combine(allUsers2026, "ThirdParty.addin"), "Third Party", Guid.NewGuid().ToString("D"), thirdPartyAssembly, vendorId: "THD");
        var planPath = Path.Combine(_root, "addins.plan.json");
        var output = new StringWriter();

        var exitCode = await AddinsCommand.ExecutePlanDisableAsync(
            "2026",
            allUsersRoot,
            perUserRoot: Path.Combine(_root, "per-user"),
            "cloud-safe",
            planPath,
            "json",
            output);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(planPath));
        using var json = JsonDocument.Parse(File.ReadAllText(planPath));
        var root = json.RootElement;
        Assert.Equal("revit-addins-disable-plan.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("cloud-safe", root.GetProperty("profile").GetString());
        Assert.Equal(1, root.GetProperty("actionCount").GetInt32());
        Assert.Equal(1, root.GetProperty("skippedCount").GetInt32());
        var action = root.GetProperty("actions").EnumerateArray().Single();
        Assert.EndsWith("ThirdParty.addin", action.GetProperty("manifestPath").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith("ThirdParty.addin.disabled", action.GetProperty("disabledPath").GetString(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("AutodeskCloud.addin", root.GetProperty("skipped").EnumerateArray().Single().GetProperty("manifestPath").GetString());
    }

    [Fact]
    public async Task ApplyAndRollback_DisablePlanMovesManifestAndRestoresIt()
    {
        var allUsersRoot = Path.Combine(_root, "all-users");
        var allUsers2026 = Path.Combine(allUsersRoot, "2026");
        Directory.CreateDirectory(allUsers2026);
        var assembly = WriteFile("ThirdParty.dll");
        var manifestPath = Path.Combine(allUsers2026, "ThirdParty.addin");
        WriteAddin(manifestPath, "Third Party", Guid.NewGuid().ToString("D"), assembly, vendorId: "THD");
        var disabledPath = manifestPath + ".disabled";
        var planPath = Path.Combine(_root, "addins.plan.json");
        var planOutput = new StringWriter();
        Assert.Equal(0, await AddinsCommand.ExecutePlanDisableAsync(
            "2026",
            allUsersRoot,
            perUserRoot: Path.Combine(_root, "per-user"),
            "clean",
            planPath,
            "json",
            planOutput));

        var dryRunOutput = new StringWriter();
        Assert.Equal(0, await AddinsCommand.ExecuteApplyAsync(
            planPath,
            dryRun: true,
            yes: false,
            receiptOutput: null,
            "json",
            dryRunOutput));
        Assert.True(File.Exists(manifestPath));
        Assert.False(File.Exists(disabledPath));
        using (var dryRunJson = JsonDocument.Parse(dryRunOutput.ToString()))
        {
            Assert.True(dryRunJson.RootElement.GetProperty("dryRun").GetBoolean());
            Assert.Equal(0, dryRunJson.RootElement.GetProperty("appliedCount").GetInt32());
        }

        var receiptPath = Path.Combine(_root, "addins.receipt.json");
        var baselinePath = WriteEnvBaseline();
        var applyOutput = new StringWriter();
        Assert.Equal(0, await AddinsCommand.ExecuteApplyAsync(
            planPath,
            dryRun: false,
            yes: true,
            receiptPath,
            "json",
            applyOutput,
            envBaselinePath: baselinePath));
        Assert.False(File.Exists(manifestPath));
        Assert.True(File.Exists(disabledPath));
        Assert.True(File.Exists(receiptPath));
        using (var receiptJson = JsonDocument.Parse(File.ReadAllText(receiptPath)))
        {
            Assert.Equal("revit-addins-disable-receipt.v1", receiptJson.RootElement.GetProperty("schemaVersion").GetString());
            Assert.Equal(1, receiptJson.RootElement.GetProperty("appliedCount").GetInt32());
            var envBaseline = receiptJson.RootElement.GetProperty("envBaseline");
            Assert.Equal("linked", envBaseline.GetProperty("status").GetString());
            Assert.Equal("revit-env-baseline.v1", envBaseline.GetProperty("schemaVersion").GetString());
            Assert.Equal(baselinePath, envBaseline.GetProperty("path").GetString());
        }

        var rollbackDryRunOutput = new StringWriter();
        Assert.Equal(0, await AddinsCommand.ExecuteRollbackAsync(
            receiptPath,
            dryRun: true,
            yes: false,
            "json",
            rollbackDryRunOutput));
        Assert.False(File.Exists(manifestPath));
        Assert.True(File.Exists(disabledPath));

        var rollbackOutput = new StringWriter();
        Assert.Equal(0, await AddinsCommand.ExecuteRollbackAsync(
            receiptPath,
            dryRun: false,
            yes: true,
            "json",
            rollbackOutput));
        Assert.True(File.Exists(manifestPath));
        Assert.False(File.Exists(disabledPath));
        using var rollbackJson = JsonDocument.Parse(rollbackOutput.ToString());
        Assert.Equal("revit-addins-disable-rollback.v1", rollbackJson.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(1, rollbackJson.RootElement.GetProperty("rolledBackCount").GetInt32());
    }

    private static void WriteAddin(
        string path,
        string name,
        string addInId,
        string assembly,
        string vendorId)
    {
        File.WriteAllText(path, $$"""
<RevitAddIns>
  <AddIn Type="Application">
    <Name>{{name}}</Name>
    <Assembly>{{assembly}}</Assembly>
    <AddInId>{{addInId}}</AddInId>
    <VendorId>{{vendorId}}</VendorId>
  </AddIn>
</RevitAddIns>
""");
    }

    private string WriteFile(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "placeholder");
        return path;
    }

    private string WriteEnvBaseline()
    {
        var path = Path.Combine(_root, "evidence", "env-baseline.json");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, """
        {
          "schemaVersion": "revit-env-baseline.v1",
          "generatedAtUtc": "2026-06-04T00:00:00Z",
          "requiresRevit": false,
          "cliVersion": "test",
          "years": [2026],
          "machine": {
            "machineName": "TEST-MACHINE"
          }
        }
        """);
        return path;
    }
}
