using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace EArm.Expressions.Evaluation;

/// <summary>
/// Resolves the tenant-dependent ids behind <c>appRole()</c>, <c>permissionScope()</c> and <c>roleTemplate()</c>
/// (plan section 1.5). The embedded <see cref="EArm.Core.WellKnownIds"/> tables are consulted first by the
/// functions themselves; this interface answers everything else: app roles and scopes of any resource application by
/// appId, and custom (or unlisted built-in) role definitions by display name. Implemented live by the engine over
/// <c>IGraphClient</c>; <see cref="PlaceholderEntraLookup"/> is the offline implementation (validation, what-if
/// without a session) and <see cref="InMemoryEntraLookup"/> a dictionary-backed one for tests.
/// <para>
/// The evaluator is synchronous; implementations are awaited on the calling thread-pool thread, exactly like
/// <see cref="IReferenceResolver"/> lookups (the console host has no synchronization context, so this cannot deadlock).
/// </para>
/// </summary>
public interface IEntraLookup
{
    /// <summary>The id of the app role whose <c>value</c> is <paramref name="value"/> on the resource application <paramref name="appId"/>.</summary>
    Task<EntraLookupResult> FindAppRoleIdAsync(string appId, string value);

    /// <summary>The id of the delegated permission scope whose <c>value</c> is <paramref name="value"/> on the resource application <paramref name="appId"/>.</summary>
    Task<EntraLookupResult> FindPermissionScopeIdAsync(string appId, string value);

    /// <summary>The id of the directory role definition (built-in or custom) whose <c>displayName</c> is <paramref name="displayName"/>.</summary>
    Task<EntraLookupResult> FindRoleDefinitionIdAsync(string displayName);
}

/// <summary>The outcome of an <see cref="IEntraLookup"/> call.</summary>
public abstract record EntraLookupResult
{
    private EntraLookupResult()
    {
    }

    /// <summary>The id was resolved.</summary>
    public sealed record Found(string Id) : EntraLookupResult;

    /// <summary>
    /// The tenant has no match. <see cref="Reason"/> may echo the arguments, so the function shows it only when the
    /// call is not tainted.
    /// </summary>
    public sealed record NotFound(string Reason) : EntraLookupResult;

    /// <summary>No tenant is available (offline): the function yields a deploy-time unknown.</summary>
    public sealed record Placeholder : EntraLookupResult
    {
        public static Placeholder Instance { get; } = new();
    }
}

/// <summary>Offline lookup: every call is a <see cref="EntraLookupResult.Placeholder"/>.</summary>
public sealed class PlaceholderEntraLookup : IEntraLookup
{
    public static PlaceholderEntraLookup Instance { get; } = new();

    public Task<EntraLookupResult> FindAppRoleIdAsync(string appId, string value)
    {
        return Task.FromResult<EntraLookupResult>(EntraLookupResult.Placeholder.Instance);
    }

    public Task<EntraLookupResult> FindPermissionScopeIdAsync(string appId, string value)
    {
        return Task.FromResult<EntraLookupResult>(EntraLookupResult.Placeholder.Instance);
    }

    public Task<EntraLookupResult> FindRoleDefinitionIdAsync(string displayName)
    {
        return Task.FromResult<EntraLookupResult>(EntraLookupResult.Placeholder.Instance);
    }
}

/// <summary>
/// A lookup backed by dictionaries, for tests and harnesses: register the app roles and scopes of an application
/// and the role definitions of the tenant; anything else is <see cref="EntraLookupResult.NotFound"/>.
/// </summary>
public sealed class InMemoryEntraLookup : IEntraLookup
{
    private readonly Dictionary<string, Dictionary<string, string>> _appRoles = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Dictionary<string, string>> _scopes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _roles = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();

    public InMemoryEntraLookup AddAppRole(string appId, string value, string id)
    {
        Add(_appRoles, appId, value, id);
        return this;
    }

    public InMemoryEntraLookup AddPermissionScope(string appId, string value, string id)
    {
        Add(_scopes, appId, value, id);
        return this;
    }

    public InMemoryEntraLookup AddRoleDefinition(string displayName, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            _roles[displayName] = id;
        }

        return this;
    }

    public Task<EntraLookupResult> FindAppRoleIdAsync(string appId, string value)
    {
        return Task.FromResult(Find(_appRoles, appId, value, "app role"));
    }

    public Task<EntraLookupResult> FindPermissionScopeIdAsync(string appId, string value)
    {
        return Task.FromResult(Find(_scopes, appId, value, "permission scope"));
    }

    public Task<EntraLookupResult> FindRoleDefinitionIdAsync(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        lock (_gate)
        {
            return Task.FromResult<EntraLookupResult>(_roles.TryGetValue(displayName, out var id)
                ? new EntraLookupResult.Found(id)
                : new EntraLookupResult.NotFound($"no role definition named '{displayName}' exists in the tenant."));
        }
    }

    private void Add(Dictionary<string, Dictionary<string, string>> table, string appId, string value, string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(appId);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        lock (_gate)
        {
            if (!table.TryGetValue(appId, out var members))
            {
                members = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                table[appId] = members;
            }

            members[value] = id;
        }
    }

    private EntraLookupResult Find(Dictionary<string, Dictionary<string, string>> table, string appId, string value, string kind)
    {
        ArgumentNullException.ThrowIfNull(appId);
        ArgumentNullException.ThrowIfNull(value);
        lock (_gate)
        {
            bool known = _appRoles.ContainsKey(appId) || _scopes.ContainsKey(appId);
            if (!known)
            {
                return new EntraLookupResult.NotFound($"no service principal or application with appId '{appId}' exists in the tenant.");
            }

            if (table.TryGetValue(appId, out var members) && members.TryGetValue(value, out var id))
            {
                return new EntraLookupResult.Found(id);
            }

            return new EntraLookupResult.NotFound($"the service principal for appId '{appId}' declares no {kind} '{value}'.");
        }
    }
}
