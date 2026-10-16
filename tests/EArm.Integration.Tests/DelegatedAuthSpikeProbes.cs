using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Phase 0 of DES-0002 (delegated sign-in with the Microsoft Graph Command Line Tools public client and explicit scopes): INTERACTIVE
/// spike probes a person runs against a dev tenant to answer the design's open questions. They are separate from the real-tenant
/// fixture (they never use EARM's sign-in or its <c>earm</c> token cache) and run only with <c>EARM_IT_AUTH_SPIKE=1</c> and
/// <c>EARM_IT_TENANT</c>. The probes RECORD what Microsoft Entra answers (<c>RESULT ...</c> lines in the test output and in the results
/// log); a probe fails only when the harness itself cannot run (bad configuration, the shared sign-in did not happen). They run in
/// method-name order and share one sign-in per client id, so the browser opens as rarely as possible. See the README section
/// "Delegated-auth spike (DES-0002)".
/// </summary>
[Collection(AuthSpikeCollection.Name)]
[Trait("Category", "Spike")]
[TestMethodOrderer(typeof(MethodNameTestMethodOrderer))]
public sealed class DelegatedAuthSpikeProbes : IClassFixture<AuthSpikeSession>
{
    private const string GraphCli = AuthSpikeSession.GraphCliToolsClientId;
    private const string AzureCli = AuthSpikeSession.AzureCliClientId;

    private readonly AuthSpikeSession _session;

