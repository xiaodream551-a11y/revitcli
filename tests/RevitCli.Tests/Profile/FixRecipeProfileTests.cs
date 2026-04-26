using System;
using System.IO;
using RevitCli.Profile;
using Xunit;

namespace RevitCli.Tests.Profile;

public class FixRecipeProfileTests
{
    private static ProjectProfile LoadYaml(string yaml)
    {
        var path = Path.Combine(Path.GetTempPath(), $"revitcli_fix_profile_{Guid.NewGuid():N}.yml");
        File.WriteAllText(path, yaml);
        try { return ProfileLoader.Load(path); }
        finally { File.Delete(path); }
    }

    [Fact]
    public void Profile_WithoutFixes_Loads()
    {
        var profile = LoadYaml("""
version: 1
checks:
  default:
    failOn: error
""");

        Assert.Empty(profile.Fixes);
    }

    [Fact]
    public void Profile_SetParamRecipe_Loads()
    {
        var profile = LoadYaml("""
version: 1
fixes:
  - rule: required-parameter
    category: doors
    parameter: Mark
    strategy: setParam
    value: "{category}-{element.id}"
    maxChanges: 20
""");

        var recipe = Assert.Single(profile.Fixes);
        Assert.Equal("required-parameter", recipe.Rule);
        Assert.Equal("doors", recipe.Category);
        Assert.Equal("Mark", recipe.Parameter);
        Assert.Equal("setParam", recipe.Strategy);
        Assert.Equal("{category}-{element.id}", recipe.Value);
        Assert.Equal(20, recipe.MaxChanges);
    }

    [Fact]
    public void Profile_RenameByPatternRecipe_Loads()
    {
        var profile = LoadYaml("""
version: 1
fixes:
  - rule: naming
    category: rooms
    parameter: Name
    strategy: renameByPattern
    match: "^Room (.+)$"
    replace: "$1"
""");

        var recipe = Assert.Single(profile.Fixes);
        Assert.Equal("renameByPattern", recipe.Strategy);
        Assert.Equal("^Room (.+)$", recipe.Match);
        Assert.Equal("$1", recipe.Replace);
    }

    [Fact]
    public void Profile_MissingStrategy_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadYaml("""
version: 1
fixes:
  - rule: naming
    parameter: Name
"""));

        Assert.Contains("fixes[0].strategy", ex.Message);
    }

    [Fact]
    public void Profile_NullFixes_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadYaml("""
version: 1
fixes: null
"""));

        Assert.Contains("fixes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Profile_NullFixItem_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadYaml("""
version: 1
fixes:
  -
"""));

        Assert.Contains("fixes[0]", ex.Message);
    }

    [Fact]
    public void Profile_UnsupportedStrategy_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadYaml("""
version: 1
fixes:
  - rule: unplaced-rooms
    strategy: purgeUnplaced
"""));

        Assert.Contains("supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Profile_SetParamMissingValue_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadYaml("""
version: 1
fixes:
  - rule: required-parameter
    parameter: Mark
    strategy: setParam
"""));

        Assert.Contains("value", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Profile_RenameInvalidRegex_Fails()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => LoadYaml("""
version: 1
fixes:
  - rule: naming
    parameter: Name
    strategy: renameByPattern
    match: "["
    replace: "$1"
"""));

        Assert.Contains("fixes[0].match", ex.Message);
    }
}
