using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using RevitCli.Client;
using RevitCli.Commands;
using RevitCli.Config;
using RevitCli.Shared;
using RevitCli.Tests.Client;
using Xunit;

namespace RevitCli.Tests.Commands;

public class DoctorCommandTests
{
    private static DoctorEnvironment CreateDoctorEnvironment()
    {
        var root = Path.Combine(Path.GetTempPath(), $"revitcli_doctor_{System.Guid.NewGuid():N}");
        var userProfile = Path.Combine(root, "user");
        var appData = Path.Combine(root, "appdata");
        var revitDir = Path.Combine(root, "Revit 2026");
        Directory.CreateDirectory(userProfile);
        Directory.CreateDirectory(appData);
        Directory.CreateDirectory(revitDir);
        File.WriteAllText(Path.Combine(revitDir, "RevitAPI.dll"), "");
        File.WriteAllText(Path.Combine(revitDir, "RevitAPIUI.dll"), "");

        var addins = Path.Combine(appData, "Autodesk", "Revit", "Addins", "2026");
        Directory.CreateDirectory(addins);
        var addinDll = Path.Combine(root, "addin", "RevitCli.Addin.dll");
        Directory.CreateDirectory(Path.GetDirectoryName(addinDll)!);
        File.WriteAllText(addinDll, "");
        File.WriteAllText(Path.Combine(addins, "RevitCli.addin"),
            $@"<?xml version=""1.0"" encoding=""utf-8""?>
<RevitAddIns>
  <AddIn Type=""Application"">
    <Assembly>{addinDll}</Assembly>
  </AddIn>
</RevitAddIns>");

        return new DoctorEnvironment
        {
            UserProfile = userProfile,
            AppData = appData,
            Revit2026InstallDir = revitDir
        };
    }

    [Fact]
    public async Task Execute_ServerDown_PrintsFail()
    {
        var handler = new FakeHttpHandler(throwException: true);
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var config = new CliConfig();
        var writer = new StringWriter();

        var exitCode = await DoctorCommand.ExecuteAsync(client, config, writer, CreateDoctorEnvironment());

        var output = writer.ToString();
        Assert.Contains("FAIL", output);
        Assert.Contains("Server URL", output);
        Assert.Equal(1, exitCode);
    }

    [Fact]
    public async Task Execute_ServerUp_PrintsOk()
    {
        var status = new StatusInfo { RevitVersion = "2026", RevitYear = 2026, DocumentName = "Test.rvt" };
        var response = ApiResponse<StatusInfo>.Ok(status);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var config = new CliConfig();
        var writer = new StringWriter();

        var exitCode = await DoctorCommand.ExecuteAsync(client, config, writer, CreateDoctorEnvironment());

        var output = writer.ToString();
        Assert.Contains("Connected to Revit 2026", output);
        Assert.Contains("Test.rvt", output);
        Assert.Equal(0, exitCode);
    }

    [Fact]
    public async Task Execute_WrongRevitYear_ReturnsFailure()
    {
        var status = new StatusInfo { RevitVersion = "2025", RevitYear = 2025, DocumentName = "Test.rvt" };
        var response = ApiResponse<StatusInfo>.Ok(status);
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(response));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await DoctorCommand.ExecuteAsync(client, new CliConfig(), writer, CreateDoctorEnvironment());

        Assert.Equal(1, exitCode);
        Assert.Contains("Revit 2026", writer.ToString());
        Assert.Contains("2025", writer.ToString());
    }

    [Fact]
    public async Task Execute_MissingRevitApiDlls_PrintsRevit2026PrecheckFailure()
    {
        var environment = CreateDoctorEnvironment();
        File.Delete(Path.Combine(environment.Revit2026InstallDir!, "RevitAPI.dll"));
        var status = new StatusInfo { RevitVersion = "2026", DocumentName = "Test.rvt" };
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(ApiResponse<StatusInfo>.Ok(status)));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await DoctorCommand.ExecuteAsync(client, new CliConfig(), writer, environment);

        Assert.Equal(1, exitCode);
        Assert.Contains("Revit 2026 API", writer.ToString());
        Assert.Contains("RevitAPI.dll", writer.ToString());
    }

    [Fact]
    public async Task Execute_MissingManifest_PrintsAddinManifestFailure()
    {
        var environment = CreateDoctorEnvironment();
        File.Delete(Path.Combine(environment.AppData, "Autodesk", "Revit", "Addins", "2026", "RevitCli.addin"));
        var status = new StatusInfo { RevitVersion = "2026", DocumentName = "Test.rvt" };
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(ApiResponse<StatusInfo>.Ok(status)));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await DoctorCommand.ExecuteAsync(client, new CliConfig(), writer, environment);

        Assert.Equal(1, exitCode);
        Assert.Contains("Add-in manifest", writer.ToString());
        Assert.Contains("RevitCli.addin", writer.ToString());
    }

    [Fact]
    public async Task Execute_StaleServerInfo_PrintsStalePidFailure()
    {
        var environment = CreateDoctorEnvironment();
        var serverDir = Path.GetDirectoryName(environment.ServerInfoPath)!;
        Directory.CreateDirectory(serverDir);
        File.WriteAllText(environment.ServerInfoPath, JsonSerializer.Serialize(new ServerInfo
        {
            Port = 17839,
            Pid = int.MaxValue,
            RevitVersion = "2026",
            Token = "abc"
        }));
        var handler = new FakeHttpHandler(throwException: true);
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await DoctorCommand.ExecuteAsync(client, new CliConfig(), writer, environment);

        Assert.Equal(1, exitCode);
        Assert.Contains("stale", writer.ToString(), System.StringComparison.OrdinalIgnoreCase);
        Assert.Contains("pid", writer.ToString(), System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Execute_ServerInfoPidBelongsToNonRevitProcess_ReturnsFailure()
    {
        var environment = CreateDoctorEnvironment();
        var serverDir = Path.GetDirectoryName(environment.ServerInfoPath)!;
        Directory.CreateDirectory(serverDir);
        File.WriteAllText(environment.ServerInfoPath, JsonSerializer.Serialize(new ServerInfo
        {
            Port = 17839,
            Pid = System.Environment.ProcessId,
            RevitVersion = "2026",
            Token = "abc"
        }));
        var status = new StatusInfo { RevitVersion = "2026", RevitYear = 2026, DocumentName = "Test.rvt" };
        var handler = new FakeHttpHandler(JsonSerializer.Serialize(ApiResponse<StatusInfo>.Ok(status)));
        var client = new RevitClient(new HttpClient(handler) { BaseAddress = new System.Uri("http://localhost:17839") });
        var writer = new StringWriter();

        var exitCode = await DoctorCommand.ExecuteAsync(client, new CliConfig(), writer, environment);

        Assert.Equal(1, exitCode);
        Assert.Contains("not Revit", writer.ToString(), System.StringComparison.OrdinalIgnoreCase);
    }
}
