using System.Diagnostics;
using System.Text.Json;

namespace RevitCli.Crash;

internal sealed class WindowsEventLogReader : IRevitEventLogReader
{
    private readonly string _powershellPath;

    private WindowsEventLogReader(string powershellPath)
    {
        _powershellPath = powershellPath;
    }

    public static IRevitEventLogReader CreateDefault()
    {
        var candidates = new[]
        {
            "powershell.exe",
            "/mnt/c/Windows/System32/WindowsPowerShell/v1.0/powershell.exe",
        };

        foreach (var candidate in candidates)
        {
            if (ExecutableExists(candidate))
                return new WindowsEventLogReader(candidate);
        }

        return new NullRevitEventLogReader();
    }

    private static bool ExecutableExists(string candidate)
    {
        if (Path.IsPathRooted(candidate))
            return File.Exists(candidate);

        var pathVariable = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathVariable))
            return false;

        foreach (var directory in pathVariable.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                if (File.Exists(Path.Combine(directory.Trim(), candidate)))
                    return true;
            }
            catch (ArgumentException)
            {
                // Ignore malformed PATH entries.
            }
        }

        return false;
    }

    public async Task<RevitEventLogResult> ReadAsync(
        int revitYear,
        DateTimeOffset sinceUtc,
        CancellationToken cancellationToken = default)
    {
        var result = new RevitEventLogResult();
        var script = BuildScript(revitYear, sinceUtc);
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = _powershellPath,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            process.StartInfo.ArgumentList.Add("-NoProfile");
            process.StartInfo.ArgumentList.Add("-ExecutionPolicy");
            process.StartInfo.ArgumentList.Add("Bypass");
            process.StartInfo.ArgumentList.Add("-Command");
            process.StartInfo.ArgumentList.Add(script);

            if (!process.Start())
            {
                result.Issues.Add(Unavailable("PowerShell did not start."));
                return result;
            }

            var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
            var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);
            var stdout = await stdoutTask;
            var stderr = await stderrTask;

            if (process.ExitCode != 0)
            {
                result.Issues.Add(Unavailable(
                    string.IsNullOrWhiteSpace(stderr)
                        ? $"PowerShell exited with code {process.ExitCode}."
                        : stderr.Trim()));
                return result;
            }

            ParseEventLogJson(stdout, result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception or JsonException)
        {
            result.Issues.Add(Unavailable(ex.Message));
        }

        return result;
    }

    private static string BuildScript(int revitYear, DateTimeOffset sinceUtc)
    {
        var since = sinceUtc.UtcDateTime.ToString("O");
        return $$$"""
$ErrorActionPreference = 'Stop'
$start = [datetime]::Parse('{{{since}}}').ToLocalTime()
$events = Get-WinEvent -FilterHashtable @{ LogName = 'Application'; StartTime = $start } |
    Where-Object {
        $_.Message -match 'Revit\.exe|Autodesk Revit|Exception code|Faulting application' -or
        $_.ProviderName -match 'Application Error|\.NET Runtime|Windows Error Reporting'
    } |
    Where-Object {
        $_.Message -match 'Revit|Revit\.exe|Autodesk' -or $_.ProviderName -match '\.NET Runtime'
    } |
    Select-Object -First 20 `
        @{Name='timeCreatedUtc';Expression={$_.TimeCreated.ToUniversalTime().ToString('o')}} ,
        @{Name='providerName';Expression={$_.ProviderName}} ,
        @{Name='id';Expression={$_.Id}} ,
        @{Name='level';Expression={$_.LevelDisplayName}} ,
        @{Name='message';Expression={$_.Message}}
$events | ConvertTo-Json -Depth 4 -Compress
""";
    }

    private static void ParseEventLogJson(string json, RevitEventLogResult result)
    {
        if (string.IsNullOrWhiteSpace(json))
            return;

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind == JsonValueKind.Array)
        {
            foreach (var entry in document.RootElement.EnumerateArray())
                result.Entries.Add(ParseEntry(entry));
            return;
        }

        if (document.RootElement.ValueKind == JsonValueKind.Object)
            result.Entries.Add(ParseEntry(document.RootElement));
    }

    private static RevitCrashEventLogEntry ParseEntry(JsonElement element)
    {
        DateTimeOffset? timeCreated = null;
        if (element.TryGetProperty("timeCreatedUtc", out var timeElement) &&
            DateTimeOffset.TryParse(timeElement.GetString(), out var parsed))
        {
            timeCreated = parsed.ToUniversalTime();
        }

        return new RevitCrashEventLogEntry
        {
            TimeCreatedUtc = timeCreated,
            ProviderName = GetString(element, "providerName"),
            Id = GetInt(element, "id"),
            Level = GetString(element, "level"),
            Message = RevitJournalParser.TrimEvidence(GetString(element, "message"), 1000),
        };
    }

    private static string GetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var property) && property.ValueKind != JsonValueKind.Null
            ? property.ToString()
            : "";

    private static int? GetInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
            return null;
        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var value))
            return value;
        return int.TryParse(property.ToString(), out var parsed) ? parsed : null;
    }

    private static RevitCrashIssue Unavailable(string message) =>
        new()
        {
            Severity = "warning",
            Code = "event-log-unavailable",
            Message = $"Windows Application Event Log query was skipped or failed: {message}",
        };
}
