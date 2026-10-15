using System;
using System.IO;

namespace EArm.Engine.State;

/// <summary>
/// Link detection for the places EARM writes secrets and state to (the state directory, the secure outputs file's folder). A symbolic link or
/// junction anywhere on the path - not only at its end - redirects every write elsewhere (review round 2, finding S2-18), so every existing
/// ancestor is checked, not just the leaf.
/// </summary>
public static class PathSafety
{
    /// <summary>
    /// The first existing component of <paramref name="path"/> (itself or an ancestor, nearest first) that is a symbolic link or a junction, or
    /// <c>null</c>. Placeholders of a cloud-sync client (OneDrive) are not links and pass. On Unix-like systems the entries directly under the root
    /// (<c>/tmp</c>, <c>/var</c>, <c>/etc</c> are links on some systems) are skipped: only an administrator can change those.
    /// </summary>
    public static string? FindLinkedComponent(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string current = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        string? root = Path.GetPathRoot(current);
        while (!string.IsNullOrEmpty(current) && !string.Equals(current, Path.TrimEndingDirectorySeparator(root ?? string.Empty), StringComparison.OrdinalIgnoreCase))
        {
            string? parent = Path.GetDirectoryName(current);
            bool rootChild = !OperatingSystem.IsWindows() && string.Equals(parent is null ? null : Path.TrimEndingDirectorySeparator(parent), Path.TrimEndingDirectorySeparator(root ?? string.Empty), StringComparison.Ordinal);
            if (!rootChild && IsLink(current))
            {
                return current;
            }

            current = parent ?? string.Empty;
        }

        return null;
    }

    /// <summary>True when <paramref name="path"/> exists as a directory or file that is a symbolic link or junction.</summary>
    public static bool IsLink(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return info.Exists && info.LinkTarget is not null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return false;
        }
    }
}
