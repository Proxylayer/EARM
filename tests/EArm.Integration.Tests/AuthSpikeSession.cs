using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// The shared state of the delegated-auth spike (DES-0002, <see cref="DelegatedAuthSpikeProbes"/>): the gate and its environment
/// variables, one interactive sign-in per client id (shared by every probe so the browser opens as few times as possible), and the
/// isolation from EARM's own sign-in. The spike never reads or writes EARM's <c>earm</c> token cache or its sign-in record: it uses
/// the persisted cache <see cref="CacheName"/> and record files under <see cref="WorkDirectory"/>, and deletes both when the class
/// finishes (unless <c>EARM_IT_AUTH_SPIKE_KEEP=1</c>). The results log (no tokens) is kept.
/// </summary>
public sealed class AuthSpikeSession : IDisposable
{
    public const string GateVariable = "EARM_IT_AUTH_SPIKE";
    public const string KeepVariable = "EARM_IT_AUTH_SPIKE_KEEP";
    public const string DeviceCodeVariable = "EARM_IT_AUTH_SPIKE_DEVICECODE";
    public const string ExtraScopesVariable = "EARM_IT_AUTH_SPIKE_EXTRA_SCOPES";
    public const string AzureCliVariable = "EARM_IT_AUTH_SPIKE_AZCLI";
    public const string KeyVaultUrlVariable = "EARM_IT_KEYVAULT_URL";
    public const string KeyVaultSecretVariable = "EARM_IT_KEYVAULT_SECRET";

    /// <summary>Microsoft Graph Command Line Tools (the public client DES-0002 makes the default).</summary>
    public const string GraphCliToolsClientId = "14d82eec-204b-4c2f-b7e8-296a70dab67e";

    /// <summary>Microsoft Azure CLI (today's default public client).</summary>
    public const string AzureCliClientId = "04b07795-8ddb-461a-bbee-02f9e1bf7b46";

    /// <summary>The spike's own persisted MSAL cache; deliberately not <see cref="EArm.Graph.Auth.GraphAuthFactory.CacheName"/> (and not matched by its logout pattern).</summary>
    public const string CacheName = "earm-spike";

    /// <summary>How long an interactive prompt (browser or device code) may take before the attempt is recorded as timed out.</summary>
    private static readonly TimeSpan _promptTimeout = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, AuthenticationRecord> _records = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AuthSpikeAttempt> _signInFailures = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock _logLock = new();
    private readonly string? _configurationError;

