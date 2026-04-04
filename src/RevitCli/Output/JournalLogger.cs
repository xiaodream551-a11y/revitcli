using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RevitCli.Output;

public static class JournalLogger
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// <summary>
    /// Append a journal entry to .revitcli/journal.jsonl (relative to profile or cwd).
    /// Each line is a self-contained JSON object.
    /// </summary>
    public static void Log(string? profileDir, object entry)
    {
        try
        {
            var baseDir = profileDir ?? Directory.GetCurrentDirectory();
            var journalDir = Path.Combine(baseDir, ".revitcli");
            Directory.CreateDirectory(journalDir);

            var journalPath = Path.Combine(journalDir, "journal.jsonl");
            var line = JsonSerializer.Serialize(entry, JsonOpts);
            File.AppendAllText(journalPath, line + Environment.NewLine);
        }
        catch
        {
            // Journal logging is best-effort — never break the command
        }
    }
}
