using RevitCli.Commands;
using Xunit;

namespace RevitCli.Tests.Commands;

public class InteractiveCommandTests
{
    [Fact]
    public void SplitArgs_SimpleCommand_ReturnsTokens()
    {
        var result = InteractiveCommand.SplitArgs("query walls");
        Assert.Equal(new[] { "query", "walls" }, result);
    }

    [Fact]
    public void SplitArgs_QuotedFilter_PreservesQuotedContent()
    {
        var result = InteractiveCommand.SplitArgs("query walls --filter \"height > 3000\"");
        Assert.Equal(new[] { "query", "walls", "--filter", "height > 3000" }, result);
    }

    [Fact]
    public void SplitArgs_SingleQuotes_PreservesQuotedContent()
    {
        var result = InteractiveCommand.SplitArgs("query walls --filter 'height > 3000'");
        Assert.Equal(new[] { "query", "walls", "--filter", "height > 3000" }, result);
    }

    [Fact]
    public void SplitArgs_ExtraSpaces_IgnoredCorrectly()
    {
        var result = InteractiveCommand.SplitArgs("  status  ");
        Assert.Equal(new[] { "status" }, result);
    }

    [Fact]
    public void SplitArgs_EmptyInput_ReturnsEmpty()
    {
        var result = InteractiveCommand.SplitArgs("");
        Assert.Empty(result);
    }

    [Fact]
    public void SplitArgs_MultipleFlags_AllReturned()
    {
        var result = InteractiveCommand.SplitArgs("export --format dwg --sheets \"A1*\" --output-dir /tmp");
        Assert.Equal(new[] { "export", "--format", "dwg", "--sheets", "A1*", "--output-dir", "/tmp" }, result);
    }
}
