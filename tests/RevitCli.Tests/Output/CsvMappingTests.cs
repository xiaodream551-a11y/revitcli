using System.Collections.Generic;
using RevitCli.Output;
using Xunit;

namespace RevitCli.Tests.Output;

public class CsvMappingTests
{
    [Fact]
    public void Build_NullMap_DefaultsToIdentity_ExcludesMatchByColumn()
    {
        var headers = new List<string> { "Mark", "锁具型号", "耐火等级" };
        var map = CsvMapping.Build(rawMap: null, headers, matchBy: "Mark");

        Assert.False(map.ContainsKey("Mark"));
        Assert.Equal("锁具型号", map["锁具型号"]);
        Assert.Equal("耐火等级", map["耐火等级"]);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void Build_WithMap_OverridesColumnsExplicitly_KeepsUnmappedAsIdentity()
    {
        var headers = new List<string> { "Mark", "锁具型号", "耐火等级" };
        var map = CsvMapping.Build(rawMap: "锁具型号:Lock", headers, matchBy: "Mark");

        Assert.Equal("Lock", map["锁具型号"]);
        Assert.Equal("耐火等级", map["耐火等级"]);
    }

    [Fact]
    public void Build_MapWithMultiplePairs_AllApplied()
    {
        var headers = new List<string> { "Mark", "A", "B" };
        var map = CsvMapping.Build("A:ParamA,B:ParamB", headers, "Mark");
        Assert.Equal("ParamA", map["A"]);
        Assert.Equal("ParamB", map["B"]);
    }

    [Fact]
    public void Build_MapPairWithoutColon_Throws()
    {
        var headers = new List<string> { "Mark", "A" };
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => CsvMapping.Build("A=ParamA", headers, "Mark"));
        Assert.Contains("--map", ex.Message);
    }

    [Fact]
    public void Build_MapReferencesUnknownColumn_Throws()
    {
        var headers = new List<string> { "Mark", "A" };
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => CsvMapping.Build("Z:ParamZ", headers, "Mark"));
        Assert.Contains("Z", ex.Message);
    }

    [Fact]
    public void Build_MatchByMissingFromHeaders_Throws()
    {
        var headers = new List<string> { "A", "B" };
        var ex = Assert.Throws<System.InvalidOperationException>(
            () => CsvMapping.Build(null, headers, "Mark"));
        Assert.Contains("Mark", ex.Message);
    }
}
