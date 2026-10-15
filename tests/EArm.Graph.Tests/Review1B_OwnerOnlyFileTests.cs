using System;
using System.IO;
using System.Linq;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>Review finding Q-15: <see cref="OwnerOnlyFile"/> (new file, replacement, directories, read-only targets, verification, no temporary leftovers).</summary>
public sealed class Review1B_OwnerOnlyFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "earm-r1b-owner-" + Guid.NewGuid().ToString("N"));

    public Review1B_OwnerOnlyFileTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(_root, recursive: true);
        }
    }

    private static void AssertOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            var security = new FileInfo(path).GetAccessControl();
            Assert.True(security.AreAccessRulesProtected, "inheritance must be cut");
            var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
            Assert.Equal(WindowsIdentity.GetCurrent().User, rule.IdentityReference);
            Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
            Assert.False(rule.IsInherited);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [Fact]
    public void ANewFileIsCreatedOwnerOnlyWithUtf8ContentAndNoByteOrderMark()
    {
        var path = Path.Combine(_root, "secret.json");

        OwnerOnlyFile.WriteAllText(path, "{\"password\":\"päss 日本\"}");

        var bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
        Assert.Equal("{\"password\":\"päss 日本\"}", Encoding.UTF8.GetString(bytes));
        AssertOwnerOnly(path);
    }

    [Fact]
    public void MissingDirectoriesAreCreated()
    {
        var path = Path.Combine(_root, "a", "b", "c", "out.txt");

        OwnerOnlyFile.WriteAllText(path, "x");

        Assert.Equal("x", File.ReadAllText(path));
    }

    [Fact]
    public void AnExistingFileWithLooseAccessIsReplacedByAnOwnerOnlyOne()
    {
        var path = Path.Combine(_root, "existing.txt");
        File.WriteAllText(path, "old content that others could read");
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        OwnerOnlyFile.WriteAllText(path, "new secret");

        Assert.Equal("new secret", File.ReadAllText(path));
        AssertOwnerOnly(path);
    }

    [Fact]
    public void NoTemporaryFileIsLeftBehindAfterASuccessfulWrite()
    {
        var path = Path.Combine(_root, "clean.txt");

        OwnerOnlyFile.WriteAllText(path, "one");
        OwnerOnlyFile.WriteAllText(path, "two");

        Assert.Equal(["clean.txt"], Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));
    }

    [Fact]
    public void EmptyAndLargeContentAreWrittenExactly()
    {
        var empty = Path.Combine(_root, "empty.txt");
        var large = Path.Combine(_root, "large.txt");
        var text = new string('é', 300_000);

        OwnerOnlyFile.WriteAllText(empty, string.Empty);
        OwnerOnlyFile.WriteAllText(large, text);

        Assert.Equal(0, new FileInfo(empty).Length);
        Assert.Equal(text, File.ReadAllText(large));
        AssertOwnerOnly(empty);
        AssertOwnerOnly(large);
    }

    [Fact]
    public void ADirectoryOrATrailingSeparatorIsRejected()
    {
        var directory = Path.Combine(_root, "dir");
        Directory.CreateDirectory(directory);

        Assert.Throws<IOException>(() => OwnerOnlyFile.WriteAllText(directory, "x"));
        Assert.Throws<IOException>(() => OwnerOnlyFile.WriteAllText(Path.Combine(_root, "newdir") + Path.DirectorySeparatorChar, "x"));
        Assert.Empty(Directory.GetFiles(directory));
    }

    [Fact]
    public void AReadOnlyTargetIsRefusedAndKeepsItsContent()
    {
        var path = Path.Combine(_root, "readonly.txt");
        File.WriteAllText(path, "keep me");
        File.SetAttributes(path, FileAttributes.ReadOnly);

        var error = Assert.Throws<IOException>(() => OwnerOnlyFile.WriteAllText(path, "overwrite"));

        Assert.Contains("read-only", error.Message, StringComparison.Ordinal);
        File.SetAttributes(path, FileAttributes.Normal);
        Assert.Equal("keep me", File.ReadAllText(path));
        Assert.Equal(["readonly.txt"], Directory.GetFileSystemEntries(_root).Select(Path.GetFileName));
    }

    [Fact]
    public void BadArgumentsAreRejectedBeforeAnythingIsWritten()
    {
        Assert.Throws<ArgumentNullException>(() => OwnerOnlyFile.WriteAllText(Path.Combine(_root, "x"), null!));
        Assert.Throws<ArgumentNullException>(() => OwnerOnlyFile.WriteAllText(null!, "x"));
        Assert.Throws<ArgumentException>(() => OwnerOnlyFile.WriteAllText("   ", "x"));
        Assert.Throws<ArgumentException>(() => OwnerOnlyFile.WriteAllText(string.Empty, "x"));
        Assert.Empty(Directory.GetFileSystemEntries(_root));
    }

    [Fact]
    public void ARelativePathIsResolvedAgainstTheCurrentDirectory()
    {
        var name = "r1b-relative-" + Guid.NewGuid().ToString("N") + ".txt";
        var full = Path.GetFullPath(name);
        try
        {
            OwnerOnlyFile.WriteAllText(name, "relative");

            Assert.Equal("relative", File.ReadAllText(full));
            AssertOwnerOnly(full);
        }
        finally
        {
            File.Delete(full);
        }
    }

    [Fact]
    public void AFileLockedByAnotherHandleFailsWithAnIoExceptionAndLeavesNoTemporaryFile()
    {
        var path = Path.Combine(_root, "locked.txt");
        File.WriteAllText(path, "locked");

        if (!OperatingSystem.IsWindows())
        {
            // POSIX rename ignores open handles: the replacement simply succeeds.
            using var open = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            OwnerOnlyFile.WriteAllText(path, "replaced");
            Assert.Equal("replaced", File.ReadAllText(path));
            return;
        }

        using var handle = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
        var error = Assert.ThrowsAny<Exception>(() => OwnerOnlyFile.WriteAllText(path, "replaced"));
        Assert.True(error is IOException or UnauthorizedAccessException, error.GetType().FullName);
        Assert.DoesNotContain(Directory.GetFileSystemEntries(_root), static e => e.EndsWith(".tmp", StringComparison.Ordinal));
    }
}
