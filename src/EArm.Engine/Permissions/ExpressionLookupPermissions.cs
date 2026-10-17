using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using EArm.Core.Types;

namespace EArm.Engine.Permissions;

/// <summary>The permissions of the expression functions that read the tenant (<c>appRole()</c>, <c>permissionScope()</c>, <c>roleTemplate()</c>, <c>roleDefinitions()</c>, <c>tenant()</c>, <c>reference(id, apiVersion)</c>), found in the template's expression strings.</summary>
internal static class ExpressionLookupPermissions
{
    private static readonly Regex AppRoleFunction = new(@"\bapprole\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex PermissionScopeFunction = new(@"\bpermissionscope\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RoleTemplateFunction = new(@"\broletemplate\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex RoleDefinitionsFunction = new(@"\broledefinitions\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex TenantFunction = new(@"\btenant\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ReferenceFunction = new(@"\breference\s*\(", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex ApiVersionLiteral = new(@"'\d{4}-\d{2}-\d{2}(-preview(\.beta)?)?'", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    private static readonly Regex TypeLiteral = new(@"'(Microsoft\.Entra/[A-Za-z/]+)'", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>Permissions of the expression functions that read the tenant (they run while the resources are applied).</summary>
    internal static void AddLookups(IEnumerable<string> templateStrings, PermissionMode mode, PermissionAccumulator graph)
    {
        bool appRole = false;
        bool permissionScope = false;
        bool roleTemplate = false;
        bool roleDefinitions = false;
        bool tenant = false;
        var existing = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string text in templateStrings)
        {
            appRole |= AppRoleFunction.IsMatch(text);
            permissionScope |= PermissionScopeFunction.IsMatch(text);
            roleTemplate |= RoleTemplateFunction.IsMatch(text);
            roleDefinitions |= RoleDefinitionsFunction.IsMatch(text);
            tenant |= TenantFunction.IsMatch(text);

            if (ReferenceFunction.IsMatch(text) && ApiVersionLiteral.IsMatch(text))
            {
                foreach (Match match in TypeLiteral.Matches(text))
                {
                    existing.Add(match.Groups[1].Value);
                }
            }
        }

        string lookups = "(expression lookups)";
        if (appRole)
        {
            graph.Add("Application.Read.All", lookups, "lookup: appRole()");
        }

        if (permissionScope)
        {
            graph.Add("Application.Read.All", lookups, "lookup: permissionScope()");
        }

        if (roleTemplate)
        {
            graph.Add("RoleManagement.Read.Directory", lookups, "lookup: roleTemplate() (custom role names)");
        }

        if (roleDefinitions)
        {
            graph.Add("RoleManagement.Read.Directory", lookups, "lookup: roleDefinitions() (custom role names)");
        }

        if (tenant)
        {
            graph.Add("Organization.Read.All", lookups, "lookup: tenant()");
        }

        var catalog = ResourceTypeCatalog.Default;
        foreach (string type in existing)
        {
            if (catalog.Find(type) is not { } definition)
            {
                continue;
            }

            foreach (string permission in definition.Permissions.Application.Select(PermissionManifest.ReadVariant).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                graph.Add(PermissionManifest.ForMode(permission, mode), $"reference({definition.Type}, apiVersion)", "lookup: existing resource");
            }
        }

    }
}
