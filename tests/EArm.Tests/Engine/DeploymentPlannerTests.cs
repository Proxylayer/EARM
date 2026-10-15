using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Engine;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.WhatIf;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>What-if planning against <see cref="FakeGraphClient"/>: reads only, placeholders, promotions, permissions, parallelism.</summary>
public sealed class DeploymentPlannerTests
{
    private static ResourceChange Row(DeploymentPlan plan, string resourceId)
    {
        var row = plan.Changes.FirstOrDefault(c => c.Id.Id == resourceId);
        Assert.NotNull(row);
        return row;
    }

    private static ResourceChange RowOfType(DeploymentPlan plan, string type)
    {
        return Assert.Single(plan.Changes, c => c.ResourceType == type);
    }

    [Fact]
    public async Task WhatIf_OnAnEmptyTenant_PlansEveryCreate_AndWritesNothing()
    {
        var host = new EngineHost();
        host.SeedExisting();

        var plan = await host.WhatIfAsync(await Templates.FixtureAsync());

        Assert.Empty(host.Fake.Writes);
        Assert.NotEmpty(host.Fake.Requests);
        Assert.Equal(Fixture.InstanceCount, plan.Changes.Count);
        Assert.Equal(PlanMode.WhatIf, plan.PlanMode);
        Assert.Equal(EngineHost.Tenant, plan.TenantId);
        Assert.StartsWith("sha256:", plan.TemplateHash, StringComparison.Ordinal);
        var summary = plan.Summary;
        Assert.Equal(15, summary.Create);
        Assert.Equal(0, summary.Modify);
        Assert.Equal(0, summary.Delete);
        Assert.Equal(0, summary.NoChange);
        Assert.Equal(1, summary.Ignore);
        Assert.Equal(0, summary.Unsupported);
        Assert.True(plan.HasChanges);
        Assert.Empty(plan.Orphans);
        Assert.Empty(plan.UnresolvedReferences);
        Assert.DoesNotContain(plan.Diagnostics, d => d.IsError);

        // Rows follow batch order (the user in batch 1 before the application in batch 2).
        Assert.True(plan.Changes.ToList().FindIndex(c => c.Id.Id == Fixture.User) < plan.Changes.ToList().FindIndex(c => c.Id.Id == Fixture.App));
    }

