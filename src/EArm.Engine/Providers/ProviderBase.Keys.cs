using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Types;
using EArm.Graph;

namespace EArm.Engine.Providers;

public abstract partial class ProviderBase
{
    // ---- key ----

    public virtual ResourceKey ResolveKey(ResourceInstance desired)
    {
        ArgumentNullException.ThrowIfNull(desired);
        if (Definition.NameKind == ResourceNameKind.Guid)
        {
            return CompositeKey(desired);
        }

        var key = KeyForIdentifier(Definition, desired.Id, desired.IsSymbolic, desired.Parent?.IsSymbolic ?? false) with { Api = desired.Api };
        if (desired.ParentGraphId is not null && key.ParentKey is not null)
        {
            key = key with { ParentGraphId = desired.ParentGraphId };
        }

        return key;
    }

    /// <summary>
    /// <c>reference(resourceId(...), apiVersion)</c>: reads an object that is not declared in the template by the key
    /// its resource id implies. GUID-named (composite-key) types cannot be addressed this way.
    /// </summary>
    public virtual Task<ExistingResource?> ReadExistingAsync(ResourceIdentifier id, string apiVersion, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(id);
        ArgumentNullException.ThrowIfNull(apiVersion);
        ArgumentNullException.ThrowIfNull(graph);

        if (Definition.NameKind == ResourceNameKind.Guid)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"'{id.Id}' is a {TypeName}, whose name is a deterministic GUID; such objects cannot be looked up as existing resources by name. Declare the assignment in the template instead.", id);
        }

        if (!Definition.TryGetGraphApi(apiVersion, out var api))
        {
            throw new ProviderException(DiagnosticCodes.UnknownApiVersion, Definition.DescribeUnknownApiVersion(apiVersion), id);
        }

        var key = KeyForIdentifier(Definition, id) with { Api = GraphApiMapping.ToAz(api) };
        return ReadAsync(key, graph, cancellationToken);
    }

    /// <summary>
    /// The key an identifier implies for a name-keyed type: the last name segment under <c>nameMapsTo</c>, plus the
    /// parent's key for child types (resolved through the catalog).
    /// </summary>
    public ResourceKey KeyForIdentifier(ResourceTypeDefinition definition, ResourceIdentifier id, bool symbolic = false, bool parentSymbolic = false)
    {
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(id);

        string property = definition.NameMapsTo ?? throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"Type '{definition.Type}' has no name-mapped property; its key is composite.", id);
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { [property] = id.NameLeaf };
        var unresolved = symbolic ? new List<string> { property } : [];

        ResourceKey? parentKey = null;
        if (definition.IsChildType && id.Parent is { } parentId)
        {
            var parentDefinition = _catalog.Find(definition.ParentType)
                ?? throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"Parent type '{definition.ParentType}' of '{definition.Type}' is not in the catalog.", id);
            parentKey = KeyForIdentifier(parentDefinition, parentId, symbolic || parentSymbolic);
        }
        else if (definition.IsChildType)
        {
            // A symbolic child name is a single segment; the parent is unknown until the name resolves.
            unresolved.Add("parent");
        }

        return new ResourceKey { Type = definition.Type, Values = values, ParentKey = parentKey, UnresolvedProperties = unresolved };
    }

    private ResourceKey CompositeKey(ResourceInstance desired)
    {
        var properties = desired.Properties
            ?? throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before its composite key can be resolved.");

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unresolved = new List<string>();
        foreach (var property in Definition.KeyProperties)
        {
            if (!JsonObjects.TryGetIgnoreCase(properties, property, out var node) || JsonObjects.IsAbsent(node))
            {
                // Absent composite member (oauth2PermissionGrants.principalId for AllPrincipals); a missing required
                // member is reported by Plan() through requiredOnCreate.
                continue;
            }

            if (JsonObjects.GetString(properties, property) is { } text)
            {
                values[property] = text;
                continue;
            }

            // A deploy-time-unknown or secret value: the key cannot be looked up yet.
            unresolved.Add(property);
        }

        return new ResourceKey { Type = TypeName, Api = desired.Api, Values = values, UnresolvedProperties = unresolved };
    }
}
