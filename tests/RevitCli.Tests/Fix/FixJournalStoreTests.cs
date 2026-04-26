using System;
using System.IO;
using RevitCli.Fix;
using Xunit;

namespace RevitCli.Tests.Fix;

public class FixJournalStoreTests
{
    [Fact]
    public void SaveAndLoad_RoundTripsJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"revitcli_fix_journal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var baseline = Path.Combine(dir, "fix-baseline.json");
            var journal = new FixJournal
            {
                SchemaVersion = 1,
                CheckName = "default",
                BaselinePath = baseline,
                StartedAt = "2026-04-26T00:00:00Z",
                User = "test"
            };
            journal.Actions.Add(new FixAction
            {
                Rule = "required-parameter",
                Strategy = "setParam",
                ElementId = 100,
                Category = "doors",
                Parameter = "Mark",
                OldValue = "",
                NewValue = "D-100",
                Confidence = "high"
            });

            var path = FixJournalStore.SaveForBaseline(baseline, journal);
            var loaded = FixJournalStore.LoadForBaseline(baseline);

            Assert.Equal(path, FixJournalStore.GetJournalPath(baseline));
            Assert.Equal("default", loaded.CheckName);
            var action = Assert.Single(loaded.Actions);
            Assert.Equal(100, action.ElementId);
            Assert.Equal("D-100", action.NewValue);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void LoadForBaseline_InvalidJson_ThrowsInvalidDataException()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"revitcli_fix_journal_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var baseline = Path.Combine(dir, "fix-baseline.json");
            var path = FixJournalStore.GetJournalPath(baseline);
            File.WriteAllText(path, "{ this is not valid json");

            var ex = Assert.Throws<InvalidDataException>(() => FixJournalStore.LoadForBaseline(baseline));

            Assert.Contains(path, ex.Message);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }
}
