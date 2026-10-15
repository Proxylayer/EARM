using System.Text.Json.Nodes;
using EArm.Core;

namespace EArm.Engine.Providers;

/// <summary>
/// The outcome of a create or update: the Graph id, the live object to publish for <c>reference()</c> (may contain
/// secret leaves, e.g. <c>secretText</c> of a new password credential, created through <c>SecretJson</c>), the
/// key the object was addressed by, and any extra data the state file should remember (<c>keyId</c>,
/// <c>endDateTime</c>...).
/// </summary>
public sealed record AppliedResource(ResourceIdentifier Id, string GraphId, JsonObject Live, ResourceKey Key, JsonObject? Extra = null);
