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

    private string WriteFile(string relativePath, string content = "")
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
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