    [Fact]
    public async Task WhatIf_SymbolicServicePrincipal_StaysSymbolicUntilTheApplicationExists()
    {
        var host = new EngineHost();
        host.SeedExisting();

        var plan = await host.WhatIfAsync(await Templates.FixtureAsync());

        var sp = RowOfType(plan, Fixture.SpType);
        Assert.Equal(Fixture.SpSymbolic, sp.Id.Id);
        Assert.True(sp.IsSymbolicId);
        Assert.True(sp.IsUnknown);
        Assert.Equal(ChangeType.Create, sp.Type);
        Assert.Contains(sp.Reasons, r => r.Contains("the name is only known once", StringComparison.Ordinal) && r.Contains(Fixture.App, StringComparison.Ordinal));
        Assert.DoesNotContain(host.Fake.Requests, r => r.Path.Contains("servicePrincipals(appId=", StringComparison.Ordinal) && !r.Path.Contains(EngineHost.GraphAppId, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WhatIf_SeededApplication_MakesTheServicePrincipalRowConcrete_AndTheApplicationAModify()
    {
        var host = new EngineHost();
        host.SeedExisting();
        const string appId = "aaaaaaaa-1111-2222-3333-444444444444";
        host.Directory.AddApplication("contoso-dev-api", "Orders API", appId);

        var plan = await host.WhatIfAsync(await Templates.FixtureAsync());

        var app = Row(plan, Fixture.App);
        Assert.Equal(ChangeType.Modify, app.Type);
        Assert.Equal(ChangeMode.Patch, app.Mode);
        Assert.NotNull(app.GraphId);
        Assert.Contains(app.Deltas, d => d.Path.ToString() == "properties.identifierUris" && d.Kind == PropertyChangeType.Create);

        var sp = RowOfType(plan, Fixture.SpType);
        Assert.Equal(Fixture.Sp(appId), sp.Id.Id);
        Assert.False(sp.IsSymbolicId);
        Assert.False(sp.IsUnknown);
        Assert.Equal(ChangeType.Create, sp.Type);
        Assert.Contains(host.Fake.Requests, r => r.Method == "GET" && r.Path == "/servicePrincipals(appId='" + appId + "')");

        // The resolved id is published under both the planned (symbolic) and the concrete id.
        var instance = EngineHost.Instance(plan, Fixture.SpSymbolic);
        Assert.Equal(Fixture.Sp(appId), instance.Id.Id);
        Assert.Equal(Fixture.SpSymbolic, instance.PlannedId.Id);
        Assert.Same(instance, plan.FindInstance(instance.Id));
        Assert.Same(instance, plan.FindInstance(instance.PlannedId));
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task WhatIf_PlaceholderInputs_FlagRowsAsUnknown_AndRenderDeployTime()
    {
        var host = new EngineHost();
        host.SeedExisting();

        var plan = await host.WhatIfAsync(await Templates.FixtureAsync());

        var assignment = RowOfType(plan, Fixture.AppRoleAssignmentType);
        Assert.True(assignment.IsUnknown);
        Assert.Equal(ChangeType.Create, assignment.Type);
        Assert.Contains(assignment.Reasons, r => r.Contains("the key cannot be resolved before deployment", StringComparison.Ordinal));
        Assert.Equal("\"(deploy-time)\"", assignment.After!["principalId"]!.ToJsonString());
        Assert.Contains(assignment.Deltas, d => d.IsUnknown && d.Path.ToString() == "properties.principalId");

        var grant = RowOfType(plan, Fixture.Oauth2GrantType);
        Assert.True(grant.IsUnknown);
        Assert.Equal("\"(deploy-time)\"", grant.After!["clientId"]!.ToJsonString());
        Assert.NotEqual("\"(deploy-time)\"", grant.After["resourceId"]!.ToJsonString());

        var user = Row(plan, Fixture.User);
        Assert.Equal("\"***\"", user.After!["passwordProfile"]!["password"]!.ToJsonString());
        Assert.DoesNotContain(user.Deltas, d => d.IsUnknown && !IsRelationshipDelta(host, Fixture.UserType, d));
    }

    [Fact]
    public async Task WhatIf_ConditionFalse_IsAnIgnoreRow_AndTrueIsACreate()
    {
        var host = new EngineHost();
        host.SeedExisting();

        var dev = await host.WhatIfAsync(await Templates.FixtureAsync(env: "dev"));
        var prod = await host.WhatIfAsync(await Templates.FixtureAsync(env: "prod"));

        var ignored = Row(dev, Fixture.Policy);
        Assert.Equal(ChangeType.Ignore, ignored.Type);
        Assert.Contains("condition evaluated to false", ignored.Reasons);
        Assert.False(ignored.IsWrite);

        var created = Row(prod, "/providers/Microsoft.Entra/conditionalAccess/policies/CA-Orders-Admins-MFA");
        Assert.Equal(ChangeType.Create, created.Type);
        Assert.True(created.IsUnknown, "the policy references the group, which does not exist yet");
        Assert.Equal(16, prod.Summary.Create);
    }

    [Fact]
    public async Task WhatIf_PublishesExistingObjects_SoLaterBatchesSeeConcreteValues()
    {
        var host = new EngineHost();
        host.SeedExisting();
        var group = host.Directory.AddGroup("contoso-dev-orders-admins", "Orders Admins", g =>
        {
            g["mailNickname"] = "orders-admins";
            g["isAssignableToRole"] = true;
        });

        var plan = await host.WhatIfAsync(await Templates.FixtureAsync());

        var groupRow = Row(plan, Fixture.Group);
        Assert.NotNull(groupRow.GraphId);
        Assert.DoesNotContain(groupRow.Deltas, d => !IsRelationshipDelta(host, Fixture.GroupType, d));
        var assignment = RowOfType(plan, Fixture.AppRoleAssignmentType);
        Assert.Equal("\"" + group["id"]!.GetValue<string>() + "\"", assignment.After!["principalId"]!.ToJsonString());
        Assert.True(assignment.IsUnknown, "resourceId still points at the service principal placeholder");
        Assert.True(plan.Resolver.IsPublished(ResourceIdentifier.Create(Fixture.GroupType, "contoso-dev-orders-admins")));
    }

    [Fact]
    public async Task MissingParameterValue_IsPromotedToAnError()
    {
        var host = new EngineHost();
        var document = await Templates.FixtureAsync(env: null);

        var validate = Assert.Throws<ValidationFailedException>(() => host.Planner().Validate(document, PlanMode.WhatIf));
        var plan = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(document));

        foreach (var error in new[] { validate, plan })
        {
            var missing = Assert.Single(error.Errors);
            Assert.Equal(DiagnosticCodes.MissingParameterValue, missing.Code);
            Assert.Equal(DiagnosticSeverity.Error, missing.Severity);
            Assert.Contains("Supply it with -p name=value or a parameter file", missing.Message);
            Assert.Contains("env", missing.Message);
        }

        Assert.Empty(host.Fake.Requests);
    }

    [Fact]
    public async Task KeyVaultReferenceReachingThePlanner_IsAnErrorForCreate_AndAWarningForWhatIf()
    {
        var host = new EngineHost();
        host.SeedExisting();
        var parameters = ParameterSet.Parse("{ \"parameters\": { \"initialPassword\": { \"reference\": { \"keyVault\": { \"id\": \"/subscriptions/s/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/contoso-kv\" }, \"secretName\": \"bg-initial\" } } } }");
        var document = await Templates.FixtureAsync(env: "dev", password: null, parameters: parameters);

        var create = Assert.Throws<ValidationFailedException>(() => host.Planner().Validate(document, PlanMode.Create));
        var createPlan = await Assert.ThrowsAsync<ValidationFailedException>(() => host.ExpandAsync(document));
        var warnings = host.Planner().Validate(document, PlanMode.WhatIf);
        var plan = await host.WhatIfAsync(document);

        var error = Assert.Single(create.Errors);
        Assert.Equal(EngineDiagnosticCodes.KeyVaultReferenceUnresolved, error.Code);
        Assert.Contains("initialPassword", error.Message);
        Assert.Equal("parameters.initialPassword", error.Path?.ToString());
        Assert.Equal(EngineDiagnosticCodes.KeyVaultReferenceUnresolved, Assert.Single(createPlan.Errors).Code);

        var warning = Assert.Single(warnings, d => d.Code == EngineDiagnosticCodes.KeyVaultReferenceUnresolved);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("deploy-time placeholder", warning.Message);
        Assert.Contains(plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.KeyVaultReferenceUnresolved && !d.IsError);
        Assert.Equal("\"(deploy-time)\"", Row(plan, Fixture.User).After!["passwordProfile"]!["password"]!.ToJsonString());
    }

    [Fact]
    public async Task UnknownProvider_ListsEveryMissingType()
    {
        var registry = new ProviderRegistry();
        registry.Register(new EchoProvider(ResourceTypeCatalogOf(Fixture.AppType)));
        registry.Register(new EchoProvider(ResourceTypeCatalogOf(Fixture.UserType)));
        var host = new EngineHost(registry: registry);

        var document = await Templates.FixtureAsync();

        var error = await Assert.ThrowsAsync<UnknownProviderException>(() => host.WhatIfAsync(document));

        Assert.Equal(13, error.Types.Count);
        Assert.Contains(Fixture.SpType, error.Types);
        Assert.Contains(Fixture.FicType, error.Types);
        Assert.Contains(Fixture.PolicyType, error.Types);
        Assert.DoesNotContain(Fixture.AppType, error.Types);
        Assert.Equal(error.Types.Distinct(StringComparer.OrdinalIgnoreCase).Count(), error.Types.Count);
        Assert.Equal([Fixture.AppType, Fixture.UserType], error.RegisteredTypes);
        Assert.Contains("No resource provider is registered for types", error.Message);
        Assert.Empty(host.Fake.Requests);
    }

    [Fact]
    public async Task RequiredPermissions_IsTheUnionOverEveryInstance()
    {
        var host = new EngineHost();
        host.SeedExisting();

        var plan = await host.WhatIfAsync(await Templates.FixtureAsync());

        string[] expected =
        [
            "AdministrativeUnit.ReadWrite.All",
            "AppRoleAssignment.ReadWrite.All",
            "Application.Read.All",
            "Application.ReadWrite.All",
            "Application.ReadWrite.OwnedBy",
            "AuthenticationContext.ReadWrite.All",
            "DelegatedPermissionGrant.ReadWrite.All",
            "Directory.Read.All",
            "Group.ReadWrite.All",
            "GroupMember.ReadWrite.All",
            "Policy.Read.All",
            "Policy.ReadWrite.ConditionalAccess",
            "RoleManagement.ReadWrite.Directory",
            "User.ReadWrite.All",
        ];
        Assert.Equal(expected.Order(StringComparer.Ordinal), plan.RequiredPermissions.Order(StringComparer.Ordinal));
    }

    [Fact]
    [Trait("Category", "Timing")]
    public async Task Parallelism_BoundsConcurrentGraphCalls()
    {
        string resources = string.Join(", ", Enumerable.Range(0, 8).Select(i => "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"u" + i + "@contoso.com\", \"properties\": { \"displayName\": \"U\" } }"));
        var document = Templates.Resources(resources);

        ConcurrencyGate? gate = null;
        var bounded = new EngineHost(inner => gate = new ConcurrencyGate(inner, TimeSpan.FromMilliseconds(300)));
        var boundedPlan = await bounded.WhatIfAsync(document, options: bounded.Options(parallelism: 2));
        int boundedMax = gate!.MaxObserved;

        var wide = new EngineHost(inner => gate = new ConcurrencyGate(inner, TimeSpan.FromMilliseconds(300)));
        var widePlan = await wide.WhatIfAsync(document, options: wide.Options(parallelism: 4));
        int wideMax = gate!.MaxObserved;

        Assert.Equal(8, boundedPlan.Changes.Count);
        Assert.Equal(8, widePlan.Changes.Count);
        Assert.Equal(2, boundedPlan.Parallelism);
        Assert.InRange(boundedMax, 1, 2);
        Assert.InRange(wideMax, 2, 4);
    }

    [Fact]
    public async Task ParallelismBelowOne_IsClampedToOne()
    {
        var host = new EngineHost();

        var plan = await host.WhatIfAsync(Templates.Resources("{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"u@contoso.com\", \"properties\": { \"displayName\": \"U\" } }"), options: host.Options(parallelism: 0));

        Assert.Equal(1, plan.Parallelism);
    }

    [Fact]
    public async Task ExistingReferenceNotFound_MakesTheRowUnsupported_WithoutFailingThePlan()
    {
        var host = new EngineHost();
        host.Directory.AddServicePrincipal(EngineHost.GraphAppId, "Microsoft Graph");
        host.Directory.AddGroup("contoso-all-staff", "All staff");

        // cio@contoso.com is not seeded: the user's manager lookup finds nothing.
        var plan = await host.WhatIfAsync(await Templates.FixtureAsync());

        var user = Row(plan, Fixture.User);
        Assert.True(user.HasError);
        Assert.Contains("cio@contoso.com", user.UnsupportedReason);
        Assert.Contains("was not found", user.UnsupportedReason);
        Assert.Equal(1, plan.Summary.Unsupported);
        Assert.Contains(plan.Changes, c => c.Id.Id == Fixture.App && !c.HasError);
    }

    [Fact]
    public async Task ExistingReferenceToAGuidNamedType_StaysAPlaceholder_AndIsReportedAsUnresolved()
    {
        var host = new EngineHost();
        var document = Templates.Resources("{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, \"description\": \"[reference(resourceId('Microsoft.Entra/appRoleAssignments', '11111111-1111-1111-1111-111111111111'), '2026-10-01-preview').id]\" } }");

        var plan = await host.WhatIfAsync(document);

        var unresolved = Assert.Single(plan.UnresolvedReferences);
        Assert.Equal("/providers/Microsoft.Entra/appRoleAssignments/11111111-1111-1111-1111-111111111111", unresolved.Id.Id);
        Assert.Equal("2026-10-01-preview", unresolved.ApiVersion);
        Assert.Contains("deterministic GUID", unresolved.Reason);
        var row = Assert.Single(plan.Changes);
        Assert.True(row.IsUnknown);
        Assert.Equal("\"(deploy-time)\"", row.After!["description"]!.ToJsonString());

        var json = WhatIfJson.Build(plan);
        var diagnostic = Assert.Single(json["diagnostics"]!.AsArray());
        Assert.Equal(EngineDiagnosticCodes.UnresolvedReference, diagnostic!["code"]!.GetValue<string>());
        Assert.Equal("Warning", diagnostic["level"]!.GetValue<string>());
    }

    [Fact]
    public async Task ReadFailure_IsReportedAsAnUnsupportedRow()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Fake.Failures.Forbid("/roleManagement/directory/roleDefinitions");

        var plan = await host.WhatIfAsync(await Templates.FixtureAsync());

        var row = Row(plan, Fixture.RoleDefinition);
        Assert.True(row.HasError);
        Assert.Contains("could not be read", row.UnsupportedReason);
        Assert.Contains("Authorization_RequestDenied", row.UnsupportedReason);
        Assert.Equal(1, plan.Summary.Unsupported);
    }

    [Fact]
    public async Task TransientReadFailures_AreRetriedThroughTheOuterPolicy()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Fake.Failures.Throttle("/users/bg-orders", times: 2, retryAfter: TimeSpan.FromSeconds(3));

        var plan = await host.WhatIfAsync(await Templates.FixtureAsync());

        Assert.False(Row(plan, Fixture.User).HasError);
        Assert.Equal([TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3)], host.Delays.Delays);
    }

