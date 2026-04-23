using System;
using System.IO;
using System.Text.Json;
using RevitCli.Shared;

namespace RevitCli.Output;

/// <summary>
/// Reads and writes baseline snapshots for `publish --since`. Write is atomic via tmp+rename
/// so a half-written file never surfaces if the process dies mid-save. Corrupted files return
/// null on Load so the caller can surface a clean error and keep the old baseline untouched.
/// </summary>
public static class BaselineManager
{
    private static readonly JsonSerializerOptions ReadOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions WriteOpts = new() { WriteIndented = true };

    /// <summary>
    /// Reads a snapshot from disk. Returns null if the file doesn't exist or is unreadable.
    /// Never throws for I/O or deserialization errors — callers decide whether missing means
    /// "no baseline yet" or "error" based on context.
    /// </summary>
    public static ModelSnapshot? Load(string path)
    {
        if (!File.Exists(path)) return null;
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<ModelSnapshot>(json, ReadOpts);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a snapshot to disk atomically (write to .tmp, then rename over target).
    /// Creates parent directories as needed. Throws on I/O failure so the caller can
    /// decide whether to preserve the old baseline or retry.
    /// </summary>
    public static void Save(string path, ModelSnapshot snapshot)
    {
        var fullPath = Path.GetFullPath(path);
        var dir = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        var tmp = fullPath + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, WriteOpts));
        // Single-syscall rename: atomic on both Linux (rename(2)) and Windows
        // (MoveFileExW with REPLACE_EXISTING). If the process crashes mid-rename,
        // the filesystem guarantees either old or new, never gone-then-new.
        File.Move(tmp, fullPath, overwrite: true);
    }
}
