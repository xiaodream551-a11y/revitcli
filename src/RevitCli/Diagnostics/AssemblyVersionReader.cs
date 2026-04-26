using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace RevitCli.Diagnostics;

internal static class AssemblyVersionReader
{
    public static string CurrentCliVersion()
    {
        var assembly = typeof(AssemblyVersionReader).Assembly;
        return assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "0.0.0";
    }

    public static bool TryRead(string path, out string version, out string? error)
    {
        version = "";
        error = null;

        if (!File.Exists(path))
        {
            error = $"Assembly does not exist: {path}";
            return false;
        }

        try
        {
            var fileInfo = FileVersionInfo.GetVersionInfo(path);
            if (TryNormalizeVersion(fileInfo.ProductVersion, out var productVersion))
            {
                version = productVersion;
                return true;
            }

            var assemblyName = AssemblyName.GetAssemblyName(path);
            version = assemblyName.Version?.ToString(3) ?? "";
            if (string.IsNullOrWhiteSpace(version))
            {
                error = $"Assembly has no readable version: {path}";
                return false;
            }

            return true;
        }
        catch (Exception ex) when (ex is BadImageFormatException or FileLoadException or IOException or UnauthorizedAccessException)
        {
            error = $"Assembly version cannot be read: {ex.Message}";
            return false;
        }
    }

    internal static bool TryNormalizeVersion(string? value, out string normalized)
    {
        normalized = "";
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var trimmed = value.Trim();
        if (ComponentVersion.TryParse(trimmed, out _))
        {
            normalized = trimmed;
            return true;
        }

        var metadata = "";
        var plusIndex = trimmed.IndexOf('+');
        var core = trimmed;
        if (plusIndex >= 0)
        {
            metadata = trimmed[plusIndex..];
            core = trimmed[..plusIndex];
        }

        var parts = core.Split('.');
        if (parts.Length == 4
            && int.TryParse(parts[0], out var major)
            && int.TryParse(parts[1], out var minor)
            && int.TryParse(parts[2], out var patch)
            && int.TryParse(parts[3], out _))
        {
            var candidate = $"{major}.{minor}.{patch}{metadata}";
            if (ComponentVersion.TryParse(candidate, out _))
            {
                normalized = candidate;
                return true;
            }
        }

        return false;
    }
}
