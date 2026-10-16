using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
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
/// <c>Microsoft.Entra/roleManagementPolicies</c>: the PIM policy (role settings) of a directory role or of a PIM for Groups membership / ownership, shaped like
/// ARM's <c>Microsoft.Authorization/roleManagementPolicies</c> (<c>properties.rules[]</c>). The resource name is a deterministic GUID (a label); the policy is
/// named by its assignment (<c>scopeType</c>, <c>scopeId</c>, <c>roleDefinitionId</c>) or the sugar <c>role</c> (→ DirectoryRole, '/', the role template id)
/// or <c>group</c> + <c>accessId</c> (→ Group, the group's id, member | owner), and read live on every run.
/// <list type="bullet">
/// <item><b>Read</b>: the policy assignment of the scope with its policy and rules (<see cref="RoleManagementPolicyLookup"/>); the policy id is the Graph id.</item>
/// <item><b>Plan</b>: only the declared rule ids are managed (live-only rules are left alone); each declared rule is filled from the live rule
/// (<see cref="RoleManagementPolicyShape.FillFromLive"/>) and compared in ARM's shape. A declared rule id the live policy does not have is an error.</item>
/// <item><b>Create</b>: never (every policy exists, Graph creates none). A directory role without a policy is <see cref="EngineDiagnosticCodes.PimPolicyNotAvailable"/>;
/// so is a group PIM for Groups has not onboarded, unless a schedule request of the same deployment onboards it: then the policy is deferred to the next
/// run (an Ignore row with a <see cref="EngineDiagnosticCodes.PimPolicyNotAvailable"/> warning) and the requests go ahead.</item>
/// <item><b>Update</b>: <c>PATCH /policies/roleManagementPolicies/{policyId}/rules/{ruleId}</c> per changed rule, with its <c>@odata.type</c>. A group's
/// first rule PATCH onboards the group, which re-ids its policy (Learn): a rule PATCH answered 404 re-reads the policy id and is sent once more.</item>
/// <item><b>Delete</b>: refused (<see cref="IDeleteRefusalSource"/>, <see cref="EngineDiagnosticCodes.NotDeletable"/>): a policy cannot be deleted; revert its
/// rules explicitly. Complete mode lists no orphans.</item>
/// </list>
/// Order: a policy is applied before the schedule requests of the same role or group the template declares (<see cref="IApplyOrderSource"/>), so they are
/// judged by the declared rules.
/// </summary>
[EntraProvider(ResourceType)]
public sealed class RoleManagementPoliciesProvider : ProviderBase, IDeleteRefusalSource, IOrphanCandidateSource, IApplyOrderSource
{
    public const string ResourceType = RoleManagementPolicyRules.ResourceType;

    private const string InvalidCode = EngineDiagnosticCodes.RoleManagementPolicyInvalid;

    /// <summary>The group policies a schedule request of the same deployment onboards (found by <see cref="AppliesBefore"/>).</summary>
    private readonly ConditionalWeakTable<ResourceInstance, object> _onboardedThisRun = new();

    public RoleManagementPoliciesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(RoleManagementPoliciesProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public RoleManagementPoliciesProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>Rules change in place; nothing is ever recreated.</summary>
    protected override bool RecreateOnImmutableChange => false;

    /// <summary>Nothing is created.</summary>
    public override bool CreateIsRepeatable => false;

    // ---- permissions ----

    /// <summary>
    /// RoleManagementPolicy.ReadWrite.Directory for a directory role policy, RoleManagementPolicy.ReadWrite.AzureADGroup for a group policy (both when the
    /// scope is computed), and Directory.Read.All for the role and group lookups.
    /// </summary>
    public override IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        var properties = desired?.Properties ?? desired?.Declaration.Properties as JsonObject;
        string? kind = properties is null ? null : RoleManagementPolicyRules.ScopeKind(properties, IsOpaqueOrExpression);
        var permissions = new List<string>();
        if (kind != RoleManagementPolicyRules.GroupScope)
        {
            permissions.Add("RoleManagementPolicy.ReadWrite.Directory");
        }

        if (kind != RoleManagementPolicyRules.DirectoryRoleScope)
        {
            permissions.Add("RoleManagementPolicy.ReadWrite.AzureADGroup");
        }

        permissions.Add("Directory.Read.All");
        return permissions;
    }

