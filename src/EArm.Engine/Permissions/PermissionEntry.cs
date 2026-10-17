using System.Collections.Generic;

namespace EArm.Engine.Permissions;

/// <summary>One required permission and the resources (and reasons) that need it.</summary>
public sealed record PermissionEntry(string Name, PermissionKind Kind, IReadOnlyList<string> Resources, IReadOnlyList<string> Reasons);
