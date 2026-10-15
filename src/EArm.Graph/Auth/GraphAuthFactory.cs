using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace EArm.Graph.Auth;

/// <summary>What <see cref="GraphAuthFactory.Logout"/> removed.</summary>
/// <param name="RecordDeleted">The sign-in record file existed and was deleted.</param>
/// <param name="CacheFilesDeleted">Number of persistent token-cache files of the <c>earm</c> cache that were deleted.</param>
public sealed record GraphLogoutResult(bool RecordDeleted, int CacheFilesDeleted);

/// <summary>
/// Builds the token credential chosen by <see cref="GraphAuthOptions"/> and performs the interactive sign-in / sign-out.
/// <list type="bullet">
/// <item><b>default</b>: <see cref="DefaultAzureCredential"/> with every credential that starts an external process excluded
/// (Azure CLI, Azure Developer CLI, Azure PowerShell, Visual Studio); what remains is environment variables (service principal secret /
/// certificate / username-password), workload identity and managed identity.</item>
/// <item><b>interactive</b>: the cached sign-in of <c>earm auth login</c> (an <see cref="InteractiveBrowserCredential"/> that never opens a
/// browser by itself).</item>
/// <item><b>auto</b>: the interactive sign-in when a record exists, else the default chain. With a record there is NO fallback: if the interactive
/// sign-in cannot produce a token silently, the call fails with guidance (<c>earm auth login</c>, or <c>--auth default</c>) instead of quietly
/// acting as a different identity.</item>
/// </list>
/// The methods are virtual so tests can substitute fake credentials without a browser.
/// </summary>
public class GraphAuthFactory
{
    /// <summary>Name of the persistent MSAL token cache (<see cref="TokenCachePersistenceOptions.Name"/>).</summary>
    public const string CacheName = "earm";

    /// <summary>The <c>AZURE_TOKEN_CREDENTIALS</c> values that cannot enable a credential that starts a process.</summary>
    private static readonly HashSet<string> SafeTokenCredentialValues = new(StringComparer.OrdinalIgnoreCase)
    {
        "prod",
        "EnvironmentCredential",
        "WorkloadIdentityCredential",
        "ManagedIdentityCredential",
    };

    /// <summary>The only <see cref="DefaultAzureCredentialOptions"/> switches left off (credentials left IN the chain): none of them starts a process or reads another tool's token cache.</summary>
    private static readonly HashSet<string> AllowedChainCredentials = new(StringComparer.Ordinal)
    {
        "ExcludeEnvironmentCredential",
        "ExcludeWorkloadIdentityCredential",
        "ExcludeManagedIdentityCredential",
    };

    private readonly ILogger _logger;
    private readonly IReadOnlyList<string>? _cacheDirectories;

    /// <param name="recordStore">Where the sign-in record lives; default <see cref="AuthRecordStore.DefaultPath"/>.</param>
    /// <param name="logger">Diagnostics.</param>
    /// <param name="cacheDirectories">The directories whose <c>earm*</c> token-cache files <see cref="Logout"/> deletes; null means the platform locations (<c>.IdentityService</c> under the local application data and the user profile). Tests pass a temp directory.</param>
    public GraphAuthFactory(AuthRecordStore? recordStore = null, ILogger? logger = null, IReadOnlyList<string>? cacheDirectories = null)
    {
        RecordStore = recordStore ?? new AuthRecordStore();
        _logger = logger ?? NullLogger.Instance;
        _cacheDirectories = cacheDirectories;
    }

    public AuthRecordStore RecordStore { get; }

    /// <summary>The token provider for the options.</summary>
    /// <exception cref="InvalidOperationException">An environment variable asks for a credential that would start an external process.</exception>
    public virtual IGraphTokenProvider Create(GraphAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var credential = CreateCredential(options);
        return new AzureIdentityTokenProvider(credential, options.Mode, logger: _logger);
    }

