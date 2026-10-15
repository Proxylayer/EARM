using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core;

namespace EArm.Engine.Providers;

/// <summary>
/// The outcome of a create or update: the Graph id, the live object to publish for <c>reference()</c> (may contain
/// secret leaves, e.g. <c>secretText</c> of a new password credential, created through <c>SecretJson</c>), the
/// key the object was addressed by, and any extra data of the write the run needs (<c>keyId</c> of a new secret,
/// <c>generatedPassword</c>...; never persisted). <see cref="Warnings"/> are non-fatal observations of the apply (never persisted).
/// </summary>
public sealed record AppliedResource(ResourceIdentifier Id, string GraphId, JsonObject Live, ResourceKey Key, JsonObject? Extra = null)
{
    /// <summary>Non-fatal observations made while applying (for example a skipped soft-delete probe); the executor reports them as warnings.</summary>
    public IReadOnlyList<ProviderWarning> Warnings { get; init; } = [];
}
