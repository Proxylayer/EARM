using System;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Graph.Auth;
using Microsoft.Extensions.Logging;

namespace EArm.Integration.Tests;

/// <summary>
/// The Azure Resource Manager side of the real-tenant fixture, built only when <c>EARM_IT_ALLOW_AZURE_PIM_POLICIES=1</c> and a valid
/// <c>EARM_IT_AZURE_SCOPE</c> are set: an <see cref="ArmClient"/> on the production stack (the fixture's token provider and retry policy, an
/// <see cref="HttpClient"/> with the production handler settings of <see cref="HttpGraphClient.CreateHandler"/>) with an <see cref="ArmResponseRecorder"/>
/// between the client and the handler, and the <see cref="AzurePimPolicyRestoreGuard"/> of the one configured policy. Construction sends nothing;
/// <see cref="PreflightAsync"/> reads the resource group once (a cheap read that proves the token, the consent and the subscription access).
/// <para>
/// The client requests the cloud's Resource Manager <c>.default</c> token from the same sign-in as Graph. A user sign-in that has not consented Azure
/// Resource Manager (<see cref="GraphCloud.ResourceManagerConsentScopes"/>) fails the preflight with a <see cref="ConsentRequiredException"/>, unless
/// <c>EARM_IT_INTERACTIVE=1</c> gave the token provider a consent prompter, which then asks once for <c>user_impersonation</c> and retries.
/// </para>
/// </summary>
internal sealed class AzureResourceManagerSession : IDisposable
{
    private readonly HttpClient _http;

    public AzureResourceManagerSession(IGraphTokenProvider tokens, RetryPolicy retryPolicy, GraphCloud cloud, AzurePimPolicyTarget target, string runDirectory, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(retryPolicy);
        ArgumentNullException.ThrowIfNull(cloud);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        Target = target;
        Responses = new ArmResponseRecorder(HttpGraphClient.CreateHandler());
        _http = new HttpClient(Responses, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
        Arm = new ArmClient(_http, tokens, retryPolicy, cloud, logger: loggerFactory.CreateLogger<ArmClient>());
        Policies = new AzurePimPolicyRestoreGuard(Arm, target, runDirectory);
    }

    /// <summary>The production Resource Manager client.</summary>
    public ArmClient Arm { get; }

    /// <summary>The status and throttling headers of every response the client received (retries included).</summary>
    public ArmResponseRecorder Responses { get; }

    /// <summary>The snapshot / restore guard of the configured policy.</summary>
    public AzurePimPolicyRestoreGuard Policies { get; }

    /// <summary>The configured scope and role.</summary>
    public AzurePimPolicyTarget Target { get; }

    /// <summary>The resource group as the preflight read it; null until <see cref="PreflightAsync"/> succeeded.</summary>
    public JsonObject? ResourceGroup { get; private set; }

    /// <summary>Reads the configured resource group (<c>GET {scope}?api-version=2021-04-01</c>).</summary>
    /// <exception cref="GraphException">ARM refused the read (403 AuthorizationFailed, 404 ResourceGroupNotFound / SubscriptionNotFound, ...).</exception>
    /// <exception cref="GraphTransportException">No token (consent missing: the inner exception chain carries a <see cref="ConsentRequiredException"/>), or ARM could not be reached.</exception>
    public async Task PreflightAsync(CancellationToken cancellationToken)
    {
        ResourceGroup = await Arm.GetAsync(Target.Scope, AzurePimPolicyTarget.ResourceGroupApiVersion, cancellationToken).ConfigureAwait(false)
            ?? throw new GraphTransportException(GraphTransportFailure.InvalidResponse, $"GET {Target.Scope}: Azure Resource Manager answered with an empty body.");
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