    /// <summary>The credential for the options (see the class summary for the modes).</summary>
    public virtual TokenCredential CreateCredential(GraphAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        switch (options.Mode)
        {
            case GraphAuthMode.Default:
                return CreateDefaultChain(options);

            case GraphAuthMode.Interactive:
            {
                var record = RecordStore.TryLoad();
                if (record is null)
                {
                    return new UnavailableCredential("No interactive sign-in found: 'earm auth login' has not been run on this machine (or it was signed out).");
                }

                if (!MatchesCloud(record, options.Cloud))
                {
                    return new UnavailableCredential($"The stored interactive sign-in belongs to another cloud ({record.Authority}); EARM_CLOUD is {options.Cloud.Key}. Run 'earm auth login' again for this cloud.");
                }

                return CreateInteractive(options, record);
            }

            default:
            {
                var record = RecordStore.TryLoad();
                if (record is not null && MatchesCloud(record, options.Cloud))
                {
                    // S-08: a stored interactive sign-in means "act as this person". If it cannot produce a token silently, FAIL with guidance; falling
                    // through to the default chain would silently act as a different identity (a service principal from the environment, a managed identity).
                    return new InteractiveOnlyCredential(CreateInteractive(options, record), record.Username);
                }

                if (record is not null)
                {
                    GraphLog.AuthRecordIgnored(_logger, record.Authority, options.Cloud.Key);
                }

                return CreateDefaultChain(options);
            }
        }
    }

    /// <summary>
    /// <see cref="DefaultAzureCredential"/> without the credentials that shell out to a CLI or helper process.
    /// Remaining chain: environment, workload identity, managed identity.
    /// </summary>
    public virtual TokenCredential CreateDefaultChain(GraphAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        RejectProcessCredentialsInEnvironment();
        return new DefaultAzureCredential(BuildDefaultChainOptions(options));
    }

    /// <summary>
    /// The options of the default chain. An ALLOW-list: every <c>Exclude*Credential</c> switch that Azure.Identity offers is set (including credentials
    /// added by a later version), except the three that never start a process or read another tool's cache: environment, workload identity and managed
    /// identity. The authority host always comes from the fixed cloud table, so <c>AZURE_AUTHORITY_HOST</c> cannot redirect the token request.
    /// </summary>
    internal static DefaultAzureCredentialOptions BuildDefaultChainOptions(GraphAuthOptions options)
    {
        var chain = new DefaultAzureCredentialOptions();
        foreach (var property in typeof(DefaultAzureCredentialOptions).GetProperties())
        {
            if (property.PropertyType == typeof(bool)
                && property.CanWrite
                && property.Name.StartsWith("Exclude", StringComparison.Ordinal)
                && property.Name.EndsWith("Credential", StringComparison.Ordinal))
            {
                property.SetValue(chain, !AllowedChainCredentials.Contains(property.Name));
            }
        }

        chain.AuthorityHost = options.Cloud.AuthorityHost;
        if (!string.IsNullOrEmpty(options.TenantId))
        {
            chain.TenantId = options.TenantId;
        }

        return chain;
    }

    /// <summary>The cached interactive sign-in: never starts a browser itself (<see cref="InteractiveBrowserCredentialOptions.DisableAutomaticAuthentication"/>).</summary>
    protected virtual TokenCredential CreateInteractive(GraphAuthOptions options, AuthenticationRecord record)
    {
        return new InteractiveBrowserCredential(BuildInteractiveOptions(options, record));
    }

    internal static InteractiveBrowserCredentialOptions BuildInteractiveOptions(GraphAuthOptions options, AuthenticationRecord record)
    {
        var interactive = new InteractiveBrowserCredentialOptions
        {
            AuthenticationRecord = record,
            DisableAutomaticAuthentication = true,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
            AuthorityHost = options.Cloud.AuthorityHost,
        };
        // The record remembers the client and tenant of the sign-in ('earm auth login --client-id ...'): without an explicit option the
        // silent refresh must go to the same application, or MSAL finds no account in the cache for the default client.
        var clientId = !string.IsNullOrEmpty(options.ClientId) ? options.ClientId : RecordClientId(record);
        if (!string.IsNullOrEmpty(clientId))
        {
            interactive.ClientId = clientId;
        }

        var tenantId = !string.IsNullOrEmpty(options.TenantId) ? options.TenantId : RecordTenantId(record);
        if (!string.IsNullOrEmpty(tenantId))
        {
            interactive.TenantId = tenantId;
        }
        return interactive;
    }

    private static string? RecordClientId(AuthenticationRecord record)
    {
        try
        {
            return record.ClientId;
        }
        catch (NullReferenceException)
        {
            // A record written before the client id was stored has no such field.
            return null;
        }
    }

