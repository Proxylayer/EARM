using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace EArm.Engine.Execution;

/// <summary>
/// Writes a file only its owner can read (<c>--secure-outputs-file</c>): a protected DACL with FullControl for the
/// current user on Windows, mode 0600 on Unix. The content is always written to a NEW file created next to the target with the
/// restricted permissions already in place (a temporary name in the same folder), then moved over the target, so the permissions of a
/// pre-existing target never apply: neither its inherited Windows rules nor a looser Unix mode survive, and the secret is never in a
/// file that others could read, not even for an instant. The result is verified before the method returns. The Az transport has an
/// equivalent for its temp files; it is internal to that assembly, hence this small copy.
/// </summary>
public static class OwnerOnlyFile
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private const UnixFileMode OwnerReadWrite = UnixFileMode.UserRead | UnixFileMode.UserWrite;

    /// <summary>
    /// Creates or replaces the file with owner-only permissions and writes the text as UTF-8 without BOM.
    /// </summary>
    /// <exception cref="IOException">The path is a directory, the existing file is read-only, or the file cannot be replaced.</exception>
    /// <exception cref="InvalidOperationException">The permissions of the written file could not be verified as owner-only (the file is deleted).</exception>
    public static void WriteAllText(string path, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(content);

        string full = Path.GetFullPath(path);
        if (Directory.Exists(full) || Path.EndsInDirectorySeparator(full))
        {
            throw new IOException($"'{path}' is a directory; the secure outputs file must name a file.");
        }

        string? directory = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (File.Exists(full) && (File.GetAttributes(full) & FileAttributes.ReadOnly) != 0)
        {
            throw new IOException($"The existing file '{path}' is read-only and cannot be replaced; remove its read-only attribute or choose another path.");
        }

        string temp = Path.Combine(directory ?? string.Empty, $".{Path.GetFileName(full)}.{Guid.NewGuid():N}.tmp");
        try
        {
            if (OperatingSystem.IsWindows())
            {
                WriteWindows(temp, full, content);
            }
            else
            {
                WriteUnix(temp, full, content);
            }
        }
        finally
        {
            DeleteQuietly(temp);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void WriteWindows(string temp, string target, string content)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current Windows identity has no security identifier.");

        var security = new FileSecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));

        // CreateNew: the descriptor is applied because the file is created here (it would be ignored over an existing file).
        using (var stream = new FileInfo(temp).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.None, security))
        using (var writer = new StreamWriter(stream, Utf8NoBom))
        {
            writer.Write(content);
        }

        // A rename keeps the moved file's own security descriptor (File.Replace would merge the replaced file's ACL into it).
        File.Move(temp, target, overwrite: true);

        if (!IsOwnerOnlyWindows(target, user))
        {
            DeleteQuietly(target);
            throw new InvalidOperationException($"The file '{target}' did not end up with an owner-only access list; it was deleted instead of leaving a secret readable by others.");
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool IsOwnerOnlyWindows(string path, SecurityIdentifier user)
    {
        var security = new FileInfo(path).GetAccessControl();
        if (!security.AreAccessRulesProtected)
        {
            return false;
        }

        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        return rules.Count == 1
            && rules[0].IdentityReference.Equals(user)
            && rules[0].AccessControlType == AccessControlType.Allow
            && !rules[0].IsInherited
            && (rules[0].FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl;
    }

    [UnsupportedOSPlatform("windows")]
    private static void WriteUnix(string temp, string target, string content)
    {
        var options = new FileStreamOptions
        {
            Mode = FileMode.CreateNew,
            Access = FileAccess.Write,
            Share = FileShare.None,
            UnixCreateMode = OwnerReadWrite,
        };

        using (var stream = new FileStream(temp, options))
        {
            // Explicit chmod before any content is written: the create mode is subject to the umask and to nothing else, but this
            // makes the 0600 state independent of how the file was created.
            File.SetUnixFileMode(stream.SafeFileHandle, OwnerReadWrite);
            using var writer = new StreamWriter(stream, Utf8NoBom);
            writer.Write(content);
        }

        // rename(2): atomic, and the target's old inode (with whatever mode it had) is replaced, not rewritten.
        File.Move(temp, target, overwrite: true);

        if (File.GetUnixFileMode(target) != OwnerReadWrite)
        {
            DeleteQuietly(target);
            throw new InvalidOperationException($"The file '{target}' did not end up with mode 0600; it was deleted instead of leaving a secret readable by others.");
        }
    }

    private static void DeleteQuietly(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best effort: the original failure (if any) is the one that matters.
        }
    }
}
