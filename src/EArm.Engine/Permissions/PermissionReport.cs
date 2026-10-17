using System.Collections.Generic;

namespace EArm.Engine.Permissions;

/// <summary>The computed manifest of <c>earm permissions</c>.</summary>
public sealed record PermissionReport(
    string Template,
    PermissionMode Mode,
    int ResourceCount,
    IReadOnlyList<PermissionEntry> Permissions,
    IReadOnlyList<RoleRequirement> Roles,
    IReadOnlyList<string> Notes);
