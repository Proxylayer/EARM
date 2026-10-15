using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine.Execution;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// Review 5M: the one-time-secrets merge under the <c>&lt;file&gt;.lock</c> survives what Windows does around a file that was just closed or replaced (a scanner or indexer
/// holding it for a moment: "access denied" while a delete is pending, a sharing violation on the replace) and never drops a secret: when it cannot finish, the
/// secrets are kept in <c>&lt;file&gt;.pending-&lt;guid&gt;</c> and the call fails loudly. The sharing scenarios are Windows behaviour; elsewhere they pass trivially.
/// </summary>
public sealed class Review5M_SecureOutputsRaceTests
{
    private const string ResourceId = "/providers/Microsoft.Entra/applications/app/passwordCredentials/c";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static JsonObject Secret(string value)
    {
        return new JsonObject { ["secretText"] = value };
    }

    private static string[] PendingFiles(string path)
    {
        return Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileName(path) + ".pending-*");
    }

    [Fact]
    public async Task ALockFileWhoseDeleteIsStillPending_IsWaitedOutNotReportedAsAccessDenied()
    {
        using var directory = new TestDirectory(nameof(ALockFileWhoseDeleteIsStillPending_IsWaitedOutNotReportedAsAccessDenied));
        string path = directory.File("secure.json");
        string lockPath = path + ".lock";

        // A scanner holding the previous lock file at the moment its holder closed it: the file is deleted but stays delete-pending, and opening it
        // meanwhile gives UnauthorizedAccessException on Windows (not the IOException of a held lock).
        var scanner = new FileStream(lockPath, FileMode.Create, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        File.Delete(lockPath);
        var release = Task.Run(async () =>
        {
            await Task.Delay(400, Ct);
            await scanner.DisposeAsync();
        }, Ct);

        SecureOutputsFile.MergeOneTimeSecret(path, ResourceId, Secret("s1"), TimeSpan.FromSeconds(20));
        await release;

        var bucket = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!["oneTimeSecrets"]!.AsObject();
        Assert.Equal("s1", bucket[ResourceId]!["secretText"]!.GetValue<string>());
        Assert.Empty(PendingFiles(path));
    }

    [Fact]
    public async Task AReplaceThatIsRefusedForAMoment_IsRetried_AndTheSecretLandsInTheFile()
    {
        using var directory = new TestDirectory(nameof(AReplaceThatIsRefusedForAMoment_IsRetried_AndTheSecretLandsInTheFile));
        string path = directory.File("secure.json");
        SecureOutputsFile.MergeOneTimeSecret(path, ResourceId + "0", Secret("s0"));

        // A reader that does not share delete: Windows refuses to replace the file under it until it is closed.
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var release = Task.Run(async () =>
        {
            await Task.Delay(400, Ct);
            await reader.DisposeAsync();
        }, Ct);

        SecureOutputsFile.MergeOneTimeSecret(path, ResourceId + "1", Secret("s1"));
        await release;

        var bucket = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!["oneTimeSecrets"]!.AsObject();
        Assert.Equal("s0", bucket[ResourceId + "0"]!["secretText"]!.GetValue<string>());
        Assert.Equal("s1", bucket[ResourceId + "1"]!["secretText"]!.GetValue<string>());
        Assert.Empty(PendingFiles(path));
    }

    [Fact]
    public async Task ALockThatIsNeverReleased_FailsLoudly_AndTheSecretIsKeptInAPendingFile()
    {
        using var directory = new TestDirectory(nameof(ALockThatIsNeverReleased_FailsLoudly_AndTheSecretIsKeptInAPendingFile));
        string path = directory.File("secure.json");
        await File.WriteAllTextAsync(path, "{ \"existing\": { \"type\": \"string\", \"value\": \"keep\" } }", Ct);
        using var holder = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

        var ex = Assert.Throws<SecureOutputsPendingException>(() => SecureOutputsFile.MergeOneTimeSecret(path, ResourceId, Secret("pending-secret-value"), TimeSpan.FromMilliseconds(300)));

        string pending = Assert.Single(PendingFiles(path));
        Assert.Equal(pending, ex.PendingPath);
        Assert.Contains(Path.GetFileName(pending), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("pending-secret-value", ex.Message, StringComparison.Ordinal);
        var kept = JsonNode.Parse(await File.ReadAllTextAsync(pending, Ct))!["oneTimeSecrets"]!.AsObject();
        Assert.Equal("pending-secret-value", kept[ResourceId]!["secretText"]!.GetValue<string>());
        Assert.Contains("keep", await File.ReadAllTextAsync(path, Ct), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecureOutputsMerge_ThatCannotGetTheLock_KeepsTheOutputsInAPendingFile_Too()
    {
        using var directory = new TestDirectory(nameof(ASecureOutputsMerge_ThatCannotGetTheLock_KeepsTheOutputsInAPendingFile_Too));
        string path = directory.File("secure.json");
        using var holder = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        var outputs = new JsonObject { ["password"] = new JsonObject { ["type"] = "securestring", ["value"] = "p-value" } };

        var ex = Assert.Throws<SecureOutputsPendingException>(() => SecureOutputsFile.MergeOutputs(path, outputs, TimeSpan.FromMilliseconds(200)));

        var kept = JsonNode.Parse(await File.ReadAllTextAsync(ex.PendingPath, Ct))!.AsObject();
        Assert.Equal("p-value", kept["password"]!["value"]!.GetValue<string>());
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task AReplaceThatKeepsFailing_FailsLoudly_AndTheSecretIsKeptInAPendingFile()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TestDirectory(nameof(AReplaceThatKeepsFailing_FailsLoudly_AndTheSecretIsKeptInAPendingFile));
        string path = directory.File("secure.json");
        SecureOutputsFile.MergeOneTimeSecret(path, ResourceId + "0", Secret("s0"));
        await using var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

        var ex = Assert.Throws<SecureOutputsPendingException>(() => SecureOutputsFile.MergeOneTimeSecret(path, ResourceId + "1", Secret("s1")));

        var kept = JsonNode.Parse(await File.ReadAllTextAsync(ex.PendingPath, Ct))!["oneTimeSecrets"]!.AsObject();
        Assert.Equal("s1", kept[ResourceId + "1"]!["secretText"]!.GetValue<string>());
        Assert.True(await LockGoneAsync(path + ".lock"));
        Assert.Empty(Directory.GetFiles(directory.Path, ".secure.json.*.tmp"));
    }

    [Fact]
    public async Task ManyConcurrentMerges_KeepEverySecret_AndLeaveNeitherALockNorAPendingFile()
    {
        using var directory = new TestDirectory(nameof(ManyConcurrentMerges_KeepEverySecret_AndLeaveNeitherALockNorAPendingFile));
        string path = directory.File("secure.json");

        await Task.WhenAll(Enumerable.Range(0, 24).Select(i => Task.Run(() => SecureOutputsFile.MergeOneTimeSecret(path, ResourceId + i, Secret("secret-" + i)), Ct)));

        var bucket = JsonNode.Parse(await File.ReadAllTextAsync(path, Ct))!["oneTimeSecrets"]!.AsObject();
        Assert.Equal(24, bucket.Count);
        Assert.Empty(PendingFiles(path));
        Assert.True(await LockGoneAsync(path + ".lock"));
    }

    /// <summary>The lock file is deleted when its holder closes it; a scanner can keep it for a moment, so the check waits a little instead of racing the operating system.</summary>
    private static async Task<bool> LockGoneAsync(string lockPath)
    {
        for (int i = 0; i < 100; i++)
        {
            if (!File.Exists(lockPath))
            {
                return true;
            }

            await Task.Delay(20, Ct);
        }

        return false;
    }
}
