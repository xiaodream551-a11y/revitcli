using System;
using System.Collections.Generic;
using System.Linq;

namespace RevitCli.Release;

internal static class ProductionSupportReviewGuard
{
    public static readonly string[] RequiredPhrases =
    {
        "Production support review",
        "private support review approved",
        "office rollout completion",
        "production support claim",
    };

    public static bool IsComplete(string text) =>
        MissingPhrases(text).Count == 0 &&
        IncompleteFields(text).Count == 0;

    public static IReadOnlyList<string> MissingPhrases(string text) =>
        RequiredPhrases
            .Where(phrase => !text.Contains(phrase, StringComparison.OrdinalIgnoreCase))
            .ToArray();

    public static IReadOnlyList<string> IncompleteFields(string text) =>
        RequiredPhrases
            .Where(label =>
            {
                var value = FindMarkdownFieldValue(text, label);
                return value is null || IsIncompleteValue(value);
            })
            .ToArray();

    private static string? FindMarkdownFieldValue(string text, string label)
    {
        var marker = label + ":";
        foreach (var line in text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("- ", StringComparison.Ordinal))
                trimmed = trimmed[2..].TrimStart();
            if (trimmed.StartsWith(marker, StringComparison.OrdinalIgnoreCase))
                return trimmed[marker.Length..].Trim();
        }

        return null;
    }

    private static bool IsIncompleteValue(string value) =>
        string.IsNullOrWhiteSpace(value) ||
        value.Equals("todo", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("tbd", StringComparison.OrdinalIgnoreCase) ||
        value.Equals("n/a", StringComparison.OrdinalIgnoreCase) ||
        value.Contains("<", StringComparison.Ordinal);
}
