using System.Text.Json.Nodes;
using EArm.Az;
using EArm.Core.Types;
using EArm.Engine.Edges;
using EArm.Engine.Providers;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Functions;

namespace EArm.Engine.Planning;

/// <summary>Deployment-wide inputs of <see cref="DeploymentPlanner"/>.</summary>
public sealed record PlannerOptions
{
    /// <summary>The deployment name (<c>-n</c>; defaults to the template file stem at the CLI).</summary>
    public string DeploymentName { get; init; } = "deployment";

    /// <summary>The tenant from preflight; <c>null</c> offline.</summary>
    public string? TenantId { get; init; }

    /// <summary>What <c>tenant()</c> returns (<see cref="DeploymentEnvironment.ReadTenantAsync"/>); <c>null</c> keeps it a deploy-time unknown.</summary>
    public JsonObject? Tenant { get; init; }

    /// <summary>What <c>environment()</c> returns (<see cref="DeploymentEnvironment.Environment"/>); <c>null</c> keeps it a deploy-time unknown.</summary>
    public JsonObject? Environment { get; init; }

    /// <summary><c>--parallelism</c>: resources read/applied concurrently inside a batch.</summary>
    public int Parallelism { get; init; } = 4;

    public bool AllowCaEnforce { get; init; }

    public SoftDeletedConflict OnSoftDeletedConflict { get; init; } = SoftDeletedConflict.Error;

    public ResourceTypeCatalog Catalog { get; init; } = ResourceTypeCatalog.Default;

    public FunctionRegistry Functions { get; init; } = FunctionRegistry.Default;

    public ExpressionEvaluator Evaluator { get; init; } = ExpressionEvaluator.Default;

    /// <summary>
    /// Outer retry policy around provider calls. Leave <c>null</c> with <see cref="AzGraphClient"/>, which retries every
    /// call itself; set one (with a fake delay provider) when driving a fake Graph client in tests.
    /// </summary>
    public RetryPolicy? RetryPolicy { get; init; }

    /// <summary>The edge-kind handlers of the edge pass (plan section 1.4); part A by default, part B kinds plug in here.</summary>
    public EdgeKindRegistry? EdgeKinds { get; init; }

    /// <summary>
    /// The object id of the signed-in principal (from preflight), so the edge pass can warn when the template removes
    /// it from an object's owners; <c>null</c> lets the pass look it up with <c>GET /me</c> when needed.
    /// </summary>
    public string? SignedInPrincipalId { get; init; }
}
