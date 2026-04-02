using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests;

public class ElementFilterTests
{
    [Fact]
    public void Parse_SimpleEquals_ReturnsFilter()
    {
        var filter = ElementFilter.Parse("Fire Rating = 60min");

        Assert.NotNull(filter);
        Assert.Equal("Fire Rating", filter.Property);
        Assert.Equal("=", filter.Operator);
        Assert.Equal("60min", filter.Value);
    }

    [Fact]
    public void Parse_GreaterThan_ReturnsFilter()
    {
        var filter = ElementFilter.Parse("height > 3000");

        Assert.NotNull(filter);
        Assert.Equal("height", filter.Property);
        Assert.Equal(">", filter.Operator);
        Assert.Equal("3000", filter.Value);
    }

    [Fact]
    public void Parse_GreaterThanOrEqual_ReturnsFilter()
    {
        var filter = ElementFilter.Parse("width >= 500");

        Assert.NotNull(filter);
        Assert.Equal("width", filter.Property);
        Assert.Equal(">=", filter.Operator);
        Assert.Equal("500", filter.Value);
    }

    [Fact]
    public void Parse_NotEqual_ReturnsFilter()
    {
        var filter = ElementFilter.Parse("status != active");

        Assert.NotNull(filter);
        Assert.Equal("status", filter.Property);
        Assert.Equal("!=", filter.Operator);
        Assert.Equal("active", filter.Value);
    }

    [Fact]
    public void Parse_InvalidExpression_ReturnsNull()
    {
        var filter = ElementFilter.Parse("no operator here");

        Assert.Null(filter);
    }

    [Fact]
    public void Parse_EmptyString_ReturnsNull()
    {
        var filter = ElementFilter.Parse("");

        Assert.Null(filter);
    }
}