    private static string? RecordTenantId(AuthenticationRecord record)
    {
        try
        {
            return record.TenantId;
        }
        catch (NullReferenceException)
        {
            return null;
        }
    }
    internal static InteractiveBrowserCredentialOptions BuildBrowserLoginOptions(GraphAuthOptions options)
    {
        var browser = new InteractiveBrowserCredentialOptions
        {
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
            AuthorityHost = options.Cloud.AuthorityHost,
        };
        if (!string.IsNullOrEmpty(options.ClientId))
        {
            browser.ClientId = options.ClientId;
        }

        if (!string.IsNullOrEmpty(options.TenantId))
        {
            browser.TenantId = options.TenantId;
        }

        return browser;
    }

    internal static DeviceCodeCredentialOptions BuildDeviceCodeLoginOptions(GraphAuthOptions options, Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeCallback)
    {
        var device = new DeviceCodeCredentialOptions
        {
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
            AuthorityHost = options.Cloud.AuthorityHost,
            DeviceCodeCallback = deviceCodeCallback ?? PrintDeviceCode,
        };
        if (!string.IsNullOrEmpty(options.ClientId))
        {
            device.ClientId = options.ClientId;
        }

        if (!string.IsNullOrEmpty(options.TenantId))
        {
            device.TenantId = options.TenantId;
        }

        return device;
    }