    public AuthSpikeSession()
    {
        Enabled = IsOn(GateVariable);
        TenantId = Read(IntegrationSettings.TenantVariable);
        Keep = IsOn(KeepVariable);
        UseDeviceCode = IsOn(DeviceCodeVariable);
        AzureCliEnabled = IsOn(AzureCliVariable);
        KeyVaultUrl = Read(KeyVaultUrlVariable);
        KeyVaultSecret = Read(KeyVaultSecretVariable);
        ExtraScopeNames = (Read(ExtraScopesVariable) ?? "Policy.Read.All")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToArray();
        WorkDirectory = Path.Combine(Path.GetTempPath(), "earm-it", "auth-spike");
        LogPath = Path.Combine(WorkDirectory, "spike-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture) + ".log");
        try
        {
            Cloud = GraphCloud.FromEnvironment();
        }
        catch (GraphConfigurationException ex)
        {
            Cloud = GraphCloud.Public;
            _configurationError = ex.Message;
        }
    }

    public bool Enabled { get; }

    public string? TenantId { get; }

    public bool Keep { get; }

    public bool UseDeviceCode { get; }

    public bool AzureCliEnabled { get; }

    public string? KeyVaultUrl { get; }

    public string? KeyVaultSecret { get; }

    /// <summary>The scope names (without resource) added in S2 step c; default <c>Policy.Read.All</c>.</summary>
    public IReadOnlyList<string> ExtraScopeNames { get; }

    public GraphCloud Cloud { get; }

    /// <summary><c>%TEMP%\earm-it\auth-spike</c>: the record files (deleted at the end) and the results logs (kept).</summary>
    public string WorkDirectory { get; }

    /// <summary>This run's results log: every diagnostic line, appended live (tail it with <c>Get-Content -Wait</c> during a device-code run).</summary>
    public string LogPath { get; }

    /// <summary>The base Graph scopes of the shared sign-in (S2 step a). The spike keeps the set it was run with; <c>earm auth login</c> now asks for <c>User.Read</c> only.</summary>
    internal IReadOnlyList<string> BaseGraphScopes => [GraphScope("User.Read"), GraphScope("Organization.Read.All")];

    internal IReadOnlyList<string> ExtendedGraphScopes => [.. BaseGraphScopes, .. ExtraScopeNames.Select(GraphScope)];

    internal string KeyVaultResource => "https://" + Cloud.KeyVaultSuffix;

    /// <summary>Skips the calling test unless the spike is enabled; fails it when the configuration is unusable.</summary>
    internal void Require()
    {
        Assert.SkipUnless(Enabled, $"{GateVariable}=1 is not set: the delegated-auth spike (DES-0002) is interactive (browser sign-in and consent prompts) and runs only on request. See tests/EArm.Integration.Tests/README.md.");
        Assert.SkipWhen(TenantId is null, $"{IntegrationSettings.TenantVariable} is not set: the spike signs in to that tenant.");
        if (_configurationError is not null)
        {
            Assert.Fail("The spike cannot run: " + _configurationError);
        }
    }

    internal string GraphScope(string name)
    {
        return Cloud.GraphOrigin + "/" + name;
    }

    internal bool HasRecord(string clientId)
    {
        return _records.ContainsKey(clientId);
    }

    internal AuthenticationRecord? RecordFor(string clientId)
    {
        return _records.GetValueOrDefault(clientId);
    }

    /// <summary>
    /// The shared sign-in for <paramref name="clientId"/>: the first caller signs in interactively with <paramref name="scopes"/> (one
    /// browser prompt) and stores the record; later callers reuse it. A failed sign-in is remembered, so the next probe does not prompt again.
    /// Returns the attempt that signed in (with the token's claims), or null when the record already existed.
    /// </summary>
    internal async Task<AuthSpikeAttempt?> EnsureSignedInAsync(string label, string clientId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        if (_records.ContainsKey(clientId))
        {
            Log($"{label}: reusing the shared sign-in of client {clientId} (no prompt).");
            return null;
        }

        if (_signInFailures.TryGetValue(clientId, out AuthSpikeAttempt? earlier))
        {
            Log($"{label}: the shared sign-in of client {clientId} already failed earlier in this run ({earlier.Verdict}); not prompting again.");
            return earlier;
        }

        AuthSpikeAttempt attempt = await SignInAsync(label, clientId, scopes, cancellationToken).ConfigureAwait(false);
        if (!attempt.IsGranted)
        {
            _signInFailures[clientId] = attempt;
        }

        return attempt;
    }

    /// <summary>An interactive sign-in (<c>AuthenticateAsync</c>, always prompts) that stores the record, followed by a silent token request for the same scopes to read the claims.</summary>
    internal async Task<AuthSpikeAttempt> SignInAsync(string label, string clientId, IReadOnlyList<string> scopes, CancellationToken cancellationToken)
    {
        Log($"{label}: interactive sign-in for client {clientId} ({(UseDeviceCode ? "device code" : "browser")}), scopes=[{string.Join(", ", scopes)}]. Complete the prompt.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_promptTimeout);
        AuthenticationRecord record;
        try
        {
            var context = new TokenRequestContext([.. scopes]);
            record = UseDeviceCode
                ? await new DeviceCodeCredential(DeviceCodeOptions(clientId, null, silent: false)).AuthenticateAsync(context, timeout.Token).ConfigureAwait(false)
                : await new InteractiveBrowserCredential(BrowserOptions(clientId, null, silent: false)).AuthenticateAsync(context, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Report(AuthSpikeAttempt.FromException(label, clientId, scopes, silent: false, ex, timedOut: true));
        }
        catch (Exception ex) when (ex is AuthenticationFailedException or CredentialUnavailableException)
        {
            return Report(AuthSpikeAttempt.FromException(label, clientId, scopes, silent: false, ex));
        }

        _records[clientId] = record;
        await SaveRecordAsync(clientId, record, cancellationToken).ConfigureAwait(false);
        Log($"{label}: signed in; record authority={record.Authority} tenant={record.TenantId} client={record.ClientId}.");
        return await TryTokenAsync(label + " (token after sign-in)", clientId, scopes, silent: true, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// One token request with a NEW credential instance bound to the stored record of <paramref name="clientId"/> (so only the persisted
    /// cache carries state between requests). <paramref name="silent"/> sets <c>DisableAutomaticAuthentication</c>; otherwise Azure.Identity
    /// tries silently first and prompts only when it must.
    /// </summary>
    internal async Task<AuthSpikeAttempt> TryTokenAsync(string label, string clientId, IReadOnlyList<string> scopes, bool silent, CancellationToken cancellationToken)
    {
        AuthenticationRecord? record = RecordFor(clientId);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_promptTimeout);
        try
        {
            TokenCredential credential = UseDeviceCode
                ? new DeviceCodeCredential(DeviceCodeOptions(clientId, record, silent))
                : new InteractiveBrowserCredential(BrowserOptions(clientId, record, silent));
            AccessToken token = await credential.GetTokenAsync(new TokenRequestContext([.. scopes]), timeout.Token).ConfigureAwait(false);
            return Report(AuthSpikeAttempt.FromToken(label, clientId, scopes, silent, token.Token));
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return Report(AuthSpikeAttempt.FromException(label, clientId, scopes, silent, ex, timedOut: true));
        }
        catch (Exception ex) when (ex is AuthenticationFailedException or AuthenticationRequiredException or CredentialUnavailableException)
        {
            return Report(AuthSpikeAttempt.FromException(label, clientId, scopes, silent, ex));
        }
    }

    /// <summary>Writes a line to the test output, the diagnostic messages and the results log.</summary>
    internal void Log(string line)
    {
        string stamped = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + line;
        TestContext.Current.TestOutputHelper?.WriteLine(stamped);
        TestContext.Current.SendDiagnosticMessage("[auth-spike] " + line);
        lock (_logLock)
        {
            Directory.CreateDirectory(WorkDirectory);
            File.AppendAllText(LogPath, stamped + Environment.NewLine);
        }
    }

    internal AuthSpikeAttempt Report(AuthSpikeAttempt attempt)
    {
        foreach (string line in attempt.Describe())
        {
            Log(line);
        }

        return attempt;
    }

    /// <summary>The header every probe prints first.</summary>
    internal void LogHeader(string probe, string clientId)
    {
        Log($"==== {probe} ==== client={clientId} tenant={TenantId} cloud={Cloud.Key} authority={Cloud.AuthorityHost} graph={Cloud.GraphOrigin} cache={CacheName} log={LogPath}");
    }

    public void Dispose()
    {
        if (!Enabled)
        {
            return;
        }

        if (Keep)
        {
            AppendLog($"cleanup skipped ({KeepVariable}=1): record files under {WorkDirectory} and the '{CacheName}' token cache are kept.");
            return;
        }

        int records = 0;
        if (Directory.Exists(WorkDirectory))
        {
            foreach (string file in Directory.EnumerateFiles(WorkDirectory, "record-*.json"))
            {
                records += TryDelete(file) ? 1 : 0;
            }
        }

        int caches = 0;
        foreach (string directory in CacheDirectories())
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (string file in Directory.EnumerateFiles(directory, CacheName + "*"))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, CacheName, StringComparison.OrdinalIgnoreCase) || name.StartsWith(CacheName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    caches += TryDelete(file) ? 1 : 0;
                }
            }
        }

        AppendLog($"cleanup: deleted {records} record file(s) and {caches} '{CacheName}' token-cache file(s). Consent granted during the spike is NOT revoked (see the README).");
    }

    private InteractiveBrowserCredentialOptions BrowserOptions(string clientId, AuthenticationRecord? record, bool silent)
    {
        return new InteractiveBrowserCredentialOptions
        {
            ClientId = clientId,
            TenantId = TenantId,
            AuthorityHost = Cloud.AuthorityHost,
            AuthenticationRecord = record,
            DisableAutomaticAuthentication = silent,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
        };
    }

    private DeviceCodeCredentialOptions DeviceCodeOptions(string clientId, AuthenticationRecord? record, bool silent)
    {
        return new DeviceCodeCredentialOptions
        {
            ClientId = clientId,
            TenantId = TenantId,
            AuthorityHost = Cloud.AuthorityHost,
            AuthenticationRecord = record,
            DisableAutomaticAuthentication = silent,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
            DeviceCodeCallback = (info, _) =>
            {
                // The test output is only shown when the test ends; the console and the live log are what a person can watch meanwhile.
                Console.Error.WriteLine(info.Message);
                Log("DEVICE CODE: " + info.Message);
                return Task.CompletedTask;
            },
        };
    }

    private async Task SaveRecordAsync(string clientId, AuthenticationRecord record, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(WorkDirectory);
        string path = Path.Combine(WorkDirectory, $"record-{clientId}.json");
        await using var stream = File.Create(path);
        await record.SerializeAsync(stream, cancellationToken).ConfigureAwait(false);
    }

    private void AppendLog(string line)
    {
        lock (_logLock)
        {
            Directory.CreateDirectory(WorkDirectory);
            File.AppendAllText(LogPath, DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + line + Environment.NewLine);
        }
    }

    private static IEnumerable<string> CacheDirectories()
    {
        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        return new[] { localAppData, profile }
            .Where(root => !string.IsNullOrEmpty(root))
            .Select(root => Path.Combine(root, ".IdentityService"))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static bool TryDelete(string file)
    {
        try
        {
            File.Delete(file);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? Read(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsOn(string name)
    {
        return Read(name) is "1" or "true" or "TRUE" or "True" or "yes";
    }
}
