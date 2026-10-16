using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Graph.Auth;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Real-tenant checks of the delegated-consent flow (DES-0002) on REAL Microsoft Entra / MSAL output, which the unit tests can only simulate:
/// <list type="number">
/// <item>a silent request for a scope the sign-in has not consented is classified as <see cref="ConsentRequiredException"/> (not "sign-in expired");</item>
/// <item>the product provider's interactive consent followed by the silent retry on a NEW credential succeeds;</item>
/// <item>the token then carries every explicitly requested scope in <c>scp</c>.</item>
/// </list>
/// Interactive (a browser sign-in and one consent prompt) and gated by <c>EARM_IT_CONSENT_PROBE=1</c> plus <c>EARM_IT_TENANT</c>. Isolated from EARM's own
/// sign-in (<see cref="ConsentProbeSession"/>); the consent granted in probe 2 stays in the tenant. The probes build on each other and run in
/// method-name order. See the README section "Consent probes".
/// </summary>
[Collection(ConsentProbeCollection.Name)]
[Trait("Category", "Consent")]
[TestMethodOrderer(typeof(MethodNameTestMethodOrderer))]
public sealed class ConsentProbes : IClassFixture<ConsentProbeSession>
{
    private readonly ConsentProbeSession _session;

    public ConsentProbes(ConsentProbeSession session)
    {
        _session = session;
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task It_Consent_1_SilentRequestForAnUnconsentedScope_IsConsentRequired()
    {
        _session.Require();
        await _session.EnsureSignedInAsync(Ct);
        using var provider = _session.NewProvider(prompter: null);

        GraphTransportException? error = null;
        try
        {
            var token = await provider.GetTokenAsync(_session.Requested, Ct);
            ConsentProbeSession.Log($"silent request for {_session.Requested} was GRANTED: scp={Scp(token)}.");
        }
        catch (GraphTransportException ex)
        {
            error = ex;
        }

        Assert.SkipWhen(
            error is null,
            $"{_session.ProbeScope} is already consented to the Microsoft Graph Command Line Tools app, so no consent error can be observed. Revoke it (README, 'Consent probes') or pick another scope with {ConsentProbeSession.ScopeVariable}.");

        ConsentProbeSession.Log("silent failure: " + error!.Message.Split('\n')[0]);
        foreach (var line in GraphAuthGuidance.CredentialLines(error.InnerException ?? error))
        {
            ConsentProbeSession.Log("  detail: " + line);
        }

        Assert.True(error.IsNotAuthenticated, "Expected a NotAuthenticated transport failure: " + error.Message);
        var consent = ConsentRequiredException.FindIn(error);
        Assert.True(consent is not null, "The silent failure was not classified as consent required (real MSAL output): " + error.Message);
        Assert.NotNull(consent.InnerException);
        Assert.True(ConsentRequiredException.IsConsentFailure(consent.InnerException), "IsConsentFailure rejected the real exception chain: " + Chain(consent.InnerException));
        Assert.Equal(_session.Requested, consent.Scopes);
        Assert.Contains("earm auth login --scopes", error.Message, StringComparison.Ordinal);
        _session.ConsentWasMissing = true;
    }

    [Fact]
    public async Task It_Consent_2_InteractiveConsentThenRetryOnANewCredential_Succeeds()
    {
        _session.Require();
        Assert.SkipUnless(_session.ConsentWasMissing, "Probe 1 did not observe a consent error in this run (run the whole class, with a scope that is not consented yet).");
        var prompter = _session.NewPrompter();
        int renewals = 0;
        using var provider = _session.NewProvider(prompter, () => renewals++);

        var token = await provider.GetTokenAsync(_session.Requested, Ct);

        ConsentProbeSession.Log($"after consent: scp={Scp(token)}.");
        var asked = Assert.Single(prompter.Calls);
        Assert.Equal(_session.Requested, asked);
        Assert.Equal(1, renewals);
        _session.TokenAfterConsent = token;
    }

    [Fact]
    public async Task It_Consent_3_TheTokenCarriesEveryExplicitlyRequestedScope()
    {
        _session.Require();
        var token = _session.TokenAfterConsent;
        if (token is null)
        {
            // Run on its own (consent already granted earlier): a silent request must work now.
            await _session.EnsureSignedInAsync(Ct);
            using var provider = _session.NewProvider(prompter: null);
            try
            {
                token = await provider.GetTokenAsync(_session.Requested, Ct);
            }
            catch (GraphTransportException ex) when (ConsentRequiredException.FindIn(ex) is not null)
            {
                Assert.Skip($"{_session.ProbeScope} is not consented yet: run the whole class (probe 2 grants it).");
            }
        }

        Assert.True(TokenClaims.TryDecode(token.Value, out var claims), "The access token could not be decoded.");
        ConsentProbeSession.Log($"aud={claims.Audience} appid={claims.AppId} scp={string.Join(' ', claims.Scopes)}.");
        var granted = new HashSet<string>(claims.Scopes, StringComparer.OrdinalIgnoreCase);
        var missing = _session.Requested.Names.Where(name => !granted.Contains(name)).ToList();
        Assert.True(missing.Count == 0, $"scp lacks requested scopes: {string.Join(' ', missing)} (scp: {string.Join(' ', claims.Scopes)}).");
    }

    private static string Scp(GraphToken token)
    {
        return TokenClaims.TryDecode(token.Value, out var claims) ? string.Join(' ', claims.Scopes) : "(not a JWT)";
    }

    private static string Chain(Exception exception)
    {
        var types = new List<string>();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            types.Add(current.GetType().Name + ": " + current.Message.Split('\n')[0]);
        }

        return string.Join(" -> ", types);
    }
}

/// <summary>The consent probes run alone: they prompt interactively and must not race the scenarios.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ConsentProbeCollection
{
    public const string Name = "ConsentProbe";
}
