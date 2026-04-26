namespace RevitCli.Fix;

internal sealed class FixAction
{
    public string Rule { get; init; } = "";
    public string Strategy { get; init; } = "";
    public long ElementId { get; init; }
    public string Category { get; init; } = "";
    public string Parameter { get; init; } = "";
    public string? OldValue { get; init; }
    public string NewValue { get; init; } = "";
    public bool Inferred { get; init; }
    public string Confidence { get; init; } = "high";
    public string Reason { get; init; } = "";
    public string RecipeKey { get; init; } = "";
    public int? RecipeMaxChanges { get; init; }
}
