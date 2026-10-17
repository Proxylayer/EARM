using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Azure.Core;
using Azure.Identity;

namespace EArm.Graph.Auth;

public partial class GraphAuthFactory
{
    internal static InteractiveBrowserCredentialOptions BuildBrowserLoginOptions(GraphAuthOptions options)
    {
        var browser = new InteractiveBrowserCredentialOptions
        {
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
            AuthorityHost = options.Cloud.AuthorityHost,
            ClientId = LoginClientId(options),
        };

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
            ClientId = LoginClientId(options),
        };

        if (!string.IsNullOrEmpty(options.TenantId))
        {
            device.TenantId = options.TenantId;
        }

        return device;
    }

    /// <summary>An interactive request as the account of <paramref name="record"/> (incremental consent): its client and tenant, the same <c>earm</c> cache, the account as login hint.</summary>
    internal static InteractiveBrowserCredentialOptions BuildBrowserConsentOptions(GraphAuthOptions options, AuthenticationRecord record)
    {
        var browser = BuildInteractiveOptions(options, record);
        browser.DisableAutomaticAuthentication = false;
        browser.LoginHint = record.Username;
        return browser;
    }

    /// <inheritdoc cref="BuildBrowserConsentOptions"/>
    internal static DeviceCodeCredentialOptions BuildDeviceCodeConsentOptions(GraphAuthOptions options, AuthenticationRecord record, Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeCallback)
    {
        var device = new DeviceCodeCredentialOptions
        {
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
            AuthorityHost = options.Cloud.AuthorityHost,
            DeviceCodeCallback = deviceCodeCallback ?? PrintDeviceCode,
            AuthenticationRecord = record,
        };
        var clientId = InteractiveClientId(options, record);
        if (!string.IsNullOrEmpty(clientId))
        {
            device.ClientId = clientId;
        }

        var tenantId = !string.IsNullOrEmpty(options.TenantId) ? options.TenantId : RecordTenantId(record);
        if (!string.IsNullOrEmpty(tenantId))
        {
            device.TenantId = tenantId;
        }

        return device;
    }

    /// <summary>
    /// Interactive sign-in (browser, or device code) against the persistent encrypted cache named <see cref="CacheName"/>, then stores the
    /// resulting <see cref="AuthenticationRecord"/>. Unencrypted token storage is never enabled: when the platform cannot protect the cache
    /// the sign-in fails with a message that says so.
    /// <para>
    /// <paramref name="scopes"/> are the consents to obtain, one token request per resource: the first is the sign-in itself (a Microsoft Graph set such as
    /// <see cref="GraphCloud.SignInScopes"/>); each further set (for example <see cref="GraphCloud.KeyVaultConsentScopes"/>) is a second interactive request
    /// as the same account. The client is <c>--client-id</c>, else the cloud's default (<see cref="LoginClientId"/>); the record always names it, so a new
    /// sign-in replaces a record of another client.
    /// </para>
    /// </summary>
    /// <param name="options">The authentication options (cloud, tenant, client).</param>
    /// <param name="scopes">The consents to obtain; at least one.</param>
    /// <param name="deviceCode">Use the device-code flow instead of a browser.</param>
    /// <param name="deviceCodeCallback">Shows the device-code instructions; null prints them to standard error.</param>
    /// <param name="cancellationToken">Cancels the sign-in.</param>
    /// <exception cref="GraphConfigurationException">No <c>--client-id</c> in a cloud without a default client (china).</exception>
    /// <exception cref="GraphTransportException">The sign-in or a consent failed (<see cref="GraphTransportFailure.NotAuthenticated"/>).</exception>
    public virtual async Task<AuthenticationRecord> LoginAsync(GraphAuthOptions options, IReadOnlyList<TokenScopes> scopes, bool deviceCode, Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeCallback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(scopes);
        if (scopes.Count == 0)
        {
            throw new ArgumentException("At least one scope set is required.", nameof(scopes));
        }

        var login = options with { ClientId = LoginClientId(options) };
        AuthenticationRecord record;
        try
        {
            record = await AuthenticateAsync(login, scopes[0], account: null, deviceCode, deviceCodeCallback, cancellationToken).ConfigureAwait(false);
            for (var i = 1; i < scopes.Count; i++)
            {
                var granted = await AuthenticateAsync(login, scopes[i], record, deviceCode, deviceCodeCallback, cancellationToken).ConfigureAwait(false);
                EnsureSameAccount(record, granted);
            }
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

    /// <summary>
    /// The seam that actually talks to the identity provider (browser or device code): one interactive request for <paramref name="scopes"/>. Without
    /// <paramref name="account"/> it is a new sign-in (the client of <see cref="LoginClientId"/>); with it, an incremental consent as that account (its client
    /// and tenant, the account as login hint). Tests override it so no browser or network is involved.
    /// </summary>
    internal virtual async Task<AuthenticationRecord> AuthenticateAsync(GraphAuthOptions options, TokenScopes scopes, AuthenticationRecord? account, bool deviceCode, Func<DeviceCodeInfo, CancellationToken, Task>? deviceCodeCallback, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var context = new TokenRequestContext([.. scopes.Scopes]);
        if (deviceCode)
        {
            var deviceOptions = account is null ? BuildDeviceCodeLoginOptions(options, deviceCodeCallback) : BuildDeviceCodeConsentOptions(options, account, deviceCodeCallback);
            return await new DeviceCodeCredential(deviceOptions).AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
        }

        var browserOptions = account is null ? BuildBrowserLoginOptions(options) : BuildBrowserConsentOptions(options, account);
        return await new InteractiveBrowserCredential(browserOptions).AuthenticateAsync(context, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>A consent prompt must be answered by the account of the sign-in: a different account would leave the stored sign-in without the grant.</summary>
    /// <exception cref="InvalidOperationException">The prompt was completed as another account.</exception>
    internal static void EnsureSameAccount(AuthenticationRecord expected, AuthenticationRecord actual)
    {
        if (!string.Equals(HomeAccountIdOf(expected), HomeAccountIdOf(actual), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"The consent prompt was completed as {actual.Username}, not as the signed-in account {expected.Username}; nothing was consented for {expected.Username}. Run it again and choose {expected.Username}.");
        }
    }

    private static string? HomeAccountIdOf(AuthenticationRecord record)
    {
        try
        {
            return record.HomeAccountId;
        }
        catch (NullReferenceException)
        {
            return null;
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
}
