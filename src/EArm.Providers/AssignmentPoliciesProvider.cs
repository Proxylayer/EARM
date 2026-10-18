using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Expressions.Values;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/entitlementManagement/accessPackages/assignmentPolicies</c> (accessPackageAssignmentPolicy): a child of its access package; the
/// child name is the policy's <c>displayName</c>, unique within the package.
/// <list type="bullet">
/// <item><b>Read</b>: the package (the engine's parent id, else <c>$filter=displayName eq</c> on the packages) → <c>GET …/assignmentPolicies?$filter=accessPackage/id eq '…'</c>,
/// matched on displayName client-side; two policies with the name are <c>AmbiguousMatch</c>.</item>
/// <item><b>Plan</b>: only what the template declares is compared (<see cref="AssignmentPolicyShape.ProjectLive"/>).</item>
/// <item><b>Create</b>: <c>POST …/assignmentPolicies</c> with <c>accessPackage { id }</c>.</item>
/// <item><b>Update</b>: Graph only offers a full-replace <c>PUT</c>: the policy is read again (<c>$expand=questions</c>) and PUT with the declared values
/// merged in (<see cref="AssignmentPolicyShape.MergeForPut"/>), so undeclared settings keep their live values.</item>
/// <item><b>Delete</b>: <c>DELETE …/assignmentPolicies/{id}</c>; Graph refuses a policy with assignments: <see cref="AccessPackagesProvider.AssignmentsExist"/>.</item>
/// </list>
/// </summary>
[EntraProvider(ResourceType)]
public sealed class AssignmentPoliciesProvider : ProviderBase, IOrphanCandidateSource, IDeclarationConflictSource
{
    public const string ResourceType = "Microsoft.Entra/entitlementManagement/accessPackages/assignmentPolicies";

    private const string KeyProperty = "displayName";

    public AssignmentPoliciesProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(AssignmentPoliciesProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    /// <summary>A warning when the policy's assignments outlive the PIM for Groups eligibility maximum of a group the package grants eligibly (<see cref="AccessPackageExpirationCheck"/>).</summary>
    public IEnumerable<DeclarationConflict> DescribeConflicts(ResourceInstance instance, IReadOnlyList<ResourceInstance> declared)
    {
        return AccessPackageExpirationCheck.Describe(instance, declared);
    }

    /// <summary>The policy list of a package can miss a new policy for a moment.</summary>
    public override bool KeyReadLagsCreate => true;

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        string name = ProviderSupport.SingleKey(key, KeyProperty);
        string? packageId = key.ParentGraphId ?? await FindPackageIdAsync(graph, key.ParentKey, key.Api, cancellationToken).ConfigureAwait(false);
        if (packageId is null)
        {
            return null;
        }

        var matches = (await ListPoliciesAsync(graph, packageId, key.Api, cancellationToken).ConfigureAwait(false))
            .Where(p => string.Equals(JsonObjects.GetString(p, KeyProperty), name, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matches.Count > 1)
        {
            throw new ProviderException(
                ProviderException.AmbiguousMatch,
                $"{matches.Count} assignment policies of access package {packageId} have displayName '{name}' (ids: {string.Join(", ", matches.Select(m => JsonObjects.GetId(m) ?? "?"))}); rename or delete the others so the name identifies one policy.",
                null);
        }

        return matches.Count == 1 ? ToExisting(matches[0]) : null;
    }

    // ---- plan ----

    /// <summary>The shared diff over the live policy cut down to the declared shape.</summary>
    public override ResourceChange Plan(ResourceInstance desired, ExistingResource? existing, PlanOptions options)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(options);
        if (existing is not null && desired.Properties is { } properties && AssignmentPolicyShape.ProjectLive(properties, existing.Properties) is JsonObject projected)
        {
            existing = existing with { Properties = projected };
        }

        var planned = base.Plan(desired, existing, options);
        if (planned.Type is not (ChangeType.Create or ChangeType.Modify) || desired.Properties is not { } evaluated
            || AccessPackageReviewRules.Check(evaluated, IsOpaque) is not { Count: > 0 } findings)
        {
            return planned;
        }

        // The review rules the offline validator applies to literal values, now on the evaluated ones (Learn: resources/accesspackageassignmentreviewsettings).
        string message = $"{desired.Describe()}: {string.Join("; ", findings.Select(f => f.Message))}.";
        return planned with { Reasons = [.. planned.Reasons, message], UnsupportedReason = message, UnsupportedCode = InvalidReviewSettings };
    }

