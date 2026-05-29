using System.Text.Json;
using RevitCli.Commands;

namespace RevitCli.Tests.Commands;

[Collection("Sequential")]
public sealed class RvtCommandTests : IDisposable
{
    private readonly string _root;

    public RvtCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "revitcli-rvt-tests-" + Guid.NewGuid().ToString("N"));
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
    public async Task Scan_Json_ClassifiesMainBackupsAndOrphans()
    {
        WriteRvt("model.rvt");
        WriteRvt("model.0001.rvt");
        WriteRvt("Upper.RVT");
        WriteRvt("Upper.0001.RVT");
        WriteRvt("project.2026.rvt");
        WriteRvt("orphan.0002.rvt");
        WriteRvt(Path.Combine("nested", "nested.rvt"));
        var output = new StringWriter();

        var exitCode = await RvtCommand.ExecuteScanAsync(_root, nonRecursive: false, outputFormat: "json", output);

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        var root = json.RootElement;
        Assert.Equal("rvt-file-scan.v1", root.GetProperty("schemaVersion").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        var summary = root.GetProperty("summary");
        Assert.Equal(7, summary.GetProperty("totalRvtFileCount").GetInt32());
        Assert.Equal(4, summary.GetProperty("mainFileCount").GetInt32());
        Assert.Equal(2, summary.GetProperty("backupFileCount").GetInt32());
        Assert.Equal(1, summary.GetProperty("orphanBackupFileCount").GetInt32());

        var files = root.GetProperty("files").EnumerateArray().ToArray();
        Assert.Contains(files, file =>
            file.GetProperty("relativePath").GetString() == "model.0001.rvt" &&
            file.GetProperty("kind").GetString() == "backup" &&
            file.GetProperty("mainRelativePath").GetString() == "model.rvt");
        Assert.Contains(files, file =>
            file.GetProperty("relativePath").GetString() == "Upper.0001.RVT" &&
            file.GetProperty("kind").GetString() == "backup" &&
            file.GetProperty("mainRelativePath").GetString() == "Upper.RVT");
        Assert.Contains(files, file =>
            file.GetProperty("relativePath").GetString() == "orphan.0002.rvt" &&
            file.GetProperty("kind").GetString() == "orphanBackup");
        Assert.Contains(files, file =>
            file.GetProperty("relativePath").GetString() == "project.2026.rvt" &&
            file.GetProperty("kind").GetString() == "main");
    }

    [Fact]
    public async Task CleanBackups_DryRun_DoesNotDeleteAndWritesReport()
    {
        WriteRvt("model.rvt");
        var backup1 = WriteRvt("model.0001.rvt");
        var backup2 = WriteRvt("model.0002.rvt");
        var reportPath = Path.Combine(_root, ".revitcli", "reports", "rvt-backups.json");
        var output = new StringWriter();

        var exitCode = await RvtCommand.ExecuteCleanBackupsAsync(
            _root,
            dryRun: true,
            apply: false,
            yes: false,
            includeOrphans: false,
            nonRecursive: false,
            olderThan: null,
            reportPath,
            outputFormat: "json",
            output);

        Assert.Equal(0, exitCode);
        Assert.True(File.Exists(backup1));
        Assert.True(File.Exists(backup2));
        Assert.True(File.Exists(reportPath));
        using var report = JsonDocument.Parse(File.ReadAllText(reportPath));
        Assert.Equal("rvt-backup-cleanup-report.v1", report.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(Path.GetFullPath(reportPath), report.RootElement.GetProperty("reportPath").GetString());
        Assert.Equal(2, report.RootElement.GetProperty("summary").GetProperty("deleteCandidateCount").GetInt32());
        Assert.Equal(0, report.RootElement.GetProperty("summary").GetProperty("deletedCount").GetInt32());
        using var stdout = JsonDocument.Parse(output.ToString());
        Assert.Equal(2, stdout.RootElement.GetProperty("summary").GetProperty("deleteCandidateCount").GetInt32());
    }

    [Fact]
    public async Task CleanBackups_ApplyWithoutYes_RefusesAndKeepsFiles()
    {
        WriteRvt("model.rvt");
        var backup = WriteRvt("model.0001.rvt");
        var output = new StringWriter();

        var exitCode = await RvtCommand.ExecuteCleanBackupsAsync(
            _root,
            dryRun: false,
            apply: true,
            yes: false,
            includeOrphans: false,
            nonRecursive: false,
            olderThan: null,
            reportPath: null,
            outputFormat: "json",
            output);

        Assert.Equal(1, exitCode);
        Assert.True(File.Exists(backup));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal("refused", json.RootElement.GetProperty("mode").GetString());
        Assert.Contains(json.RootElement.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "confirmation-required");
    }

    [Fact]
    public async Task CleanBackups_ApplyYes_DeletesOnlyAssociatedLeadingZeroBackups()
    {
        WriteRvt("model.rvt");
        var backup = WriteRvt("model.0001.rvt");
        var orphan = WriteRvt("orphan.0001.rvt");
        var yearNamedFile = WriteRvt("project.2026.rvt");
        var nonLeadingZeroFile = WriteRvt("model.1000.rvt");
        var output = new StringWriter();

        var exitCode = await RvtCommand.ExecuteCleanBackupsAsync(
            _root,
            dryRun: false,
            apply: true,
            yes: true,
            includeOrphans: false,
            nonRecursive: false,
            olderThan: null,
            reportPath: null,
            outputFormat: "json",
            output);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(backup));
        Assert.True(File.Exists(orphan));
        Assert.True(File.Exists(yearNamedFile));
        Assert.True(File.Exists(nonLeadingZeroFile));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal("applied", json.RootElement.GetProperty("mode").GetString());
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("deletedCount").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("orphanBackupFileCount").GetInt32());
    }

    [Fact]
    public async Task CleanBackups_IncludeOrphans_DeletesOrphanBackups()
    {
        var orphan = WriteRvt("orphan.0001.rvt");
        var output = new StringWriter();

        var exitCode = await RvtCommand.ExecuteCleanBackupsAsync(
            _root,
            dryRun: false,
            apply: true,
            yes: true,
            includeOrphans: true,
            nonRecursive: false,
            olderThan: null,
            reportPath: null,
            outputFormat: "json",
            output);

        Assert.Equal(0, exitCode);
        Assert.False(File.Exists(orphan));
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("deleteCandidateCount").GetInt32());
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("deletedCount").GetInt32());
    }

    [Fact]
    public async Task CleanBackups_OlderThan_FiltersDeleteCandidates()
    {
        WriteRvt("model.rvt");
        var oldBackup = WriteRvt("model.0001.rvt");
        var newBackup = WriteRvt("model.0002.rvt");
        File.SetLastWriteTimeUtc(oldBackup, DateTime.UtcNow.AddDays(-10));
        File.SetLastWriteTimeUtc(newBackup, DateTime.UtcNow.AddHours(-1));
        var output = new StringWriter();

        var exitCode = await RvtCommand.ExecuteCleanBackupsAsync(
            _root,
            dryRun: true,
            apply: false,
            yes: false,
            includeOrphans: false,
            nonRecursive: false,
            olderThan: "7d",
            reportPath: null,
            outputFormat: "json",
            output);

        Assert.Equal(0, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.Equal(1, json.RootElement.GetProperty("summary").GetProperty("deleteCandidateCount").GetInt32());
        var files = json.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Contains(files, file =>
            file.GetProperty("relativePath").GetString() == "model.0001.rvt" &&
            file.GetProperty("deleteCandidate").GetBoolean());
        Assert.Contains(files, file =>
            file.GetProperty("relativePath").GetString() == "model.0002.rvt" &&
            !file.GetProperty("deleteCandidate").GetBoolean() &&
            file.GetProperty("skippedReason").GetString() == "newer-than-threshold");
    }

    [Fact]
    public async Task CleanBackups_MissingRoot_ReturnsJsonError()
    {
        var output = new StringWriter();
        var missing = Path.Combine(_root, "missing");

        var exitCode = await RvtCommand.ExecuteCleanBackupsAsync(
            missing,
            dryRun: true,
            apply: false,
            yes: false,
            includeOrphans: false,
            nonRecursive: false,
            olderThan: null,
            reportPath: null,
            outputFormat: "json",
            output);

        Assert.Equal(1, exitCode);
        using var json = JsonDocument.Parse(output.ToString());
        Assert.False(json.RootElement.GetProperty("success").GetBoolean());
        Assert.Contains(json.RootElement.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "root-missing");
    }

    private string WriteRvt(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "rvt");
        return path;
    }
}
