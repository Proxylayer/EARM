using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Net.Http;
using EArm.Graph;
using EArm.Graph.Auth;
using EArm.Core.Types;
using EArm.Engine.Edges;
using EArm.Engine.Providers;
using EArm.Providers;
using Microsoft.Extensions.Logging;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// The shared environment of the real-tenant tests (one xUnit collection, <see cref="RealTenantCollection"/>).
/// <para>
/// Construction never touches the network: when <c>EARM_IT_TENANT</c> is unset the fixture only records why the
/// tests skip. When it is set, <see cref="InitializeAsync"/> builds the stack the CLI uses
/// (<see cref="GraphAuthFactory"/> → <see cref="IGraphTokenProvider"/> → <see cref="HttpGraphClient"/> + <see cref="RetryPolicy"/>,
/// <see cref="ProviderDiscovery"/>; compare
/// <c>src/EArm.Cli/Hosting/EngineServices.cs</c>) and runs the real <see cref="GraphPreflight"/>; a missing sign-in or
/// a tenant other than <c>EARM_IT_TENANT</c> turns every test into a skip with that reason.
/// </para>
/// <para>
/// Sign-in: a user sign-in (a stored <c>earm auth login</c> record, the main path) requests the delegated scopes of the enabled scenarios explicitly
/// (<see cref="IntegrationScopes"/>: their what-if, create and delete sets plus the cleanup scopes), so one consent covers the run. When that consent is
/// missing, <c>EARM_IT_INTERACTIVE=1</c> opens ONE consent prompt up front; otherwise every test skips with the exact <c>earm auth login --scopes ...</c>
/// command. A service principal (<c>AZURE_*</c> variables, or <c>EARM_AUTH=default</c>) keeps <c>.default</c>.
/// </para>
/// <para>
/// Cleanup guard: <see cref="DisposeAsync"/> (1) sweeps everything whose name starts with the run
/// prefix (<see cref="TenantCleanup"/>) and purges their soft-deleted copies, (2) writes a log into the run
/// directory and fails loudly when a live object with the prefix could not be removed. Objects without the prefix are
/// never touched.
/// </para>
/// </summary>
public sealed class IntegrationFixture : IAsyncLifetime
{
    private readonly ConcurrentQueue<string> _trackedPaths = new();
    private readonly ConcurrentDictionary<string, byte> _reservedContextIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly CapturingLoggerProvider _logCapture = new();
    private ILoggerFactory? _loggerFactory;
    private HttpClient? _http;
    private IGraphTokenProvider? _tokens;
    private HttpGraphClient? _graph;
    private string _unavailableReason = string.Empty;

    public IntegrationFixture()
    {
        Settings = IntegrationSettings.FromEnvironment();
        string runDirectory = Path.Combine(Path.GetTempPath(), "earm-it", Settings.Prefix);
        RunDirectory = runDirectory;
        ScratchDirectory = Path.Combine(runDirectory, "scratch");
        _unavailableReason = Settings.IsConfigured
            ? string.Empty
            : $"{IntegrationSettings.TenantVariable} is not set; the real-tenant tests are skipped. See tests/EArm.Integration.Tests/README.md.";
    }

    internal IntegrationSettings Settings { get; }

    /// <summary>Name prefix of every object this run creates.</summary>
    public string Prefix => Settings.Prefix;

    /// <summary>The run's private folder in the temp directory: the cleanup log and <see cref="ScratchDirectory"/>.</summary>
    public string RunDirectory { get; }

    /// <summary>Where the tests write secure-output files and template variants.</summary>
    public string ScratchDirectory { get; }

    /// <summary>True when the tenant is configured and the sign-in works for it.</summary>
    public bool IsAvailable => _unavailableReason.Length == 0;

    /// <summary>Why the tests skip, empty when <see cref="IsAvailable"/>.</summary>
    public string UnavailableReason => _unavailableReason;

    /// <summary>What the preflight learned (tenant, signed-in principal); null until a sign-in was verified.</summary>
    public GraphPreflightResult? Session { get; private set; }