    // ---- order ----

    /// <summary>The schedule requests of the same role or group the template declares are applied after the policy (<see cref="RoleManagementPolicyApplyOrder"/>).</summary>
    public IEnumerable<ResourceInstance> AppliesBefore(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(declared);
        var requests = RoleManagementPolicyApplyOrder.GovernedRequests(instance, declared);
        if (requests.Count > 0 && instance.Declaration.Properties is JsonObject properties
            && RoleManagementPolicyRules.ScopeKind(properties, IsOpaqueOrExpression) == RoleManagementPolicyRules.GroupScope)
        {
            _onboardedThisRun.AddOrUpdate(instance, requests);
        }

        return requests;
    }

    // ---- prepare (sugar + live validation) ----

    public override async Task PrepareAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before preparing.");
        if (RoleManagementPolicyRules.Check(properties, IsOpaque) is { Count: > 0 } findings)
        {
            throw Invalid(desired, string.Join("; ", findings.Select(f => f.Message)));
        }

        switch (RoleManagementPolicyRules.ScopeKind(properties, IsOpaque))
        {
            case RoleManagementPolicyRules.DirectoryRoleScope:
                await PrepareRoleAsync(desired, properties, graph, cancellationToken).ConfigureAwait(false);
                break;
            case RoleManagementPolicyRules.GroupScope:
                await PrepareGroupAsync(desired, properties, graph, cancellationToken).ConfigureAwait(false);
                break;
        }

