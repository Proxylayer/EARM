using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Engine.Providers;

/// <summary>How the orphan report bounds the live objects it lists for a type (docs/whatif.md, "Orphan report").</summary>
public enum OrphanScope
{
    /// <summary>A small collection listed whole (Conditional Access policies, named locations, custom authentication strengths, custom role definitions, administrative units, authentication contexts).</summary>
    Collection,

    /// <summary>The children of the parents the template declares (<see cref="IOrphanCandidateSource.OrphanParentType"/>): federated identity credentials, password credentials, the service principals of declared applications.</summary>
    ChildrenOfDeclaredParents,

    /// <summary>The assignments and grants of the principals (clients) the declared resources of the type name: role and app role assignments, OAuth2 permission grants. Reported as retained, in both modes.</summary>
    DeclaredPrincipals,

    /// <summary>The eligibilities of the target groups the declared resources of the type name (<c>groupId</c>): PIM for Groups eligibilities. Reported as retained, in both modes, like <see cref="DeclaredPrincipals"/>.</summary>
    DeclaredTargetGroups,
}

/// <summary>
/// Optional provider capability: lists the live objects of the provider's type that the orphan report compares with the declared resources. A type whose
/// provider does not implement it (applications, groups, users: tenant-wide collections) is skipped, and the report says so. Listing is read-only.
/// </summary>
public interface IOrphanCandidateSource
{
    OrphanScope OrphanScope { get; }

    /// <summary>For <see cref="Providers.OrphanScope.ChildrenOfDeclaredParents"/>: the type whose declared, live resources bound the listing; <c>null</c> otherwise.</summary>
    string? OrphanParentType => null;

    /// <summary>The live candidates in scope. Graph and provider exceptions propagate (the report then notes that the type was not listed).</summary>
    Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken);
}

/// <summary>What bounds one listing of <see cref="IOrphanCandidateSource.ListOrphanCandidatesAsync"/>.</summary>
public sealed record OrphanQuery
{
    /// <summary>The Graph endpoint to read through (that of the declared resources of the type).</summary>
    public GraphApi Api { get; init; } = GraphApi.V1;

    /// <summary><see cref="OrphanScope.ChildrenOfDeclaredParents"/>: the declared parents that exist (their live objects).</summary>
    public IReadOnlyList<OrphanParent> Parents { get; init; } = [];

    /// <summary><see cref="OrphanScope.DeclaredPrincipals"/> and <see cref="OrphanScope.DeclaredTargetGroups"/>: the resolved keys of the declared resources of the type (the principals / clients / target groups come from them).</summary>
    public IReadOnlyList<ResourceKey> DeclaredKeys { get; init; } = [];
}

/// <summary>A declared parent that exists: its template name, Graph id and live object.</summary>
public sealed record OrphanParent(string Name, string GraphId, JsonObject Live);

/// <summary>
/// A live object the orphan report considers: its Graph id (compared with the Graph ids of the declared resources), the name a template would give it
/// (displayName, credential name, appId, or the Graph id for GUID-named assignments), the parent's template name for child types, and a short description for the report.
/// </summary>
public sealed record OrphanCandidate(string GraphId, string Name, string? ParentName = null, string? Description = null);
