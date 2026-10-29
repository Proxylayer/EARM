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
/// What the Azure role grants share (<see cref="AzureRoleEligibilityScheduleRequestsProvider"/> and <see cref="AzureRoleAssignmentsProvider"/>): a
/// declaration named by a deterministic GUID (a label) and identified by (<c>scope</c>, <c>roleDefinitionId</c>, <c>principalId</c>), read live on every run
/// through Azure Resource Manager (<see cref="IResourceManagerProvider"/>).
/// <list type="bullet">
/// <item><b>Prepare</b>: the type's rules, then the sugar <c>role</c> (a role name, or a GUID) resolved at the scope like
/// <see cref="AzureRoleManagementPoliciesProvider"/> does (<see cref="AzureRoleManagementPolicyLookup.ResolveRoleAsync"/>), and the scope, role definition id
/// (subscription-qualified, or tenant-level at a management group) and principal id normalised.</item>
/// <item><b>Plan</b>: a grant that exists at exactly the scope (an inherited one does not count) is adopted, <see cref="ChangeType.NoChange"/>: every
/// property is part of the key or create-time metadata, so nothing is ever updated in place; a live difference the type can see (an eligibility's end) is
/// reported in the row's reasons, never rewritten. A missing grant is a <see cref="ChangeType.Create"/>.</item>
/// </list>
/// Calls go through the run's <see cref="ArmClient"/>; an offline registry has none, and there any Resource Manager call fails with an
/// <see cref="InvalidOperationException"/>. No Microsoft Graph permission: the grant is an Azure RBAC role of the signed-in identity at the scope.
/// </summary>
public abstract class AzureRoleGrantProvider : ProviderBase, IResourceManagerProvider
{
    private readonly ArmClient? _resourceManager;

