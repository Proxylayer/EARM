using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;

namespace EArm.Expressions.Functions;

/// <summary>
/// Group 6, the Entra-specific functions: <c>wellKnownApp(name)</c> (pure, from
/// <see cref="WellKnownIds"/>), <c>appRole(ref, value)</c> / <c>permissionScope(ref, value)</c> and
/// <c>roleTemplate(nameOrId)</c>. The lookups resolve, in order, from the live object of a template resource, then from
/// the tenant through <see cref="EvaluationContext.EntraLookup"/> (the tenant wins over the embedded Microsoft Graph
/// permission tables, which answer only offline or when the tenant cannot be asked); built-in roles come from the table
/// first. Offline and not in a table they yield deploy-time unknowns.
/// <para>
/// Phase rule: <c>appRole</c>, <c>permissionScope</c>, <c>roleTemplate</c> and
/// <c>roleDefinitions</c> may ask the tenant and are rejected in parameter defaults, variables and user-defined functions
/// (<c>FunctionNotAllowedHere</c>); <c>wellKnownApp</c> is pure and allowed anywhere. (<c>tenant()</c> is not restricted: its data comes from
/// the preflight, before any expression is evaluated.)
/// </para>
/// </summary>
internal static class EntraFunctions
{
    private const string Group = "entra";

    /// <summary>The functions that are only legal in resource properties and outputs.</summary>
    public static readonly IReadOnlyList<string> ResourceScopeOnly = ["appRole", "permissionScope", "roleTemplate", "roleDefinitions"];

    /// <summary>The resource types the first argument of <c>appRole()</c>/<c>permissionScope()</c> may name.</summary>
    public static readonly IReadOnlyList<string> ApplicationTypes = ["Microsoft.Entra/applications", "Microsoft.Entra/servicePrincipals"];

    public static void Register(FunctionRegistry registry)
    {
        registry.Register(new FunctionDescriptor { Name = "wellKnownApp", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = WellKnownApp });
        registry.Register(new FunctionDescriptor { Name = "appRole", Group = Group, MinArgs = 2, MaxArgs = 2, HandlesUnknown = true, Implementation = call => Permission(call, appRoles: true) });
        registry.Register(new FunctionDescriptor { Name = "permissionScope", Group = Group, MinArgs = 2, MaxArgs = 2, HandlesUnknown = true, Implementation = call => Permission(call, appRoles: false) });
        registry.Register(new FunctionDescriptor { Name = "roleTemplate", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = RoleTemplate });
        registry.Register(new FunctionDescriptor { Name = "roleDefinitions", Group = Group, MinArgs = 1, MaxArgs = 1, Implementation = RoleDefinitions });
    }