    public DelegatedAuthSpikeProbes(AuthSpikeSession session)
    {
        _session = session;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    // ---- S1: Key Vault data-plane token with the Graph CLI Tools client ----

    [Fact]
    public async Task It_Spike_S1_KeyVaultTokenWithGraphCliToolsClient()
    {
        _session.Require();
        _session.LogHeader("S1 Key Vault token with Microsoft Graph Command Line Tools", GraphCli);
        var results = new List<string>();
        await RequireSharedSignInAsync("S1", GraphCli, results);

        AuthSpikeAttempt? usable = null;
        (string Letter, string Name, string Scope)[] requests =
        [
            ("a", "user_impersonation", _session.KeyVaultResource + "/user_impersonation"),
            ("b", ".default", _session.KeyVaultResource + "/.default"),
        ];
        foreach (var (letter, name, scope) in requests)
        {
            AuthSpikeAttempt silent = await _session.TryTokenAsync($"S1{letter} {name} silent", GraphCli, [scope], silent: true, Ct);
            AuthSpikeAttempt final = silent.IsGranted
                ? silent
                : await _session.TryTokenAsync($"S1{letter} {name} interactive", GraphCli, [scope], silent: false, Ct);
            string how = silent.IsGranted ? "silently, from the Graph sign-in's refresh token" : $"silent was {silent.Verdict}, then interactive";
            results.Add($"RESULT S1{letter}: KeyVault token ({scope}) with {GraphCli} = {Line(final)} [{how}]");
            usable ??= final.IsGranted ? final : null;
        }

        results.Add("RESULT S1c: " + await KeyVaultUsabilityAsync(usable));
        Summarize("S1", results);
    }

    // ---- S2: incremental consent, then silent refresh ----

    [Fact]
    public async Task It_Spike_S2_IncrementalConsentThenSilentRefresh()
    {
        _session.Require();
        _session.LogHeader("S2 incremental consent then silent refresh", GraphCli);
        var results = new List<string>();
        IReadOnlyList<string> baseScopes = _session.BaseGraphScopes;
        IReadOnlyList<string> extended = _session.ExtendedGraphScopes;
        string extra = string.Join(", ", _session.ExtraScopeNames);

        // (a) interactive sign-in for the base set, record saved (or the shared sign-in an earlier probe made with the same base set).
        AuthSpikeAttempt? signIn = await RequireSharedSignInAsync("S2a", GraphCli, results);
        results.Add(signIn is null
            ? $"RESULT S2a: interactive sign-in for the base set = REUSED (signed in earlier in this run with [{string.Join(", ", baseScopes)}])"
            : $"RESULT S2a: interactive sign-in for the base set = {Line(signIn)}");

        // (b) new credential + record + DisableAutomaticAuthentication, base set: expected GRANTED.
        AuthSpikeAttempt b = await _session.TryTokenAsync("S2b silent base set", GraphCli, baseScopes, silent: true, Ct);
        results.Add($"RESULT S2b: silent base set (new credential + record) = {Line(b)}{Expectation(b.IsGranted, "GRANTED")}");

        // (c) silent base + extra: the consent error EARM must map, without any prompt.
        AuthSpikeAttempt c = await _session.TryTokenAsync("S2c silent base + extra", GraphCli, extended, silent: true, Ct);
        results.Add(c.IsGranted
            ? $"RESULT S2c: silent base + [{extra}] = {Line(c)} [the extra scope(s) are already consented for this user or tenant, so no consent error was observable; revoke them (README) and re-run to see it]"
            : $"RESULT S2c: silent base + [{extra}] = {Line(c)} [this is the failure EARM must map to 'consent needed']");

        // (d) the same set with prompting allowed: the incremental consent prompt (no prompt when c was granted).
        AuthSpikeAttempt d = await _session.TryTokenAsync("S2d interactive base + extra", GraphCli, extended, silent: false, Ct);
        results.Add($"RESULT S2d: interactive base + [{extra}] (incremental consent) = {Line(d)}");

        // (e) a fresh credential with the same record, silent, extended set: expected GRANTED once d was.
        AuthSpikeAttempt e = await _session.TryTokenAsync("S2e silent base + extra after consent", GraphCli, extended, silent: true, Ct);
        results.Add($"RESULT S2e: silent base + [{extra}] after consent (new credential + record) = {Line(e)}{Expectation(e.IsGranted || !d.IsGranted, "GRANTED after S2d")}");

        Summarize("S2", results);
    }

    // ---- S3: graph/.default with the Graph CLI Tools client after consent ----

    [Fact]
    public async Task It_Spike_S3_GraphDefaultScopeAfterConsent()
    {
        _session.Require();
        _session.LogHeader("S3 graph/.default with Microsoft Graph Command Line Tools after consent", GraphCli);
        var results = new List<string>();
        await RequireSharedSignInAsync("S3", GraphCli, results);

        AuthSpikeAttempt attempt = await _session.TryTokenAsync("S3 silent .default", GraphCli, [_session.Cloud.GraphScope], silent: true, Ct);
        results.Add($"RESULT S3: silent {_session.Cloud.GraphScope} with {GraphCli} = {Line(attempt)}");
        if (attempt.IsGranted)
        {
            var granted = new HashSet<string>(attempt.GrantedScopes, StringComparer.OrdinalIgnoreCase);
            string[] wanted = ["User.Read", "Organization.Read.All", .. _session.ExtraScopeNames];
            string[] missing = wanted.Where(s => !granted.Contains(s)).ToArray();
            results.Add(missing.Length == 0
                ? $"RESULT S3: .default carries every scope consented in this run ({string.Join(", ", wanted)}); {attempt.GrantedScopes.Count} scope(s) in total"
                : $"RESULT S3: .default is MISSING [{string.Join(", ", missing)}] of the scopes requested in this run (consent not given, or S2 did not run first)");
        }

        Summarize("S3", results);
    }

    // ---- S4: Azure CLI client comparison (optional) ----

    [Fact]
    public async Task It_Spike_S4_AzureCliClientComparison()
    {
        _session.Require();
        Assert.SkipUnless(_session.AzureCliEnabled, $"{AuthSpikeSession.AzureCliVariable}=1 is not set: the Azure CLI client comparison is optional (it costs up to two more browser prompts).");
        _session.LogHeader("S4 Azure CLI client comparison", AzureCli);
        var results = new List<string>();
        string vaultDefault = _session.KeyVaultResource + "/.default";
        string conditionalAccess = _session.GraphScope("Policy.ReadWrite.ConditionalAccess");

        // Key Vault first: it is expected to succeed and gives the record the second request reuses.
        AuthSpikeAttempt vault = await _session.EnsureSignedInAsync("S4b KeyVault .default", AzureCli, [vaultDefault], Ct)
            ?? await _session.TryTokenAsync("S4b KeyVault .default silent", AzureCli, [vaultDefault], silent: true, Ct);

        AuthSpikeAttempt ca = _session.HasRecord(AzureCli)
            ? await _session.TryTokenAsync("S4a Policy.ReadWrite.ConditionalAccess", AzureCli, [conditionalAccess], silent: false, Ct)
            : await _session.SignInAsync("S4a Policy.ReadWrite.ConditionalAccess", AzureCli, [conditionalAccess], Ct);

        results.Add($"RESULT S4a: {conditionalAccess} with {AzureCli} = {Line(ca)}{Expectation(ca.AadstsCode == "AADSTS65002", "DENIED (AADSTS65002)")}");
        results.Add($"RESULT S4b: {vaultDefault} with {AzureCli} = {Line(vault)}{Expectation(vault.IsGranted, "GRANTED")}");
        Summarize("S4", results);
    }

    // ---- S5: cloud selection ----

    [Fact]
    public void It_Spike_S5_CloudEndpoints()
    {
        _session.Require();
        _session.LogHeader("S5 cloud endpoints (EARM_CLOUD)", GraphCli);
        var cloud = _session.Cloud;
        var results = new List<string>
        {
            $"RESULT S5: EARM_CLOUD={Environment.GetEnvironmentVariable(EArm.Graph.GraphCloud.EnvironmentVariable) ?? "(unset)"} -> cloud={cloud.Key} ({cloud.EnvironmentName})",
            $"RESULT S5: authority={cloud.AuthorityHost} token endpoint={cloud.AuthorityHost}{_session.TenantId}/oauth2/v2.0/token",
            $"RESULT S5: graph={cloud.GraphOrigin} graph scope={cloud.GraphScope} keyvault resource={_session.KeyVaultResource}",
        };
        foreach (string client in new[] { GraphCli, AzureCli })
        {
            var record = _session.RecordFor(client);
            if (record is not null)
            {
                results.Add($"RESULT S5: sign-in record of {client}: authority={record.Authority} tenant={record.TenantId}");
            }
        }

        Summarize("S5", results);
    }

    // ---- helpers ----

    /// <summary>The shared Graph sign-in of <paramref name="clientId"/> with the base scopes; fails the probe (it cannot run) when there is none.</summary>
    private async Task<AuthSpikeAttempt?> RequireSharedSignInAsync(string probe, string clientId, List<string> results)
    {
        AuthSpikeAttempt? attempt = await _session.EnsureSignedInAsync(probe + " shared sign-in", clientId, _session.BaseGraphScopes, Ct);
        if (_session.HasRecord(clientId))
        {
            return attempt;
        }

        results.Add($"RESULT {probe}: base sign-in with {clientId} = {(attempt is null ? "NO RECORD" : Line(attempt))} - the probe could not run");
        Summarize(probe, results);
        Assert.Fail($"{probe}: the interactive sign-in with client {clientId} did not complete ({attempt?.Verdict ?? "no record"}{(attempt?.ErrorMessage is null ? string.Empty : ": " + attempt.ErrorMessage)}). The remaining steps need it; see {_session.LogPath}.");
        return null;
    }

    /// <summary>S1c: proves the Key Vault token is usable by listing secret metadata (never a value); reports the HTTP status only.</summary>
    private async Task<string> KeyVaultUsabilityAsync(AuthSpikeAttempt? usable)
    {
        if (_session.KeyVaultUrl is null)
        {
            return $"Key Vault usability = NOT TRIED ({AuthSpikeSession.KeyVaultUrlVariable} is not set)";
        }

        if (usable?.Token is null)
        {
            return "Key Vault usability = NOT TRIED (no Key Vault token was issued)";
        }

        string suffix = "." + _session.Cloud.KeyVaultSuffix;
        if (!Uri.TryCreate(_session.KeyVaultUrl, UriKind.Absolute, out Uri? vault)
            || vault.Scheme != Uri.UriSchemeHttps
            || !vault.Host.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail($"{AuthSpikeSession.KeyVaultUrlVariable}='{_session.KeyVaultUrl}' is not an https URL on *{suffix} (cloud {_session.Cloud.Key}); the spike sends a bearer token only to a vault of the selected cloud.");
        }

        // Metadata only: the versions list (or the secrets list) carries ids and attributes, never secret values.
        string path = _session.KeyVaultSecret is null
            ? "/secrets?maxresults=1&api-version=7.4"
            : $"/secrets/{Uri.EscapeDataString(_session.KeyVaultSecret)}/versions?maxresults=1&api-version=7.4";
        var target = new Uri(new Uri("https://" + vault.Host), path);
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            using var request = new HttpRequestMessage(HttpMethod.Get, target);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", usable.Token);
            using HttpResponseMessage response = await http.SendAsync(request, Ct);
            _session.Log($"S1c: GET {target.GetLeftPart(UriPartial.Path)} with the {usable.Label} token -> HTTP {(int)response.StatusCode}");
            return $"Key Vault usability (GET {target.AbsolutePath}, token from '{usable.Label}') = HTTP {(int)response.StatusCode} {response.StatusCode}";
        }
        catch (HttpRequestException ex)
        {
            return $"Key Vault usability = FAILED (HttpRequestException: {ex.Message})";
        }
        catch (TaskCanceledException) when (!Ct.IsCancellationRequested)
        {
            return "Key Vault usability = FAILED (timed out after 30 s)";
        }
    }

    private void Summarize(string probe, List<string> results)
    {
        _session.Log($"==== SUMMARY {probe} ====");
        foreach (string line in results)
        {
            _session.Log(line);
        }
    }

    private static string Line(AuthSpikeAttempt attempt)
    {
        string type = attempt.ExceptionType is null ? string.Empty : $" [{attempt.ExceptionType}]";
        string scp = attempt.Claims.TryGetValue("scp", out string? s) ? $" scp=[{s}]" : string.Empty;
        string aud = attempt.Claims.TryGetValue("aud", out string? a) ? $" aud={a}" : string.Empty;
        string error = attempt.ErrorMessage is null ? string.Empty : $" - {attempt.ErrorMessage}";
        return attempt.Verdict + type + aud + scp + error;
    }

    private static string Expectation(bool met, string expected)
    {
        return met ? $" (as expected: {expected})" : $" (UNEXPECTED: expected {expected})";
    }
}

/// <summary>
/// The delegated-auth spike runs alone (no other collection in parallel) and without the real-tenant fixture: its prompts must not
/// race the scenarios, and it never touches EARM's sign-in.
/// </summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class AuthSpikeCollection
{
    public const string Name = "AuthSpike";
}