    /// <summary>A deploy-time-unknown or secret value cannot be judged at plan time.</summary>
    private static bool IsOpaque(JsonNode? node)
    {
        return UnknownJson.IsUnknown(node) || SecretJson.IsSecret(node, out _);
    }

    /// <summary>The code of a policy whose <c>reviewSettings</c> break a rule of <see cref="AccessPackageReviewRules"/>.</summary>
    public const string InvalidReviewSettings = DiagnosticCodes.InvalidAccessPackageReviewSettings;

    /// <summary>The code when Graph refuses access reviews of the policy for want of a licence (Microsoft Entra ID Governance, or P2).</summary>
    public const string GovernanceLicenseRequired = EngineDiagnosticCodes.GovernanceLicenseRequired;

    // ---- create ----

    public override async Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        string packageId = await RequirePackageIdAsync(desired, graph, cancellationToken).ConfigureAwait(false);
        var body = BuildCreateBody(desired);
        body["accessPackage"] = new JsonObject { ["id"] = packageId };
        GraphResponse response;
        try
        {
            response = await graph.PostAsync(new GraphRequest(CollectionPath, desired.Api, hint: desired.RetryHint), body, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (IsReviewLicenseRefusal(ex, body))
        {
            throw ReviewLicenseRequired(desired, ex);
        }

        var live = response.Body as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned no object for the created assignment policy.", desired.Id);
        string id = JsonObjects.GetId(live)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: Graph returned an assignment policy without 'id'.", desired.Id);
        return new AppliedResource(desired.Id, id, live, ResolveKey(desired) with { ParentGraphId = packageId });
    }

    // ---- update ----

    public override async Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        string packageId = await RequirePackageIdAsync(desired, graph, cancellationToken).ConfigureAwait(false);
        string path = CollectionPath + "/" + GraphPath.Segment(existing.GraphId);
        var current = await ReadForPutAsync(graph, path, desired, cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the assignment policy ({existing.GraphId}) vanished before the update.", desired.Id);

        var document = AssignmentPolicyShape.MergeForPut(current, BuildCreateBody(desired));
        document["accessPackage"] = new JsonObject { ["id"] = packageId };
        GraphResponse response;
        try
        {
            response = await graph.PutAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), document, cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (IsReviewLicenseRefusal(ex, document))
        {
            throw ReviewLicenseRequired(desired, ex);
        }

        var live = response.Body as JsonObject
            ?? await graph.GetAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), cancellationToken).ConfigureAwait(false) as JsonObject
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: the assignment policy ({existing.GraphId}) vanished after the update.", desired.Id);
        return new AppliedResource(desired.Id, existing.GraphId, live, ResolveKey(desired) with { ParentGraphId = packageId });
    }

    /// <summary>The policy with its questions (a PUT must carry them, Learn: accesspackageassignmentpolicy-update); without the expand when Graph refuses it.</summary>
    private static async Task<JsonObject?> ReadForPutAsync(IGraphClient graph, string path, ResourceInstance desired, CancellationToken cancellationToken)
    {
        try
        {
            return await graph.GetAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint).WithExpand("questions"), cancellationToken).ConfigureAwait(false) as JsonObject;
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            return await graph.GetAsync(new GraphRequest(path, desired.Api, hint: desired.RetryHint), cancellationToken).ConfigureAwait(false) as JsonObject;
        }
    }

    // ---- delete ----

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        try
        {
            await graph.DeleteAsync(new GraphRequest(CollectionPath + "/" + GraphPath.Segment(live.GraphId), key.Api), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (ex.IsNotFound)
        {
            // Already gone.
        }
        catch (GraphException ex) when (ex.Status == HttpStatusCode.BadRequest)
        {
            bool assignments = EntitlementManagementGraph.IsAssignmentsRefusal(ex);
            throw new ProviderException(
                assignments ? AccessPackagesProvider.AssignmentsExist : EngineDiagnosticCodes.ResourceApplyFailed,
                assignments
                    ? $"{TypeName} {key.Describe()}: Graph refused to delete assignment policy {live.GraphId} because it still has assignments ({ex.Code}: {ex.GraphMessage}); assignments of users deleted in the last 30 days count too. EARM never removes assignments; remove them (or move them to another policy), then run the delete again."
                    : $"{TypeName} {key.Describe()}: Graph refused to delete assignment policy {live.GraphId} ({ex.Code}: {ex.GraphMessage}).",
                null,
                ex);
        }
    }

    // ---- helpers ----

    /// <summary>
    /// Graph's refusal of a policy with enabled access reviews for want of a licence: the error code is undocumented, so a 400 or 403 whose code is
    /// <c>NoGovernanceSku</c> or whose message names a licence / SKU, sent with <c>reviewSettings.isEnabled: true</c>. Learn (licensing-fundamentals,
    /// "Access reviews"): access reviews need Microsoft Entra ID Governance for the member users in scope; some capabilities still run with P2.
    /// </summary>
    internal static bool IsReviewLicenseRefusal(GraphException ex, JsonObject body)
    {
        bool reviewsEnabled = body["reviewSettings"] is JsonObject review && review["isEnabled"] is JsonValue enabled && enabled.TryGetValue(out bool on) && on;
        return reviewsEnabled && ex.Status is HttpStatusCode.BadRequest or HttpStatusCode.Forbidden
            && (ResourceRoleScopesProvider.IsNoGovernanceSku(ex)
                || ex.GraphMessage.Contains("licens", StringComparison.OrdinalIgnoreCase) || ex.GraphMessage.Contains("SKU", StringComparison.OrdinalIgnoreCase));
    }

    private static ProviderException ReviewLicenseRequired(ResourceInstance desired, GraphException ex)
    {
        return new ProviderException(
            GovernanceLicenseRequired,
            $"{desired.Describe()}: Graph refused the policy's access reviews ({ex.Code}: {ex.GraphMessage}). Access reviews of access package assignments need Microsoft Entra ID Governance (or Microsoft Entra ID P2 for the reviews P2 already offered) for the member users who review or are reviewed (Learn: licensing-fundamentals); sponsors as reviewers need ID Governance. Assign the licence, or set reviewSettings.isEnabled to false.",
            desired.Id,
            ex);
    }

    private static async Task<IReadOnlyList<JsonObject>> ListPoliciesAsync(IGraphClient graph, string packageId, GraphApi api, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(EntitlementManagementGraph.AssignmentPoliciesPath, api)
            .WithFilter($"accessPackage/id eq '{ProviderSupport.EscapeLiteral(packageId)}'");
        var policies = new List<JsonObject>();
        await foreach (var item in graph.GetAllPagesAsync(request, cancellationToken).ConfigureAwait(false))
        {
            if (item is JsonObject policy)
            {
                policies.Add(policy);
            }
        }

        return policies;
    }

    private static async Task<string?> FindPackageIdAsync(IGraphClient graph, ResourceKey? parentKey, GraphApi api, CancellationToken cancellationToken)
    {
        if (parentKey is null || parentKey.UnresolvedProperties.Count > 0 || !parentKey.TryGet(KeyProperty, out var packageName))
        {
            return null;
        }

        var package = await AccessPackagesProvider.FindAsync(graph, packageName, api, cancellationToken).ConfigureAwait(false);
        return package is null ? null : JsonObjects.GetId(package);
    }

    private async Task<string> RequirePackageIdAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        if (desired.ParentGraphId is { Length: > 0 } known)
        {
            return known;
        }

        return await FindPackageIdAsync(graph, ResolveKey(desired).ParentKey, desired.Api, cancellationToken).ConfigureAwait(false)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: access package '{desired.Id.Parent?.NameLeaf}' does not exist; declare it in the template (or create it first).", desired.Id);
    }

    // ---- orphan report ----

    /// <summary>The policies of the declared packages that exist.</summary>
    public OrphanScope OrphanScope => OrphanScope.ChildrenOfDeclaredParents;

    public string? OrphanParentType => Definition.ParentType;

    public async Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        var candidates = new List<OrphanCandidate>();
        foreach (var parent in query.Parents)
        {
            foreach (var policy in await ListPoliciesAsync(graph, parent.GraphId, query.Api, cancellationToken).ConfigureAwait(false))
            {
                if (JsonObjects.GetId(policy) is { Length: > 0 } id)
                {
                    candidates.Add(new OrphanCandidate(id, JsonObjects.GetString(policy, KeyProperty) ?? id, parent.Name, OrphanListing.Describe(policy, "allowedTargetScope")));
                }
            }
        }

        return candidates;
    }
}
