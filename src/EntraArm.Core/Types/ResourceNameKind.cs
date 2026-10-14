namespace EntraArm.Core.Types;

/// <summary>How a resource's ARM-style <c>name</c> maps to its Graph identity (plan section 1.3).</summary>
public enum ResourceNameKind
{
    /// <summary>Graph <c>uniqueName</c> alternate key (applications, groups).</summary>
    UniqueName,

    /// <summary>Graph <c>userPrincipalName</c> (users).</summary>
    UserPrincipalName,

    /// <summary>Graph <c>appId</c> alternate key (servicePrincipals); must be a GUID when literal.</summary>
    AppId,

    /// <summary>A child's own name under its parent (federatedIdentityCredentials <c>name</c>, passwordCredentials <c>displayName</c>).</summary>
    ChildName,

    /// <summary>Looked up by <c>displayName</c>; the Graph id is pinned in state.</summary>
    DisplayName,

    /// <summary>A deterministic GUID chosen by the author (<c>guid(...)</c>); the object is found by composite properties.</summary>
    Guid,

    /// <summary>The Graph <c>id</c> itself (authenticationContextClassReferences <c>c1</c>..<c>c99</c>).</summary>
    Id,
}

public static class ResourceNameKinds
{
    public static bool TryParse(string? text, out ResourceNameKind kind)
    {
        kind = ResourceNameKind.DisplayName;
        switch (text?.Trim().ToLowerInvariant())
        {
            case "uniquename":
                kind = ResourceNameKind.UniqueName;
                return true;
            case "userprincipalname":
                kind = ResourceNameKind.UserPrincipalName;
                return true;
            case "appid":
                kind = ResourceNameKind.AppId;
                return true;
            case "childname":
                kind = ResourceNameKind.ChildName;
                return true;
            case "displayname":
                kind = ResourceNameKind.DisplayName;
                return true;
            case "guid":
                kind = ResourceNameKind.Guid;
                return true;
            case "id":
                kind = ResourceNameKind.Id;
                return true;
            default:
                return false;
        }
    }
}