    /// <summary>
    /// The delegated Graph scopes the fixture's client requests for a user sign-in (<see cref="IntegrationScopes.ComputeAsync"/>); empty for a service
    /// principal (<c>.default</c>).
    /// </summary>
    public IReadOnlyList<string> RunScopes { get; private set; } = [];

    /// <summary>The production Graph client (HTTP with the retry policy).</summary>
    internal HttpGraphClient Graph => _graph ?? throw NotAvailable();

    internal ProviderRegistry Providers { get; private set; } = ProviderRegistry.Empty;

    internal EdgeKindRegistry EdgeKinds { get; private set; } = EdgeKindRegistry.Default;

    /// <summary>Live objects the last sweep could not remove (empty when the cleanup was complete).</summary>
    internal IReadOnlyList<string> Leftovers => _leftovers;

    internal CliDriver Cli { get; } = new CliDriver();

    /// <summary>What the cleanup guard did (filled by <see cref="DisposeAsync"/> or <see cref="SweepNowAsync"/>).</summary>
    public IReadOnlyList<string> CleanupLog { get; private set; } = [];

    // ---- skip guards ----

    /// <summary>Skips the calling test unless the tenant is usable and every requirement is met.</summary>
    internal void Require(Needs needs = Needs.Tenant)
    {
        Assert.SkipUnless(IsAvailable, _unavailableReason);
        Assert.SkipUnless(!needs.HasFlag(Needs.Domain) || Settings.Domain is not null, $"{IntegrationSettings.DomainVariable} is not set (a verified domain for user principal names is required).");
        Assert.SkipUnless(!needs.HasFlag(Needs.ConditionalAccess) || Settings.AllowConditionalAccess, $"{IntegrationSettings.AllowCaVariable}=1 is required to write Conditional Access objects.");
        Assert.SkipUnless(!needs.HasFlag(Needs.Roles) || Settings.AllowRoles, $"{IntegrationSettings.AllowRolesVariable}=1 is required to write directory roles, administrative units, app role assignments and consent grants.");
    }

    /// <summary>The verified domain; only valid after <see cref="Require"/> with <see cref="Needs.Domain"/>.</summary>
    public string Domain => Settings.Domain ?? throw new InvalidOperationException("Call Require(Needs.Domain) first.");

    // ---- naming, templates, tracking ----

    /// <summary>A name that starts with the run prefix: <c>&lt;prefix&gt;-&lt;suffix&gt;</c>.</summary>
    public string Name(string suffix)
    {
        return Prefix + "-" + suffix;
    }

    /// <summary>A user principal name under the run prefix and the verified domain.</summary>
    public string Upn(string localSuffix)
    {
        return Name(localSuffix) + "@" + Domain;
    }

    /// <summary>The path of a template file copied next to the test binaries (<c>Templates/&lt;file&gt;</c>).</summary>
    public static string TemplatePath(string file)
    {
        return Path.Combine(AppContext.BaseDirectory, "Templates", file);
    }

    /// <summary>A deployment whose name (a label) carries the prefix, with the <c>prefix</c> template parameter set.</summary>
    internal DeploymentRun NewRun(string shortName, string templateFile)
    {
        return new DeploymentRun(Name(shortName), TemplatePath(templateFile), ["-p", "prefix=" + Prefix]);
    }

    /// <summary>Registers an entity path (<c>/applications/{id}</c>) created outside a deployment; the guard deletes it (and purges it) at teardown when its name still starts with the prefix.</summary>
    public void Track(string entityPath)
    {
        _trackedPaths.Enqueue(entityPath);
    }

    /// <summary>
    /// Reserves a free authentication context id (<c>c99</c> downwards). A context template writes with an upsert, so the
    /// id must be one that does not exist: a foreign context is never overwritten.
    /// </summary>
    internal async Task<string> AllocateContextIdAsync(CancellationToken cancellationToken)
    {
        for (int number = 99; number >= 50; number--)
        {
            string id = "c" + number;
            if (_reservedContextIds.ContainsKey(id))
            {
                continue;
            }

            var existing = await Graph.GetAsync(new GraphRequest("/identity/conditionalAccess/authenticationContextClassReferences/" + id), cancellationToken);
            if (existing is null && _reservedContextIds.TryAdd(id, 0))
            {
                return id;
            }
        }

        throw new InvalidOperationException("No free authentication context id between c50 and c99.");
    }

