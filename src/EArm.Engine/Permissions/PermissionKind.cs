namespace EArm.Engine.Permissions;

/// <summary>What kind of grant a <see cref="PermissionEntry"/> is.</summary>
public enum PermissionKind
{
    GraphApplication,
    GraphDelegated,
    AzureRbac,
}
