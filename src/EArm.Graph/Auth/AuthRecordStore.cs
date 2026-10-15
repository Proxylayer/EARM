using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Azure.Identity;

namespace EArm.Graph.Auth;

/// <summary>
/// Where the non-secret <see cref="AuthenticationRecord"/> of an interactive sign-in lives:
/// <c>%LOCALAPPDATA%\EArm\auth\record.json</c> on Windows, <c>$XDG_DATA_HOME/earm/auth/record.json</c>
/// (default <c>~/.local/share</c>) elsewhere. The record names the account (username, tenant, client, authority) so Azure.Identity can find
/// the right entry in the encrypted token cache; it holds no token. It is still written owner-only (<see cref="OwnerOnlyFile"/>).
/// </summary>
public sealed class AuthRecordStore
{
    private readonly string _path;

    public AuthRecordStore(string? path = null)
    {
        _path = path ?? DefaultPath();
    }

    /// <summary>The full path of the record file.</summary>
    public string FilePath => _path;

    public static string DefaultPath()
    {
        if (OperatingSystem.IsWindows())
        {
            var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
            if (string.IsNullOrEmpty(localAppData))
            {
                localAppData = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "EArm-" + Environment.UserName);
            }

            return System.IO.Path.Combine(localAppData, "EArm", "auth", "record.json");
        }

        var dataHome = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
        if (string.IsNullOrWhiteSpace(dataHome) || !System.IO.Path.IsPathRooted(dataHome))
        {
            dataHome = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify), ".local", "share");
        }

        return System.IO.Path.Combine(dataHome, "earm", "auth", "record.json");
    }

    public bool Exists => File.Exists(_path);

    /// <summary>The stored record, or null when there is none or it cannot be read (a damaged file is treated as "not signed in").</summary>
    public AuthenticationRecord? TryLoad()
    {
        if (!File.Exists(_path))
        {
            return null;
        }

        try
        {
            using var stream = new FileStream(_path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var record = AuthenticationRecord.Deserialize(stream);
            return IsUsable(record) ? record : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    /// <summary>A record is usable when it names an account: "{}" or a record of another shape deserializes into an empty shell that no credential could use.</summary>
    private static bool IsUsable(AuthenticationRecord record)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(record.Username) && !string.IsNullOrWhiteSpace(record.Authority) && !string.IsNullOrWhiteSpace(record.HomeAccountId);
        }
        catch (NullReferenceException)
        {
            // AuthenticationRecord.HomeAccountId throws when the field was absent.
            return false;
        }
    }

    /// <summary>Writes the record owner-only.</summary>
    public async Task SaveAsync(AuthenticationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        using var buffer = new MemoryStream();
        await record.SerializeAsync(buffer, cancellationToken).ConfigureAwait(false);
        OwnerOnlyFile.WriteAllText(_path, Encoding.UTF8.GetString(buffer.ToArray()));
    }

    /// <summary>Deletes the record. True when a file was removed.</summary>
    public bool Delete()
    {
        if (!File.Exists(_path))
        {
            return false;
        }

        File.Delete(_path);
        return true;
    }
}