    [Fact]
    public async Task WhatIfJson_MirrorsTheArmShape()
    {
        var host = new EngineHost();
        host.SeedExisting();
        var plan = await host.WhatIfAsync(await Templates.FixtureAsync(), options: host.Options(deploymentName: "orders"));

        var json = WhatIfJson.Build(plan);
        var idOnly = WhatIfJson.Build(plan, ResultFormat.ResourceIdOnly);

        Assert.Equal("Succeeded", json["status"]!.GetValue<string>());
        Assert.Equal("orders", json["deploymentName"]!.GetValue<string>());
        Assert.Equal(EngineHost.Tenant, json["tenantId"]!.GetValue<string>());
        Assert.Equal("Incremental", json["mode"]!.GetValue<string>());
        Assert.Equal(15, json["summary"]!["create"]!.GetValue<int>());
        Assert.Equal(1, json["summary"]!["ignore"]!.GetValue<int>());
        var changes = json["changes"]!.AsArray();
        Assert.Equal(Fixture.InstanceCount, changes.Count);
        var user = Assert.Single(changes, c => c!["resourceId"]!.GetValue<string>() == Fixture.User);
        Assert.Equal("Create", user!["changeType"]!.GetValue<string>());

        // ARM shape (what-if renderer): a Create carries the payload under "after" and no delta; secrets are ***.
        var after = user["after"]!.AsObject();
        var payload = after.ContainsKey("properties") ? after["properties"]!.AsObject() : after;
        Assert.Equal("***", payload["passwordProfile"]!["password"]!.GetValue<string>());
        Assert.Equal("Break Glass (Orders)", payload["displayName"]!.GetValue<string>());
        Assert.Null(user["before"]);
        Assert.Null(user["unsupportedReason"]);
        Assert.DoesNotContain(json.ToJsonString(), Templates.DefaultPassword, StringComparison.Ordinal);

        var idOnlyUser = Assert.Single(idOnly["changes"]!.AsArray(), c => c!["resourceId"]!.GetValue<string>() == Fixture.User);
        Assert.Null(idOnlyUser!["after"]);
        Assert.Null(idOnlyUser["delta"]);
    }

