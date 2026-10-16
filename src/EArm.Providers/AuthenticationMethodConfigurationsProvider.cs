using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/authenticationMethodConfigurations</c>: the per-method configurations of the tenant authentication methods policy. Each one is a
/// tenant singleton that always exists; the resource name is the method id (<c>fido2</c>, <c>temporaryAccessPass</c>, <c>x509Certificate</c>,
/// <c>microsoftAuthenticator</c>, <c>sms</c>, <c>voice</c>, <c>email</c>, <c>softwareOath</c>; case-insensitive, Graph returns <c>Fido2</c>), and the
/// <c>@odata.type</c> every PATCH must carry is derived from it (<see cref="AuthenticationMethodConfigurationRules"/>).
/// <para>
/// Requests per operation:
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /policies/authenticationMethodsPolicy/authenticationMethodConfigurations/{GraphId}</c>; a 404 is a missing configuration.</item>
/// <item><b>Create</b>: never. A configuration that does not exist is an unsupported plan row (and a <see cref="ProviderException"/> when applied).</item>
/// <item><b>Update</b>: <c>PATCH …/{GraphId}</c> with <c>@odata.type</c> and the changed top-level properties (a merge PATCH) → <c>GET …/{GraphId}</c>.</item>
/// <item><b>Delete</b>: <c>DELETE …/{GraphId}</c>, which Graph documents as "revert to the default configuration", for fido2, temporaryAccessPass and
/// x509Certificate (<see cref="IDeleteWarningSource"/> says so); every other method is kept (<see cref="IDeleteRefusalSource"/>, <see cref="NotDeletable"/>).</item>
/// </list>
/// </para>
/// <para>
/// Only declared top-level properties are compared (Graph PATCH is a merge). Below them, Graph replaces a complex property whole on PATCH, so every
/// undeclared sub-property of a declared object, and of a declared <c>includeTargets</c> / <c>excludeTargets</c> / <c>passkeyProfiles</c> member
/// (matched by <c>id</c>), is filled in from the live object before the diff (<see cref="AuthenticationMethodConfigurationsShape"/>): a declared
/// <c>authenticationModeConfiguration</c> without <c>rules</c> keeps (and resends) the live certificate rules, an explicit <c>rules: []</c> clears them.
/// AAGUIDs and target / profile arrays compare as sets (the overlay's <c>memberKey</c>, AAGUIDs normalised to lower case). The legacy FIDO2
/// <c>isAttestationEnforced</c> / <c>keyRestrictions</c> are supported with a <see cref="ProviderWarnings.DeprecatedAuthenticationMethodProperty"/>
/// warning. Complete mode lists no orphans: every method always exists.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class AuthenticationMethodConfigurationsProvider : ProviderBase, IDeleteWarningSource, IDeleteRefusalSource, IOrphanCandidateSource
{
    public const string ResourceType = AuthenticationMethodConfigurationRules.ResourceType;

    /// <summary><see cref="ProviderException.Code"/> when a delete is asked for a method Graph cannot reset.</summary>
    public const string NotDeletable = EngineDiagnosticCodes.NotDeletable;

    private const string KeyProperty = "id";

    public AuthenticationMethodConfigurationsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(AuthenticationMethodConfigurationsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public AuthenticationMethodConfigurationsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>The name is the method: it never changes in place, and nothing is ever recreated.</summary>
    protected override bool RecreateOnImmutableChange => false;

    /// <summary>Nothing is created, so nothing is ever repeated.</summary>
    public override bool CreateIsRepeatable => false;

    // ---- read ----

    /// <summary>Reads the configuration of the method the key names.</summary>
    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);

        var method = MethodOf(key);
        var live = await graph.GetAsync(new GraphRequest(EntityPath(method), key.Api), cancellationToken).ConfigureAwait(false) as JsonObject;
        return live is null ? null : ToExisting(live);
    }

    // ---- plan ----

    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);

        if (desired.IsIgnored || desired.Properties is not { } properties)
        {
            return base.Plan(desired, existing, options);
        }

        var problems = Prepare(desired, properties, out var method);
        if (problems.Count > 0)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", problems)}.");
        }

        if (existing is null)
        {
            return Unsupported(desired, null, MissingMessage(desired, method!));
        }

        AuthenticationMethodConfigurationsShape.FillUndeclared(properties, existing.Properties, Definition.MemberKey);
        var change = base.Plan(desired, existing, options);
        var deprecated = AuthenticationMethodConfigurationRules.DeprecatedProperties(method!, properties);
        if (deprecated.Count == 0)
        {
            return change;
        }

        string warning = ProviderWarnings.Format(
            ProviderWarnings.DeprecatedAuthenticationMethodProperty,
            $"{string.Join(" and ", deprecated)} {(deprecated.Count == 1 ? "is" : "are")} deprecated by Microsoft and will be removed in October 2027; move the attestation and AAGUID settings to passkeyProfiles (and defaultPasskeyProfile).");
        return change with { Reasons = [.. change.Reasons, warning] };
    }

    // ---- create ----

    /// <summary>Never creates: the configurations are tenant singletons.</summary>
    public override Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var method = AuthenticationMethodConfigurationRules.Find(desired.Id.NameLeaf);
        string message = method is null
            ? $"{desired.Describe()}: {AuthenticationMethodConfigurationRules.NameProblem(desired.Id.NameLeaf)}."
            : MissingMessage(desired, method);
        throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, message, desired.Id);
    }

    // ---- update ----

    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before updating.");

        var problems = Prepare(desired, properties, out var method);
        if (problems.Count > 0)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {string.Join("; ", problems)}.", desired.Id);
        }

        AuthenticationMethodConfigurationsShape.FillUndeclared(properties, existing.Properties, Definition.MemberKey);
        var delta = BuildPatchBody(desired, existing, change);
        if (delta.Count == 0)
        {
            return new AppliedResource(desired.Id, existing.GraphId, (JsonObject)JsonValues.Clone(existing.Raw)!, ResolveKey(desired));
        }

        // Graph refuses a PATCH without the derived type ("@odata.type" first, as the Graph samples send it).
        var body = new JsonObject { [AuthenticationMethodConfigurationRules.ODataTypeProperty] = method!.ODataType };
        foreach (var name in delta.Select(p => p.Key).ToList())
        {
            var value = delta[name];
            delta.Remove(name);
            body[name] = value;
        }

        string path = EntityPath(method);
        await graph.PatchAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        var live = await graph.GetAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the {method.Name} authentication method configuration vanished after the update.", desired.Id);
        return new AppliedResource(desired.Id, existing.GraphId, live, ResolveKey(desired));
    }

    // ---- delete ----

    /// <summary>Resets a resettable method to Microsoft's default (Graph's DELETE); any other method is refused with <see cref="NotDeletable"/>.</summary>
    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);

        var method = MethodOf(key);
        if (!method.CanReset)
        {
            throw new ProviderException(NotDeletable, $"{TypeName} {key.Describe()}: {RefusalText(method)}");
        }

        try
        {
            await graph.DeleteAsync(new GraphRequest(EntityPath(method), key.Api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Nothing to reset.
        }
    }

    /// <summary>The delete of a resettable method does not remove it: it reverts every customisation to Microsoft's default.</summary>
    public string? DescribeDeleteRisk(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        var method = AuthenticationMethodConfigurationRules.Find(KeyValue(key));
        return method is { CanReset: true }
            ? $"resets the {method.Name} authentication method policy to the Microsoft default: state, targets and every setting are lost (the configuration itself is never removed)"
            : null;
    }

    /// <summary>The methods without a documented reset are kept.</summary>
    public string? DescribeDeleteRefusal(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        var method = AuthenticationMethodConfigurationRules.Find(KeyValue(key));
        return method is { CanReset: false } ? RefusalText(method) : null;
    }

    // ---- orphan report ----

    /// <summary>Every method always exists, so nothing can be an orphan: the listing is empty (and no Graph call is made).</summary>
    public OrphanScope OrphanScope => OrphanScope.Collection;

    public Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        return Task.FromResult<IReadOnlyList<OrphanCandidate>>([]);
    }

    // ---- helpers ----

    private string EntityPath(AuthenticationMethod method)
    {
        return CollectionPath + "/" + GraphPath.Segment(method.GraphId);
    }

    private AuthenticationMethod MethodOf(ResourceKey key)
    {
        string name = ProviderSupport.SingleKey(key, KeyProperty);
        return AuthenticationMethodConfigurationRules.Find(name)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: {AuthenticationMethodConfigurationRules.NameProblem(name)}.");
    }

    private static string? KeyValue(ResourceKey key)
    {
        return key.TryGet(KeyProperty, out var value) ? value : null;
    }

    private static string RefusalText(AuthenticationMethod method)
    {
        return $"the {method.Name} authentication method configuration is a tenant singleton that Graph can neither delete nor reset, so it is kept; set its state to disabled to switch the method off";
    }

    private string MissingMessage(ResourceInstance desired, AuthenticationMethod method)
    {
        return $"{desired.Describe()}: the {method.Name} authentication method configuration does not exist in the tenant (GET {EntityPath(method)} answered 404). Authentication method configurations are tenant singletons that EARM never creates; check that the tenant offers the method.";
    }

    /// <summary>The name check, the shape checks and (when they pass) the in-place normalisation of the evaluated properties.</summary>
    private static List<string> Prepare(ResourceInstance desired, JsonObject properties, out AuthenticationMethod? method)
    {
        var problems = new List<string>();
        method = AuthenticationMethodConfigurationRules.Find(desired.Id.NameLeaf);
        if (method is null)
        {
            problems.Add(AuthenticationMethodConfigurationRules.NameProblem(desired.Id.NameLeaf)!);
            return problems;
        }

        problems.AddRange(AuthenticationMethodConfigurationRules.Check(method, properties, IsOpaque).Select(f => f.Message));
        if (problems.Count == 0)
        {
            AuthenticationMethodConfigurationRules.Normalize(method, properties, IsOpaque);
        }

        return problems;
    }

    /// <summary>A deploy-time-unknown or secret value cannot be judged at plan time.</summary>
    private static bool IsOpaque(JsonNode? node)
    {
        return UnknownJson.IsUnknown(node) || SecretJson.IsSecret(node, out _);
    }

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }

    private ResourceChange Unsupported(ResourceInstance desired, ExistingResource? existing, string message)
    {
        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = existing is null ? ChangeType.Create : ChangeType.Modify,
            Mode = existing is null ? ChangeMode.Create : ChangeMode.Patch,
            GraphId = existing?.GraphId,
            Before = existing is null ? null : RenderBefore(existing.Properties, desired.Properties),
            After = JsonDiff.RenderObject(desired.Properties),
            IsUnknown = desired.HasUnknownProperties || desired.IsSymbolic,
            IsSymbolicId = desired.IsSymbolic,
            Reasons = [message],
            UnsupportedReason = message,
        };
    }
}
