using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace EArm.Core.Types;

/// <summary>
/// The Graph application permissions and Entra roles a type needs (the delegated scopes come from the providers' conditional rules). <see cref="Raw"/> keeps the overlay
/// object verbatim so conditional entries can be read without a model change.
/// </summary>
public sealed record PermissionSet(
    IReadOnlyList<string> Application,
    IReadOnlyList<string> Roles,
    JsonObject? Raw)
{
    public static PermissionSet Empty { get; } = new([], [], null);
}