    [Fact]
    public async Task WhatIfText_RendersSymbolsAndSummary()
    {
        var host = new EngineHost();
        host.SeedExisting();
        var plan = await host.WhatIfAsync(await Templates.FixtureAsync());
        using var writer = new System.IO.StringWriter();

        WhatIfText.Render(plan, writer);
        string text = writer.ToString();

        Assert.Contains("15 to create, 0 to modify, 0 to delete, 0 unchanged, 1 ignored", text);
        Assert.StartsWith("Deployment '" + EngineHost.DeploymentName + "' (Incremental) on tenant " + EngineHost.Tenant, text, StringComparison.Ordinal);
        Assert.Contains("  + " + Fixture.RoleDefinition + " [" + Fixture.RoleDefinitionType + "]", text);

        // The user declares edges to resources that do not exist yet (memberOf, ownerOf, ...): the row is marked '?'.
        Assert.Contains("  +? " + Fixture.User + " [" + Fixture.UserType + "]", text);
        Assert.Contains("  * " + Fixture.Policy + " [" + Fixture.PolicyType + "]", text);
        Assert.Contains("condition evaluated to false", text);
        Assert.DoesNotContain(Templates.DefaultPassword, text, StringComparison.Ordinal);
    }

    // ---- Tenant(), environment() and the Entra lookups during planning ----

