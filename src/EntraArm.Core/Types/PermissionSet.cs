using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace EntraArm.Core.Types;

/// <summary>
/// The Graph permissions and Entra roles a type needs (plan section 3.4). <see cref="Raw"/> keeps the overlay
/// object verbatim so later work items can read conditional entries without a model change.
/// </summary>
public sealed record PermissionSet(
    IReadOnlyList<string> Application,
    IReadOnlyList<string> Delegated,
    IReadOnlyList<string> Roles,
    JsonObject? Raw)
{
    public static PermissionSet Empty { get; } = new([], [], [], null);
}