        Normalize(properties);
    }

    private static async Task PrepareRoleAsync(ResourceInstance desired, JsonObject properties, IGraphClient graph, CancellationToken cancellationToken)
    {
        if (Text(properties, "role") is { } role)
        {
            var resolved = await RoleAssignmentsLookup.ResolveRoleAsync(graph, role, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            Set(properties, "roleDefinitionId", JsonValue.Create(resolved.Id ?? throw Invalid(desired, resolved.Error!)));
            Remove(properties, "role");
        }
        else if (Unknown(properties, "role") is { } unknownRole)
        {
            Set(properties, "roleDefinitionId", unknownRole);
            Remove(properties, "role");
        }

        Set(properties, "scopeType", JsonValue.Create(RoleManagementPolicyRules.DirectoryRoleScope));
        Set(properties, "scopeId", JsonValue.Create(RoleManagementPolicyRules.RootScopeId));
    }

    private static async Task PrepareGroupAsync(ResourceInstance desired, JsonObject properties, IGraphClient graph, CancellationToken cancellationToken)
    {
        string? declaredGroup = Text(properties, "group") ?? Text(properties, "scopeId");
        if (declaredGroup is not null)
        {
            var found = await GroupSchedulesLookup.ReadGroupAsync(graph, declaredGroup, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false)
                ?? throw Invalid(desired, $"no group with uniqueName or id '{declaredGroup}' exists in the tenant; deploy a Microsoft.Entra/groups resource for it (and reference it), or name an existing group.");
            if (GroupSchedulesLookup.UnsupportedGroupReason(found) is { } reason)
            {
                throw Invalid(desired, reason);
            }

            Set(properties, "scopeId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(JsonObjects.GetId(found) ?? throw Invalid(desired, $"the group '{declaredGroup}' was read without an id."))));
            Remove(properties, "group");
        }
        else if (Unknown(properties, "group") is { } unknownGroup)
        {
            Set(properties, "scopeId", unknownGroup);
            Remove(properties, "group");
        }

        if (Get(properties, "accessId") is { } access)
        {
            Set(properties, "roleDefinitionId", JsonValues.Clone(access));
            Remove(properties, "accessId");
        }

        Set(properties, "scopeType", JsonValue.Create(RoleManagementPolicyRules.GroupScope));
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        var scope = ScopeOf(key, null);
        var assignment = await RoleManagementPolicyLookup.FindAsync(graph, scope, key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        return assignment is null ? null : ToExisting(assignment, scope);
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
            return Unsupported(desired, existing, $"{desired.Describe()}: the name '{desired.Id.NameLeaf}' is not a GUID; name role management policies with a deterministic GUID, for example [guid('pim-policy', 'Global Administrator')].", InvalidCode);
        }

        if (RoleManagementPolicyRules.Check(properties, IsOpaque) is { Count: > 0 } findings)
        {
            return Unsupported(desired, existing, $"{desired.Describe()}: {string.Join("; ", findings.Select(f => f.Message))}.", InvalidCode);
        }

        Normalize(properties);
        if (existing is null)
        {
            return PlanMissing(desired, properties);
        }

        if (DeclaredRules(properties) is not { } declared)
        {
            return Unknown(desired, existing, "the rules are only known at deployment time");
        }

        var liveRules = LiveRules(existing.Properties);
        var missing = declared.Select(JsonObjects.GetId).Where(id => id is not null && !liveRules.ContainsKey(id)).ToList();
        if (missing.Count > 0)
        {
            return Unsupported(
                desired,
                existing,
                $"{desired.Describe()}: the policy {existing.GraphId} has no rule {string.Join(", ", missing)}; its rules are {string.Join(", ", liveRules.Keys.Order(StringComparer.Ordinal))}.",
                InvalidCode);
        }

        foreach (var rule in declared)
        {
            RoleManagementPolicyShape.FillFromLive(rule, liveRules[JsonObjects.GetId(rule)!]);
        }

        ReplaceRules(properties, declared.Select(RoleManagementPolicyShape.Comparable));
        var aligned = new ExistingResource(existing.GraphId, AlignedLive(existing.Properties, declared), existing.Raw);
        var change = base.Plan(desired, aligned, options);
        return change is { Type: ChangeType.Modify, HasError: false }
            ? change with { Reasons = [.. change.Reasons, "each changed rule is updated in place (PATCH of the rule); undeclared rules are left as they are"] }
            : change;
    }

    /// <summary>No live policy: an unknown scope is resolved at deployment; a directory role is an error; a group is deferred when this deployment onboards it.</summary>
    private ResourceChange PlanMissing(ResourceInstance desired, JsonObject properties)
    {
        if (desired.IsSymbolic || UnknownJson.ContainsUnknown(Get(properties, "scopeId")) || UnknownJson.ContainsUnknown(Get(properties, "roleDefinitionId"))
            || Get(properties, "role") is not null || Get(properties, "group") is not null)
        {
            return Unknown(desired, null, "the policy is looked up when the deployment runs (its scope is only known then)");
        }

        var scope = ScopeOf(properties);
        if (scope is null)
        {
            return Unknown(desired, null, "the policy is looked up when the deployment runs (its scope is only known then)");
        }

        if (!scope.IsGroup)
        {
            return Unsupported(desired, null, $"{desired.Describe()}: Graph lists no PIM policy for directory role {scope.RoleDefinitionId} (GET {RoleManagementPolicyLookup.AssignmentsPath}?$filter={RoleManagementPolicyLookup.Filter(scope)}); check that roleDefinitionId is the role's template id. Role management policies are never created.", EngineDiagnosticCodes.PimPolicyNotAvailable);
        }

        string notOnboarded = $"Graph lists no PIM policy for the {scope.RoleDefinitionId} access of group {scope.ScopeId}: PIM for Groups has not onboarded the group";
        if (_onboardedThisRun.TryGetValue(desired, out _))
        {
            string message = ProviderWarnings.Format(
                EngineDiagnosticCodes.PimPolicyNotAvailable,
                $"{notOnboarded} yet. A schedule request of this deployment onboards it; the policy is applied by the next run (deploy again once this one succeeded). Until then the group's default policy applies to those requests.");
            return new ResourceChange
            {
                Id = desired.Id,
                ResourceType = TypeName,
                Type = ChangeType.Ignore,
                Mode = ChangeMode.None,
                After = JsonDiff.RenderObject(desired.Properties),
                Reasons = [message],
            };
        }

        return Unsupported(desired, null, $"{desired.Describe()}: {notOnboarded}, and no schedule request of this deployment onboards it. PIM onboards a group on its first eligibility or assignment request (or policy update; Learn: privilegedidentitymanagement-for-groups-api-overview); declare a privilegedAccess/groupEligibilityScheduleRequests (or groupAssignmentScheduleRequests) for the group in the same template, or onboard it in the portal, then deploy again.", EngineDiagnosticCodes.PimPolicyNotAvailable);
    }

    // ---- create ----

    /// <summary>Never creates: a policy exists for every role and onboarded group (Plan reports the missing ones).</summary>
    public override Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        throw new ProviderException(EngineDiagnosticCodes.PimPolicyNotAvailable, $"{desired.Describe()}: the PIM policy does not exist; role management policies are never created.", desired.Id);
    }

    // ---- update ----

    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        var properties = desired.Properties ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before updating.");
        var key = ResolveKey(desired);
        var scope = ScopeOf(key, desired.Id);
        var declared = DeclaredRules(properties) ?? throw Invalid(desired, "the rules are not known");
        var liveRules = LiveRules(existing.Properties);
        string policyId = existing.GraphId;

        foreach (var rule in declared)
        {
            string ruleId = JsonObjects.GetId(rule) ?? throw Invalid(desired, "a rule has no id");
            if (!liveRules.TryGetValue(ruleId, out var liveRule))
            {
                throw Invalid(desired, $"the policy {policyId} has no rule {ruleId}");
            }

            RoleManagementPolicyShape.FillFromLive(rule, liveRule);
            var comparable = RoleManagementPolicyShape.Comparable(rule);
            if (!Differs(comparable, liveRule))
            {
                continue;
            }

            var body = (JsonObject)SecretJson.Reveal(comparable)!;
            try
            {
                await RoleManagementPolicyLookup.PatchRuleAsync(graph, policyId, body, desired.Api, desired.RetryHint, cancellationToken).ConfigureAwait(false);
            }
            catch (GraphException ex) when (ex.IsNotFound && scope.IsGroup)
            {
                // The group's first policy update onboarded it in PIM for Groups, which re-ids its policy (Learn): find the new id and send the rule again.
                var reread = await RoleManagementPolicyLookup.FindAsync(graph, scope, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false);
                string? newId = reread is null ? null : RoleManagementPolicyLookup.PolicyId(reread);
                if (newId is null || string.Equals(newId, policyId, StringComparison.OrdinalIgnoreCase))
                {
                    throw;
                }

                policyId = newId;
                await RoleManagementPolicyLookup.PatchRuleAsync(graph, policyId, body, desired.Api, RetryHint.ExpectReplicationLag, cancellationToken).ConfigureAwait(false);
            }
        }

        var live = await RoleManagementPolicyLookup.FindAsync(graph, scope, desired.Api, scope.IsGroup ? RetryHint.ExpectReplicationLag : desired.RetryHint, cancellationToken).ConfigureAwait(false);
        string graphId = (live is null ? null : RoleManagementPolicyLookup.PolicyId(live)) ?? policyId;
        return new AppliedResource(desired.Id, graphId, live ?? (JsonObject)JsonValues.Clone(existing.Raw)!, key);
    }

    // ---- delete ----

    /// <summary>Never deletes: Graph cannot delete a PIM policy (<see cref="EngineDiagnosticCodes.NotDeletable"/>).</summary>
    public override Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        throw new ProviderException(EngineDiagnosticCodes.NotDeletable, $"{TypeName} {key.Describe()}: {RefusalText}");
    }

    public string? DescribeDeleteRefusal(ResourceKey key, ExistingResource live)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        return RefusalText;
    }

    private const string RefusalText = "a PIM role management policy cannot be deleted (every role and onboarded group always has one), so it is kept with its current rules; to undo a setting, declare the rule with the value to restore and deploy";

    // ---- orphan report ----

    /// <summary>Policies always exist and are never created, so none can be an orphan: the listing is empty (no Graph call).</summary>
    public OrphanScope OrphanScope => OrphanScope.Collection;

    public Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        return Task.FromResult<IReadOnlyList<OrphanCandidate>>([]);
    }

    // ---- helpers ----

    /// <summary>The scope a key names.</summary>
    private PolicyScope ScopeOf(ResourceKey key, ResourceIdentifier? id)
    {
        if (!key.TryGet("scopeType", out var scopeType) || !key.TryGet("scopeId", out var scopeId) || !key.TryGet("roleDefinitionId", out var roleDefinitionId)
            || RoleManagementPolicyRules.NormalizeScopeType(scopeType) is not { } kind)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} does not carry scopeType, scopeId and roleDefinitionId.", id);
        }

        return Scope(kind, scopeId, roleDefinitionId);
    }

    /// <summary>The scope prepared properties name, or <c>null</c> when part of it is not known.</summary>
    private static PolicyScope? ScopeOf(JsonObject properties)
    {
        return Text(properties, "scopeType") is { } scopeType && RoleManagementPolicyRules.NormalizeScopeType(scopeType) is { } kind
            && Text(properties, "scopeId") is { } scopeId && Text(properties, "roleDefinitionId") is { } roleDefinitionId
            ? Scope(kind, scopeId, roleDefinitionId)
            : null;
    }

    private static PolicyScope Scope(string kind, string scopeId, string roleDefinitionId)
    {
        return kind == RoleManagementPolicyRules.GroupScope
            ? new PolicyScope(kind, RoleAssignmentsRules.IsGuid(scopeId) ? RoleAssignmentsRules.NormalizeId(scopeId) : scopeId.Trim(), GroupEligibilityScheduleRules.NormalizeAccessId(roleDefinitionId) ?? roleDefinitionId.Trim())
            : new PolicyScope(kind, RoleManagementPolicyRules.RootScopeId, RoleAssignmentsRules.IsGuid(roleDefinitionId) ? RoleAssignmentsRules.NormalizeId(roleDefinitionId) : roleDefinitionId.Trim());
    }

    private static ExistingResource ToExisting(JsonObject assignment, PolicyScope scope)
    {
        string policyId = RoleManagementPolicyLookup.PolicyId(assignment)
            ?? throw new ProviderException(EngineDiagnosticCodes.ReadFailed, $"The PIM policy assignment of {scope.Describe()} names no policy.");
        return new ExistingResource(policyId, RoleManagementPolicyLookup.LiveView(assignment, scope), (JsonObject)JsonValues.Clone(assignment)!);
    }

    /// <summary>The scope values and the declared rules in canonical form (Graph casing, ids, enums); opaque values are left alone.</summary>
    private static void Normalize(JsonObject properties)
    {
        if (Text(properties, "scopeType") is { } scopeType && RoleManagementPolicyRules.NormalizeScopeType(scopeType) is { } kind)
        {
            Set(properties, "scopeType", JsonValue.Create(kind));
            if (Text(properties, "scopeId") is { } scopeId && RoleAssignmentsRules.IsGuid(scopeId))
            {
                Set(properties, "scopeId", JsonValue.Create(RoleAssignmentsRules.NormalizeId(scopeId)));
            }

            if (Text(properties, "roleDefinitionId") is { } roleDefinitionId)
            {
                Set(properties, "roleDefinitionId", JsonValue.Create(kind == RoleManagementPolicyRules.GroupScope
                    ? GroupEligibilityScheduleRules.NormalizeAccessId(roleDefinitionId) ?? roleDefinitionId
                    : RoleAssignmentsRules.IsGuid(roleDefinitionId) ? RoleAssignmentsRules.NormalizeId(roleDefinitionId) : roleDefinitionId));
            }
        }

        if (Get(properties, "rules") is JsonArray rules && !IsOpaque(rules))
        {
            RoleManagementPolicyRules.NormalizeRules(rules, IsOpaque);
        }
    }

    /// <summary>The declared rules (objects with a known id), or <c>null</c> when the array or a rule id is only known at deployment.</summary>
    private static List<JsonObject>? DeclaredRules(JsonObject properties)
    {
        if (Get(properties, "rules") is not JsonArray rules || UnknownJson.ContainsUnknown(rules))
        {
            return null;
        }

        var declared = rules.OfType<JsonObject>().ToList();
        return declared.Count == rules.Count && declared.All(r => JsonObjects.GetId(r) is not null) ? declared : null;
    }

    /// <summary>The live rules by id.</summary>
    private static Dictionary<string, JsonObject> LiveRules(JsonObject? live)
    {
        var rules = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (live is not null && JsonObjects.TryGetIgnoreCase(live, "rules", out var node) && node is JsonArray array)
        {
            foreach (var rule in array.OfType<JsonObject>())
            {
                if (JsonObjects.GetId(rule) is { } id)
                {
                    rules[id] = rule;
                }
            }
        }

        return rules;
    }

    /// <summary>The live view reduced to the declared rules (live-only rules are not managed), in the declared order.</summary>
    private static JsonObject AlignedLive(JsonObject? live, IReadOnlyList<JsonObject> declared)
    {
        var aligned = live is null ? new JsonObject() : (JsonObject)JsonValues.Clone(live)!;
        var liveRules = LiveRules(live);
        aligned["rules"] = new JsonArray(declared.Select(r => (JsonNode?)JsonValues.Clone(liveRules[JsonObjects.GetId(r)!])).ToArray());
        return aligned;
    }

    private static void ReplaceRules(JsonObject properties, IEnumerable<JsonObject> rules)
    {
        Set(properties, "rules", new JsonArray(rules.Select(r => (JsonNode?)r).ToArray()));
    }

    /// <summary>True when the (filled, comparable) desired rule differs from the live rule anywhere.</summary>
    private static bool Differs(JsonObject desired, JsonObject live)
    {
        var deltas = JsonDiff.Diff(new JsonObject { ["rule"] = JsonValues.Clone(desired) }, new JsonObject { ["rule"] = JsonValues.Clone(live) });
        return deltas.Any(d => d.HasEffect);
    }

    private ResourceChange Unknown(ResourceInstance desired, ExistingResource? existing, string reason)
    {
        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = ChangeType.Modify,
            Mode = ChangeMode.Patch,
            GraphId = existing?.GraphId,
            After = JsonDiff.RenderObject(desired.Properties),
            IsUnknown = true,
            IsSymbolicId = desired.IsSymbolic,
            Reasons = [reason],
        };
    }

    private ResourceChange Unsupported(ResourceInstance desired, ExistingResource? existing, string message, string code)
    {
        return new ResourceChange
        {
            Id = desired.Id,
            ResourceType = TypeName,
            Type = ChangeType.Modify,
            Mode = ChangeMode.Patch,
            GraphId = existing?.GraphId,
            Before = existing is null ? null : RenderBefore(existing.Properties, desired.Properties),
            After = JsonDiff.RenderObject(desired.Properties),
            IsUnknown = desired.HasUnknownProperties || desired.IsSymbolic,
            IsSymbolicId = desired.IsSymbolic,
            Reasons = [message],
            UnsupportedReason = message,
            UnsupportedCode = code,
        };
    }

    private static ProviderException Invalid(ResourceInstance desired, string message)
    {
        return new ProviderException(InvalidCode, $"{desired.Describe()}: {message}", desired.Id);
    }

    /// <summary>A deploy-time-unknown or secret value cannot be judged at plan time.</summary>
    private static bool IsOpaque(JsonNode? node)
    {
        return UnknownJson.IsUnknown(node) || SecretJson.IsSecret(node, out _);
    }

    /// <summary>Opaque, or (on an unevaluated declaration) a template expression.</summary>
    private static bool IsOpaqueOrExpression(JsonNode? node)
    {
        return IsOpaque(node) || (node is JsonValue value && value.TryGetValue(out string? text) && ExpressionSyntax.IsExpression(text));
    }

    private static JsonNode? Get(JsonObject properties, string name)
    {
        return JsonObjects.TryGetIgnoreCase(properties, name, out var value) && !JsonObjects.IsAbsent(value) ? value : null;
    }

    private static string? Text(JsonObject properties, string name)
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

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType) ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }
}
