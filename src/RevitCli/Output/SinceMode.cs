namespace RevitCli.Output;

public enum SinceMode
{
    /// <summary>
    /// Compare sheets using ContentHash (sheet meta + elements on placed views).
    /// Falls back to MetaHash-only when either side's ContentHash is empty (e.g. a P1 baseline).
    /// </summary>
    Content,

    /// <summary>
    /// Compare sheets using MetaHash only (sheet own parameters + viewId).
    /// Faster, but won't detect changes to elements drawn on the sheet.
    /// </summary>
    Meta
}

public static class SinceModeParser
{
    /// <summary>Parse the profile string into a SinceMode. Unrecognized strings default to Content.</summary>
    public static SinceMode Parse(string? raw) =>
        string.Equals(raw, "meta", System.StringComparison.OrdinalIgnoreCase)
            ? SinceMode.Meta
            : SinceMode.Content;
}
