using System;
using System.Text.RegularExpressions;
using RevitCli.Shared;

namespace RevitCli.Fix;

internal static class FixTemplateRenderer
{
    public static string Render(string template, AuditIssue issue, string parameter)
    {
        if (template is null)
        {
            throw new ArgumentNullException(nameof(template));
        }

        if (issue is null)
        {
            throw new ArgumentNullException(nameof(issue));
        }

        return Regex.Replace(template, "\\{([^}]+)\\}", match =>
        {
            var token = match.Groups[1].Value;
            return token switch
            {
                "element.id" => issue.ElementId?.ToString() ?? string.Empty,
                "category" => issue.Category ?? string.Empty,
                "parameter" => parameter ?? string.Empty,
                "currentValue" => issue.CurrentValue ?? string.Empty,
                "expectedValue" => issue.ExpectedValue ?? string.Empty,
                _ => throw new InvalidOperationException($"Unknown token '{{{token}}}' in fix template.")
            };
        });
    }
}