    /// <param name="definition">The catalog definition of the type.</param>
    /// <param name="catalog">The catalog.</param>
    /// <param name="resourceType">The type the subclass implements.</param>
    /// <param name="resourceManager">The run's Azure Resource Manager client; <c>null</c> in an offline registry (validate, permissions).</param>
    protected AzureRoleGrantProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog, string resourceType, ArmClient? resourceManager)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, resourceType))
        {
            throw new ArgumentException($"{GetType().Name} implements '{resourceType}', not '{definition.Type}'.", nameof(definition));
        }

        _resourceManager = resourceManager;
    }

    /// <summary>Every property is part of the key or create-time metadata: nothing is changed in place, and nothing is recreated.</summary>
    protected override bool RecreateOnImmutableChange => false;

    /// <summary>The <see cref="ProviderException"/> code of a declaration that cannot be applied.</summary>
    protected abstract string InvalidCode { get; }

    /// <summary>An example guid() name for the "not a GUID" message.</summary>
    protected abstract string NameExample { get; }

    /// <summary>What the type's live objects are called in messages ("Azure role eligibility").</summary>
    protected abstract string Noun { get; }

    /// <summary>The type's offline rules, opaque values skipped.</summary>
    protected abstract IReadOnlyList<PimRequestFinding> Check(JsonObject properties);

    /// <summary>The Resource Manager client of an online run.</summary>
    protected ArmClient ResourceManager => _resourceManager
        ?? throw new InvalidOperationException($"{TypeName} calls Azure Resource Manager, but this provider registry was built without a Resource Manager client (an offline registry); build it with ProviderDiscovery.CreateRegistry(catalog, settle, resourceManager).");

    // ---- permissions ----

    /// <summary>No Microsoft Graph permission: the grant is written through Azure Resource Manager, an Azure RBAC role at the scope (see <c>earm permissions</c>).</summary>
    public override IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        return [];
    }

    /// <summary>The Azure RBAC roles that may write the grant at its scope, for the what-if footer.</summary>
    public abstract IReadOnlyList<string> RequiredAzureRoles(ResourceInstance? desired);

    /// <summary>The literal scope of <paramref name="desired"/> in its canonical form, as declared when it is not an Azure scope, or a placeholder.</summary>
    protected static string DeclaredScope(ResourceInstance? desired)
    {
        return desired?.Properties is { } properties && JsonObjects.GetString(properties, "scope") is { } declared
            ? AzureScopeRules.NormalizeScope(declared) ?? declared
            : "the declared scope";
    }

    // ---- prepare (sugar + live validation) ----

    /// <summary>Checks the declaration, resolves the <c>role</c> sugar to its role definition (at the scope) and normalises the scope, role and principal.</summary>
    public override async Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before preparing.");
        if (Findings(properties) is { Length: > 0 } findings)
        {
            throw Invalid(desired, findings);
        }

        if (Text(properties, "role") is { } role)
        {
            if (Text(properties, "scope") is { } declaredScope && AzureScopeRules.NormalizeScope(declaredScope) is { } scope)
            {
                string id = AzureScopeRules.RoleDefinitionGuid(role)?.ToString("D")
                    ?? await ResolveRoleAsync(desired, scope, role, cancellationToken).ConfigureAwait(false);
                Set(properties, "roleDefinitionId", JsonValue.Create(id));
                Remove(properties, "role");
            }
            else if (Unknown(properties, "scope") is { } unknownScope)
            {
                // The role is resolved at its scope, which is only known at deployment: so is the role definition id.
                Set(properties, "roleDefinitionId", unknownScope);
                Remove(properties, "role");
            }
        }
        else if (Unknown(properties, "role") is { } unknownRole)
        {
            Set(properties, "roleDefinitionId", unknownRole);
            Remove(properties, "role");
        }

        Normalize(properties);
    }

    private async Task<string> ResolveRoleAsync(ResourceInstance desired, string scope, string role, CancellationToken cancellationToken)
    {
        var (id, error) = await AzureRoleManagementPolicyLookup.ResolveRoleAsync(ResourceManager, scope, role, cancellationToken).ConfigureAwait(false);
        return id ?? throw Invalid(desired, error!);
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

        if (!desired.IsSymbolic && !RoleAssignmentsRules.IsGuid(desired.Id.NameLeaf))
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: the name '{desired.Id.NameLeaf}' is not a GUID; name {Noun} declarations with a deterministic GUID, for example {NameExample}.");
        }

        if (Findings(properties) is { Length: > 0 } findings)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {findings}.");
        }

        Normalize(properties);
        if (existing is null)
        {
            // Not yet prepared (a symbolic name is planned before PrepareAsync runs): the role name stands in for the role definition id for this plan only,
            // so a later PrepareAsync still sees the declaration as written.
            var role = Get(properties, "roleDefinitionId") is null ? Get(properties, "role") : null;
            bool standIn = role is not null;
            if (standIn)
            {
                Set(properties, "roleDefinitionId", UnknownJson.IsUnknown(role, out var symbol) ? UnknownJson.Create(symbol) : UnknownJson.Create("role", UnknownOrigins.Reference));
            }

            try
            {
                var change = base.Plan(desired, null, options);
                return change.HasError ? change : change with { Reasons = [.. change.Reasons, .. CreateReasons(properties)] };
            }
            finally
            {
                if (standIn)
                {
                    Remove(properties, "roleDefinitionId");
                }
            }
        }

        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = ChangeType.NoChange,
            Mode = ChangeMode.None,
            Before = RenderBefore(existing.Properties, properties),
            After = JsonDiff.RenderObject(properties),
            GraphId = existing.GraphId,
            IsUnknown = desired.HasUnknownProperties,
            Reasons = [$"the {Noun} exists at exactly this scope and is adopted as it is", .. Drift(properties, existing)],
        };
    }

    /// <summary>What a Create row adds to its reasons (for example the request that is sent); none by default.</summary>
    protected virtual IEnumerable<string> CreateReasons(JsonObject properties)
    {
        return [];
    }

    /// <summary>The differences between the declaration and an adopted live grant that are reported and left as they are; none by default.</summary>
    protected virtual IEnumerable<string> Drift(JsonObject properties, ExistingResource existing)
    {
        return [];
    }

    // ---- update ----

    /// <summary>Never updates: Plan adopts an existing grant (NoChange) and plans no Modify row.</summary>
    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: an {Noun} is never changed in place; an existing one is adopted as it is.", desired.Id);
    }

    // ---- helpers ----

    /// <summary>The grant a key names.</summary>
    protected AzureRoleGrant GrantOf(ResourceKey key, ResourceIdentifier? id)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!key.TryGet("scope", out var scope) || !key.TryGet("roleDefinitionId", out var roleDefinitionId) || !key.TryGet("principalId", out var principalId)
            || AzureScopeRules.NormalizeScope(scope) is not { } normalized
            || AzureScopeRules.QualifiedRoleDefinitionId(normalized, roleDefinitionId) is not { } role
            || AzureScopeRules.NormalizePrincipalId(principalId) is not { } principal)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} does not carry an Azure scope, a role definition id and a principal id.", id);
        }

        return new AzureRoleGrant(normalized, role, principal);
    }

    /// <summary>The offline findings of a declaration, joined, plus a scope ARM cannot be sent (<see cref="ArmClient.IsValidResourcePath"/>); empty when there are none.</summary>
    private string Findings(JsonObject properties)
    {
        var messages = Check(properties).Select(f => f.Message).ToList();
        if (messages.Count == 0 && Text(properties, "scope") is { } scope && AzureScopeRules.NormalizeScope(scope) is { } normalized && !ArmClient.IsValidResourcePath(normalized))
        {
            messages.Add($"properties.scope '{scope}' has characters Azure Resource Manager paths cannot carry unescaped (letters, digits and - . _ ~ ! $ & ' ( ) * + , ; = : @ only)");
        }

        return string.Join("; ", messages);
    }

    /// <summary>The scope, the role definition id (qualified at the scope) and the principal id in canonical form; opaque values are left alone.</summary>
    private static void Normalize(JsonObject properties)
    {
        if (Text(properties, "scope") is { } declaredScope && AzureScopeRules.NormalizeScope(declaredScope) is { } scope)
        {
            Set(properties, "scope", JsonValue.Create(scope));
            if (AzureScopeRules.QualifiedRoleDefinitionId(scope, Text(properties, "roleDefinitionId")) is { } role)
            {
                Set(properties, "roleDefinitionId", JsonValue.Create(role));
            }
        }

        if (AzureScopeRules.NormalizePrincipalId(Text(properties, "principalId")) is { } principal)
        {
            Set(properties, "principalId", JsonValue.Create(principal));
        }
    }

    /// <summary>A failure of the declaration, with the type's code.</summary>
    protected ProviderException Invalid(ResourceInstance desired, string message)
    {
        return new ProviderException(InvalidCode, $"{desired.Describe()}: {message}", desired.Id);
    }

    /// <summary>A row that cannot be applied.</summary>
    protected ResourceChange Unsupported(ResourceInstance desired, ExistingResource? existing, string message)
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
            UnsupportedCode = InvalidCode,
        };
    }

    /// <summary>A deploy-time-unknown or secret value cannot be judged at plan time.</summary>
    protected static bool IsOpaque(JsonNode? node)
    {
        return UnknownJson.IsUnknown(node) || SecretJson.IsSecret(node, out _);
    }

    /// <summary>A declared, non-null property, or <c>null</c>.</summary>
    protected static JsonNode? Get(JsonObject properties, string name)
    {
        return JsonObjects.TryGetIgnoreCase(properties, name, out var value) && !JsonObjects.IsAbsent(value) ? value : null;
    }

    /// <summary>A known, non-empty string property (trimmed), or <c>null</c>.</summary>
    protected static string? Text(JsonObject properties, string name)
    {
        return Get(properties, name) is { } node && !IsOpaque(node) && JsonValues.TryGetString(node, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null;
    }

    private static JsonNode? Unknown(JsonObject properties, string name)
    {
        return Get(properties, name) is { } node && UnknownJson.IsUnknown(node) ? JsonValues.Clone(node) : null;
    }

    private static void Set(JsonObject properties, string name, JsonNode? value)
    {
        properties[JsonObjects.FindKey(properties, name) ?? name] = value;
    }

    private static void Remove(JsonObject properties, string name)
    {
        if (JsonObjects.FindKey(properties, name) is { } key)
        {
            properties.Remove(key);
        }
    }
}
