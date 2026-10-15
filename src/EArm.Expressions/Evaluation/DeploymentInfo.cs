using System.Text.Json.Nodes;
using EArm.Expressions.Values;

namespace EArm.Expressions.Evaluation;

/// <summary>
/// What <c>deployment()</c>, <c>tenant()</c> and <c>environment()</c> return. <see cref="Tenant"/> and
/// <see cref="Environment"/> are <c>null</c> offline, in which case the functions yield unknown values.
/// </summary>
public sealed record DeploymentInfo(string Name, string? TemplateHash, string? TenantId, string Mode)
{
    /// <summary>The offline context used by <c>deployment validate</c>.</summary>
    public static DeploymentInfo Offline { get; } = new("validate", null, null, "Incremental");

    /// <summary><c>{ tenantId, defaultDomain, displayName }</c> from <c>/organization</c>; <c>null</c> offline.</summary>
    public JsonObject? Tenant { get; init; }

    /// <summary><c>{ name, graph, authentication: { loginEndpoint } }</c> from <c>az cloud show</c>; <c>null</c> offline.</summary>
    public JsonObject? Environment { get; init; }

    /// <summary>The object <c>deployment()</c> returns.</summary>
    public JsonObject ToDeploymentObject()
    {
        return new JsonObject
        {
            ["name"] = Name,
            ["templateHash"] = TemplateHash is null ? null : JsonValue.Create(TemplateHash),
            ["tenantId"] = TenantId is null ? null : JsonValue.Create(TenantId),
            ["mode"] = Mode,
        };
    }

    public JsonNode TenantValue()
    {
        return Tenant is null ? UnknownJson.Create("tenant()", UnknownOrigins.Deployment) : JsonValues.Clone(Tenant)!;
    }

    public JsonNode EnvironmentValue()
    {
        return Environment is null ? UnknownJson.Create("environment()", UnknownOrigins.Deployment) : JsonValues.Clone(Environment)!;
    }
}
