using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace EntraArm.Engine.Execution;

/// <summary>
/// Writes a file only its owner can read (<c>--secure-outputs-file</c>): a protected DACL with FullControl for the
/// current user on Windows, mode 0600 on Unix. The Az transport has an equivalent for its temp files; it is internal
/// to that assembly, hence this small copy.
/// </summary>
public static class OwnerOnlyFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>Creates or replaces the file with owner-only permissions and writes the text as UTF-8 without BOM.</summary>
    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        string full = Path.GetFullPath(path);
        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (OperatingSystem.IsWindows())
        {
            WriteWindows(full, content);
        }
        else
        {
            WriteUnix(full, content);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindows(string path, string content)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current Windows identity has no security identifier.");

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));

        var info = new FileInfo(path);
        using (var stream = info.Create(FileMode.Create, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security))
        using (var writer = new StreamWriter(stream, Utf8NoBom))
        {
            writer.Write(content);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteUnix(string path, string content)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.Create,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite,
        };

        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream, Utf8NoBom))
        {
            writer.Write(content);
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
