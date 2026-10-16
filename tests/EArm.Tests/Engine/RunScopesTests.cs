using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using EArm.Cli.Commands;
using EArm.Core;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Engine.Permissions;
using EArm.Providers;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary><see cref="RunScopes"/>: the per-verb delegated scope sets, computed offline over the samples (no token, no engine services).</summary>
public sealed class RunScopesTests
{
    private static async Task<TemplateDocument> LoadAsync(string template, string? parameters = null)
    {
        string[] tokens = parameters is null ? [] : ["@" + TestPaths.Sample(parameters)];
        var set = ValidateCommand.BuildParameterSet(tokens, out _);
        return await new TemplateLoader(ResourceTypeCatalog.Default).LoadAsync(TestPaths.Sample(template), set, TestContext.Current.CancellationToken);
    }

    private static async Task<RunScopeSet> ComputeAsync(string template, string? parameters, RunVerb verb)
    {
        var document = await LoadAsync(template, parameters);
        return RunScopes.Compute(document, ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default), verb);
    }

    private static Task<RunScopeSet> OrdersAsync(RunVerb verb)
    {
        return ComputeAsync("orders-api.entra.json", "orders-api.parameters.json", verb);
    }

    private static Task<RunScopeSet> TenantSecurityAsync(RunVerb verb)
    {
        return ComputeAsync("tenant-security.entra.json", "tenant-security.parameters.json", verb);
    }

    public static TheoryData<string, string?> Samples => new()
    {
        { "orders-api.entra.json", "orders-api.parameters.json" },
        { "orders-api.flat.entra.json", "orders-api.parameters.json" },
        { "app-with-sp-and-secret.entra.json", "app-with-sp-and-secret.parameters.json" },
        { "tenant-security.entra.json", "tenant-security.parameters.json" },
        { "authentication-methods.entra.json", "authentication-methods.parameters.json" },
        { "pim-eligibility.entra.json", "pim-eligibility.parameters.json" },
        { "pim-active.entra.json", "pim-active.parameters.json" },
        { "pim-policies.entra.json", "pim-policies.parameters.json" },
        { "orders-api.entra.json", null },
    };

    // ---- PIM eligibility ----

    [Fact]
    public async Task PimEligibility_WritesWithRoleEligibilityScheduleReadWrite_AndWhatIfReadsWithItsReadVariant()
    {
        var create = await ComputeAsync("pim-eligibility.entra.json", "pim-eligibility.parameters.json", RunVerb.Create);
        var whatIf = await ComputeAsync("pim-eligibility.entra.json", "pim-eligibility.parameters.json", RunVerb.WhatIf);
        var delete = await ComputeAsync("pim-eligibility.entra.json", "pim-eligibility.parameters.json", RunVerb.Delete);

        Assert.Contains("RoleEligibilitySchedule.ReadWrite.Directory", create.GraphScopes);
        Assert.DoesNotContain("RoleEligibilitySchedule.Read.Directory", create.GraphScopes);
        Assert.Contains("RoleEligibilitySchedule.Read.Directory", whatIf.GraphScopes);
        Assert.DoesNotContain("RoleEligibilitySchedule.ReadWrite.Directory", whatIf.GraphScopes);
        Assert.Contains("RoleEligibilitySchedule.ReadWrite.Directory", delete.GraphScopes);
        Assert.Contains(create.Scopes.Single(s => s.Name == "RoleEligibilitySchedule.ReadWrite.Directory").Resources, r => r.StartsWith("/providers/Microsoft.Entra/roleEligibilityScheduleRequests/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PimGroupEligibility_WritesWithPrivilegedEligibilityScheduleReadWrite_AndWhatIfReadsWithItsReadVariant()
    {
        var create = await ComputeAsync("pim-eligibility.entra.json", "pim-eligibility.parameters.json", RunVerb.Create);
        var whatIf = await ComputeAsync("pim-eligibility.entra.json", "pim-eligibility.parameters.json", RunVerb.WhatIf);
        var delete = await ComputeAsync("pim-eligibility.entra.json", "pim-eligibility.parameters.json", RunVerb.Delete);

        Assert.Contains("PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup", create.GraphScopes);
        Assert.DoesNotContain("PrivilegedEligibilitySchedule.Read.AzureADGroup", create.GraphScopes);
        Assert.Contains("PrivilegedEligibilitySchedule.Read.AzureADGroup", whatIf.GraphScopes);
        Assert.DoesNotContain("PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup", whatIf.GraphScopes);
        Assert.Contains("PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup", delete.GraphScopes);
        Assert.Contains(create.Scopes.Single(s => s.Name == "PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup").Resources, r => r.StartsWith("/providers/Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PimPolicies_WriteWithTheScopesOfTheirScope_AndWhatIfReadsWithTheReadVariants()
    {
        var create = await ComputeAsync("pim-policies.entra.json", "pim-policies.parameters.json", RunVerb.Create);
        var whatIf = await ComputeAsync("pim-policies.entra.json", "pim-policies.parameters.json", RunVerb.WhatIf);

        Assert.Contains("RoleManagementPolicy.ReadWrite.Directory", create.GraphScopes);
        Assert.Contains("RoleManagementPolicy.ReadWrite.AzureADGroup", create.GraphScopes);
        Assert.DoesNotContain("RoleManagementPolicy.Read.Directory", create.GraphScopes);
        Assert.Contains("RoleManagementPolicy.Read.Directory", whatIf.GraphScopes);
        Assert.Contains("RoleManagementPolicy.Read.AzureADGroup", whatIf.GraphScopes);
        Assert.DoesNotContain("RoleManagementPolicy.ReadWrite.Directory", whatIf.GraphScopes);
        Assert.DoesNotContain("RoleManagementPolicy.ReadWrite.AzureADGroup", whatIf.GraphScopes);
        Assert.Equal(2, create.Scopes.Single(s => s.Name == "RoleManagementPolicy.ReadWrite.Directory").Resources.Count);
        Assert.Single(create.Scopes.Single(s => s.Name == "RoleManagementPolicy.ReadWrite.AzureADGroup").Resources);
    }

    [Fact]
    public async Task PimRoleActiveAssignment_WritesWithRoleAssignmentScheduleReadWrite_AndWhatIfReadsWithItsReadVariant()
    {
        var create = await ComputeAsync("pim-active.entra.json", "pim-active.parameters.json", RunVerb.Create);
        var whatIf = await ComputeAsync("pim-active.entra.json", "pim-active.parameters.json", RunVerb.WhatIf);
        var delete = await ComputeAsync("pim-active.entra.json", "pim-active.parameters.json", RunVerb.Delete);

        Assert.Contains("RoleAssignmentSchedule.ReadWrite.Directory", create.GraphScopes);
        Assert.DoesNotContain("RoleAssignmentSchedule.Read.Directory", create.GraphScopes);
        Assert.Contains("RoleAssignmentSchedule.Read.Directory", whatIf.GraphScopes);
        Assert.DoesNotContain("RoleAssignmentSchedule.ReadWrite.Directory", whatIf.GraphScopes);
        Assert.Contains("RoleAssignmentSchedule.ReadWrite.Directory", delete.GraphScopes);
        Assert.Contains(create.Scopes.Single(s => s.Name == "RoleAssignmentSchedule.ReadWrite.Directory").Resources, r => r.StartsWith("/providers/Microsoft.Entra/roleAssignmentScheduleRequests/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PimGroupActiveAssignment_WritesWithPrivilegedAssignmentScheduleReadWrite_AndWhatIfReadsWithItsReadVariant()
    {
        var create = await ComputeAsync("pim-active.entra.json", "pim-active.parameters.json", RunVerb.Create);
        var whatIf = await ComputeAsync("pim-active.entra.json", "pim-active.parameters.json", RunVerb.WhatIf);
        var delete = await ComputeAsync("pim-active.entra.json", "pim-active.parameters.json", RunVerb.Delete);

        Assert.Contains("PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup", create.GraphScopes);
        Assert.DoesNotContain("PrivilegedAssignmentSchedule.Read.AzureADGroup", create.GraphScopes);
        Assert.Contains("PrivilegedAssignmentSchedule.Read.AzureADGroup", whatIf.GraphScopes);
        Assert.DoesNotContain("PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup", whatIf.GraphScopes);
        Assert.Contains("PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup", delete.GraphScopes);
        Assert.Contains(create.Scopes.Single(s => s.Name == "PrivilegedAssignmentSchedule.ReadWrite.AzureADGroup").Resources, r => r.StartsWith("/providers/Microsoft.Entra/privilegedAccess/groupAssignmentScheduleRequests/", StringComparison.Ordinal));
    }

    // ---- authentication methods policy ----

    [Fact]
    public async Task AuthenticationMethods_WriteWithPolicyReadWriteAuthenticationMethod_AndWhatIfReadsWithItsReadVariant()
    {
        var create = await ComputeAsync("authentication-methods.entra.json", "authentication-methods.parameters.json", RunVerb.Create);
        var whatIf = await ComputeAsync("authentication-methods.entra.json", "authentication-methods.parameters.json", RunVerb.WhatIf);
        var delete = await ComputeAsync("authentication-methods.entra.json", "authentication-methods.parameters.json", RunVerb.Delete);

        Assert.Equal(["Organization.Read.All", "Policy.ReadWrite.AuthenticationMethod", "User.Read"], create.GraphScopes);
        Assert.Equal(["Organization.Read.All", "Policy.Read.AuthenticationMethod", "User.Read"], whatIf.GraphScopes);
        Assert.Contains("Policy.ReadWrite.AuthenticationMethod", delete.GraphScopes);
        Assert.Contains("/providers/Microsoft.Entra/authenticationMethodConfigurations/fido2", create.Scopes.Single(s => s.Name == "Policy.ReadWrite.AuthenticationMethod").Resources);
    }

    // ---- invariants over every sample ----

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task WhatIf_RequestsNoWriteScope(string template, string? parameters)
    {
        var scopes = await ComputeAsync(template, parameters, RunVerb.WhatIf);

        Assert.NotEmpty(scopes.GraphScopes);
        Assert.DoesNotContain(scopes.GraphScopes, s => s.Contains("ReadWrite", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(scopes.GraphScopes, s => s.Contains("OwnedBy", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task EveryVerbButValidate_IncludesThePreflightReadAndTheSignInScope(string template, string? parameters)
    {
        foreach (var verb in new[] { RunVerb.WhatIf, RunVerb.Create, RunVerb.Delete })
        {
            var scopes = await ComputeAsync(template, parameters, verb);

            Assert.Contains(PermissionManifest.Preflight, scopes.GraphScopes);
            Assert.Contains(RunScopes.SignIn, scopes.GraphScopes);
            Assert.Contains("preflight: GET /organization", scopes.Scopes.Single(s => s.Name == PermissionManifest.Preflight).Reasons);
            Assert.Equal(verb, scopes.Verb);
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Scopes_AreSortedAndDistinct_AndNoReadScopeSitsNextToItsWriteScope(string template, string? parameters)
    {
        foreach (var verb in new[] { RunVerb.WhatIf, RunVerb.Create, RunVerb.Delete })
        {
            var scopes = await ComputeAsync(template, parameters, verb);

            Assert.Equal(scopes.GraphScopes.OrderBy(s => s, StringComparer.OrdinalIgnoreCase), scopes.GraphScopes);
            Assert.Equal(scopes.GraphScopes.Count, scopes.GraphScopes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            foreach (string scope in scopes.GraphScopes.Where(s => s.Contains(".Read.", StringComparison.Ordinal)))
            {
                Assert.DoesNotContain(scope.Replace(".Read.", ".ReadWrite.", StringComparison.Ordinal), scopes.GraphScopes);
            }

            Assert.All(scopes.Scopes, s => Assert.NotEmpty(s.Reasons));
        }
    }

    [Theory]
    [MemberData(nameof(Samples))]
    public async Task Validate_IsEmpty(string template, string? parameters)
    {
        var scopes = await ComputeAsync(template, parameters, RunVerb.Validate);

        Assert.Empty(scopes.GraphScopes);
        Assert.Empty(scopes.Scopes);
        Assert.False(scopes.NeedsKeyVault);
    }

    // ---- per-verb sets ----

    [Fact]
    public async Task OrdersSample_Create_IsTheDelegatedManifestPlusThePreflightAndSignIn()
    {
        var scopes = await OrdersAsync(RunVerb.Create);

        Assert.Equal(
        [
            "AdministrativeUnit.ReadWrite.All",
            "Application.ReadWrite.All",
            "AppRoleAssignment.ReadWrite.All",
            "AuthenticationContext.ReadWrite.All",
            "DelegatedPermissionGrant.ReadWrite.All",
            "Directory.Read.All",
            "Group.ReadWrite.All",
            "GroupMember.ReadWrite.All",
            "LicenseAssignment.ReadWrite.All",
            "Organization.Read.All",
            "Policy.Read.All",
            "Policy.ReadWrite.ConditionalAccess",
            "RoleManagement.ReadWrite.Directory",
            "User.Read",
            "User.ReadWrite.All",
        ], scopes.GraphScopes);
    }

    [Fact]
    public async Task OrdersSample_WhatIf_IsTheReadVariantOfEveryWriteScope()
    {
        var scopes = await OrdersAsync(RunVerb.WhatIf);

        Assert.Equal(
        [
            "AdministrativeUnit.Read.All",
            "Application.Read.All",
            "AuthenticationContext.Read.All",
            "DelegatedPermissionGrant.Read.All",
            "Directory.Read.All",
            "Group.Read.All",
            "GroupMember.Read.All",
            "LicenseAssignment.Read.All",
            "Organization.Read.All",
            "Policy.Read.All",
            "Policy.Read.ConditionalAccess",
            "RoleManagement.Read.Directory",
            "User.Read",
            "User.Read.All",
        ], scopes.GraphScopes);
    }

    [Fact]
    public async Task WhatIf_NeverRequestsAppRoleAssignmentReadAll_WhichIsNotAScope()
    {
        var scopes = await OrdersAsync(RunVerb.WhatIf);

        Assert.DoesNotContain(scopes.GraphScopes, s => s.StartsWith("AppRoleAssignment.", StringComparison.OrdinalIgnoreCase));
        Assert.Contains(scopes.Scopes.Single(s => s.Name == "Application.Read.All").Reasons, r => r.Contains("AppRoleAssignment.Read.All is not a scope", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatIf_ReasonsNameTheWriteScopeTheyWereFoldedFrom()
    {
        var scopes = await OrdersAsync(RunVerb.WhatIf);

        var policy = scopes.Scopes.Single(s => s.Name == "Policy.Read.ConditionalAccess");
        Assert.Contains("provider (read of Policy.ReadWrite.ConditionalAccess)", policy.Reasons);
        Assert.Contains("/providers/Microsoft.Entra/conditionalAccess/policies/CA-Orders-Admins-MFA", policy.Resources);
    }

    [Fact]
    public async Task Delete_KeepsWriteForDeclaredTypes_AndFoldsEdgeOnlyWritesToReads()
    {
        var create = await TenantSecurityAsync(RunVerb.Create);
        var delete = await TenantSecurityAsync(RunVerb.Delete);

        // Application.ReadWrite.All comes only from the group's members edge (a service principal member): deleting the group needs no write on it.
        Assert.Contains("Application.ReadWrite.All", create.GraphScopes);
        Assert.DoesNotContain("Application.ReadWrite.All", delete.GraphScopes);
        Assert.Contains("Application.Read.All", delete.GraphScopes);

        // The declared types' own write permissions stay.
        Assert.Contains("Group.ReadWrite.All", delete.GraphScopes);
        Assert.Contains("Policy.ReadWrite.ConditionalAccess", delete.GraphScopes);
        Assert.Contains("RoleManagement.ReadWrite.Directory", delete.GraphScopes);
    }

    // ---- edges and lookups ----

    [Fact]
    public async Task EdgeScopes_AreIncluded_WithTheirProperty()
    {
        var create = await OrdersAsync(RunVerb.Create);
        var whatIf = await OrdersAsync(RunVerb.WhatIf);

        Assert.Contains("edge: members", create.Scopes.Single(s => s.Name == "GroupMember.ReadWrite.All").Reasons);
        Assert.Contains("edge: manager", create.Scopes.Single(s => s.Name == "User.ReadWrite.All").Reasons);
        Assert.Contains("edge: members (read of GroupMember.ReadWrite.All)", whatIf.Scopes.Single(s => s.Name == "GroupMember.Read.All").Reasons);
    }

    [Fact]
    public async Task LookupFunctionScopes_AreIncluded()
    {
        var create = await OrdersAsync(RunVerb.Create);
        var whatIf = await OrdersAsync(RunVerb.WhatIf);

        // appRole()/permissionScope() need Application.Read.All, which the create set's Application.ReadWrite.All covers.
        var write = create.Scopes.Single(s => s.Name == "Application.ReadWrite.All");
        Assert.Contains("lookup: appRole()", write.Reasons);
        Assert.Contains("(expression lookups)", write.Resources);
        Assert.Contains("lookup: permissionScope()", whatIf.Scopes.Single(s => s.Name == "Application.Read.All").Reasons);
    }

    // ---- Key Vault ----

    [Fact]
    public async Task NeedsKeyVault_FollowsTheTemplatesKeyVaultReferences()
    {
        Assert.True((await OrdersAsync(RunVerb.WhatIf)).NeedsKeyVault);
        Assert.True((await OrdersAsync(RunVerb.Create)).NeedsKeyVault);
        Assert.True((await OrdersAsync(RunVerb.Delete)).NeedsKeyVault);
        Assert.False((await TenantSecurityAsync(RunVerb.Create)).NeedsKeyVault);
        Assert.False((await OrdersAsync(RunVerb.Validate)).NeedsKeyVault);
    }

    // ---- inputs ----

    [Fact]
    public async Task ExpansionOverload_MatchesTheDocumentOverload()
    {
        var document = await LoadAsync("orders-api.entra.json", "orders-api.parameters.json");
        var registry = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);
        var expansion = OfflineTemplateExpansion.Run(document, ResourceTypeCatalog.Default);

        Assert.True(expansion.Succeeded);
        Assert.Equal(RunScopes.Compute(document, registry, RunVerb.Create).GraphScopes, RunScopes.Compute(expansion, registry, RunVerb.Create).GraphScopes);
    }

    [Fact]
    public async Task BrokenTemplate_IsRejected_AndItsExpansionCarriesTheErrors()
    {
        var set = ValidateCommand.BuildParameterSet([], out _);
        var document = await new TemplateLoader(ResourceTypeCatalog.Default).LoadAsync(TestPaths.InvalidFixture("unknown-type.entra.json"), set, TestContext.Current.CancellationToken);
        var registry = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);

        var expansion = OfflineTemplateExpansion.Run(document, ResourceTypeCatalog.Default);

        Assert.False(expansion.Succeeded);
        Assert.Contains(expansion.Errors, d => d.IsError);
        Assert.Empty(expansion.Instances);
        Assert.Throws<InvalidOperationException>(() => RunScopes.Compute(document, registry, RunVerb.Create));
        Assert.Throws<ArgumentException>(() => PermissionManifest.Build("t", PermissionMode.Delegated, expansion, registry));
    }

    [Fact]
    public void NullArguments_AreRejected()
    {
        var registry = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);

        Assert.Throws<ArgumentNullException>(() => RunScopes.Compute((TemplateDocument)null!, registry, RunVerb.Create));
        Assert.Throws<ArgumentNullException>(() => RunScopes.Compute((OfflineTemplateExpansion)null!, registry, RunVerb.Create));
        Assert.Throws<ArgumentNullException>(() => OfflineTemplateExpansion.Run(null!, ResourceTypeCatalog.Default));
    }
}
