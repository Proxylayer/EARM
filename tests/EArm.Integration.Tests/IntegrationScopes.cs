using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Commands;
using EArm.Engine.Permissions;

namespace EArm.Integration.Tests;

/// <summary>A scenario template, the gates of the tests that deploy it, and placeholder values for its other required parameters.</summary>
/// <param name="File">The file under <c>Templates/</c>.</param>
/// <param name="Needs">What the tests that deploy it need besides the tenant (as they pass to <see cref="IntegrationFixture.Require"/>).</param>
/// <param name="Parameters">Values for its required parameters other than <c>prefix</c>: only used to expand it offline, never deployed.</param>
internal sealed record ScenarioTemplate(string File, Needs Needs, IReadOnlyDictionary<string, string> Parameters);

/// <summary>
/// The delegated Graph scopes a real-tenant run requests when it signs in as a user (a stored <c>earm auth login</c> record): the union of the
/// <see cref="RunScopes"/> sets (what-if, create and delete, as the in-process CLI requests them) of every scenario template whose gates are enabled, plus
/// <see cref="CleanupScopes"/> for the fixture's own Graph client (preflight, direct test calls, the cleanup sweep and purge). Requesting the whole union up
/// front means one consent covers the run; every CLI invocation then requests a subset, which the refresh token serves silently.
/// </summary>
internal static class IntegrationScopes
{
    /// <summary>
    /// What the fixture's own Graph client needs besides the scenarios: the preflight, the direct reads and writes of the tests and probes (applications,
    /// groups, users), and the cleanup guard, which lists every object kind with the prefix (policies and role management read) and deletes and purges
    /// applications, service principals, groups, users and administrative units (purging a soft-deleted user takes <c>User.DeleteRestore.All</c>).
    /// Kept in step with the "Every test" row of tests/EArm.Integration.Tests/README.md.
    /// </summary>
    internal static readonly IReadOnlyList<string> CleanupScopes =
    [
        RunScopes.SignIn,
        "Organization.Read.All",
        "Application.ReadWrite.All",
        "Group.ReadWrite.All",
        "User.ReadWrite.All",
        "User.DeleteRestore.All",
        "AdministrativeUnit.ReadWrite.All",
        "Policy.Read.All",
        "RoleManagement.Read.Directory",
    ];

    /// <summary>
    /// What the cleanup guard needs besides <see cref="CleanupScopes"/> when <c>EARM_IT_ALLOW_PIM=1</c>: it lists and removes (adminRemove) the PIM
    /// eligibilities and time-bound active assignments of prefixed principals and groups (<see cref="PimCleanup"/>).
    /// </summary>
    internal static readonly IReadOnlyList<string> PimCleanupScopes =
    [
        "RoleEligibilitySchedule.ReadWrite.Directory",
        "RoleAssignmentSchedule.ReadWrite.Directory",
        "PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup",
        "PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup",
    ];

    /// <summary>
    /// What the fixture needs besides <see cref="CleanupScopes"/> when <c>EARM_IT_ALLOW_AUTH_METHODS=1</c>: it snapshots and restores authentication method
    /// configurations (<see cref="AuthMethodRestoreGuard"/>), also at teardown.
    /// </summary>
    internal static readonly IReadOnlyList<string> AuthMethodScopes =
    [
        "Policy.ReadWrite.AuthenticationMethod",
    ];

    /// <summary>
    /// What the fixture needs besides <see cref="CleanupScopes"/> when <c>EARM_IT_ALLOW_PIM=1</c> and <c>EARM_IT_ALLOW_PIM_POLICIES=1</c>: it snapshots and
    /// restores role management policies (<see cref="PimPolicyRestoreGuard"/>, also at teardown) of a directory role and of PIM for Groups, and the PP
    /// probes read the tenant's authentication contexts.
    /// </summary>
    internal static readonly IReadOnlyList<string> PimPolicyScopes =
    [
        "RoleManagementPolicy.ReadWrite.Directory",
        "RoleManagementPolicy.ReadWrite.AzureADGroup",
        "AuthenticationContext.Read.All",
    ];

