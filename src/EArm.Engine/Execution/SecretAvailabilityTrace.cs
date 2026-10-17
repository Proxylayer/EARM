using System.Collections.Generic;
using EArm.Core;
using EArm.Engine.Providers;
using EArm.Expressions.Evaluation;

namespace EArm.Engine.Execution;

/// <summary>
/// Forwards to the plan's resolver and records the ids of published objects whose one-time secret is not
/// available in this run (<see cref="LiveSecrets.IsOneTimeSecretUnavailable"/>), per output.
/// </summary>
internal sealed class SecretAvailabilityTrace : IReferenceResolver
{
    private readonly IReferenceResolver _inner;
    private readonly List<ResourceIdentifier> _unavailable = [];

    public SecretAvailabilityTrace(IReferenceResolver inner)
    {
        _inner = inner;
    }

    public IReadOnlyList<ResourceIdentifier> UnavailableSecrets => _unavailable;

    /// <summary>One of <see cref="UnavailableSecrets"/> is a password credential's <c>secretText</c>.</summary>
    public bool UnavailableSecretTexts { get; private set; }

    /// <summary>One of <see cref="UnavailableSecrets"/> is a user's initial password.</summary>
    public bool UnavailableInitialPasswords { get; private set; }

    public void Reset()
    {
        _unavailable.Clear();
        UnavailableSecretTexts = false;
        UnavailableInitialPasswords = false;
    }

    public bool IsConditionFalse(ResourceIdentifier id)
    {
        return _inner.IsConditionFalse(id);
    }

    public ReferenceResult Resolve(ResourceIdentifier id, string? apiVersion)
    {
        var result = _inner.Resolve(id, apiVersion);
        if (result is ReferenceResult.Live live && LiveSecrets.IsOneTimeSecretUnavailable(live.Object) && !_unavailable.Contains(id))
        {
            _unavailable.Add(id);
            UnavailableSecretTexts |= LiveSecrets.IsSecretTextUnavailable(live.Object);
            UnavailableInitialPasswords |= LiveSecrets.IsInitialPasswordUnavailable(live.Object);
        }

        return result;
    }
}
