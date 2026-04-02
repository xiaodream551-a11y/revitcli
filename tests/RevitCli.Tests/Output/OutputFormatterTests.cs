using System.Collections.Generic;
using RevitCli.Output;
using RevitCli.Shared;
using Xunit;

namespace RevitCli.Tests.Output;

public class OutputFormatterTests
{
    private readonly ElementInfo[] _elements =
    {
        new()
        {
            Id = 100,
            Name = "Wall 1",
            Category = "Walls",
            TypeName = "Generic - 200mm",
            Parameters = new Dictionary<string, string> { { "Height", "3000" }, { "Width", "200" } }
        },
        new()
        {
            Id = 200,
            Name = "Wall 2",
            Category = "Walls",
            TypeName = "Generic - 300mm",
            Parameters = new Dictionary<string, string> { { "Height", "2700" }, { "Width", "300" } }
        }
    };

    [Fact]
    public void FormatJson_ReturnsValidJson()
    {
        var result = OutputFormatter.FormatElements(_elements, "json");

        Assert.Contains("\"id\": 100", result);
        Assert.Contains("\"name\": \"Wall 1\"", result);
    }

    [Fact]
    public void FormatCsv_ReturnsHeaderAndRows()
    {
        var result = OutputFormatter.FormatElements(_elements, "csv");

        var lines = result.Split('\n', System.StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, lines.Length); // header + 2 rows
        Assert.StartsWith("Id,Name,Category,TypeName", lines[0]);
        Assert.Contains("100", lines[1]);
        Assert.Contains("Wall 1", lines[1]);
    }

    [Fact]
    public void FormatTable_ReturnsNonEmptyString()
    {
        var result = OutputFormatter.FormatElements(_elements, "table");

        Assert.Contains("Wall 1", result);
        Assert.Contains("Wall 2", result);
    }

    [Fact]
    public void FormatElements_EmptyArray_ReturnsNoElementsMessage()
    {
        var result = OutputFormatter.FormatElements(System.Array.Empty<ElementInfo>(), "table");

        Assert.Equal("No elements matched.", result);
    }
}
