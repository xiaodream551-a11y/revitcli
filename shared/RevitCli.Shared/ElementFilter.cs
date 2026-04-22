using System.Text.RegularExpressions;

namespace RevitCli.Shared;

public class ElementFilter
{
    public string Property { get; set; } = "";
    public string Operator { get; set; } = "";
    public string Value { get; set; } = "";

    private static readonly Regex FilterPattern =
        new(@"^(.+?)\s*(>=|<=|!=|>|<|=)\s*(.+)$", RegexOptions.Compiled);

    public static ElementFilter? Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        var match = FilterPattern.Match(expression.Trim());
        if (!match.Success)
            return null;

        return new ElementFilter
        {
            Property = match.Groups[1].Value.Trim(),
            Operator = match.Groups[2].Value,
            Value = match.Groups[3].Value.Trim()
        };
    }
}
