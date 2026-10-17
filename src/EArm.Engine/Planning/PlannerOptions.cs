using System.Text.Json.Nodes;
using EArm.Graph;
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

    /// <summary>What <c>deployer()</c> returns (<see cref="DeploymentEnvironment.Deployer"/>); <c>null</c> keeps it a deploy-time unknown.</summary>
    public JsonObject? Deployer { get; init; }

    /// <summary>What <c>environment()</c> returns (<see cref="DeploymentEnvironment.Environment"/>); <c>null</c> keeps it a deploy-time unknown.</summary>
    public JsonObject? Environment { get; init; }

    /// <summary><c>--parallelism</c>: resources read/applied concurrently inside a batch.</summary>
    public int Parallelism { get; init; } = 4;

    /// <summary><c>--allow-ca-enforce</c>: see <see cref="Providers.PlanOptions.AllowCaEnforce"/>.</summary>
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

    /// <summary>
    /// How an empty key read of a type whose key read lags (<see cref="IResourceProvider.KeyReadLagsCreate"/>) is confirmed before a create is
    /// planned; <c>null</c> is <see cref="KeyAbsenceCheck.Default"/>. Tests pass one with a delay provider that does not sleep.
    /// </summary>
    public KeyAbsenceCheck? KeyAbsenceCheck { get; init; }

    /// <summary>
    /// True (the default) confirms an empty key read with the extra reads of <see cref="KeyAbsenceCheck"/> before a create is planned; false plans on
    /// the first read (no extra reads, no waiting). <c>deployment delete</c> keeps the confirmation: it deletes what the plan finds. <c>what-if</c> and the
    /// planning pass of <c>create</c> turn it off: the executor repeats the full check right before every POST (<see cref="Execution.DeploymentExecutor"/>), so a
    /// false "absent" here only shows a create in the preview, never makes a duplicate.
    /// </summary>
    public bool ConfirmKeyAbsence { get; init; } = true;

    /// <summary>The edge-kind handlers of the edge pass; part A by default, part B kinds plug in here.</summary>
    public EdgeKindRegistry? EdgeKinds { get; init; }

    /// <summary>
    /// The object id of the signed-in principal (from preflight), so the edge pass can warn when the template removes
    /// it from an object's owners; <c>null</c> lets the pass look it up with <c>GET /me</c> when needed.
    /// </summary>
    public string? SignedInPrincipalId { get; init; }

    /// <summary>
    /// Reads the templates and parameter files of linked deployments (<c>templateLink</c>, <c>parametersLink</c>); <c>null</c> reads local files
    /// only (an https link is then an error). The CLI passes one that downloads https links with the rules of its https parameter files.
    /// </summary>
    public Nested.LinkedTemplateReader? LinkedTemplates { get; init; }

    /// <summary>True when the token is an application (app-only) token (no <c>scp</c> claim; preflight reports principal type <c>application</c>): <c>ownerOf</c> on a user is then reported as <c>EdgeUnsupportedInAppMode</c> instead of read.</summary>
    public bool AppOnlyToken { get; init; }
}
