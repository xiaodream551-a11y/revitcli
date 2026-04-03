namespace RevitCli.Shared;

public class ElementFilter
{
    public string Property { get; set; } = "";
    public string Operator { get; set; } = "";
    public string Value { get; set; } = "";

    public static ElementFilter? Parse(string expression)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return null;

        string[] operators = { ">=", "<=", "!=", ">", "<", "=" };
        foreach (var op in operators)
        {
            var idx = expression.IndexOf(op);
            if (idx > 0)
            {
                var property = expression.Substring(0, idx).Trim();
                var value = expression.Substring(idx + op.Length).Trim();

                if (string.IsNullOrEmpty(property) || string.IsNullOrEmpty(value))
                    return null;

                return new ElementFilter
                {
                    Property = property,
                    Operator = op,
                    Value = value
                };
            }
        }
        return null;
    }
}