    /// <summary>True when <paramref name="function"/> is one of <see cref="ResourceScopeOnly"/>.</summary>
    public static bool IsResourceScopeOnly(string function)
    {
        return ResourceScopeOnly.Any(name => name.Equals(function, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The <c>FunctionNotAllowedHere</c> message for a resource-scope-only function in <paramref name="scope"/>, or <c>null</c> when the scope is fine.</summary>
    public static string? NotAllowedMessage(string function, EvaluationScope scope)
    {
        string? where = scope switch
        {
            EvaluationScope.ParameterDefault => "a parameter default value",
            EvaluationScope.Variable => "a variable",
            EvaluationScope.UserFunction => "a user-defined function",
            _ => null,
        };

        return where is null ? null : $"{function}() cannot be used in {where}: it needs the tenant, which is only consulted while resources are applied; use it in a resource property or an output.";
    }

    internal static void RequireResourceOrOutputScope(FunctionCall call)
    {
        if (NotAllowedMessage(call.Name, call.Context.Scope) is { } message)
        {
            throw new ExpressionEvaluationException(DiagnosticCodes.FunctionNotAllowedHere, message, call.Offset);
        }
    }

    private static JsonNode? WellKnownApp(FunctionCall call)
    {
        string name = call.String(0);
        if (WellKnownIds.TryGetAppId(name, out var appId))
        {
            return JsonValue.Create(appId);
        }

        throw call.Error($"'{call.Redact(0, name)}' is not a well-known application; known names: {string.Join(", ", WellKnownIds.AppNames)}.");
    }

    /// <summary><c>appRole(ref, value)</c> and <c>permissionScope(ref, value)</c>.</summary>
    private static JsonNode? Permission(FunctionCall call, bool appRoles)
    {
        RequireResourceOrOutputScope(call);
        if (call.IsUnknown(1))
        {
            return call.UnknownFromArguments();
        }

        string value = call.String(1);
        var target = call.Arg(0);
        if (UnknownJson.IsUnknown(target, out var unknownTarget))
        {
            // An appId computed at deploy time (reference('app').appId before the app exists): the nested reference()
            // carries the dependency; this result is just unknown.
            return call.Unknown(unknownTarget.Origins, unknownTarget.Tainted);
        }

        string text = call.String(0);
        string appId;
        JsonObject? live = null;
        if (Guid.TryParse(text, out _))
        {
            appId = text;
        }
        else if (TryResolveTemplateResource(call.Context, text, out var id, out var problem))
        {
            switch (call.Context.References.Resolve(id, null))
            {
                case ReferenceResult.Live resolved:
                    live = resolved.Object as JsonObject;
                    appId = live is not null && TryGetString(live, "appId", out var liveAppId)
                        ? liveAppId
                        : throw call.Failure($"the live object of '{call.Redact(0, id.Id)}' has no appId.");
                    break;
                case ReferenceResult.Placeholder placeholder:
                    // Source carries the dependency for the reference scanner; the symbol is the reference's appId.
                    return UnknownJson.Create($"{call.Name}({DeploymentFunctions.Symbol(call.Redact(0, placeholder.Id.Id), null)}.appId, {call.Render(1)})", UnknownOrigins.Reference, call.IsTainted, source: placeholder.Id);
                default:
                    throw call.Failure($"'{call.Redact(0, id.Id)}' could not be resolved.");
            }
        }
        else
        {
            throw call.Error($"argument 1 must be an application id (GUID) or the name or resource id of a {string.Join(" or ", ApplicationTypes)} resource of this template, got {call.Render(0)}{problem}; use wellKnownApp('Name') for a well-known resource application.");
        }

        // 1. The live object of a template resource declares its own roles and scopes (no replication lag, no request).
        if (live is not null && TryFindInObject(live, appRoles, value, out var fromLive))
        {
            return JsonValue.Create(fromLive);
        }

        // 2. The tenant wins over the embedded Microsoft Graph tables (the tables are the OFFLINE fallback: an id that is
        //    wrong in the table must never beat what the tenant answers).
        string? known = null;
        bool inTable = WellKnownIds.IsMicrosoftGraph(appId) && (appRoles ? WellKnownIds.TryGetGraphAppRoleId(value, out known) : WellKnownIds.TryGetGraphScopeId(value, out known));
        var lookup = call.Context.EntraLookup;
        EntraLookupResult result;
        try
        {
            result = Await(call, appRoles ? lookup.FindAppRoleIdAsync(appId, value) : lookup.FindPermissionScopeIdAsync(appId, value));
        }
        catch (ExpressionException) when (inTable)
        {
            // The tenant could not be asked (transport failure): the embedded table still answers for Microsoft Graph.
            return JsonValue.Create(known);
        }

        switch (result)
        {
            case EntraLookupResult.Found found:
                return JsonValue.Create(found.Id);
            case EntraLookupResult.Placeholder when inTable:
            case EntraLookupResult.NotFound when inTable:
                // Offline (no tenant), or the tenant has no such Microsoft Graph service principal to ask: the table.
                return JsonValue.Create(known);
            case EntraLookupResult.Placeholder:
                string shownApp = live is null ? call.Render(0) : "'" + call.Redact(0, appId) + "'";
                return UnknownJson.Create($"{call.Name}({shownApp}, {call.Render(1)})", UnknownOrigins.Deployment, call.IsTainted);
            case EntraLookupResult.NotFound notFound:
                throw call.Failure(call.IsTainted ? "no match was found for the secure arguments." : notFound.Reason);
            default:
                throw call.Failure($"the lookup returned an unexpected result ({result.GetType().Name}).");
        }
    }

    /// <summary><c>roleTemplate(nameOrId)</c>: GUID passthrough, built-in display names from the table, custom roles from the tenant.</summary>
    private static JsonNode? RoleTemplate(FunctionCall call)
    {
        RequireResourceOrOutputScope(call);
        return ResolveRoleDefinitionId(call, call.String(0));
    }

    /// <summary>
    /// <c>roleDefinitions(roleName)</c>, ARM's name-based role helper applied to Microsoft Entra directory roles: <c>{ id, roleDefinitionId, name,
    /// templateId }</c> of the directory role definition with that display name (built-in roles from the table, custom roles from the tenant;
    /// a GUID passes through). ARM's <c>id</c> is an Azure resource id; a directory role definition is addressed by its GUID, so <c>id</c>,
    /// <c>roleDefinitionId</c> and <c>templateId</c> all hold it (a directory role definition's id is its template id), which is what
    /// <c>Microsoft.Entra/roleAssignments</c> and the PIM types take as <c>roleDefinitionId</c>.
    /// </summary>
    private static JsonNode? RoleDefinitions(FunctionCall call)
    {
        RequireResourceOrOutputScope(call);
        string name = call.String(0);
        var id = ResolveRoleDefinitionId(call, name);
        if (UnknownJson.IsUnknown(id))
        {
            return call.Unknown(UnknownOrigins.Deployment, call.IsTainted);
        }

        return new JsonObject
        {
            ["id"] = JsonValues.Clone(id),
            ["roleDefinitionId"] = JsonValues.Clone(id),
            ["name"] = name,
            ["templateId"] = JsonValues.Clone(id),
        };
    }

    private static JsonNode? ResolveRoleDefinitionId(FunctionCall call, string text)
    {
        if (Guid.TryParse(text, out _))
        {
            return JsonValue.Create(text);
        }

        if (WellKnownIds.TryGetRoleTemplateId(text, out var templateId))
        {
            return JsonValue.Create(templateId);
        }

        var result = Await(call, call.Context.EntraLookup.FindRoleDefinitionIdAsync(text));
        switch (result)
        {
            case EntraLookupResult.Found found:
                return JsonValue.Create(found.Id);
            case EntraLookupResult.Placeholder:
                return UnknownJson.Create($"{call.Name}({call.Render(0)})", UnknownOrigins.Deployment, call.IsTainted);
            case EntraLookupResult.NotFound notFound:
                throw call.Failure(call.IsTainted
                    ? "no role definition matches the secure argument."
                    : $"{notFound.Reason} Built-in roles are matched by display name from the embedded table; a custom role declared in this template is referenced as reference('<name>').id, which also orders the deployment.");
            default:
                throw call.Failure($"the lookup returned an unexpected result ({result.GetType().Name}).");
        }
    }

    /// <summary>
    /// Resolves the <c>ref</c> argument of <c>appRole()</c>/<c>permissionScope()</c> with the <c>reference()</c>
    /// name rules (resource id, unique bare name, <c>type/name</c>) to a template resource of an
    /// <see cref="ApplicationTypes"/> type. False with a short reason suffix when nothing matches; throws
    /// <c>AmbiguousReference</c> when a bare name matches both an application and a service principal.
    /// </summary>
    internal static bool TryResolveTemplateResource(EvaluationContext context, string text, [NotNullWhen(true)] out ResourceIdentifier? id, out string problem)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(text);
        id = null;
        problem = string.Empty;

        ResourceIdentifier? candidate = null;
        if (text.StartsWith(ResourceIdentifier.ProvidersPrefix, StringComparison.OrdinalIgnoreCase))
        {
            if (!context.Catalog.TryParseResourceId(text, out candidate) || candidate is null)
            {
                problem = " (not a valid resource id)";
                return false;
            }
        }
        else if (DeploymentFunctions.TryParseTypeAndName(context.Catalog, text, out var typed))
        {
            candidate = typed;
        }
        else
        {
            var byName = context.Resources.FindByName(text).Where(entry => IsApplicationType(entry.Id.Type)).ToList();
            if (byName.Count > 1)
            {
                throw new ExpressionEvaluationException(DiagnosticCodes.AmbiguousReference, $"'{text}' names both an application and a service principal of this template; use resourceId() to pick one.");
            }

            if (byName.Count == 1)
            {
                id = byName[0].Id;
                return true;
            }

            return false;
        }

        if (!IsApplicationType(candidate.Type))
        {
            problem = $" (a {candidate.Type} resource)";
            return false;
        }

        if (!context.Resources.Contains(candidate))
        {
            problem = " (not a resource of this template)";
            return false;
        }

        id = candidate;
        return true;
    }

    private static bool IsApplicationType(string type)
    {
        return ApplicationTypes.Any(accepted => ResourceIdentifier.TypeEquals(accepted, type));
    }

    /// <summary>Finds a role or scope by <c>value</c> in a live application (<c>appRoles</c>, <c>api.oauth2PermissionScopes</c>) or service principal (<c>appRoles</c>, <c>oauth2PermissionScopes</c>) object.</summary>
    private static bool TryFindInObject(JsonObject live, bool appRoles, string value, out string id)
    {
        id = string.Empty;
        JsonArray? members = null;
        if (appRoles)
        {
            members = PropertyIgnoreCase(live, "appRoles") as JsonArray;
        }
        else
        {
            members = PropertyIgnoreCase(live, "oauth2PermissionScopes") as JsonArray
                ?? PropertyIgnoreCase(PropertyIgnoreCase(live, "api") as JsonObject, "oauth2PermissionScopes") as JsonArray;
        }

        if (members is null)
        {
            return false;
        }

        foreach (var member in members.OfType<JsonObject>())
        {
            if (TryGetString(member, "value", out var memberValue) && memberValue.Equals(value, StringComparison.OrdinalIgnoreCase) && TryGetString(member, "id", out id))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonNode? PropertyIgnoreCase(JsonObject? obj, string name)
    {
        if (obj is null)
        {
            return null;
        }

        if (obj.TryGetPropertyValue(name, out var exact))
        {
            return exact;
        }

        foreach (var property in obj)
        {
            if (property.Key.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }

        return null;
    }

    private static bool TryGetString(JsonObject obj, string name, out string value)
    {
        value = string.Empty;
        return JsonValues.TryGetString(PropertyIgnoreCase(obj, name), out value) && value.Length > 0;
    }

    /// <summary>
    /// Awaits a lookup on the calling thread (see <see cref="IEntraLookup"/>). A transport failure becomes
    /// <c>ExpressionEvaluationFailed</c>; its message is withheld for a tainted call because a Graph error may echo
    /// the request.
    /// </summary>
    private static EntraLookupResult Await(FunctionCall call, Task<EntraLookupResult> task)
    {
        try
        {
            return task.GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not ExpressionException and not OperationCanceledException)
        {
            throw call.Failure(call.IsTainted ? "the tenant lookup failed." : $"the tenant lookup failed: {ex.Message}", ex);
        }
    }
}
