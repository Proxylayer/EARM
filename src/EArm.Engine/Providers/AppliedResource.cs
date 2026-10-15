using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core;

namespace EArm.Engine.Providers;

/// <summary>
/// The outcome of a create or update: the Graph id, the live object to publish for <c>reference()</c> (may contain
/// secret leaves, e.g. <c>secretText</c> of a new password credential, created through <c>SecretJson</c>), the
/// key the object was addressed by, and any extra data the state file should remember (<c>keyId</c>,
/// <c>endDateTime</c>...). <see cref="Warnings"/> are non-fatal observations of the apply (never persisted).
/// </summary>
public sealed record AppliedResource(ResourceIdentifier Id, string GraphId, JsonObject Live, ResourceKey Key, JsonObject? Extra = null)
{
    /// <summary>Non-fatal observations made while applying (for example a skipped soft-delete probe); the executor reports them as warnings.</summary>
    public IReadOnlyList<ProviderWarning> Warnings { get; init; } = [];

    /// <summary>
    /// Set by the executor, never by a provider: whether this run created the object or took over one that existed before. The state
    /// sink records it (<c>null</c> keeps the previous entry's provenance).
    /// </summary>
    public EArm.Engine.State.StateProvenance? Provenance { get; init; }
}
