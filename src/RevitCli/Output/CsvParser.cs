using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace RevitCli.Output;

public class CsvData
{
    public Encoding Encoding { get; init; } = Encoding.UTF8;
    public string EncodingName { get; init; } = "utf-8";
    public List<string> Headers { get; init; } = new();
    public List<List<string>> Rows { get; init; } = new();
}

public static class CsvParser
{
    /// <summary>
    /// Detect encoding (BOM &#x2192; strict UTF-8 &#x2192; GBK), decode, parse RFC 4180.
    /// </summary>
    public static CsvData Parse(byte[] bytes, string encodingHint = "auto")
    {
        var (text, encoding, encName) = Decode(bytes, encodingHint);
        var (headers, rows) = ParseText(text);
        return new CsvData
        {
            Encoding = encoding,
            EncodingName = encName,
            Headers = headers,
            Rows = rows
        };
    }

    public static CsvData ParseFile(string path, string encodingHint = "auto")
    {
        var bytes = File.ReadAllBytes(path);
        return Parse(bytes, encodingHint);
    }

    private static (string Text, Encoding Encoding, string Name) Decode(byte[] bytes, string hint)
    {
        if (string.Equals(hint, "utf-8", StringComparison.OrdinalIgnoreCase))
            return (StripBom(new UTF8Encoding(false, true).GetString(bytes)), Encoding.UTF8, "utf-8");

        if (string.Equals(hint, "gbk", StringComparison.OrdinalIgnoreCase))
        {
            var gbk = Encoding.GetEncoding("gbk");
            return (gbk.GetString(bytes), gbk, "gbk");
        }

        // auto
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return (Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3), Encoding.UTF8, "utf-8");
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return (Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2), Encoding.Unicode, "utf-16le");
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return (Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2), Encoding.BigEndianUnicode, "utf-16be");

        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return (strict.GetString(bytes), Encoding.UTF8, "utf-8");
        }
        catch (DecoderFallbackException)
        {
            try
            {
                var gbk = Encoding.GetEncoding("gbk");
                return (gbk.GetString(bytes), gbk, "gbk");
            }
            catch (ArgumentException ex)
            {
                throw new InvalidOperationException(
                    "GBK decoding unavailable. Run with --encoding utf-8 after re-saving the file as UTF-8, " +
                    "or ensure CodePagesEncodingProvider is registered.", ex);
            }
        }
    }

    private static string StripBom(string s) =>
        (s.Length > 0 && s[0] == '\uFEFF') ? s.Substring(1) : s;

    /// <summary>
    /// Parse RFC 4180 CSV text. Supports double-quoted values, escaped quotes ("" inside ""),
    /// embedded commas and newlines inside quotes, CRLF or LF line endings.
    /// </summary>
    private static (List<string> Headers, List<List<string>> Rows) ParseText(string text)
    {
        var rows = new List<List<string>>();
        var current = new List<string>();
        var field = new StringBuilder();
        var inQuotes = false;
        var i = 0;
        var emittedAnyField = false;

        while (i < text.Length)
        {
            var c = text[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"')
                    {
                        field.Append('"');
                        i += 2;
                        continue;
                    }
                    inQuotes = false;
                    i++;
                    continue;
                }
                field.Append(c);
                i++;
                continue;
            }

            if (c == '"')
            {
                inQuotes = true;
                i++;
                continue;
            }
            if (c == ',')
            {
                current.Add(field.ToString());
                field.Clear();
                emittedAnyField = true;
                i++;
                continue;
            }
            if (c == '\r')
            {
                i++;
                continue;
            }
            if (c == '\n')
            {
                current.Add(field.ToString());
                field.Clear();
                if (current.Count > 0 && !(current.Count == 1 && current[0].Length == 0))
                    rows.Add(current);
                current = new List<string>();
                emittedAnyField = false;
                i++;
                continue;
            }
            field.Append(c);
            i++;
        }

        if (field.Length > 0 || emittedAnyField)
        {
            current.Add(field.ToString());
        }
        if (current.Count > 0 && !(current.Count == 1 && current[0].Length == 0))
            rows.Add(current);

        if (rows.Count == 0)
            return (new List<string>(), new List<List<string>>());

        var headers = rows[0].Select(h => h.Trim()).ToList();
        var dataRows = rows.Skip(1).ToList();
        return (headers, dataRows);
    }
}
