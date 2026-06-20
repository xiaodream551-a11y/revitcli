using System.Text.Json;
using RevitCli.Commands;

namespace RevitCli.Tests.Commands;

public sealed class EvidenceCommandTests : IDisposable
{
    private readonly string _root;

    public EvidenceCommandTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "revitcli-evidence-tests-" + Guid.NewGuid().ToString("N"));
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
    public async Task PackageJson_CopiesLocalArtifactsAndWritesManifest()
    {
        WriteProjectArtifact(".revitcli/analytics/ledger-stats.json", """{"schemaVersion":"ledger-stats.v1","summary":{"operationCount":1}}""");
        WriteProjectArtifact(".revitcli/deliveries/manifest.jsonl", """{"schemaVersion":"delivery-manifest-entry.v1","receipt":"receipts/export.json"}""");
        WriteProjectArtifact(".revitcli/crash/latest/manifest.json", """{"schemaVersion":"revit-crash-packet-manifest.v1","fileCount":1}""");
        WriteProjectArtifact(".revitcli/journal.jsonl", """{"schemaVersion":"journal-entry.v1","action":"status"}""");
        var packet = Path.Combine(_root, "packet");
        var output = new StringWriter();

        var exitCode = await EvidenceCommand.ExecutePackageAsync(_root, packet, "json", output);

        Assert.Equal(0, exitCode);
        var manifestPath = Path.Combine(packet, "evidence-manifest.json");
        Assert.True(File.Exists(manifestPath));
        using var manifest = JsonDocument.Parse(File.ReadAllText(manifestPath));
        var root = manifest.RootElement;
        Assert.Equal("bimops-evidence-package.v1", root.GetProperty("schemaVersion").GetString());
        Assert.True(root.GetProperty("success").GetBoolean());
        Assert.Equal(Path.GetFullPath(_root), root.GetProperty("sourceDirectory").GetString());
        Assert.Equal(4, root.GetProperty("summary").GetProperty("artifactCount").GetInt32());
        Assert.Contains(root.GetProperty("artifacts").EnumerateArray(), artifact =>
            artifact.GetProperty("category").GetString() == "ledger" &&
            artifact.GetProperty("schemaHint").GetString() == "ledger-stats.v1" &&
            artifact.GetProperty("sha256").GetString()!.Length == 64 &&
            File.Exists(Path.Combine(packet, artifact.GetProperty("packetRelativePath").GetString()!)));
        Assert.Contains("bimops-evidence-package.v1", output.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PackageJson_DoesNotSkipSiblingDirectoryWithPacketPrefix()
    {
        WriteProjectArtifact(".revitcli/evidence-extra/summary.json", """{"schemaVersion":"custom-evidence.v1"}""");
        var packet = Path.Combine(_root, ".revitcli", "evidence");
        var output = new StringWriter();

        var exitCode = await EvidenceCommand.ExecutePackageAsync(_root, packet, "json", output);

        Assert.Equal(0, exitCode);
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(packet, "evidence-manifest.json")));
        var artifacts = manifest.RootElement.GetProperty("artifacts").EnumerateArray().ToArray();
        Assert.Contains(artifacts, artifact =>
            artifact.GetProperty("sourceRelativePath").GetString() == ".revitcli/evidence-extra/summary.json" &&
            artifact.GetProperty("schemaHint").GetString() == "custom-evidence.v1");
        Assert.DoesNotContain(artifacts, artifact =>
            artifact.GetProperty("sourceRelativePath").GetString()!.StartsWith(".revitcli/evidence/artifacts/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task VerifyJson_FailsWhenPackagedArtifactHashChanges()
    {
        WriteProjectArtifact(".revitcli/analytics/ledger-stats.json", """{"schemaVersion":"ledger-stats.v1"}""");
        var packet = Path.Combine(_root, "packet");
        Assert.Equal(0, await EvidenceCommand.ExecutePackageAsync(_root, packet, "json", new StringWriter()));
        var packagedArtifact = Directory.GetFiles(Path.Combine(packet, "artifacts"), "*", SearchOption.AllDirectories)
            .Single(path => Path.GetFileName(path) == "ledger-stats.json");
        await File.AppendAllTextAsync(packagedArtifact, "tampered");
        var output = new StringWriter();

        var exitCode = await EvidenceCommand.ExecuteVerifyAsync(packet, "json", output);

        Assert.Equal(1, exitCode);
        using var verify = JsonDocument.Parse(output.ToString());
        var root = verify.RootElement;
        Assert.Equal("bimops-evidence-package-verify.v1", root.GetProperty("schemaVersion").GetString());
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Contains(root.GetProperty("issues").EnumerateArray(), issue =>
            issue.GetProperty("code").GetString() == "sha256-mismatch");
    }

    [Fact]
    public async Task SiteJson_WritesStaticIndexAndManifestCopy()
    {
        WriteProjectArtifact(".revitcli/reports/weekly.md", "# Weekly report");
        var packet = Path.Combine(_root, "packet");
        var site = Path.Combine(_root, "site");
        Assert.Equal(0, await EvidenceCommand.ExecutePackageAsync(_root, packet, "json", new StringWriter()));
        var output = new StringWriter();

        var exitCode = await EvidenceCommand.ExecuteSiteAsync(packet, site, "json", output);

        Assert.Equal(0, exitCode);
        var indexPath = Path.Combine(site, "index.html");
        var manifestPath = Path.Combine(site, "evidence-manifest.json");
        Assert.True(File.Exists(indexPath));
        Assert.True(File.Exists(manifestPath));
        Assert.Contains("BIMOps Evidence Package", File.ReadAllText(indexPath), StringComparison.Ordinal);
        using var result = JsonDocument.Parse(output.ToString());
        Assert.Equal("bimops-evidence-site.v1", result.RootElement.GetProperty("schemaVersion").GetString());
        Assert.Equal(Path.GetFullPath(site), result.RootElement.GetProperty("siteDirectory").GetString());
    }

    private void WriteProjectArtifact(string relativePath, string content)
    {
        var path = Path.Combine(_root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