    /// <summary>Every scenario template and the gates of the tests that deploy it (keep in step with the <c>Fixture.Require</c> calls of the scenario classes).</summary>
    internal static readonly IReadOnlyList<ScenarioTemplate> Templates =
    [
        new("app-roundtrip.entra.json", Needs.Tenant, Empty()),
        new("named-locations.entra.json", Needs.Tenant, Empty()),
        new("guest-member.entra.json", Needs.Tenant, With(("guestUpn", "guest_example.com#EXT#@example.onmicrosoft.com"))),
        new("groups-edges.entra.json", Needs.Domain, With(("domain", "example.onmicrosoft.com"), ("u2Password", PlaceholderPassword))),
        new("users.entra.json", Needs.Domain, With(("domain", "example.onmicrosoft.com"), ("u2Password", PlaceholderPassword))),
        new("ca-policy.entra.json", Needs.ConditionalAccess, Empty()),
        new("ca-strength-context.entra.json", Needs.ConditionalAccess, With(("contextId", "c99"))),
        new("roles.entra.json", Needs.Roles, Empty()),
        new("assignments.entra.json", Needs.Roles, Empty()),
        new("orders-it.entra.json", Needs.Domain | Needs.ConditionalAccess | Needs.Roles, With(("domain", "example.onmicrosoft.com"), ("contextId", "c99"), ("initialPassword", PlaceholderPassword))),
        new("pim-role-eligibility.entra.json", Needs.Domain | Needs.Pim, With(("domain", "example.onmicrosoft.com"), ("e1Name", PlaceholderGuid1), ("e2Name", PlaceholderGuid2), ("e2End", PlaceholderEnd))),
        new("pim-group-eligibility.entra.json", Needs.Domain | Needs.Pim, With(("domain", "example.onmicrosoft.com"), ("memberName", PlaceholderGuid1), ("ownerName", PlaceholderGuid2), ("ownerEnd", PlaceholderEnd))),
        new("pim-active.entra.json", Needs.Domain | Needs.Pim, With(("domain", "example.onmicrosoft.com"), ("roleName", PlaceholderGuid1), ("memberName", PlaceholderGuid2), ("ownerName", PlaceholderGuid3), ("activeEnd", PlaceholderEnd))),
        new("pim-active-conflicts.entra.json", Needs.Domain | Needs.Pim, With(("domain", "example.onmicrosoft.com"))),
        new("auth-methods.entra.json", Needs.AuthMethods, Empty()),
        new("pim-policy-role.entra.json", Needs.Pim | Needs.PimPolicies, With(("approverGroupId", PlaceholderGuid1))),
        new("pim-policy-group.entra.json", Needs.Domain | Needs.Pim | Needs.PimPolicies, With(("domain", "example.onmicrosoft.com"), ("eligibilityName", PlaceholderGuid1))),
    ];

    private const string PlaceholderGuid1 = "00000000-0000-0000-0000-0000000000a1";
    private const string PlaceholderGuid2 = "00000000-0000-0000-0000-0000000000a2";
    private const string PlaceholderGuid3 = "00000000-0000-0000-0000-0000000000a3";
    private const string PlaceholderEnd = "2030-01-01T00:00:00Z";

    /// <summary>The verbs the scenarios run through the CLI: what-if requests read variants that a write scope does not grant, so all three are consented.</summary>
    private static readonly RunVerb[] Verbs = [RunVerb.WhatIf, RunVerb.Create, RunVerb.Delete];

    /// <summary>A syntactically valid password for the offline expansion only (never sent anywhere).</summary>
    private const string PlaceholderPassword = "Placeholder-Expansion-Only-1!";

    /// <summary>The templates the configuration enables.</summary>
    internal static IEnumerable<ScenarioTemplate> Enabled(IntegrationSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Templates.Where(template => settings.Meets(template.Needs));
    }

    /// <summary>The run's delegated Graph scope names (short, sorted, distinct case-insensitively).</summary>
    /// <exception cref="InvalidOperationException">A template does not expand offline (its scopes cannot be computed).</exception>
    internal static async Task<IReadOnlyList<string>> ComputeAsync(IntegrationSettings settings, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var names = new SortedSet<string>(CleanupScopes, StringComparer.OrdinalIgnoreCase);
        if (settings.AllowPim)
        {
            names.UnionWith(PimCleanupScopes);
        }

        if (settings.AllowAuthMethods)
        {
            names.UnionWith(AuthMethodScopes);
        }

        if (settings.AllowPim && settings.AllowPimPolicies)
        {
            names.UnionWith(PimPolicyScopes);
        }

        foreach (var template in Enabled(settings))
        {
            // The values of the -p options (name=value), as the CLI passes them on.
            var parameters = new List<string> { "prefix=" + settings.Prefix };
            parameters.AddRange(template.Parameters.Select(p => p.Key + "=" + p.Value));

            foreach (var verb in Verbs)
            {
                var computed = await TemplateRunScopes.ComputeAsync(IntegrationFixture.TemplatePath(template.File), parameters, verb, cancellationToken).ConfigureAwait(false);
                if (computed.Scopes is null)
                {
                    var reason = computed.Errors.Count > 0 ? computed.Errors[0].Message : "exit code " + computed.ExitCode;
                    throw new InvalidOperationException($"The delegated scopes of {template.File} ({verb}) cannot be computed: {reason}");
                }

                names.UnionWith(computed.Scopes.GraphScopes);
            }
        }

        return [.. names];
    }

    /// <summary>The one interactive command that consents every scope of the run (<paramref name="scopes"/>) for the configured tenant and sign-in app.</summary>
    internal static string LoginCommand(IReadOnlyList<string> scopes, string? tenantId, string? clientId)
    {
        ArgumentNullException.ThrowIfNull(scopes);
        var command = "earm auth login";
        if (!string.IsNullOrWhiteSpace(tenantId))
        {
            command += " --tenant " + tenantId;
        }

        if (!string.IsNullOrWhiteSpace(clientId))
        {
            command += " --client-id " + clientId;
        }

        return command + " --scopes " + string.Join(' ', scopes);
    }

    private static Dictionary<string, string> Empty()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal);
    }

    private static Dictionary<string, string> With(params (string Name, string Value)[] values)
    {
        return values.ToDictionary(v => v.Name, v => v.Value, StringComparer.Ordinal);
    }
}
