using System.Text.Json.Nodes;

namespace EntraArm.Engine.Providers;

/// <summary>
/// A live Graph object found by <see cref="IResourceProvider.ReadAsync"/>.
/// </summary>
/// <param name="GraphId">The object's Graph <c>id</c>.</param>
/// <param name="Properties">The object with server-managed paths removed (<c>stripOnRead</c>): the view the diff runs against.</param>
/// <param name="Raw">The object as Graph returned it (<c>id</c>, <c>appId</c>, ...): what <c>reference()</c> hands to expressions.</param>
public sealed record ExistingResource(string GraphId, JsonObject Properties, JsonObject Raw);
