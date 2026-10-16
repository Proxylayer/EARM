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
using EArm.Graph.Auth;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// The shared state of <see cref="ConsentProbes"/>: the gate (<c>EARM_IT_CONSENT_PROBE=1</c> and <c>EARM_IT_TENANT</c>), one interactive base sign-in with
/// the Microsoft Graph Command Line Tools client, and what the probes learned from each other (consent was missing; the token after consent).
/// <para>
/// Isolation: the probes never use EARM's sign-in record or its <c>earm</c> token cache. The sign-in record lives in memory only, the tokens in the
/// persisted cache <see cref="CacheName"/>, whose files are deleted when the class finishes. What cannot be isolated is the consent itself: it is a grant to
/// the Graph Command Line Tools app in the tenant (shared with Microsoft Graph PowerShell and the Graph CLI), and it stays after the run. Revoke the probe
/// scope to run the probes again (see the README).
/// </para>
/// </summary>
public sealed class ConsentProbeSession : IDisposable
{
    public const string GateVariable = "EARM_IT_CONSENT_PROBE";
    public const string ScopeVariable = "EARM_IT_CONSENT_PROBE_SCOPE";

    /// <summary>The default probe scope: user-consentable, harmless (reads the user's own presence) and rarely consented to the Graph CLI Tools app.</summary>
    public const string DefaultProbeScope = "Presence.Read";

    /// <summary>The probes' own persisted MSAL cache; deliberately not <see cref="GraphAuthFactory.CacheName"/> (and not matched by its logout pattern).</summary>
    public const string CacheName = "earm-consent-probe";

    /// <summary>How long an interactive prompt may take.</summary>
    private static readonly TimeSpan PromptTimeout = TimeSpan.FromMinutes(5);

    private readonly string? _configurationError;
    private AuthenticationRecord? _record;

    public ConsentProbeSession()
    {
        Enabled = IsOn(GateVariable);
        TenantId = Read(IntegrationSettings.TenantVariable);
        ProbeScope = Read(ScopeVariable) ?? DefaultProbeScope;
        try
        {
            Cloud = GraphCloud.FromEnvironment();
        }
        catch (GraphConfigurationException ex)
        {
            Cloud = GraphCloud.Public;
            _configurationError = ex.Message;
        }

        BaseScopes = Cloud.SignInScopes;
        Requested = TokenScopes.Explicit(Cloud.GraphOrigin, [.. BaseScopes.Names, ProbeScope]);
    }

    public bool Enabled { get; }

    public string? TenantId { get; }

    public GraphCloud Cloud { get; }

    /// <summary>The Graph scope name the probes request incrementally (<c>EARM_IT_CONSENT_PROBE_SCOPE</c>, default <see cref="DefaultProbeScope"/>).</summary>
    public string ProbeScope { get; }

    /// <summary>What the base sign-in consents: <c>User.Read Organization.Read.All</c>, as <c>earm auth login</c> does.</summary>
    internal TokenScopes BaseScopes { get; }

    /// <summary>The explicitly requested set of the probes: the base scopes plus <see cref="ProbeScope"/>.</summary>
    internal TokenScopes Requested { get; }

    /// <summary>Set by probe 1: a silent request for <see cref="Requested"/> was classified as consent required.</summary>
    internal bool ConsentWasMissing { get; set; }

    /// <summary>Set by probe 2: the token for <see cref="Requested"/> after consent.</summary>
    internal GraphToken? TokenAfterConsent { get; set; }

    internal string ClientId => PublicClients.DefaultFor(Cloud);

    /// <summary>Skips the calling probe unless enabled; fails it when the configuration is unusable.</summary>
    internal void Require()
    {
        Assert.SkipUnless(Enabled, $"{GateVariable}=1 is not set: the consent probes are interactive (browser sign-in and a consent prompt) and grant a delegated scope to the Microsoft Graph Command Line Tools app, so they run only on request. See tests/EArm.Integration.Tests/README.md.");
        Assert.SkipWhen(TenantId is null, $"{IntegrationSettings.TenantVariable} is not set: the consent probes sign in to that tenant.");
        if (_configurationError is not null)
        {
            Assert.Fail("The consent probes cannot run: " + _configurationError);
        }
    }

