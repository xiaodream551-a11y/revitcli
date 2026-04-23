using System.IO;
using System.Text.Json;
using RevitCli.Output;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Output;

public class BaselineManagerTests
{
    private static string TempPath() =>
        Path.Combine(Path.GetTempPath(), "revitcli-baseline-test-" + System.Guid.NewGuid() + ".json");

    [Fact]
    public void Load_MissingFile_ReturnsNull()
    {
        var path = TempPath();
        Assert.False(File.Exists(path));

        var result = BaselineManager.Load(path);

        Assert.Null(result);
    }

    [Fact]
    public void Load_ValidSnapshot_ReturnsIt()
    {
        var path = TempPath();
        var original = new ModelSnapshot
        {
            SchemaVersion = 1,
            TakenAt = "2026-04-23T00:00:00Z",
            Revit = new SnapshotRevit { Version = "2026", DocumentPath = "/a.rvt" }
        };
        File.WriteAllText(path, JsonSerializer.Serialize(original));
        try
        {
            var loaded = BaselineManager.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal("2026", loaded!.Revit.Version);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Load_CorruptedJson_ReturnsNull()
    {
        var path = TempPath();
        File.WriteAllText(path, "{not valid json");
        try
        {
            var result = BaselineManager.Load(path);
            Assert.Null(result);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Save_WritesJsonAtomically()
    {
        var path = TempPath();
        var snap = new ModelSnapshot { SchemaVersion = 1, TakenAt = "2026-04-23T10:00:00Z" };
        try
        {
            BaselineManager.Save(path, snap);
            Assert.True(File.Exists(path));

            var loaded = BaselineManager.Load(path);
            Assert.NotNull(loaded);
            Assert.Equal("2026-04-23T10:00:00Z", loaded!.TakenAt);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }

    [Fact]
    public void Save_CreatesParentDirectory_IfMissing()
    {
        var dir = Path.Combine(Path.GetTempPath(), "revitcli-baseline-" + System.Guid.NewGuid());
        var path = Path.Combine(dir, "nested", "baseline.json");
        var snap = new ModelSnapshot { SchemaVersion = 1 };
        try
        {
            BaselineManager.Save(path, snap);
            Assert.True(File.Exists(path));
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public void Save_OverwritesExistingFile()
    {
        var path = TempPath();
        var a = new ModelSnapshot { SchemaVersion = 1, TakenAt = "first" };
        var b = new ModelSnapshot { SchemaVersion = 1, TakenAt = "second" };
        try
        {
            BaselineManager.Save(path, a);
            BaselineManager.Save(path, b);

            var loaded = BaselineManager.Load(path);
            Assert.Equal("second", loaded!.TakenAt);
        }
        finally { if (File.Exists(path)) File.Delete(path); }
    }
}