    // ---- lifecycle ----

    public async ValueTask InitializeAsync()
    {
        if (!Settings.IsConfigured)
        {
            return;
        }

        var cancellationToken = TestContext.Current.CancellationToken;
        Directory.CreateDirectory(ScratchDirectory);

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Warning);
            builder.AddProvider(_logCapture);
        });

        var cloud = GraphCloud.FromEnvironment();
        var auth = GraphAuthOptions.Resolve(null, null, null, cloud);
        var factory = new GraphAuthFactory(null, _loggerFactory.CreateLogger<GraphAuthFactory>());

        // A user sign-in (stored 'earm auth login' record) requests the run's delegated scopes explicitly, as the CLI does; a service principal from the
        // AZURE_* variables (or EARM_AUTH=default) keeps .default.
        TokenScopes? scopes = null;
        IConsentPrompter? prompter = null;
        if (factory.UsesStoredSignIn(auth))
        {
            try
            {
                RunScopes = await IntegrationScopes.ComputeAsync(Settings, cancellationToken);
            }
            catch (InvalidOperationException ex)
            {
                _unavailableReason = "The delegated scopes of the enabled scenarios could not be computed, so the real-tenant tests are skipped: " + ex.Message;
                return;
            }

            scopes = TokenScopes.Explicit(cloud.GraphOrigin, RunScopes);
            prompter = Settings.InteractiveConsent
                ? factory.CreateConsentPrompter(auth, deviceCode: false, deviceCodeCallback: null, notice: notice => TestContext.Current.SendDiagnosticMessage("[consent] " + notice))
                : null;
        }

        _tokens = factory.Create(auth, prompter);
        _http = HttpGraphClient.CreateHttpClient();
        var retryPolicy = new RetryPolicy(_loggerFactory.CreateLogger<RetryPolicy>());
        _graph = new HttpGraphClient(_http, _tokens, retryPolicy, new GraphOptions { Cloud = cloud, Scopes = scopes }, _loggerFactory.CreateLogger<HttpGraphClient>());

        // The preflight prompts through the provider (credential renewed after consent), like EngineServices.
        var preflightPrompter = _tokens is AzureIdentityTokenProvider provider ? provider.ConsentPrompter : prompter;
        var preflight = new GraphPreflight(_tokens, _graph, cloud, auth.Mode, _loggerFactory.CreateLogger<GraphPreflight>(), scopes, preflightPrompter);
        Providers = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);
        EdgeKinds = ProviderDiscovery.CreateEdgeKinds(ResourceTypeCatalog.Default);

        try
        {
            Session = await preflight.RunAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is GraphTransportException or GraphException)
        {
            _unavailableReason = ConsentRequiredException.FindIn(ex) is { } consent
                ? ConsentMissingReason(consent, auth)
                : $"The sign-in preflight failed, so the real-tenant tests are skipped: {ex.Message}";
            return;
        }

        if (!string.Equals(Session.TenantId, Settings.TenantId, StringComparison.OrdinalIgnoreCase))
        {
            _unavailableReason = $"EARM is signed in to tenant {Session.TenantId}, but {IntegrationSettings.TenantVariable} is {Settings.TenantId}; sign in to that tenant (AZURE_TENANT_ID, or 'earm auth login --tenant {Settings.TenantId}'). The real-tenant tests are skipped.";
            Session = null;
        }
    }

    /// <summary>Teardown: deployments first, then the prefix sweep; throws when a live object with the prefix remains.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Session is not null && _graph is not null)
            {
                await SweepNowAsync(CancellationToken.None);
                if (_leftovers.Count > 0)
                {
                    throw new InvalidOperationException(
                        $"Cleanup left {_leftovers.Count} object(s) behind in tenant {Settings.TenantId} (prefix '{Prefix}'); remove them by hand (see README.md, 'Manual cleanup'):{Environment.NewLine}  - {string.Join(Environment.NewLine + "  - ", _leftovers)}");
                }
            }
        }
        finally
        {
            _http?.Dispose();
            (_tokens as IDisposable)?.Dispose();
            _loggerFactory?.Dispose();
            _logCapture.Dispose();
            TryDeleteRunDirectory();
        }
    }

    private List<string> _leftovers = [];

    /// <summary>The cleanup guard: sweeps the prefix and purges soft-deleted copies. Safe to call repeatedly.</summary>
    internal async Task SweepNowAsync(CancellationToken cancellationToken)
    {
        if (Session is null || _graph is null)
        {
            return;
        }

        var log = new List<string>();
        _leftovers = [];

        // The prefix sweep removes everything the runs created (every test object carries the run prefix), also what a failed
        // 'deployment delete' left behind and everything tests created directly.
        var cleanup = new TenantCleanup(_graph, Prefix);
        try
        {
            await cleanup.SweepAsync(_trackedPaths.ToArray(), cancellationToken);
        }
        catch (Exception ex) when (ex is GraphException or GraphTransportException)
        {
            log.Add($"prefix sweep aborted: {ex.GetType().Name}: {ex.Message}");
            _leftovers.Add("the prefix sweep was aborted before it finished: " + ex.Message);
        }

        log.AddRange(cleanup.Entries);
        _leftovers.AddRange(cleanup.Leftovers);
        log.AddRange(_logCapture.Lines.Select(line => "stack: " + line));
        CleanupLog = log;
        WriteCleanupLog(log);
    }

    private void WriteCleanupLog(IReadOnlyList<string> log)
    {
        string text = string.Join(Environment.NewLine, log.Prepend($"EARM integration cleanup, prefix '{Prefix}', tenant {Settings.TenantId}, {DateTimeOffset.UtcNow:O}"));
        try
        {
            Directory.CreateDirectory(RunDirectory);
            File.WriteAllText(Path.Combine(RunDirectory, "cleanup.log"), text, new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // The log is a convenience; the diagnostic message below still carries it.
        }

        TestContext.Current.SendDiagnosticMessage(text);
    }

    private void TryDeleteRunDirectory()
    {
        // Keep the scratch files (and cleanup.log) when something is left behind: they help to find what to remove.
        if (_leftovers.Count > 0)
        {
            return;
        }

        try
        {
            if (Directory.Exists(ScratchDirectory))
            {
                Directory.Delete(ScratchDirectory, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    /// <summary>
    /// Why the run skips when the user sign-in lacks consent and no prompt is allowed: the missing scopes and the ONE command that consents the whole run
    /// (the full list, so a later run with the same gates needs no further consent).
    /// </summary>
    private string ConsentMissingReason(ConsentRequiredException consent, GraphAuthOptions auth)
    {
        string? clientId = auth.ClientId ?? (PublicClients.NameOf(consent.ClientId) is null ? consent.ClientId : null);
        string account = string.IsNullOrWhiteSpace(consent.Account) ? "The stored sign-in" : $"The stored sign-in ({consent.Account})";
        return $"{account} has not been granted every delegated scope the enabled scenarios need (missing: {string.Join(' ', consent.Scopes.Names)}), so the real-tenant tests are skipped. "
            + $"Consent once, interactively, for the whole run with:{Environment.NewLine}  {IntegrationScopes.LoginCommand(RunScopes, Settings.TenantId, clientId)}{Environment.NewLine}"
            + $"or set {IntegrationSettings.InteractiveVariable}=1 to be prompted once at the start of the run. "
            + GraphAuthGuidance.SharedConsentNote;
    }

    private InvalidOperationException NotAvailable()
    {
        return new InvalidOperationException("The real-tenant stack is not available: " + (_unavailableReason.Length == 0 ? "the fixture was not initialised." : _unavailableReason));
    }
}

/// <summary>
/// One collection for every real-tenant test class: the classes share the fixture (one sign-in, one cleanup guard)
/// and run sequentially, which the in-process CLI requires (its global options are static).
/// </summary>
[CollectionDefinition(Name)]
public sealed class RealTenantCollection : ICollectionFixture<IntegrationFixture>
{
    public const string Name = "RealTenant";
}
