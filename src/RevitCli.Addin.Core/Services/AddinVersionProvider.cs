using System.Reflection;
using System.Text.RegularExpressions;

namespace RevitCli.Addin.Services;

internal static class AddinVersionProvider
{
    private const string NumericIdentifier = @"(?:0|[1-9][0-9]*)";
    private const string PrereleaseIdentifier = @"(?:0|[1-9][0-9]*|[0-9A-Za-z-]*[A-Za-z-][0-9A-Za-z-]*)";
    private const string BuildIdentifier = @"[0-9A-Za-z-]+";

    private static readonly Regex SemVer = new(
        "^" +
        NumericIdentifier + @"\." + NumericIdentifier + @"\." + NumericIdentifier +
        "(?:-" + PrereleaseIdentifier + @"(?:\." + PrereleaseIdentifier + ")*)?" +
        @"(?:\+" + BuildIdentifier + @"(?:\." + BuildIdentifier + ")*)?" +
        "$",
        RegexOptions.CultureInvariant);

    public static string Current()
    {
        var assembly = typeof(AddinVersionProvider).Assembly;
        var informationalVersion = assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion;

        return NormalizeVersion(informationalVersion, assembly.GetName().Version);
    }

    internal static string NormalizeVersion(string? informationalVersion, Version? assemblyNameVersion)
    {
        if (!string.IsNullOrWhiteSpace(informationalVersion))
        {
            var match = SemVer.Match(informationalVersion);
            if (match.Success)
                return match.Value;
        }

        if (assemblyNameVersion == null)
            return "0.0.0";

        var patch = assemblyNameVersion.Build >= 0 ? assemblyNameVersion.Build : 0;
        return $"{assemblyNameVersion.Major}.{assemblyNameVersion.Minor}.{patch}";
    }
}
