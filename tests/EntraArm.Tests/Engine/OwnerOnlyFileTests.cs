using System;
using System.IO;
using System.Linq;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using EntraArm.Engine.Execution;
using Xunit;

namespace EntraArm.Tests.Engine;

/// <summary>The owner-only writer behind <c>--secure-outputs-file</c>: content, encoding, replacement and (per platform) permissions.</summary>
public sealed class OwnerOnlyFileTests
{
    [Fact]
    public void WriteAllText_WritesUtf8WithoutBom_AndCreatesMissingDirectories()
    {
        using var directory = new TestDirectory(nameof(WriteAllText_WritesUtf8WithoutBom_AndCreatesMissingDirectories));
        string path = Path.Combine(directory.Path, "a", "b", "secure.json");

        OwnerOnlyFile.WriteAllText(path, "{ \"secret\": \"ünïcode ✓\" }");

        byte[] bytes = File.ReadAllBytes(path);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no BOM");
        Assert.Equal("{ \"secret\": \"ünïcode ✓\" }", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void WriteAllText_ReplacesAnExistingFile_EvenAShorterOne()
    {
        using var directory = new TestDirectory(nameof(WriteAllText_ReplacesAnExistingFile_EvenAShorterOne));
        string path = directory.File("secure.json");
        File.WriteAllText(path, new string('x', 500));

        OwnerOnlyFile.WriteAllText(path, "short");

        Assert.Equal("short", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_AnEmptyString_CreatesAnEmptyFile()
    {
        using var directory = new TestDirectory(nameof(WriteAllText_AnEmptyString_CreatesAnEmptyFile));
        string path = directory.File("empty.json");

        OwnerOnlyFile.WriteAllText(path, string.Empty);

        Assert.True(File.Exists(path));
        Assert.Equal(0, new FileInfo(path).Length);
    }

    [Fact]
    public void WriteAllText_RelativePaths_ResolveAgainstTheCurrentDirectory()
    {
        using var directory = new TestDirectory(nameof(WriteAllText_RelativePaths_ResolveAgainstTheCurrentDirectory));
        string relative = Path.GetRelativePath(Directory.GetCurrentDirectory(), directory.File("relative.json"));

        OwnerOnlyFile.WriteAllText(relative, "x");

        Assert.Equal("x", File.ReadAllText(directory.File("relative.json")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void WriteAllText_RejectsABlankPath(string? path)
    {
        Assert.ThrowsAny<ArgumentException>(() => OwnerOnlyFile.WriteAllText(path!, "x"));
    }

    [Fact]
    public void WriteAllText_RejectsNullContent()
    {
        using var directory = new TestDirectory(nameof(WriteAllText_RejectsNullContent));

        Assert.Throws<ArgumentNullException>(() => OwnerOnlyFile.WriteAllText(directory.File("x.json"), null!));
        Assert.False(File.Exists(directory.File("x.json")));
    }

    [Fact]
    public void WriteAllText_OnWindows_GrantsFullControlToTheCurrentUserOnly_AndBlocksInheritance()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DACL assertions need Windows.");
            return;
        }

        using var directory = new TestDirectory(nameof(WriteAllText_OnWindows_GrantsFullControlToTheCurrentUserOnly_AndBlocksInheritance));
        string path = directory.File("secure.json");

        OwnerOnlyFile.WriteAllText(path, "secret");

        AssertOwnerOnlyAcl(path);
    }

    /// <summary>
    /// OwnerOnlyFile.WriteWindows used to pass the owner-only FileSecurity to FileInfo.Create(FileMode.Create, ...). CreateFile applies a
    /// security descriptor only when it creates the file; over an existing file the old DACL, inherited entries included, stayed, so a
    /// --secure-outputs-file that already existed was overwritten with the secret and remained readable by everyone its folder grants. The
    /// content is now written to a new owner-only temp file in the same folder and moved over the target.
    /// </summary>
    [Fact]
    public void WriteAllText_OnWindows_ReplacingAnExistingFile_DoesNotKeepItsOldDacl()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DACL assertions need Windows.");
            return;
        }

        using var directory = new TestDirectory(nameof(WriteAllText_OnWindows_ReplacingAnExistingFile_DoesNotKeepItsOldDacl));
        string path = directory.File("secure.json");
        File.WriteAllText(path, "pre-existing, default ACL");

        OwnerOnlyFile.WriteAllText(path, "secret");

        AssertOwnerOnlyAcl(path);
        Assert.Equal("secret", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_OnWindows_AnExistingFileWithAWideOpenDacl_EndsOwnerOnly()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("DACL assertions need Windows.");
            return;
        }

        using var directory = new TestDirectory(nameof(WriteAllText_OnWindows_AnExistingFileWithAWideOpenDacl_EndsOwnerOnly));
        string path = directory.File("secure.json");
        File.WriteAllText(path, "old");
        GrantEveryoneRead(path);
        Assert.True(HasEveryoneRule(path), "the precondition: the file was readable by Everyone");

        OwnerOnlyFile.WriteAllText(path, "secret");

        AssertOwnerOnlyAcl(path);
        Assert.False(HasEveryoneRule(path), "Everyone must not keep any access");
    }

    [Fact]
    public void WriteAllText_ReplacingTwiceInARow_StaysOwnerOnly_AndLeavesNoTempFileBehind()
    {
        using var directory = new TestDirectory(nameof(WriteAllText_ReplacingTwiceInARow_StaysOwnerOnly_AndLeavesNoTempFileBehind));
        string path = directory.File("secure.json");

        OwnerOnlyFile.WriteAllText(path, "one");
        OwnerOnlyFile.WriteAllText(path, "two");
        OwnerOnlyFile.WriteAllText(path, "three");

        Assert.Equal("three", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
        VerifyOwnerOnly(path);
    }

    [Fact]
    public void WriteAllText_OnUnix_AnExistingFileWithALooserMode_EndsAs0600()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes need a Unix host.");
            return;
        }

        using var directory = new TestDirectory(nameof(WriteAllText_OnUnix_AnExistingFileWithALooserMode_EndsAs0600));
        string path = directory.File("secure.json");
        File.WriteAllText(path, "old");
        SetLooseMode(path);

        OwnerOnlyFile.WriteAllText(path, "secret");

        AssertMode0600(path);
        Assert.Equal("secret", File.ReadAllText(path));
    }

    [Fact]
    public void WriteAllText_ADirectoryPath_IsAClearIOException_AndNothingIsCreated()
    {
        using var directory = new TestDirectory(nameof(WriteAllText_ADirectoryPath_IsAClearIOException_AndNothingIsCreated));
        string target = directory.Sub("secure.json");

        var ex = Assert.Throws<IOException>(() => OwnerOnlyFile.WriteAllText(target, "secret"));

        Assert.Contains("is a directory", ex.Message, StringComparison.Ordinal);
        Assert.True(Directory.Exists(target));
        Assert.Empty(Directory.GetFileSystemEntries(target));
        Assert.Equal([target], Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public void WriteAllText_ATrailingSeparator_IsAClearIOException()
    {
        using var directory = new TestDirectory(nameof(WriteAllText_ATrailingSeparator_IsAClearIOException));

        var ex = Assert.Throws<IOException>(() => OwnerOnlyFile.WriteAllText(directory.Path + Path.DirectorySeparatorChar + "missing" + Path.DirectorySeparatorChar, "secret"));

        Assert.Contains("is a directory", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteAllText_AReadOnlyExistingFile_IsAClearIOException_AndKeepsItsContent()
    {
        using var directory = new TestDirectory(nameof(WriteAllText_AReadOnlyExistingFile_IsAClearIOException_AndKeepsItsContent));
        string path = directory.File("secure.json");
        File.WriteAllText(path, "keep me");
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var ex = Assert.Throws<IOException>(() => OwnerOnlyFile.WriteAllText(path, "secret"));

            Assert.Contains("read-only", ex.Message, StringComparison.Ordinal);
            Assert.Equal("keep me", File.ReadAllText(path));
            Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void WriteAllText_WhenTheTargetIsHeldOpen_FailsAndRemovesTheTempFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix lets a rename replace an open file; the sharing violation is Windows behaviour.");
            return;
        }

        using var directory = new TestDirectory(nameof(WriteAllText_WhenTheTargetIsHeldOpen_FailsAndRemovesTheTempFile));
        string path = directory.File("secure.json");
        File.WriteAllText(path, "locked");
        using var holder = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);

        var failure = Assert.ThrowsAny<Exception>(() => OwnerOnlyFile.WriteAllText(path, "secret"));
        Assert.True(failure is IOException or UnauthorizedAccessException, failure.GetType().Name);

        holder.Dispose();
        Assert.Equal("locked", File.ReadAllText(path));
        Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public void WriteAllText_ALongSecret_IsWrittenInFull()
    {
        using var directory = new TestDirectory(nameof(WriteAllText_ALongSecret_IsWrittenInFull));
        string path = directory.File("secure.json");
        string secret = new('s', 1_000_000);

        OwnerOnlyFile.WriteAllText(path, secret);

        Assert.Equal(secret, File.ReadAllText(path));
        VerifyOwnerOnly(path);
    }

    [Fact]
    public void WriteAllText_ANameWithSpacesAndUnicode_Works()
    {
        using var directory = new TestDirectory(nameof(WriteAllText_ANameWithSpacesAndUnicode_Works));
        string path = directory.File("secure outputs ünï ✓.json");

        OwnerOnlyFile.WriteAllText(path, "x");
        OwnerOnlyFile.WriteAllText(path, "y");

        Assert.Equal("y", File.ReadAllText(path));
        VerifyOwnerOnly(path);
    }

    private static void VerifyOwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            AssertOwnerOnlyAcl(path);
        }
        else
        {
            AssertMode0600(path);
        }
    }

    [SupportedOSPlatform("windows")]
    private static void GrantEveryoneRead(string path)
    {
        var info = new FileInfo(path);
        var security = info.GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Read, AccessControlType.Allow));
        info.SetAccessControl(security);
    }

    [SupportedOSPlatform("windows")]
    private static bool HasEveryoneRule(string path)
    {
        var everyone = new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        return new FileInfo(path).GetAccessControl().GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Any(r => r.IdentityReference.Equals(everyone));
    }

    [UnsupportedOSPlatform("windows")]
    private static void SetLooseMode(string path)
    {
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead | UnixFileMode.OtherWrite);
    }

    [Fact]
    public void WriteAllText_OnUnix_UsesMode0600()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("Unix file modes need a Unix host.");
            return;
        }

        using var directory = new TestDirectory(nameof(WriteAllText_OnUnix_UsesMode0600));
        string path = directory.File("secure.json");

        OwnerOnlyFile.WriteAllText(path, "secret");

        AssertMode0600(path);
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnerOnlyAcl(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        using var identity = WindowsIdentity.GetCurrent();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        Assert.True(security.AreAccessRulesProtected, "inheritance from the parent folder is blocked");
        var rule = Assert.Single(rules);
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.False(rule.IsInherited);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights & FileSystemRights.FullControl);
    }

    [UnsupportedOSPlatform("windows")]
    private static void AssertMode0600(string path)
    {
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
    }
}