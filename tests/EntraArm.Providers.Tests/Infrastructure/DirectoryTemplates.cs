using System;
using System.Linq;
using System.Text.Json.Nodes;

namespace EntraArm.Providers.Tests.Infrastructure;

/// <summary>The built-in directory roles (id = template id) seeded into the fake the way Graph lists them.</summary>
internal static class BuiltInRoles
{
    public const string GlobalReader = "f2ef992c-3afb-46b9-b7cf-a126ee74c451";
    public const string GlobalAdministrator = "62e90394-69f5-4237-9190-012177145e10";
    public const string SecurityReader = "5d6b6bb7-de71-4623-b4af-96380a352509";
    public const string UserAdministrator = "fe930be7-5e62-47db-91af-98c3a49a38b1";
    public const string DirectoryReaders = "88d8e3e3-8f55-4a1e-953a-9b9898b8876b";

    public const string DefinitionsPath = "/roleManagement/directory/roleDefinitions";
    public const string AssignmentsPath = "/roleManagement/directory/roleAssignments";
    public const string UnitsPath = "/directory/administrativeUnits";

    public static void Seed(ProviderHost host)
    {
        Add(host, GlobalReader, "Global Reader");
        Add(host, GlobalAdministrator, "Global Administrator");
        Add(host, SecurityReader, "Security Reader");
        Add(host, UserAdministrator, "User Administrator");
        Add(host, DirectoryReaders, "Directory Readers");
    }

    private static void Add(ProviderHost host, string id, string displayName)
    {
        host.Directory.Add(DefinitionsPath, new JsonObject { ["id"] = id, ["templateId"] = id, ["displayName"] = displayName, ["isBuiltIn"] = true, ["isEnabled"] = true, ["rolePermissions"] = new JsonArray() });
    }
}

/// <summary>Inline template builders for the WI-15 resources (administrative units, role definitions, role assignments, directoryRoles).</summary>
internal static class DirectoryTemplates
{
    public const string AliceId = "/providers/Microsoft.Entra/users/alice@contoso.com";
    public const string GroupId = "/providers/Microsoft.Entra/groups/test-admins";
    public const string AuId = "/providers/Microsoft.Entra/administrativeUnits/Orders AU";
    public const string RoleDefId = "/providers/Microsoft.Entra/roleDefinitions/Orders Support";
    public const string AliceRef = "\"principalId\": \"[reference('alice@contoso.com').id]\"";

    public static string Join(params string[] resources)
    {
        return string.Join(",", resources.Where(r => r.Length > 0));
    }

    public static string Alice(string? edges = null)
    {
        return "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"alice@contoso.com\", \"properties\": { \"displayName\": \"Alice\"" + (edges is null ? string.Empty : ", " + edges) + " } }";
    }

    public static string Group(string? edges = null, bool assignable = true, string? dependsOn = null)
    {
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"test-admins\", \"properties\": { \"displayName\": \"Test Admins\", \"securityEnabled\": true, \"mailEnabled\": false, \"isAssignableToRole\": "
            + (assignable ? "true" : "\"[equals(1, 2)]\"") + (edges is null ? string.Empty : ", " + edges) + " }" + (dependsOn is null ? string.Empty : ", \"dependsOn\": [" + dependsOn + "]") + " }";
    }

    public static string Au(string name = "Orders AU", string props = "\"description\": \"Scoped admin for Orders\"", string? dependsOn = null)
    {
        return "{ \"type\": \"Microsoft.Entra/administrativeUnits\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { " + props + " }" + (dependsOn is null ? string.Empty : ", \"dependsOn\": [" + dependsOn + "]") + " }";
    }

    public static string RoleDef(string actions = "\"microsoft.directory/users/password/update\", \"microsoft.directory/users/standard/read\"", string extra = "", string name = "Orders Support")
    {
        return "{ \"type\": \"Microsoft.Entra/roleDefinitions\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"description\": \"Reset passwords for Orders staff\"" + extra + ", \"rolePermissions\": [{ \"allowedResourceActions\": [" + actions + "] }] } }";
    }

    public static string Standalone(string props, string name = "[guid('alice', 'role', 'test')]", string? dependsOn = null)
    {
        return "{ \"type\": \"Microsoft.Entra/roleAssignments\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { " + props + " }" + (dependsOn is null ? string.Empty : ", \"dependsOn\": [" + dependsOn + "]") + " }";
    }

    public static string DirectoryRoles(params string[] items)
    {
        return "\"directoryRoles\": [" + string.Join(", ", items) + "]";
    }

    public static string DependsOnRole(string name = "Orders Support")
    {
        return "\"[resourceId('Microsoft.Entra/roleDefinitions', '" + name + "')]\"";
    }

    public static string DependsOnUnit(string name = "Orders AU")
    {
        return "\"[resourceId('Microsoft.Entra/administrativeUnits', '" + name + "')]\"";
    }
}
