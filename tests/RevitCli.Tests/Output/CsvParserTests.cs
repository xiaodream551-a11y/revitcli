using System.Text;
using RevitCli.Output;
using Xunit;

namespace RevitCli.Tests.Output;

public class CsvParserTests
{
    [Fact]
    public void Parse_Utf8WithBom_StripsBom_AndReportsUtf8()
    {
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var body = Encoding.UTF8.GetBytes("Mark,Lock\nW01,YALE-500\n");
        var bytes = new byte[bom.Length + body.Length];
        bom.CopyTo(bytes, 0);
        body.CopyTo(bytes, bom.Length);

        var data = CsvParser.Parse(bytes);

        Assert.Equal("utf-8", data.EncodingName);
        Assert.Equal(new[] { "Mark", "Lock" }, data.Headers);
        Assert.Single(data.Rows);
        Assert.Equal(new[] { "W01", "YALE-500" }, data.Rows[0]);
    }

    [Fact]
    public void Parse_Utf8NoBom_AutoDetectsUtf8_WithChinese()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,锁具型号,耐火等级\nW01,YALE-500,甲级\nW02,YALE-700,乙级\n");
        var data = CsvParser.Parse(bytes);

        Assert.Equal("utf-8", data.EncodingName);
        Assert.Equal(new[] { "Mark", "锁具型号", "耐火等级" }, data.Headers);
        Assert.Equal(2, data.Rows.Count);
        Assert.Equal("甲级", data.Rows[0][2]);
    }

    [Fact]
    public void Parse_GbkChinese_FallsBackToGbk_WhenStrictUtf8Fails()
    {
        var gbk = Encoding.GetEncoding("gbk");
        var bytes = gbk.GetBytes("Mark,锁具型号\nW01,YALE-500\n");
        var data = CsvParser.Parse(bytes);

        Assert.Equal("gbk", data.EncodingName);
        Assert.Equal(new[] { "Mark", "锁具型号" }, data.Headers);
        Assert.Equal("YALE-500", data.Rows[0][1]);
    }

    [Fact]
    public void Parse_QuotedValueWithComma_PreservesComma()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Notes\nW01,\"a, b, c\"\n");
        var data = CsvParser.Parse(bytes);
        Assert.Equal("a, b, c", data.Rows[0][1]);
    }

    [Fact]
    public void Parse_EscapedDoubleQuote_DecodesToSingleQuote()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Notes\nW01,\"say \"\"hi\"\"\"\n");
        var data = CsvParser.Parse(bytes);
        Assert.Equal("say \"hi\"", data.Rows[0][1]);
    }

    [Fact]
    public void Parse_QuotedValueWithNewline_PreservesNewline()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Notes\nW01,\"line1\nline2\"\n");
        var data = CsvParser.Parse(bytes);
        Assert.Equal("line1\nline2", data.Rows[0][1]);
    }

    [Fact]
    public void Parse_CrlfLineEndings_HandledLikeLf()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Lock\r\nW01,A\r\nW02,B\r\n");
        var data = CsvParser.Parse(bytes);
        Assert.Equal(2, data.Rows.Count);
        Assert.Equal(new[] { "W01", "A" }, data.Rows[0]);
        Assert.Equal(new[] { "W02", "B" }, data.Rows[1]);
    }

    [Fact]
    public void Parse_TrailingNewlineMissing_LastRowStillEmitted()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Lock\nW01,A");
        var data = CsvParser.Parse(bytes);
        Assert.Single(data.Rows);
        Assert.Equal("A", data.Rows[0][1]);
    }

    [Fact]
    public void Parse_EmptyCells_PreservedAsEmptyString()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Lock,Fire\nW01,,甲级\n");
        var data = CsvParser.Parse(bytes);
        Assert.Equal(new[] { "W01", "", "甲级" }, data.Rows[0]);
    }

    [Fact]
    public void Parse_OnlyHeader_ReturnsEmptyRows()
    {
        var bytes = Encoding.UTF8.GetBytes("Mark,Lock\n");
        var data = CsvParser.Parse(bytes);
        Assert.Equal(new[] { "Mark", "Lock" }, data.Headers);
        Assert.Empty(data.Rows);
    }
}