    /// <summary>The base sign-in (one browser prompt for <see cref="BaseScopes"/>), once per class; later callers reuse the record.</summary>
    internal async Task EnsureSignedInAsync(CancellationToken cancellationToken)
    {
        if (_record is not null)
        {
            return;
        }

        Log($"base sign-in: client {ClientId}, tenant {TenantId}, cloud {Cloud.Key}, scopes [{string.Join(' ', BaseScopes.Names)}], cache '{CacheName}'. Complete the browser prompt.");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(PromptTimeout);
        _record = await new InteractiveBrowserCredential(Options(record: null, silent: false))
            .AuthenticateAsync(new TokenRequestContext([.. BaseScopes.Scopes]), timeout.Token)
            .ConfigureAwait(false);
        Log($"signed in as {_record.Username} (tenant {_record.TenantId}, client {_record.ClientId}).");
    }

    /// <summary>
    /// The product token provider over a NEW silent credential (the record, <c>DisableAutomaticAuthentication</c>), as <see cref="GraphAuthFactory"/> builds it
    /// for a stored sign-in. With <paramref name="prompter"/>, a consent failure prompts once and retries on a credential from <see cref="NewSilentCredential"/>.
    /// </summary>
    internal AzureIdentityTokenProvider NewProvider(IConsentPrompter? prompter, Action? onRenew = null)
    {
        var record = _record ?? throw new InvalidOperationException("Call EnsureSignedInAsync first.");
        Func<TokenCredential>? renew = prompter is null
            ? null
            : () =>
            {
                onRenew?.Invoke();
                return NewSilentCredential();
            };
        return new AzureIdentityTokenProvider(NewSilentCredential(), GraphAuthMode.Interactive, signIn: new SignInAccount(record.Username, record.ClientId), consentPrompter: prompter, renewCredential: renew);
    }

    /// <summary>A new credential bound to the record that never prompts (only the persisted cache carries state between credentials).</summary>
    internal TokenCredential NewSilentCredential()
    {
        return new InteractiveBrowserCredential(Options(_record ?? throw new InvalidOperationException("Call EnsureSignedInAsync first."), silent: true));
    }

    /// <summary>The interactive incremental-consent prompt as the signed-in account (what <c>InteractiveConsentPrompter</c> does against the <c>earm</c> cache).</summary>
    internal RecordingConsentPrompter NewPrompter()
    {
        return new RecordingConsentPrompter(this);
    }

    /// <summary>Writes a line to the test output and the diagnostic messages.</summary>
    internal static void Log(string line)
    {
        string stamped = DateTime.Now.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " " + line;
        TestContext.Current.TestOutputHelper?.WriteLine(stamped);
        TestContext.Current.SendDiagnosticMessage("[consent-probe] " + line);
    }

    public void Dispose()
    {
        if (!Enabled)
        {
            return;
        }

        foreach (string directory in CacheDirectories().Where(Directory.Exists))
        {
            foreach (string file in Directory.EnumerateFiles(directory, CacheName + "*"))
            {
                string name = Path.GetFileName(file);
                if (string.Equals(name, CacheName, StringComparison.OrdinalIgnoreCase) || name.StartsWith(CacheName + ".", StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(file);
                }
            }
        }
    }

    private InteractiveBrowserCredentialOptions Options(AuthenticationRecord? record, bool silent)
    {
        return new InteractiveBrowserCredentialOptions
        {
            ClientId = ClientId,
            TenantId = TenantId,
            AuthorityHost = Cloud.AuthorityHost,
            AuthenticationRecord = record,
            LoginHint = record?.Username,
            DisableAutomaticAuthentication = silent,
            TokenCachePersistenceOptions = new TokenCachePersistenceOptions { Name = CacheName },
        };
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

    private static void TryDelete(string file)
    {
        try
        {
            File.Delete(file);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
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

    /// <summary>An interactive consent request (browser) as the session's account for exactly the asked set; records every call.</summary>
    internal sealed class RecordingConsentPrompter : IConsentPrompter
    {
        private readonly ConsentProbeSession _session;
        private readonly List<TokenScopes> _calls = [];

        public RecordingConsentPrompter(ConsentProbeSession session)
        {
            _session = session;
        }

        public IReadOnlyList<TokenScopes> Calls => _calls;

        public async Task ConsentAsync(TokenScopes scopes, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(scopes);
            _calls.Add(scopes);
            var record = _session._record ?? throw new InvalidOperationException("Call EnsureSignedInAsync first.");
            Log($"consent prompt for {scopes} as {record.Username}. Complete the browser prompt; do NOT tick 'Consent on behalf of your organization'.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(PromptTimeout);
            var granted = await new InteractiveBrowserCredential(_session.Options(record, silent: false))
                .AuthenticateAsync(new TokenRequestContext([.. scopes.Scopes]), timeout.Token)
                .ConfigureAwait(false);
            if (!string.Equals(granted.HomeAccountId, record.HomeAccountId, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"The consent prompt was completed as {granted.Username}, not {record.Username}.");
            }
        }
    }
}
