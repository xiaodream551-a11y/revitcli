using System;
using System.Collections.Generic;
using System.Linq;
using RevitCli.Profile;
using RevitCli.Shared;

namespace RevitCli.Checks;

internal static class CheckSuppressionRules
{
    public static bool IsSuppressed(AuditIssue issue, List<Suppression> suppressions)
    {
        foreach (var s in suppressions)
        {
            if (!string.Equals(s.Rule, issue.Rule, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!string.IsNullOrEmpty(s.Category) &&
                !ContainsWord(issue.Message, s.Category))
                continue;

            if (!string.IsNullOrEmpty(s.Parameter) &&
                !ContainsWord(issue.Message, s.Parameter))
                continue;

            if (s.ElementIds != null && s.ElementIds.Count > 0)
            {
                if (issue.ElementId.HasValue && s.ElementIds.Contains(issue.ElementId.Value))
                    return true;
            }
            else
            {
                return true;
            }
        }
        return false;
    }

    public static bool IsExpired(string? expires)
    {
        if (string.IsNullOrWhiteSpace(expires))
            return false;

        if (DateTime.TryParse(expires, out var expiryDate))
            return DateTime.Now > expiryDate;

        return false;
    }

    private static bool ContainsWord(string text, string word)
    {
        int idx = 0;
        while ((idx = text.IndexOf(word, idx, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            bool startOk = idx == 0 || !char.IsLetterOrDigit(text[idx - 1]);
            bool endOk = idx + word.Length >= text.Length || !char.IsLetterOrDigit(text[idx + word.Length]);
            if (startOk && endOk) return true;
            idx += word.Length;
        }
        return false;
    }
}
