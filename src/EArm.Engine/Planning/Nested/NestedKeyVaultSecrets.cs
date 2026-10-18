using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Engine.KeyVault;
using EArm.Engine.Providers;

namespace EArm.Engine.Planning.Nested;

/// <summary>
/// The Key Vault references among the parameters of nested templates: an inline <c>reference</c> entry of the deployment's <c>parameters</c>
/// (<see cref="NestedParameterBinder"/>) or a <c>reference</c> in its linked parameter file (<c>parametersLink</c>), as ARM allows
/// (https://learn.microsoft.com/azure/azure-resource-manager/templates/key-vault-parameter). Every reference is recorded (permissions and the run's
/// Key Vault token need them). Offline (validate, permissions, the checks before sign-in) they stay unresolved: the parameter is an unknown, secure
/// value. When the engine plans after the preflight, they are resolved with <see cref="KeyVaultSecretResolver"/>, like those of the main template:
/// the value is always secure (tainted), whatever the declared type, and must satisfy the parameter's constraints.
/// </summary>
internal sealed class NestedKeyVaultSecrets
{
    private readonly KeyVaultSecretResolver? _resolver;
    private readonly bool _offline;
    private readonly CancellationToken _cancellationToken;
    private readonly List<(string Parameter, KeyVaultReference Reference)> _references = [];

    /// <param name="resolver">Reads the secrets; <c>null</c> when none is available (then a reference is an error, unless <paramref name="offline"/>).</param>
    /// <param name="offline">True for an offline expansion: references are recorded, never resolved.</param>
    /// <param name="cancellationToken">Cancels a read.</param>
    public NestedKeyVaultSecrets(KeyVaultSecretResolver? resolver, bool offline, CancellationToken cancellationToken)
    {
        _resolver = resolver;
        _offline = offline;
        _cancellationToken = cancellationToken;
    }

    /// <summary>Every reference met so far, by <c>&lt;deployment path&gt;/&lt;parameter&gt;</c>.</summary>
    public IReadOnlyList<(string Parameter, KeyVaultReference Reference)> References => _references;

    /// <summary>
    /// Records the pending references of <paramref name="document"/> (the template of <paramref name="deployment"/>) and, online, returns the document
    /// with their secrets bound. Returns <c>null</c> when a secret cannot be read (reported, pointing at the entry or the parameter file).
    /// </summary>
    public TemplateDocument? Resolve(ResourceInstance deployment, TemplateScope parent, TemplateDocument document, DeploymentSettings settings, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(deployment);
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(diagnostics);
        var pending = document.ParameterValues.Values.Where(b => b.PendingReference is not null).OrderBy(b => b.Name, StringComparer.OrdinalIgnoreCase).ToList();
        if (pending.Count == 0)
        {
            return document;
        }

        string prefix = string.Join('/', parent.DeploymentPath.Append(deployment.Id.Name));
        foreach (var bound in pending)
        {
            _references.Add(($"{prefix}/{bound.Name}", bound.PendingReference!));
        }

        if (_offline)
        {
            return document;
        }

        if (_resolver is null)
        {
            var first = pending[0];
            diagnostics.Add(parent.Error(EngineDiagnosticCodes.KeyVaultReferenceUnresolved, $"Parameter '{first.Name}' of nested deployment '{deployment.Id.Name}' is a Key Vault reference, and this planner has no Key Vault access to resolve it (the CLI resolves it after the sign-in).", PathOf(deployment, settings, first.Name)));
            return null;
        }

        try
        {
            // The planner is synchronous at this point (expansion), as for linked template downloads: the console host has no synchronization context.
            return _resolver.ResolveAsync(document, _cancellationToken).GetAwaiter().GetResult();
        }
        catch (KeyVaultResolutionException ex)
        {
            diagnostics.Add(parent.Error(ex.Code, $"Nested deployment '{deployment.Id.Name}': {ex.Message}", PathOf(deployment, settings, ex.ParameterName)));
            return null;
        }
    }

    /// <summary>Where a parameter's reference is declared: its entry of <c>properties.parameters</c>, or the <c>parametersLink</c> that holds it.</summary>
    private static JsonPath PathOf(ResourceInstance deployment, DeploymentSettings settings, string parameter)
    {
        var properties = deployment.Declaration.Path.Append("properties");
        if (settings.Parameters is { } map && map.FirstOrDefault(p => string.Equals(p.Key, parameter, StringComparison.OrdinalIgnoreCase)) is { Key: { } key })
        {
            return properties.Append("parameters").Append(key).Append("reference");
        }

        return settings.ParametersLocation is null ? properties : properties.Append("parametersLink");
    }
}
