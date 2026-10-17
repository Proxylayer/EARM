using System;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>Soft delete in <see cref="FakeDirectory"/>: <c>/directory/deletedItems</c>, restore, keys held by deleted objects and the 30-day purge.</summary>
public sealed partial class FakeDirectory
{
    /// <summary>
    /// Seeds a soft-deleted object that originally lived in <paramref name="collectionPath"/> (<c>/applications</c>,
    /// <c>/groups</c>, <c>/users</c>, <c>/servicePrincipals</c>, <c>/directory/administrativeUnits</c>): it lands in
    /// <c>/directory/deletedItems</c> with <c>@odata.type</c> and <c>deletedDateTime</c>, keeps its keys
    /// (<c>uniqueName</c>...) and can be listed, restored or purged like in Graph.
    /// </summary>
    public JsonObject AddDeleted(string collectionPath, JsonObject entity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(collectionPath);
        ArgumentNullException.ThrowIfNull(entity);
        lock (_gate)
        {
            if (!ODataTypeByCollection.TryGetValue(Normalize(collectionPath), out var odataType))
            {
                throw new ArgumentException($"Objects of '{collectionPath}' are not soft-deletable in the fake.", nameof(collectionPath));
            }

            var deleted = (JsonObject)entity.DeepClone();
            if (GetString(deleted, "id") is null)
            {
                deleted["id"] = Guid.NewGuid().ToString("D");
            }

            deleted["@odata.type"] = odataType;
            deleted["deletedDateTime"] ??= DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Collection(DeletedItemsPath).Add(deleted);
            return (JsonObject)deleted.DeepClone();
        }
    }

    /// <summary>
    /// Moves a live object to <c>/directory/deletedItems</c> (what Graph does on DELETE for applications, groups, users,
    /// service principals and administrative units) and drops the references to it. <c>DELETE</c> through the client
    /// soft-deletes too (the types <see cref="IsSoftDeletable"/> names, through <see cref="SoftDeleteWithCascade"/>); call this
    /// to stage the "key held by a soft-deleted object" scenario without a request.
    /// </summary>
    public JsonObject SoftDelete(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            var found = LocateById(id, out var collectionPath)
                ?? throw new InvalidOperationException($"No object with id '{id}'.");
            if (!ODataTypeByCollection.TryGetValue(collectionPath, out var odataType))
            {
                throw new InvalidOperationException($"Objects of '{collectionPath}' are not soft-deletable in the fake.");
            }

            Collection(collectionPath).Remove(found);
            RemoveReferencesTo(id);
            var deleted = (JsonObject)found.DeepClone();
            deleted["@odata.type"] = odataType;
            deleted["deletedDateTime"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);
            Collection(DeletedItemsPath).Add(deleted);
            return (JsonObject)deleted.DeepClone();
        }
    }

    /// <summary><c>POST /directory/deletedItems/{id}/restore</c>: the object goes back to the collection its <c>@odata.type</c> names.</summary>
    private JsonObject Restore(JsonObject entity, string collectionPath, RetryHint hint)
    {
        if (!collectionPath.Equals(DeletedItemsPath, StringComparison.OrdinalIgnoreCase))
        {
            return (JsonObject)entity.DeepClone();
        }

        string? odataType = GetString(entity, "@odata.type");
        if (odataType is null || !CollectionByODataType.TryGetValue(odataType, out var target))
        {
            throw BadRequest("The deleted item has no restorable @odata.type.", hint);
        }

        var restored = (JsonObject)entity.DeepClone();
        restored.Remove("deletedDateTime");
        restored.Remove("@odata.type");

        // The keys are free again only if nobody took them while the object sat in the recycle bin.
        foreach (string key in new[] { "uniqueName", "userPrincipalName", "appId" })
        {
            if (GetString(restored, key) is { } value && Collection(target).Any(o => ValueEquals(GetString(o, key), value)))
            {
                throw new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", $"Another object with the same value for property {key} already exists.", hint);
            }
        }

        Collection(collectionPath).Remove(entity);
        Collection(target).Add(restored);
        return (JsonObject)restored.DeepClone();
    }

    // ---- soft delete ----

    /// <summary>
    /// Graph soft-deletes applications, service principals, users, administrative units, Microsoft 365 groups AND security groups (Learn group-delete:
    /// "both Microsoft 365 and security groups are moved to a temporary container and can be restored within 30 days ... This doesn't apply to Distribution
    /// groups which are permanently deleted immediately"); distribution lists and every other type are deleted permanently.
    /// </summary>
    private static bool IsSoftDeletable(string collectionPath, JsonObject entity)
    {
        if (!ODataTypeByCollection.ContainsKey(collectionPath))
        {
            return false;
        }

        return !collectionPath.Equals("/groups", StringComparison.OrdinalIgnoreCase) || !IsDistributionList(entity);
    }

    /// <summary>Mail-enabled, not security-enabled, no <c>Unified</c>: the one group kind that is deleted immediately.</summary>
    private static bool IsDistributionList(JsonObject entity)
    {
        return !IsUnified(entity) && GetBool(entity, "mailEnabled") == true && GetBool(entity, "securityEnabled") != true;
    }

    private static bool? GetBool(JsonObject entity, string property)
    {
        foreach (var pair in entity)
        {
            if (pair.Key.Equals(property, StringComparison.OrdinalIgnoreCase) && pair.Value is JsonValue value)
            {
                return value.GetValueKind() switch
                {
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null,
                };
            }
        }

        return null;
    }
    private static bool IsUnified(JsonObject entity)
    {
        return entity["groupTypes"] is JsonArray types && types.Any(t => t is JsonValue v && v.TryGetValue<string>(out var text) && text.Equals("Unified", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Deleting an application also soft-deletes its service principal (Graph cascades the home principal).</summary>
    private void SoftDeleteWithCascade(JsonObject entity, string collectionPath)
    {
        string id = GetString(entity, "id")!;
        if (collectionPath.Equals("/applications", StringComparison.OrdinalIgnoreCase) && GetString(entity, "appId") is { } appId)
        {
            foreach (var principal in Collection("/servicePrincipals").Where(p => ValueEquals(GetString(p, "appId"), appId)).ToList())
            {
                SoftDelete(GetString(principal, "id")!);
            }
        }

        SoftDelete(id);
    }

    /// <summary>A key held by a soft-deleted object of the same type conflicts like a live one until the object is restored or purged.</summary>
    private void RejectDeletedDuplicate(string collectionPath, JsonObject entity, string property, RetryHint hint)
    {
        string? value = GetString(entity, property);
        if (value is null || !ODataTypeByCollection.TryGetValue(collectionPath, out var odataType))
        {
            return;
        }

        if (Collection(DeletedItemsPath).Any(d => ValueEquals(GetString(d, "@odata.type"), odataType) && ValueEquals(GetString(d, property), value)))
        {
            throw new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", $"Another object with the same value for property {property} already exists.", hint);
        }
    }

    private void PurgeExpiredDeletedItems()
    {
        if (!_collections.TryGetValue(DeletedItemsPath, out var deleted) || deleted.Count == 0)
        {
            return;
        }

        var cutoff = Clock.GetUtcNow() - DeletedRetention;
        deleted.RemoveAll(d => GetString(d, "deletedDateTime") is { } stamp
            && DateTimeOffset.TryParse(stamp, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var when)
            && when < cutoff);
    }
}
