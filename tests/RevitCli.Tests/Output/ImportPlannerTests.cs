using System.Collections.Generic;
using RevitCli.Output;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Output;

public class ImportPlannerTests
{
    private static List<ElementInfo> Elements(params (long Id, string Mark, string? Lock)[] items)
    {
        var result = new List<ElementInfo>();
        foreach (var (id, mark, lck) in items)
        {
            var p = new Dictionary<string, string> { ["Mark"] = mark };
            if (lck != null) p["Lock"] = lck;
            result.Add(new ElementInfo
            {
                Id = id,
                Name = $"E{id}",
                Category = "doors",
                TypeName = "Door",
                Parameters = p
            });
        }
        return result;
    }

    private static CsvData Csv(List<string> headers, params List<string>[] rows) =>
        new() { Headers = headers, Rows = new List<List<string>>(rows) };

    [Fact]
    public void Plan_AllRowsMatch_OneGroupPerUniqueParamValuePair()
    {
        var elements = Elements((101, "W01", null), (102, "W02", null));
        var csv = Csv(
            new List<string> { "Mark", "Lock" },
            new List<string> { "W01", "YALE-500" },
            new List<string> { "W02", "YALE-500" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Single(plan.Groups);
        var g = plan.Groups[0];
        Assert.Equal("Lock", g.Param);
        Assert.Equal("YALE-500", g.Value);
        Assert.Equal(new[] { 101L, 102L }, g.ElementIds);
        Assert.Empty(plan.Misses);
        Assert.Empty(plan.Duplicates);
    }

    [Fact]
    public void Plan_DifferentValuesPerRow_ProducesGroupPerValue()
    {
        var elements = Elements((101, "W01", null), (102, "W02", null));
        var csv = Csv(
            new List<string> { "Mark", "Lock" },
            new List<string> { "W01", "A" },
            new List<string> { "W02", "B" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Equal(2, plan.Groups.Count);
        Assert.Contains(plan.Groups, g => g.Value == "A" && g.ElementIds[0] == 101);
        Assert.Contains(plan.Groups, g => g.Value == "B" && g.ElementIds[0] == 102);
    }

    [Fact]
    public void Plan_EmptyCell_SkipsThatColumnForRow()
    {
        var elements = Elements((101, "W01", null));
        var csv = Csv(
            new List<string> { "Mark", "Lock", "Fire" },
            new List<string> { "W01", "", "甲级" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock", ["Fire"] = "FireRating" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Single(plan.Groups);
        Assert.Equal("FireRating", plan.Groups[0].Param);
        Assert.Equal("甲级", plan.Groups[0].Value);
        Assert.Single(plan.Skipped);
        Assert.Equal("W01", plan.Skipped[0].MatchByValue);
        Assert.Equal("Lock", plan.Skipped[0].Param);
    }

    [Fact]
    public void Plan_RowMatchByValueNotInRevit_OnMissingError_RecordsMiss()
    {
        var elements = Elements((101, "W01", null));
        var csv = Csv(
            new List<string> { "Mark", "Lock" },
            new List<string> { "W99", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "error", "error");

        Assert.Empty(plan.Groups);
        Assert.Single(plan.Misses);
        Assert.Equal("W99", plan.Misses[0].MatchByValue);
        Assert.Equal(2, plan.Misses[0].RowNumber); // header is row 1, first data row is row 2
    }

    [Fact]
    public void Plan_MultipleElementsShareMatchByValue_OnDuplicateError_RecordsDuplicate()
    {
        var elements = Elements((101, "W01", null), (102, "W01", null));
        var csv = Csv(
            new List<string> { "Mark", "Lock" },
            new List<string> { "W01", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Empty(plan.Groups);
        Assert.Single(plan.Duplicates);
        Assert.Equal(new[] { 101L, 102L }, plan.Duplicates[0].ElementIds);
    }

    [Fact]
    public void Plan_OnDuplicateFirst_PicksLowestId()
    {
        var elements = Elements((102, "W01", null), (101, "W01", null));
        var csv = Csv(
            new List<string> { "Mark", "Lock" },
            new List<string> { "W01", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "first");

        Assert.Single(plan.Groups);
        Assert.Equal(new[] { 101L }, plan.Groups[0].ElementIds);
        Assert.Empty(plan.Duplicates);
    }

    [Fact]
    public void Plan_OnDuplicateAll_AppliesToAllMatches()
    {
        var elements = Elements((101, "W01", null), (102, "W01", null));
        var csv = Csv(
            new List<string> { "Mark", "Lock" },
            new List<string> { "W01", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "all");

        Assert.Single(plan.Groups);
        Assert.Equal(new[] { 101L, 102L }, plan.Groups[0].ElementIds);
        Assert.Empty(plan.Duplicates);
    }

    [Fact]
    public void Plan_TwoRowsSameElementSameParam_LastWins_WithWarning()
    {
        var elements = Elements((101, "W01", null));
        var csv = Csv(
            new List<string> { "Mark", "Lock" },
            new List<string> { "W01", "A" },
            new List<string> { "W01", "B" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Single(plan.Groups);
        Assert.Equal("B", plan.Groups[0].Value);
        Assert.Single(plan.Warnings);
        Assert.Contains("W01", plan.Warnings[0]);
        Assert.Contains("Lock", plan.Warnings[0]);
    }

    [Fact]
    public void Plan_MatchByValueWhitespace_TrimmedBeforeCompare()
    {
        var elements = Elements((101, "W01", null));
        var csv = Csv(
            new List<string> { "Mark", "Lock" },
            new List<string> { "  W01  ", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Single(plan.Groups);
        Assert.Equal(new[] { 101L }, plan.Groups[0].ElementIds);
    }

    [Fact]
    public void Plan_RowMissingMatchByCell_RecordedAsSkippedWithReason()
    {
        var elements = Elements((101, "W01", null));
        var csv = Csv(
            new List<string> { "Mark", "Lock" },
            new List<string> { "", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Empty(plan.Groups);
        Assert.Empty(plan.Misses);
        Assert.Single(plan.Skipped);
        Assert.Contains("empty", plan.Skipped[0].Reason);
    }

    [Fact]
    public void Plan_OnMissingSkip_DropsMissSilently_NoRecord()
    {
        var elements = Elements((101, "W01", null));
        var csv = Csv(
            new List<string> { "Mark", "Lock" },
            new List<string> { "W99", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "skip", "error");

        Assert.Empty(plan.Groups);
        Assert.Empty(plan.Misses);          // skip drops without recording
        Assert.Empty(plan.Skipped);         // not the same as Skipped (which tracks empty cells)
    }

    [Fact]
    public void Plan_OnMissingWarn_RecordsMiss_LikeError_ButCallerInterprets()
    {
        var elements = Elements((101, "W01", null));
        var csv = Csv(
            new List<string> { "Mark", "Lock" },
            new List<string> { "W99", "X" });
        var mapping = new Dictionary<string, string> { ["Lock"] = "Lock" };

        var plan = ImportPlanner.Plan(csv, elements, mapping, "Mark", "warn", "error");

        Assert.Empty(plan.Groups);
        Assert.Single(plan.Misses);
        Assert.Equal("W99", plan.Misses[0].MatchByValue);
    }
}
