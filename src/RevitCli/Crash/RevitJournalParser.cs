using System.Text.RegularExpressions;

namespace RevitCli.Crash;

internal sealed class RevitJournalParseResult
{
    public RevitCrashJournal Journal { get; init; } = new();
    public IReadOnlyList<JournalLine> Lines { get; init; } = Array.Empty<JournalLine>();
}

internal sealed record JournalLine(int Number, string Text);

internal static partial class RevitJournalParser
{
    private const int MaxLines = 5000;

    public static RevitJournalParseResult Parse(string journalPath)
    {
        var info = new FileInfo(journalPath);
        var lines = ReadTailLines(journalPath, MaxLines);
        var journal = new RevitCrashJournal
        {
            Path = info.FullName,
            LastWriteUtc = new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero),
            SizeBytes = info.Length,
            AnalyzedLineCount = lines.Count,
            LastModelPath = FindLastModelPath(lines),
            LastCommand = FindLastCommand(lines),
        };

        return new RevitJournalParseResult
        {
            Journal = journal,
            Lines = lines,
        };
    }

    public static string BuildContext(IReadOnlyList<JournalLine> lines, int lineNumber, int contextLines)
    {
        if (contextLines <= 0)
            return "";

        var index = -1;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Number == lineNumber)
            {
                index = i;
                break;
            }
        }

        if (index < 0)
            return "";

        var start = Math.Max(0, index - contextLines);
        var end = Math.Min(lines.Count - 1, index + contextLines);
        return string.Join(
            Environment.NewLine,
            lines
                .Skip(start)
                .Take(end - start + 1)
                .Select(line => $"{line.Number}: {TrimEvidence(line.Text, 300)}"));
    }

    internal static string TrimEvidence(string? value, int maxLength = 500)
    {
        if (string.IsNullOrWhiteSpace(value))
            return "";

        var normalized = value.Replace('\t', ' ').Trim();
        while (normalized.Contains("  ", StringComparison.Ordinal))
            normalized = normalized.Replace("  ", " ", StringComparison.Ordinal);

        return normalized.Length <= maxLength
            ? normalized
            : normalized[..maxLength] + "...";
    }

    private static List<JournalLine> ReadTailLines(string journalPath, int maxLines)
    {
        var queue = new Queue<JournalLine>(maxLines);
        var lineNumber = 0;
        foreach (var line in File.ReadLines(journalPath))
        {
            lineNumber++;
            if (queue.Count == maxLines)
                queue.Dequeue();
            queue.Enqueue(new JournalLine(lineNumber, line));
        }

        return queue.ToList();
    }

    private static string? FindLastModelPath(IEnumerable<JournalLine> lines)
    {
        string? modelPath = null;
        foreach (var line in lines)
        {
            var match = RvtPathRegex().Match(line.Text);
            if (match.Success)
                modelPath = match.Groups["path"].Value.Trim('"');
        }

        return modelPath;
    }

    private static string? FindLastCommand(IEnumerable<JournalLine> lines)
    {
        string? command = null;
        foreach (var line in lines)
        {
            if (line.Text.Contains("Jrn.Command", StringComparison.OrdinalIgnoreCase) ||
                line.Text.Contains("Jrn.RibbonEvent", StringComparison.OrdinalIgnoreCase) ||
                line.Text.Contains("Jrn.PushButtonEvent", StringComparison.OrdinalIgnoreCase))
            {
                command = TrimEvidence(line.Text, 300);
            }
        }

        return command;
    }

    [GeneratedRegex("(?<path>([A-Za-z]:\\\\|/)[^\\\"'<>|?*\\r\\n]+?\\.rvt)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex RvtPathRegex();
}
