namespace RevitCli.Libraries;

internal sealed class RevitIniDocument
{
    private readonly Dictionary<string, Dictionary<string, string>> _sections;

    private RevitIniDocument(Dictionary<string, Dictionary<string, string>> sections)
    {
        _sections = sections;
    }

    public static RevitIniDocument Load(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        var currentSection = "";

        foreach (var rawLine in File.ReadLines(path))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith(';') || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']') && line.Length > 2)
            {
                currentSection = line[1..^1].Trim();
                if (!sections.ContainsKey(currentSection))
                    sections[currentSection] = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                continue;
            }

            var equals = line.IndexOf('=');
            if (equals <= 0)
                continue;

            if (!sections.TryGetValue(currentSection, out var values))
            {
                values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[currentSection] = values;
            }

            values[line[..equals].Trim()] = line[(equals + 1)..].Trim();
        }

        return new RevitIniDocument(sections);
    }

    public string? GetDirectoryValue(string locale, string key)
    {
        var localeSection = $"Directories{locale}";
        if (_sections.TryGetValue(localeSection, out var localeValues) &&
            localeValues.TryGetValue(key, out var localeValue))
            return localeValue;

        if (_sections.TryGetValue("Directories", out var defaultValues) &&
            defaultValues.TryGetValue(key, out var defaultValue))
            return defaultValue;

        return null;
    }

    public IReadOnlyList<RevitIniLibraryPath> GetDataLibraryLocations(string locale)
    {
        var value = GetDirectoryValue(locale, "DataLibraryLocations");
        if (string.IsNullOrWhiteSpace(value))
            return Array.Empty<RevitIniLibraryPath>();

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(ParseDataLibraryLocation)
            .Where(entry => entry is not null)
            .Cast<RevitIniLibraryPath>()
            .ToArray();
    }

    private static RevitIniLibraryPath? ParseDataLibraryLocation(string value)
    {
        var equals = value.IndexOf('=');
        if (equals <= 0 || equals >= value.Length - 1)
            return null;

        return new RevitIniLibraryPath(value[..equals].Trim(), value[(equals + 1)..].Trim());
    }
}

internal sealed record RevitIniLibraryPath(string Name, string Path);
