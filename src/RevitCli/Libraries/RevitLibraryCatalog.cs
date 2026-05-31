namespace RevitCli.Libraries;

internal static class RevitLibraryCatalog
{
    public const string AutodeskAccountUrl = "https://manage.autodesk.com/products";
    public const string ContentDownloadArticleUrl =
        "https://www.autodesk.com/support/technical/article/caas/sfdcarticles/sfdcarticles/Where-to-find-Revit-Content-Libraries-to-download.html";
    public const string AutodeskRevitContentArticleUrl =
        "https://www.autodesk.com/support/technical/article/caas/tsarticles/ts/24e0wvcXEokcfVbvUZtezv.html";

    public static RevitLibrarySourceReport CreateSourceReport(int year, string locale)
    {
        var normalizedLocale = NormalizeLocale(locale);
        return new RevitLibrarySourceReport
        {
            RevitYear = year,
            Locale = normalizedLocale,
            Sources = CreateSources(year, normalizedLocale),
            Package = CreatePackage(year, normalizedLocale),
        };
    }

    public static RevitLibrarySource[] CreateSources(int year, string locale) =>
        new[]
        {
            new RevitLibrarySource
            {
                Id = "load-autodesk-family",
                Kind = "in-product-cloud",
                Name = "Load Autodesk Family",
                Url = ContentDownloadArticleUrl,
                RequiresAutodeskSignIn = false,
                Notes = "Revit 2021.1 and later can load many default families from Autodesk cloud content on demand.",
            },
            new RevitLibrarySource
            {
                Id = "autodesk-account-libraries",
                Kind = "account-download",
                Name = "Autodesk Account Libraries tab",
                Url = AutodeskAccountUrl,
                RequiresAutodeskSignIn = true,
                Notes = "Open Autodesk Account, choose All Products and Services > Revit > Available downloads > Libraries, then download the desired content pack.",
            },
            new RevitLibrarySource
            {
                Id = "download-install-content-help",
                Kind = "official-help",
                Name = $"Download and Install Revit Content {year}",
                Url = $"https://help.autodesk.com/view/RVT/{year}/ENU/?guid=GUID-A69690A6-62F3-48C6-9F34-043051F815CE",
                RequiresAutodeskSignIn = false,
                Notes = "Autodesk documents core content, optional language content packs, and the Libraries tab workflow.",
            },
            new RevitLibrarySource
            {
                Id = "autodesk-revit-content",
                Kind = "official-support",
                Name = "Autodesk Revit Content",
                Url = AutodeskRevitContentArticleUrl,
                RequiresAutodeskSignIn = false,
                Notes = "Autodesk support index for Revit content pack workflows and version-specific content information.",
            },
        };

    public static RevitContentPackage CreatePackage(int year, string locale) =>
        new()
        {
            RevitYear = year,
            Locale = NormalizeLocale(locale),
            Name = $"Autodesk Revit {year} {NormalizeLocale(locale)} content library",
            DirectDownloadUrl = null,
            RequiresAutodeskSignIn = true,
            ManualDownloadUrl = AutodeskAccountUrl,
            ManualSteps = new[]
            {
                "Sign in to Autodesk Account.",
                "Open All Products and Services.",
                $"Open Revit {year} details.",
                "Choose Available downloads > Libraries.",
                $"Download the {NormalizeLocale(locale)} or office-required content pack.",
            },
        };

    public static bool IsSupportedYear(int year) => year is >= 2024 and <= 2026;

    public static string NormalizeLocale(string? locale) =>
        string.IsNullOrWhiteSpace(locale) ? "ENU" : locale.Trim().ToUpperInvariant();

    public static bool IsOfficialAutodeskUrl(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
            return false;

        return string.Equals(uri.Host, "autodesk.com", StringComparison.OrdinalIgnoreCase) ||
               uri.Host.EndsWith(".autodesk.com", StringComparison.OrdinalIgnoreCase);
    }
}

public sealed class RevitLibrarySourceReport
{
    public string SchemaVersion { get; init; } = "revit-library-sources.v1";
    public DateTimeOffset GeneratedAtUtc { get; init; } = DateTimeOffset.UtcNow;
    public int RevitYear { get; init; }
    public string Locale { get; init; } = "ENU";
    public RevitContentPackage Package { get; init; } = new();
    public RevitLibrarySource[] Sources { get; init; } = Array.Empty<RevitLibrarySource>();
}

public sealed class RevitContentPackage
{
    public int RevitYear { get; init; }
    public string Locale { get; init; } = "ENU";
    public string Name { get; init; } = "";
    public string? DirectDownloadUrl { get; init; }
    public bool RequiresAutodeskSignIn { get; init; }
    public string ManualDownloadUrl { get; init; } = RevitLibraryCatalog.AutodeskAccountUrl;
    public string[] ManualSteps { get; init; } = Array.Empty<string>();
}

public sealed class RevitLibrarySource
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string Url { get; init; } = "";
    public bool RequiresAutodeskSignIn { get; init; }
    public string Notes { get; init; } = "";
}