    [Fact]
    public async Task WhatIf_TenantAndEnvironment_AreConcreteOnlyWhenThePlannerIsGivenThem()
    {
        var document = Templates.Resources("{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, \"description\": \"[concat(tenant().displayName, '/', environment().name)]\" } }");
        var offline = new EngineHost();
        var online = new EngineHost();
        var options = online.Options() with
        {
            Tenant = new JsonObject { ["tenantId"] = EngineHost.Tenant, ["defaultDomain"] = "contoso.com", ["displayName"] = "Contoso" },
            Environment = DeploymentEnvironment.Environment(EArm.Graph.GraphCloud.Public),
        };

        var unknown = Assert.Single((await offline.WhatIfAsync(document)).Changes);
        var concrete = Assert.Single((await online.WhatIfAsync(document, options: options)).Changes);

        Assert.True(unknown.IsUnknown);
        Assert.Equal("\"(deploy-time)\"", unknown.After!["description"]!.ToJsonString());
        Assert.False(concrete.IsUnknown);
        Assert.Equal("\"Contoso/AzureCloud\"", concrete.After!["description"]!.ToJsonString());
    }

    [Fact]
    public async Task WhatIf_EntraLookups_ResolveLiveThroughGraph_AndTablesNeedNoRequest()
    {
        const string appId = "aaaaaaaa-1111-2222-3333-444444444444";
        var host = new EngineHost();
        host.Directory.AddServicePrincipal(appId, "Orders API", sp => sp["appRoles"] = new JsonArray(new JsonObject { ["id"] = "role-admin", ["value"] = "Orders.Admin" }));
        var document = Templates.Resources("{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, "
            + "\"description\": \"[appRole('" + appId + "', 'Orders.Admin')]\", \"theme\": \"[permissionScope(wellKnownApp('MicrosoftGraph'), 'User.Read')]\", \"classification\": \"[roleTemplate('Global Reader')]\" } }");

        var plan = await host.WhatIfAsync(document);

        var row = Assert.Single(plan.Changes);
        Assert.False(row.IsUnknown);
        Assert.Equal("\"role-admin\"", row.After!["description"]!.ToJsonString());
        Assert.Equal("\"e1fe6dd8-ba31-4d61-89e7-88639da4683d\"", row.After["theme"]!.ToJsonString());
        Assert.Equal("\"f2ef992c-3afb-46b9-b7cf-a126ee74c451\"", row.After["classification"]!.ToJsonString());
        Assert.Single(host.Fake.Requests, r => r.Path == "/servicePrincipals(appId='" + appId + "')");
        // Review 1D / G-01: the tenant is asked first even for Microsoft Graph (the embedded table is only the offline fallback); this fake
        // tenant has no Graph service principal, so the (verified) table answers.
        Assert.Contains(host.Fake.Requests, r => r.Path.Contains("00000003-0000-0000-c000-000000000000", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(host.Fake.Requests, r => r.Path.Contains("roleDefinitions", StringComparison.Ordinal));
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task WhatIf_EntraLookupWithoutAMatch_IsAnUnsupportedRow()
    {
        var host = new EngineHost();
        var document = Templates.Resources("{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, \"description\": \"[appRole('aaaaaaaa-1111-2222-3333-444444444444', 'Orders.Admin')]\" } }");

        var plan = await host.WhatIfAsync(document);

        var row = Assert.Single(plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains("no service principal or application with appId", row.UnsupportedReason);
    }

    /// <summary>True when the delta sits under a relationship property (edge-pass rows merged into the object row).</summary>
    private static bool IsRelationshipDelta(EngineHost host, string type, PropertyDelta delta)
    {
        return delta.Path.Depth > 1 && host.Catalog.Find(type)!.IsRelationshipProperty(delta.Path.Segments[1].Name ?? string.Empty);
    }

    private static EArm.Core.Types.ResourceTypeDefinition ResourceTypeCatalogOf(string type)
    {
        return EArm.Core.Types.ResourceTypeCatalog.Default.Find(type)!;
    }
}
