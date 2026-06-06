using System.Net;
using System.Text.Json;
using RevitCli.Commands;

namespace RevitCli.Tests.Commands;

[Collection("Sequential")]
public sealed class LibraryCommandTests : IDisposable
{
    private readonly string _root;

    public LibraryCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "revitcli-library-tests-" + Guid.NewGuid().ToString("N"));
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
    public async Task Check_Json_ReportsInstalledDefaultContent()
    {
        WriteFile(Path.Combine("content", "Libraries", "US Imperial", "Doors", "Door.rfa"));
        WriteFile(Path.Combine("content", "Family Templates", "English_I", "Door.rft"));
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteCheckAsync(
            2026,
            "ENU",
            Path.Combine(_root, "content"),
            revitIni: Path.Combine(_root, "missing.ini"),
            "json",
            output);

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal("revit-library-check.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("familyFileCount").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("templateFileCount").GetInt32());
    }

    [Fact]
    public async Task Check_UsesRevitIniDataLibraryLocations()
    {
        var officeLibrary = Path.Combine(_root, "office-library");
        var officeTemplates = Path.Combine(_root, "office-templates");
        WriteFile(Path.Combine("office-library", "Casework", "Desk.rfa"));
        WriteFile(Path.Combine("office-templates", "Generic Model.rft"));
        var iniPath = WriteFile(
            "Revit.ini",
            $"""
            [DirectoriesENU]
            DataLibraryLocations=Office Library={officeLibrary}
            FamilyTemplatePath={officeTemplates}
            """);
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteCheckAsync(
            2026,
            "ENU",
            Path.Combine(_root, "missing-default-content"),
            iniPath,
            "json",
            output);

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal(iniPath, root.GetProperty("revitIniPath").GetString());
        Assert.Contains(root.GetProperty("libraryPaths").EnumerateArray(), path =>
            path.GetProperty("name").GetString() == "Office Library" &&
            path.GetProperty("source").GetString() == "revit.ini" &&
            path.GetProperty("rfaCount").GetInt32() == 1);
    }

    [Fact]
    public async Task Check_MissingLibrary_ReturnsErrorAndOfficialSources()
    {
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteCheckAsync(
            2026,
            "ENU",
            Path.Combine(_root, "empty-content"),
            revitIni: Path.Combine(_root, "missing.ini"),
            "json",
            output);

        Assert.Equal(1, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Contains(root.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "library-content-missing");
        Assert.Contains(root.GetProperty("officialSources").EnumerateArray(), source =>
            source.GetProperty("id").GetString() == "autodesk-account-libraries");
    }

    [Fact]
    public async Task Check_FindingsJson_EmitsBimOpsFindingEnvelope()
    {
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteCheckAsync(
            2026,
            "ENU",
            Path.Combine(_root, "empty-content"),
            revitIni: Path.Combine(_root, "missing.ini"),
            "json",
            output,
            findings: true);

        Assert.Equal(1, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("bimops-finding.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("library.check", root.GetProperty("source").GetString());
        Assert.False(root.GetProperty("requiresRevit").GetBoolean());
        Assert.True(root.GetProperty("summary").GetProperty("errors").GetInt32() >= 1);

        var findings = root.GetProperty("findings").EnumerateArray().ToArray();
        Assert.Contains(findings, finding =>
            finding.GetProperty("ruleId").GetString() == "library.library-content-missing" &&
            finding.GetProperty("severity").GetString() == "error" &&
            finding.GetProperty("fixability").GetString() == "manual");
        Assert.Contains(findings, finding =>
            finding.GetProperty("remediation").ValueKind == JsonValueKind.Object &&
            finding.GetProperty("remediation").GetProperty("verifyCommand").GetString() ==
            "revitcli library check --year 2026 --locale ENU --findings --output json");
    }

    [Fact]
    public async Task Check_FindingsRequiresJsonOutput()
    {
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteCheckAsync(
            2026,
            "ENU",
            Path.Combine(_root, "empty-content"),
            revitIni: Path.Combine(_root, "missing.ini"),
            "markdown",
            output,
            findings: true);

        Assert.Equal(1, exitCode);
        Assert.Contains("--findings currently requires '--output json'", output.ToString());
    }

    [Fact]
    public async Task Sources_Json_PrintsOfficialAutodeskAccountFallback()
    {
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteSourcesAsync(2026, "enu", "json", output);

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("revit-library-sources.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("ENU", root.GetProperty("locale").GetString());
        Assert.True(root.GetProperty("package").GetProperty("requiresAutodeskSignIn").GetBoolean());
        Assert.Contains(root.GetProperty("sources").EnumerateArray(), source =>
            source.GetProperty("id").GetString() == "load-autodesk-family");
    }

    [Fact]
    public async Task Download_WithoutUrl_ReturnsManualAuthRequired()
    {
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteDownloadAsync(
            2026,
            "ENU",
            Path.Combine(_root, "downloads"),
            packageUrl: null,
            openAccount: false,
            "json",
            output);

        Assert.Equal(1, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("manual-download-required", root.GetProperty("mode").GetString());
        Assert.Contains(root.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "manual-auth-required");
    }

    [Fact]
    public async Task Download_NonAutodeskUrl_IsRefused()
    {
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteDownloadAsync(
            2026,
            "ENU",
            Path.Combine(_root, "downloads"),
            "https://example.com/RevitContent.exe",
            openAccount: false,
            "json",
            output);

        Assert.Equal(1, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Contains(json.RootElement.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "non-autodesk-url");
    }

    [Fact]
    public async Task Download_OfficialUrl_WritesPackage()
    {
        var output = new StringWriter();
        using var client = new HttpClient(new DownloadHandler("installer"));

        var exitCode = await LibraryCommand.ExecuteDownloadAsync(
            2026,
            "ENU",
            Path.Combine(_root, "downloads"),
            "https://download.autodesk.com/RevitContent.exe",
            openAccount: false,
            "json",
            output,
            client);

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var packagePath = json.RootElement.GetProperty("packagePath").GetString();
        Assert.NotNull(packagePath);
        Assert.True(File.Exists(packagePath));
        Assert.Equal("installer", File.ReadAllText(packagePath!));
    }

    [Fact]
    public async Task Install_DryRun_DoesNotStartLauncher()
    {
        var package = WriteFile(Path.Combine("downloads", "RevitContent.exe"));
        var launched = false;
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteInstallAsync(
            package,
            dryRun: true,
            apply: false,
            yes: false,
            "json",
            output,
            launcher: _ =>
            {
                launched = true;
                return true;
            });

        Assert.Equal(0, exitCode);
        Assert.False(launched);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("dry-run", json.RootElement.GetProperty("mode").GetString());
        Assert.False(json.RootElement.GetProperty("processStarted").GetBoolean());
    }

    [Fact]
    public async Task Install_DryRunWithReceiptOutput_IsRefused()
    {
        var package = WriteFile(Path.Combine("downloads", "RevitContent.exe"));
        var receiptPath = Path.Combine(_root, "receipts", "library-install.json");
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteInstallAsync(
            package,
            dryRun: true,
            apply: false,
            yes: false,
            "json",
            output,
            receiptOutput: receiptPath);

        Assert.Equal(1, exitCode);
        Assert.False(File.Exists(receiptPath));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("refused", json.RootElement.GetProperty("mode").GetString());
        Assert.Contains(json.RootElement.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "receipt-output-refused");
    }

    [Fact]
    public async Task Install_ApplyWithoutYes_Refuses()
    {
        var package = WriteFile(Path.Combine("downloads", "RevitContent.exe"));
        var launched = false;
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteInstallAsync(
            package,
            dryRun: false,
            apply: true,
            yes: false,
            "json",
            output,
            launcher: _ =>
            {
                launched = true;
                return true;
            });

        Assert.Equal(1, exitCode);
        Assert.False(launched);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("refused", json.RootElement.GetProperty("mode").GetString());
        Assert.Contains(json.RootElement.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "confirmation-required");
    }

    [Fact]
    public async Task Install_ApplyYes_StartsLauncher()
    {
        var package = WriteFile(Path.Combine("downloads", "RevitContent.exe"));
        var launchedPath = "";
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteInstallAsync(
            package,
            dryRun: false,
            apply: true,
            yes: true,
            "json",
            output,
            launcher: path =>
            {
                launchedPath = path;
                return true;
            });

        Assert.Equal(0, exitCode);
        Assert.Equal(package, launchedPath);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("started", json.RootElement.GetProperty("mode").GetString());
        Assert.True(json.RootElement.GetProperty("processStarted").GetBoolean());
    }

    [Fact]
    public async Task Install_ApplyYes_WritesInstallerLaunchReceipt()
    {
        var package = WriteFile(Path.Combine("downloads", "RevitContent.exe"), "installer-bytes");
        var receiptPath = Path.Combine(_root, "receipts", "library-install.receipt.json");
        var baselinePath = WriteEnvBaseline();
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteInstallAsync(
            package,
            dryRun: false,
            apply: true,
            yes: true,
            "json",
            output,
            launcher: _ => true,
            receiptOutput: receiptPath,
            year: 2026,
            locale: "enu",
            envBaselinePath: baselinePath);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(receiptPath));
        using var json = JsonDocument.Parse(File.ReadAllText(receiptPath));
        var root = json.RootElement;
        Assert.Equal("revit-library-install.v1", root.GetProperty("schemaVersion").GetString());
        Assert.Equal("started", root.GetProperty("mode").GetString());
        Assert.True(root.GetProperty("processStarted").GetBoolean());
        Assert.True(root.GetProperty("receiptSaved").GetBoolean());
        Assert.Equal(receiptPath, root.GetProperty("receiptPath").GetString());
        Assert.Equal(2026, root.GetProperty("revitYear").GetInt32());
        Assert.Equal("ENU", root.GetProperty("locale").GetString());
        Assert.Equal("installer-bytes".Length, root.GetProperty("packageBytes").GetInt64());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("packageSha256").GetString()));
        Assert.Equal(
            "revitcli library check --year 2026 --locale ENU --output markdown",
            root.GetProperty("verifyCommand").GetString());
        var envBaseline = root.GetProperty("envBaseline");
        Assert.Equal("linked", envBaseline.GetProperty("status").GetString());
        Assert.Equal("revit-env-baseline.v1", envBaseline.GetProperty("schemaVersion").GetString());
        Assert.Equal(baselinePath, envBaseline.GetProperty("path").GetString());
        Assert.False(string.IsNullOrWhiteSpace(envBaseline.GetProperty("sha256").GetString()));

        using var stdout = JsonDocument.Parse(output.ToString());
        Assert.Equal(receiptPath, stdout.RootElement.GetProperty("receiptPath").GetString());
    }

    [Fact]
    public async Task RepairPlan_InvalidFamilyTemplatePath_WritesReversiblePlan()
    {
        var templateRoot = Path.Combine(_root, "content", "Family Templates");
        WriteFile(Path.Combine("content", "Libraries", "US Imperial", "Door.rfa"));
        WriteFile(Path.Combine("content", "Family Templates", "English_I", "Door.rft"));
        var badTemplatePath = Path.Combine(_root, "missing-templates");
        var iniPath = WriteFile(
            "Revit.ini",
            $"""
            [DirectoriesENU]
            FamilyTemplatePath={badTemplatePath}
            """);
        var planPath = Path.Combine(_root, "library.plan.json");
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteRepairPlanAsync(
            2026,
            "ENU",
            Path.Combine(_root, "content"),
            iniPath,
            planPath,
            "json",
            output);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(planPath));
        Assert.Contains(badTemplatePath, File.ReadAllText(iniPath));

        using var json = JsonDocument.Parse(File.ReadAllText(planPath));
        var root = json.RootElement;
        Assert.Equal("revit-library-repair-plan.v1", root.GetProperty("schemaVersion").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(1, root.GetProperty("actionCount").GetInt32());
        Assert.Equal(planPath, root.GetProperty("planOutputPath").GetString());
        var action = root.GetProperty("actions").EnumerateArray().Single();
        Assert.Equal("set-revit-ini-value", action.GetProperty("kind").GetString());
        Assert.Equal("DirectoriesENU", action.GetProperty("section").GetString());
        Assert.Equal("FamilyTemplatePath", action.GetProperty("key").GetString());
        Assert.Equal(badTemplatePath, action.GetProperty("beforeValue").GetString());
        Assert.Equal(templateRoot, action.GetProperty("afterValue").GetString());
        Assert.True(action.GetProperty("reversible").GetBoolean());
        Assert.Equal(badTemplatePath, action.GetProperty("rollback").GetProperty("restoreValue").GetString());

        using var stdoutJson = JsonDocument.Parse(output.ToString());
        Assert.Equal(planPath, stdoutJson.RootElement.GetProperty("planOutputPath").GetString());
    }

    [Fact]
    public async Task RepairPlan_NoTemplateCandidate_ReturnsFailureWithoutAction()
    {
        var iniPath = WriteFile(
            "Revit.ini",
            $"""
            [DirectoriesENU]
            FamilyTemplatePath={Path.Combine(_root, "missing-templates")}
            """);
        var output = new StringWriter();

        var exitCode = await LibraryCommand.ExecuteRepairPlanAsync(
            2026,
            "ENU",
            Path.Combine(_root, "empty-content"),
            iniPath,
            planOutput: null,
            "json",
            output);

        Assert.Equal(1, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(0, root.GetProperty("actionCount").GetInt32());
        Assert.Contains(root.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "family-template-candidate-missing");
    }

    [Fact]
    public async Task RepairApply_WritesReceiptAndRollbackRestoresRevitIni()
    {
        var templateRoot = Path.Combine(_root, "content", "Family Templates");
        WriteFile(Path.Combine("content", "Libraries", "US Imperial", "Door.rfa"));
        WriteFile(Path.Combine("content", "Family Templates", "English_I", "Door.rft"));
        var badTemplatePath = Path.Combine(_root, "missing-templates");
        var iniPath = WriteFile(
            "Revit.ini",
            $"""
            [DirectoriesENU]
            FamilyTemplatePath={badTemplatePath}
            """);
        var planPath = Path.Combine(_root, "library.plan.json");
        var baselinePath = WriteEnvBaseline();
        var planOutput = new StringWriter();
        Assert.Equal(0, await LibraryCommand.ExecuteRepairPlanAsync(
            2026,
            "ENU",
            Path.Combine(_root, "content"),
            iniPath,
            planPath,
            "json",
            planOutput));

        var applyOutput = new StringWriter();
        var applyExitCode = await LibraryCommand.ExecuteRepairApplyAsync(
            planPath,
            dryRun: false,
            yes: true,
            receiptOutput: null,
            "json",
            applyOutput,
            envBaselinePath: baselinePath);

        Assert.Equal(0, applyExitCode);
        Assert.Contains($"FamilyTemplatePath={templateRoot}", File.ReadAllText(iniPath));
        Assert.True(File.Exists(iniPath + ".revitcli.bak"));
        var receiptPath = planPath + ".receipt.json";
        Assert.True(File.Exists(receiptPath));
        using (var receiptJson = JsonDocument.Parse(File.ReadAllText(receiptPath)))
        {
            var root = receiptJson.RootElement;
            Assert.Equal("revit-library-repair-receipt.v1", root.GetProperty("schemaVersion").GetString());
            Assert.True(root.GetProperty("success").GetBoolean());
            Assert.Equal(1, root.GetProperty("appliedCount").GetInt32());
            Assert.Equal(receiptPath, root.GetProperty("receiptPath").GetString());
            var envBaseline = root.GetProperty("envBaseline");
            Assert.Equal("linked", envBaseline.GetProperty("status").GetString());
            Assert.Equal(baselinePath, envBaseline.GetProperty("path").GetString());
        }

        var rollbackOutput = new StringWriter();
        var rollbackExitCode = await LibraryCommand.ExecuteRepairRollbackAsync(
            receiptPath,
            dryRun: false,
            yes: true,
            "json",
            rollbackOutput);

        Assert.Equal(0, rollbackExitCode);
        Assert.Contains($"FamilyTemplatePath={badTemplatePath}", File.ReadAllText(iniPath));
        Assert.True(File.Exists(iniPath + ".revitcli.rollback.bak"));
        using var rollbackJson = JsonDocument.Parse(rollbackOutput.ToString());
        Assert.Equal("revit-library-repair-rollback.v1", rollbackJson.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(1, rollbackJson.RootElement.GetProperty("rolledBackCount").GetInt32());
    }

    [Fact]
    public async Task RepairApply_DryRunDoesNotWriteReceiptOrRevitIni()
    {
        WriteFile(Path.Combine("content", "Libraries", "US Imperial", "Door.rfa"));
        WriteFile(Path.Combine("content", "Family Templates", "English_I", "Door.rft"));
        var badTemplatePath = Path.Combine(_root, "missing-templates");
        var iniPath = WriteFile(
            "Revit.ini",
            $"""
            [DirectoriesENU]
            FamilyTemplatePath={badTemplatePath}
            """);
        var planPath = Path.Combine(_root, "library.plan.json");
        var planOutput = new StringWriter();
        Assert.Equal(0, await LibraryCommand.ExecuteRepairPlanAsync(
            2026,
            "ENU",
            Path.Combine(_root, "content"),
            iniPath,
            planPath,
            "json",
            planOutput));

        var before = File.ReadAllText(iniPath);
        var applyOutput = new StringWriter();
        var applyExitCode = await LibraryCommand.ExecuteRepairApplyAsync(
            planPath,
            dryRun: true,
            yes: false,
            receiptOutput: null,
            "json",
            applyOutput);

        Assert.Equal(0, applyExitCode);
        Assert.Equal(before, File.ReadAllText(iniPath));
        Assert.False(File.Exists(planPath + ".receipt.json"));
        using var json = JsonDocument.Parse(applyOutput.ToString());
        Assert.True(json.RootElement.GetProperty("dryRun").GetBoolean());
        Assert.Equal(0, json.RootElement.GetProperty("appliedCount").GetInt32());
    }

    private string WriteFile(string relativePath, string content = "")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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

    private sealed class DownloadHandler : HttpMessageHandler
    {
        private readonly string _body;

        public DownloadHandler(string body)
        {
            _body = body;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_body),
            };
            response.Content.Headers.ContentDisposition = new("attachment")
            {
                FileName = "\"RevitCPENU.exe\"",
            };
            return Task.FromResult(response);
        }
    }
}