    /// <summary>
    /// Interactive sign-in (browser, or device code) against the persistent encrypted cache named <see cref="CacheName"/>, then stores the
    /// resulting <see cref="AuthenticationRecord"/>. Unencrypted token storage is never enabled: when the platform cannot protect the cache
    /// the sign-in fails with a message that says so.
    /// </summary>
    /// <param name="deviceCodeCallback">Shows the device-code instructions; null prints them to standard error.</param>
    public virtual async Task<AuthenticationRecord> LoginAsync(GraphAuthOptions options, bool deviceCode, Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeCallback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        AuthenticationRecord record;
        try
        {
            record = await AuthenticateAsync(options, deviceCode, deviceCodeCallback, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new GraphTransportException(GraphTransportFailure.NotAuthenticated, DescribeLoginFailure(ex), ex);
        }

        await RecordStore.SaveAsync(record, cancellationToken).ConfigureAwait(false);
        return record;
    }

    /// <summary>The seam that actually talks to the identity provider (browser or device code); tests override it so no browser or network is involved.</summary>
    internal virtual async Task<AuthenticationRecord> AuthenticateAsync(GraphAuthOptions options, bool deviceCode, Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeCallback, CancellationToken cancellationToken)
    {
        var context = new TokenRequestContext([options.Cloud.GraphScope]);
        if (deviceCode)
        {
            return await new DeviceCodeCredential(BuildDeviceCodeLoginOptions(options, deviceCodeCallback)).AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
        }

        return await new InteractiveBrowserCredential(BuildBrowserLoginOptions(options)).AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Forgets the interactive sign-in: deletes the record and the files of the persistent <c>earm</c> token cache. The cache is private to EARM
    /// (it is named <c>earm</c>), so removing it signs out every account EARM stored. A platform keychain entry (macOS, Linux secret service)
    /// holds only the encryption key and is left in place.
    /// </summary>
    public virtual GraphLogoutResult Logout()
    {
        var recordDeleted = RecordStore.Delete();
        return new GraphLogoutResult(recordDeleted, DeleteCacheFiles(_cacheDirectories));
    }

    private static bool MatchesCloud(AuthenticationRecord record, GraphCloud cloud)
    {
        return string.Equals(record.Authority, cloud.AuthorityHost.Host, StringComparison.OrdinalIgnoreCase);
    }

    private static void RejectProcessCredentialsInEnvironment()
    {
        var value = Environment.GetEnvironmentVariable("AZURE_TOKEN_CREDENTIALS");
        if (!string.IsNullOrEmpty(value) && string.IsNullOrWhiteSpace(value))
        {
            // Azure.Identity rejects a blank value with an unhelpful message; say what to do instead.
            throw new InvalidOperationException("AZURE_TOKEN_CREDENTIALS is set to a blank value. Unset it, or use 'prod', 'EnvironmentCredential', 'WorkloadIdentityCredential' or 'ManagedIdentityCredential'. EARM never starts external processes (Azure CLI, azd, Azure PowerShell, Visual Studio).");
        }

        if (!string.IsNullOrWhiteSpace(value) && !SafeTokenCredentialValues.Contains(value.Trim()))
        {
            throw new InvalidOperationException($"AZURE_TOKEN_CREDENTIALS is set to '{value.Trim()}', which could enable a credential that starts an external process (Azure CLI, azd, Azure PowerShell, Visual Studio). EARM never starts external processes: unset it, or use 'prod', 'EnvironmentCredential', 'WorkloadIdentityCredential' or 'ManagedIdentityCredential'.");
        }
    }

    private static Task PrintDeviceCode(DeviceCodeInfo info, CancellationToken cancellationToken)
    {
        Console.Error.WriteLine(info.Message);
        return Task.CompletedTask;
    }

    private static string DescribeLoginFailure(Exception ex)
    {
        var detail = GraphAuthGuidance.CredentialLines(ex);
        var text = detail.Count > 0 ? detail[0] : ex.GetType().Name;
        var lowered = (ex.ToString()).ToLowerInvariant();
        if (lowered.Contains("persist", StringComparison.Ordinal) || lowered.Contains("keychain", StringComparison.Ordinal) || lowered.Contains("libsecret", StringComparison.Ordinal) || lowered.Contains("dpapi", StringComparison.Ordinal) || lowered.Contains("encrypt", StringComparison.Ordinal))
        {
            return "Sign-in failed: this machine cannot provide secure (encrypted) token storage, and EARM never stores tokens unencrypted. "
                + "Use a service principal (AZURE_TENANT_ID / AZURE_CLIENT_ID / AZURE_CLIENT_SECRET) or a managed identity instead. Detail: " + text;
        }

        return "Sign-in failed: " + text;
    }

    private static int DeleteCacheFiles(IReadOnlyList<string>? explicitDirectories)
    {
        var deleted = 0;
        var directories = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (explicitDirectories is not null)
        {
            directories.UnionWith(explicitDirectories);
        }

        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.DoNotVerify);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile, Environment.SpecialFolderOption.DoNotVerify);
        foreach (var root in explicitDirectories is not null ? Array.Empty<string>() : new[] { localAppData, profile })
        {
            if (!string.IsNullOrEmpty(root))
            {
                directories.Add(Path.Combine(root, ".IdentityService"));
            }
        }

        foreach (var directory in directories)
        {
            if (!Directory.Exists(directory))
            {
                continue;
            }

            foreach (var file in Directory.EnumerateFiles(directory, CacheName + "*"))
            {
                var name = Path.GetFileName(file);
                if (!string.Equals(name, CacheName, StringComparison.OrdinalIgnoreCase) && !name.StartsWith(CacheName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(file);
                    deleted++;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // Best effort: the record is gone, so EARM no longer uses the account.
                }
            }
        }

        return deleted;
    }

    /// <summary>
    /// The interactive credential of <c>auto</c> mode, with the no-fallback rule: any failure to obtain a token silently becomes an
    /// <see cref="InteractiveSignInExpiredException"/> (which <see cref="GraphAuthGuidance.NotSignedIn"/> turns into "run 'earm auth login' or pass
    /// '--auth default'"). Nothing else is tried.
    /// </summary>
    private sealed class InteractiveOnlyCredential : TokenCredential
    {
        private readonly TokenCredential _inner;
        private readonly string? _account;

        public InteractiveOnlyCredential(TokenCredential inner, string? account)
        {
            _inner = inner;
            _account = account;
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            try
            {
                return _inner.GetToken(requestContext, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InteractiveSignInExpiredException(_account, ex);
            }
        }

        public override async ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            try
            {
                return await _inner.GetTokenAsync(requestContext, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                throw new InteractiveSignInExpiredException(_account, ex);
            }
        }
    }

    /// <summary>A credential that is never available; its message ends up in the "credentials tried" list.</summary>
    private sealed class UnavailableCredential : TokenCredential
    {
        private readonly string _message;

        public UnavailableCredential(string message)
        {
            _message = message;
        }

        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            throw new CredentialUnavailableException(_message);
        }

        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken)
        {
            throw new CredentialUnavailableException(_message);
        }
    }
}
