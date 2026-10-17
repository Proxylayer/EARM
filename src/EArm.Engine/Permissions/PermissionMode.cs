namespace EArm.Engine.Permissions;

/// <summary>Which credentials the permissions are computed for (<c>permissions --mode</c>).</summary>
public enum PermissionMode
{
    /// <summary>A service principal, workload identity or managed identity (<c>AZURE_*</c> environment): Microsoft Graph application permissions (app roles) on the service principal.</summary>
    App,

    /// <summary>An interactive sign-in (<c>earm auth login</c>): delegated scopes plus the Entra roles the user must hold.</summary>
    Delegated,
}
