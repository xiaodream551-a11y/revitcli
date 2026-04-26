using System.Collections.Generic;

namespace RevitCli.Fix;

internal sealed class FixPlanOptions
{
    public HashSet<string> Rules { get; init; } = new(System.StringComparer.OrdinalIgnoreCase);
    public string? Severity { get; init; }
}
