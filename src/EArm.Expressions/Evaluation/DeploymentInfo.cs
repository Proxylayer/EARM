using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Expressions.Values;

namespace EArm.Expressions.Evaluation;

/// <summary>
/// What <c>deployment()</c>, <c>tenant()</c>, <c>deployer()</c> and <c>environment()</c> return. <see cref="Tenant"/>, <see cref="Deployer"/>,
/// <see cref="Environment"/> and <see cref="Parameters"/> are <c>null</c> offline, in which case the functions (or that part of
/// <c>deployment()</c>) yield unknown values.
/// </summary>
public sealed record DeploymentInfo(string Name, string? TemplateHash, string? TenantId, string Mode)
{
    /// <summary>The offline context used by <c>deployment validate</c>.</summary>
    public static DeploymentInfo Offline { get; } = new("validate", null, null, "Incremental");

    /// <summary><c>{ countryCode, displayName, id, tenantId, defaultDomain }</c> from <c>/organization</c>; <c>null</c> offline.</summary>
    public JsonObject? Tenant { get; init; }

    /// <summary><c>{ objectId, tenantId, userPrincipalName }</c> of the signed-in principal from the preflight; <c>null</c> offline.</summary>
    public JsonObject? Deployer { get; init; }

    /// <summary>What <c>environment()</c> returns for the selected cloud (<c>EARM_CLOUD</c>); <c>null</c> offline.</summary>
    public JsonObject? Environment { get; init; }

    /// <summary>
    /// <c>deployment().properties.parameters</c>: the supplied, non-secure parameter values as <c>{ name: { value } }</c>
    /// (<see cref="SuppliedParameters"/>); <c>null</c> keeps it unknown.
    /// </summary>
    public JsonObject? Parameters { get; init; }

    /// <summary>
    /// The object <c>deployment()</c> returns: ARM's <c>{ name, properties: { templateHash, parameters, mode } }</c> (no <c>templateLink</c>:
    /// EARM deploys local files only; no <c>template</c> or <c>provisioningState</c>), plus the earlier flat keys <c>templateHash</c>,
    /// <c>tenantId</c> and <c>mode</c> as aliases so templates written against them keep working.
    /// </summary>
    public JsonObject ToDeploymentObject()
    {
        return new JsonObject
        {
            ["name"] = Name,
            ["properties"] = new JsonObject
            {
                ["templateHash"] = Text(TemplateHash),
                ["parameters"] = Parameters is null ? UnknownJson.Create("deployment().properties.parameters", UnknownOrigins.Deployment) : JsonValues.Clone(Parameters),
                ["mode"] = Mode,
            },
            ["templateHash"] = Text(TemplateHash),
            ["tenantId"] = Text(TenantId),
            ["mode"] = Mode,
        };
    }

    public JsonNode TenantValue()
    {
        return Tenant is null ? UnknownJson.Create("tenant()", UnknownOrigins.Deployment) : JsonValues.Clone(Tenant)!;
    }

    public JsonNode DeployerValue()
    {
        return Deployer is null ? UnknownJson.Create("deployer()", UnknownOrigins.Deployment) : JsonValues.Clone(Deployer)!;
    }

    public JsonNode EnvironmentValue()
    {
        return Environment is null ? UnknownJson.Create("environment()", UnknownOrigins.Deployment) : JsonValues.Clone(Environment)!;
    }

    /// <summary>
    /// <c>{ name: { value } }</c> for every parameter supplied by a parameter file or <c>-p name=value</c>, as ARM lists the deployment's parameters.
    /// Secure parameters, Key Vault references, values that failed conversion and values holding a secure leaf are left out, so nothing secret can
    /// reach <c>deployment()</c>; defaults are not listed (ARM lists what the deployment request carried).
    /// </summary>
    public static JsonObject SuppliedParameters(IReadOnlyDictionary<string, BoundParameter> bound)
    {
        ArgumentNullException.ThrowIfNull(bound);
        var result = new JsonObject();
        foreach (var parameter in bound.Values.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            if (parameter.Source == ParameterValueSource.Default || parameter.IsSecure || parameter.IsMissing || parameter.ConversionFailed
                || parameter.PendingReference is not null || UnknownJson.ContainsTaint(parameter.Value))
            {
                continue;
            }

            result[parameter.Name] = new JsonObject { ["value"] = JsonValues.Clone(parameter.Value) };
        }

        return result;
    }

    private static JsonValue? Text(string? value)
    {
        return value is null ? null : JsonValue.Create(value);
    }
}
