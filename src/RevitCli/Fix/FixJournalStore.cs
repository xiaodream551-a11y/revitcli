using System.IO;
using System.Text.Json;

namespace RevitCli.Fix;

internal static class FixJournalStore
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static readonly JsonSerializerOptions ReadOptions = new() { PropertyNameCaseInsensitive = true };

    public static string GetJournalPath(string baselinePath)
    {
        if (string.IsNullOrWhiteSpace(baselinePath))
        {
            throw new ArgumentException("Baseline path is required.", nameof(baselinePath));
        }

        var full = Path.GetFullPath(baselinePath);
        var dir = Path.GetDirectoryName(full);
        if (string.IsNullOrWhiteSpace(dir))
        {
            dir = Directory.GetCurrentDirectory();
        }

        var name = Path.GetFileNameWithoutExtension(full);
        return Path.Combine(dir, $"{name}.fixjournal.json");
    }

    public static string SaveForBaseline(string baselinePath, FixJournal journal)
    {
        var path = GetJournalPath(baselinePath);
        if (journal == null)
        {
            throw new ArgumentNullException(nameof(journal));
        }

        journal.BaselinePath = Path.GetFullPath(baselinePath);

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        File.WriteAllText(path, JsonSerializer.Serialize(journal, JsonOptions));
        return path;
    }

    public static FixJournal LoadForBaseline(string baselinePath)
    {
        var path = GetJournalPath(baselinePath);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Fix journal not found: {path}");
        }

        FixJournal? journal;
        try
        {
            journal = JsonSerializer.Deserialize<FixJournal>(File.ReadAllText(path), ReadOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Invalid fix journal: {path}", ex);
        }
        if (journal == null || journal.SchemaVersion != 1)
        {
            throw new InvalidDataException($"Invalid fix journal: {path}");
        }

        return journal;
    }
}
