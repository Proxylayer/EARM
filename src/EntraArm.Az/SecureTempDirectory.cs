using System;
using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace EntraArm.Az;

/// <summary>
/// The per-user directory holding request, body and output files for az invocations. Created on demand with an
/// owner-only ACL on Windows (protected DACL, FullControl for the current user, inherited by files) and mode 0700
/// on Unix (files are created 0600 by <see cref="SecureFile"/>).
/// </summary>
internal sealed class SecureTempDirectory
{
    private readonly object _gate = new();
    private bool _created;

    public SecureTempDirectory(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
    }

    public string Path { get; }

    /// <summary>Creates the directory (and parents) and (re)applies the owner-only permissions. Idempotent.</summary>
    public void EnsureCreated()
    {
        lock (_gate)
        {
            if (_created)
            {
                return;
            }

            var parent = System.IO.Path.GetDirectoryName(Path);
            if (!string.IsNullOrEmpty(parent))
            {
                Directory.CreateDirectory(parent);
            }

            if (OperatingSystem.IsWindows())
            {
                CreateWithOwnerOnlyAcl(new DirectoryInfo(Path));
            }
            else
            {
                CreateWithUnixMode(Path);
            }

            _created = true;
        }
    }

    /// <summary>A path inside the directory for a new file with the given name.</summary>
    public string FilePath(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        return System.IO.Path.Combine(Path, fileName);
    }

    [SupportedOSPlatform("windows")]
    private static void CreateWithOwnerOnlyAcl(DirectoryInfo directory)
    {
        using var identity = WindowsIdentity.GetCurrent();
        var user = identity.User ?? throw new InvalidOperationException("The current Windows identity has no security identifier.");

        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(
            user,
            FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        if (directory.Exists)
        {
            directory.SetAccessControl(security);
        }
        else
        {
            directory.Create(security);
        }
    }

    [UnsupportedOSPlatform("windows")]
    private static void CreateWithUnixMode(string path)
    {
        const UnixFileMode ownerOnly = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
        if (!Directory.Exists(path))
        {
            Directory.CreateDirectory(path, ownerOnly);
        }

        File.SetUnixFileMode(path, ownerOnly);
    }
}
