namespace RevitCli.Shared;

public class ElementFilter
{
    public string Property { get; set; } = "";
    public string Operator { get; set; } = "";
    public string Value { get; set; } = "";

    public static ElementFilter? Parse(string expression)
    {
        string[] operators = { ">=", "<=", "!=", ">", "<", "=" };
        foreach (var op in operators)
        {
            var idx = expression.IndexOf(op);
            if (idx > 0)
            {
                return new ElementFilter
                {
                    Property = expression.Substring(0, idx).Trim(),
                    Operator = op,
                    Value = expression.Substring(idx + op.Length).Trim()
                };
            }
        }
        return null;
    }
}
