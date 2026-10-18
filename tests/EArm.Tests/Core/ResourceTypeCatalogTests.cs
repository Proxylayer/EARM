using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Core;
using EArm.Core.Types;
using Xunit;

namespace EArm.Tests;

public sealed class ResourceTypeCatalogTests
{
    private static readonly ResourceTypeCatalog Catalog = ResourceTypeCatalog.Default;

    /// <summary>The fifteen v1 types, in catalog (case-insensitive ordinal) order.</summary>
    private static readonly string[] ExpectedTypes =
    [
        "Microsoft.Entra/administrativeUnits",
        "Microsoft.Entra/applications",
        "Microsoft.Entra/applications/federatedIdentityCredentials",
        "Microsoft.Entra/applications/passwordCredentials",
        "Microsoft.Entra/appRoleAssignments",
        "Microsoft.Entra/authenticationMethodConfigurations",
        "Microsoft.Entra/conditionalAccess/authenticationContextClassReferences",
        "Microsoft.Entra/conditionalAccess/authenticationStrengths",
        "Microsoft.Entra/conditionalAccess/namedLocations",
        "Microsoft.Entra/conditionalAccess/policies",
        "Microsoft.Entra/entitlementManagement/accessPackages",
        "Microsoft.Entra/entitlementManagement/accessPackages/assignmentPolicies",
        "Microsoft.Entra/entitlementManagement/catalogResources",
        "Microsoft.Entra/entitlementManagement/catalogs",
        "Microsoft.Entra/entitlementManagement/resourceRoleScopes",
        "Microsoft.Entra/groupLifecyclePolicies",
        "Microsoft.Entra/groups",
        "Microsoft.Entra/oauth2PermissionGrants",
        "Microsoft.Entra/policies/claimsMappingPolicies",
        "Microsoft.Entra/policies/tokenLifetimePolicies",
        "Microsoft.Entra/privilegedAccess/groupAssignmentScheduleRequests",
        "Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests",
        "Microsoft.Entra/roleAssignments",
        "Microsoft.Entra/roleAssignmentScheduleRequests",
        "Microsoft.Entra/roleDefinitions",
        "Microsoft.Entra/roleEligibilityScheduleRequests",
        "Microsoft.Entra/roleManagementPolicies",
        "Microsoft.Entra/servicePrincipals",
        "Microsoft.Entra/users",
    ];

    public static TheoryData<string> AllTypes()
    {
        var data = new TheoryData<string>();
        foreach (var type in ExpectedTypes)
        {
            data.Add(type);
        }

        return data;
    }

    private static IReadOnlyList<string> SampleNames(ResourceTypeDefinition definition)
    {
        return definition.Depth == 1 ? ["name-1"] : ["parent-1", "child-1"];
    }

    [Fact]
    public void Default_ContainsExactlyTheTwentySixTypes()
    {
        Assert.Equal(ExpectedTypes, Catalog.Types.Select(t => t.Type).ToList());
        Assert.Equal(29, EmbeddedSchemas.NamesUnder(EmbeddedSchemas.TypeSchemaPrefix).Count);
        Assert.Same(Catalog, ResourceTypeCatalog.Default);
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void ResourceId_RoundTripsThroughTryParseResourceId(string type)
    {
        Assert.True(Catalog.TryGet(type, out var definition));
        var names = SampleNames(definition);
        var identifier = new ResourceIdentifier(type, names);

        Assert.True(Catalog.TryParseResourceId(identifier.Id, out var parsed), identifier.Id);
        Assert.NotNull(parsed);
        Assert.Equal(identifier, parsed);
        Assert.Equal(definition.Type, parsed.Type);
        Assert.Equal(names, parsed.NameSegments);
        Assert.Equal(identifier.Id, parsed.Id);
        Assert.Equal(definition.Depth, parsed.NameSegments.Count);
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void TryGetGraphApi_MapsBothApiVersionsAndRejectsOthers(string type)
    {
        Assert.True(Catalog.TryGet(type, out var definition));

        Assert.True(definition.TryGetGraphApi("2026-10-01-preview", out var v1));
        Assert.Equal(GraphApi.V1, v1);
        Assert.True(definition.TryGetGraphApi("2026-10-01-preview.beta", out var beta));
        Assert.Equal(GraphApi.Beta, beta);

        Assert.True(Catalog.TryGetGraphApi(type, "2026-10-01-preview", out var viaCatalog));
        Assert.Equal(GraphApi.V1, viaCatalog);
        Assert.True(Catalog.TryGetGraphApi(type.ToUpperInvariant(), "2026-10-01-preview.beta", out var viaCatalogBeta));
        Assert.Equal(GraphApi.Beta, viaCatalogBeta);

        Assert.False(definition.TryGetGraphApi("2020-01-01", out _));
        Assert.False(definition.TryGetGraphApi("2026-10-01", out _));
        Assert.False(Catalog.TryGetGraphApi(type, "2026-10-01", out _));
        Assert.False(definition.TryGetGraphApi("v1.0", out _));
        Assert.False(definition.TryGetGraphApi("beta", out _));
        Assert.False(definition.TryGetGraphApi(null, out _));
        Assert.Equal(2, definition.ApiVersions.Count);
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void DescribeUnknownApiVersion_ExplainsTheRenameForTheRetiredLabelAndListsTheValidOnesOtherwise(string type)
    {
        Assert.True(Catalog.TryGet(type, out var definition));

        Assert.Equal(
            "apiVersion '2026-10-01' was renamed '2026-10-01-preview' (the format is in preview); use '2026-10-01-preview' (Graph v1.0) or '2026-10-01-preview.beta' (Graph beta). Valid: 2026-10-01-preview, 2026-10-01-preview.beta.",
            definition.DescribeUnknownApiVersion("2026-10-01"));

        string other = definition.DescribeUnknownApiVersion("2020-01-01");
        Assert.Contains("'2020-01-01'", other, StringComparison.Ordinal);
        Assert.Contains(definition.Type, other, StringComparison.Ordinal);
        Assert.Contains("2026-10-01-preview, 2026-10-01-preview.beta", other, StringComparison.Ordinal);
        Assert.DoesNotContain("renamed", other, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(AllTypes))]
    public void Overlay_DeclaresGraphPathNameMappingAndPermissions(string type)
    {
        Assert.True(Catalog.TryGet(type, out var definition));

        Assert.Equal(type, definition.Type);
        Assert.StartsWith("/", definition.GraphPath);
        Assert.False(string.IsNullOrWhiteSpace(definition.Description), "description");
        Assert.True(Enum.IsDefined(definition.NameKind), "nameKind");

        // GUID-named (composite key) types have no property the name is injected into; every other type does.
        if (definition.NameKind == ResourceNameKind.Guid)
        {
            Assert.Null(definition.NameMapsTo);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(definition.NameMapsTo), "nameMapsTo");
        }

        Assert.NotEmpty(definition.Permissions.Application);
        // Delegated scopes come from the providers' conditional rules (RunScopes); an overlay list would be an unused, drifting union.
        Assert.Null(definition.Permissions.Raw?["delegated"]);
        // Every type names the Entra roles a user needs: alternatives (roles), or roles needed together (rolesAllOf: catalog resources).
        Assert.True(definition.Permissions.Roles.Count > 0 || definition.Permissions.Raw?["rolesAllOf"] is System.Text.Json.Nodes.JsonArray { Count: > 1 }, "roles or rolesAllOf");
        Assert.NotNull(definition.Permissions.Raw);
        Assert.NotNull(definition.Schema);
        Assert.StartsWith(EmbeddedSchemas.TypeSchemaPrefix, definition.SchemaResourceName);
    }

    [Fact]
    public void OverlayNotes_NameTheGovernanceLicence_WithOneWording()
    {
        // docs/permissions.md says "Microsoft Entra ID P2 or ID Governance"; the overlay notes say it the same way.
        var notes = Catalog.Types
            .Select(t => (t.Type, Notes: t.Permissions.Raw?["notes"]?.GetValue<string>() ?? string.Empty))
            .Where(n => n.Notes.Contains("P2", StringComparison.Ordinal))
            .ToList();

        Assert.NotEmpty(notes);
        Assert.All(notes, n => Assert.True(n.Notes.Contains("Microsoft Entra ID P2 or ID Governance", StringComparison.Ordinal), n.Type + ": " + n.Notes));
    }

    [Fact]
    public void ChildTypes_DeclareApplicationsAsParentAndDepthTwo()
    {
        var children = Catalog.ChildrenOf("microsoft.entra/APPLICATIONS");

        Assert.Equal(
            ["Microsoft.Entra/applications/federatedIdentityCredentials", "Microsoft.Entra/applications/passwordCredentials"],
            children.Select(c => c.Type).ToList());
        Assert.All(children, child =>
        {
            Assert.True(child.IsChildType);
            Assert.Equal("Microsoft.Entra/applications", child.ParentType);
            Assert.Equal(2, child.Depth);
            Assert.Equal(ResourceNameKind.ChildName, child.NameKind);
            Assert.Contains("{parentId}", child.GraphPath);
        });

        Assert.Equal(26, Catalog.Types.Count(t => !t.IsChildType));
        Assert.All(Catalog.Types.Where(t => !t.IsChildType), t => Assert.Equal(1, t.Depth));
        Assert.Empty(Catalog.ChildrenOf("Microsoft.Entra/groups"));
    }

    [Fact]
    public void NameKinds_MatchThePlanCatalog()
    {
        Assert.Equal(ResourceNameKind.UniqueName, Catalog.Find("Microsoft.Entra/applications")!.NameKind);
        Assert.Equal("uniqueName", Catalog.Find("Microsoft.Entra/groups")!.NameMapsTo);
        Assert.Equal(ResourceNameKind.UserPrincipalName, Catalog.Find("Microsoft.Entra/users")!.NameKind);
        Assert.Equal(ResourceNameKind.AppId, Catalog.Find("Microsoft.Entra/servicePrincipals")!.NameKind);
        Assert.Equal(ResourceNameKind.Id, Catalog.Find("Microsoft.Entra/conditionalAccess/authenticationContextClassReferences")!.NameKind);
        Assert.Equal(ResourceNameKind.Id, Catalog.Find("Microsoft.Entra/authenticationMethodConfigurations")!.NameKind);
        Assert.Equal("^c[1-9][0-9]?$", Catalog.Find("Microsoft.Entra/conditionalAccess/authenticationContextClassReferences")!.NamePattern);
        Assert.Equal(ResourceNameKind.Guid, Catalog.Find("Microsoft.Entra/appRoleAssignments")!.NameKind);
        Assert.Equal(ResourceNameKind.Guid, Catalog.Find("Microsoft.Entra/oauth2PermissionGrants")!.NameKind);
        Assert.Equal(ResourceNameKind.Guid, Catalog.Find("Microsoft.Entra/roleEligibilityScheduleRequests")!.NameKind);
        Assert.Equal(ResourceNameKind.Guid, Catalog.Find("Microsoft.Entra/roleManagementPolicies")!.NameKind);
        Assert.Equal(ResourceNameKind.Guid, Catalog.Find("Microsoft.Entra/privilegedAccess/groupEligibilityScheduleRequests")!.NameKind);
        Assert.Equal(ResourceNameKind.Guid, Catalog.Find("Microsoft.Entra/roleAssignmentScheduleRequests")!.NameKind);
        Assert.Equal(ResourceNameKind.Guid, Catalog.Find("Microsoft.Entra/privilegedAccess/groupAssignmentScheduleRequests")!.NameKind);
        Assert.Equal(ResourceNameKind.Guid, Catalog.Find("Microsoft.Entra/roleAssignments")!.NameKind);
        Assert.Equal(ResourceNameKind.DisplayName, Catalog.Find("Microsoft.Entra/conditionalAccess/policies")!.NameKind);
        Assert.Equal("^[A-Za-z0-9][A-Za-z0-9_-]{2,119}$", Catalog.Find("Microsoft.Entra/applications/federatedIdentityCredentials")!.NamePattern);
        Assert.Equal("displayName", Catalog.Find("Microsoft.Entra/applications/passwordCredentials")!.NameMapsTo);
    }

    [Theory]
    [InlineData("/providers/Microsoft.Entra/applications/orders-api", "Microsoft.Entra/applications", "orders-api")]
    [InlineData("/PROVIDERS/microsoft.entra/APPLICATIONS/Orders-Api", "Microsoft.Entra/applications", "Orders-Api")]
    [InlineData("  /providers/Microsoft.Entra/users/bg-orders@contoso.com  ", "Microsoft.Entra/users", "bg-orders@contoso.com")]
    [InlineData("/providers/Microsoft.Entra/conditionalAccess/policies/CA-Orders-Admins-MFA", "Microsoft.Entra/conditionalAccess/policies", "CA-Orders-Admins-MFA")]
    [InlineData("/providers/Microsoft.Entra/conditionalaccess/NamedLocations/Contoso offices", "Microsoft.Entra/conditionalAccess/namedLocations", "Contoso offices")]
    [InlineData("/providers/Microsoft.Entra/applications/orders-api/federatedIdentityCredentials/github-main", "Microsoft.Entra/applications/federatedIdentityCredentials", "orders-api/github-main")]
    [InlineData("/providers/Microsoft.Entra/applications/orders-api/passwordCredentials/ci-secret", "Microsoft.Entra/applications/passwordCredentials", "orders-api/ci-secret")]
    public void TryParseResourceId_ParsesKnownIdsCaseInsensitivelyWithCanonicalTypes(string id, string expectedType, string expectedName)
    {
        Assert.True(Catalog.TryParseResourceId(id, out var identifier));

        Assert.NotNull(identifier);
        Assert.Equal(expectedType, identifier.Type);
        Assert.Equal(expectedName, identifier.Name);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("orders-api")]
    [InlineData("/subscriptions/x/providers/Microsoft.Entra/applications/a")]
    [InlineData("/providers/Microsoft.Other/applications/a")]
    [InlineData("/providers/Microsoft.Entra")]
    [InlineData("/providers/Microsoft.Entra/applications")]
    [InlineData("/providers/Microsoft.Entra/bogus/a")]
    [InlineData("/providers/Microsoft.Entra/applications/a/b")]
    [InlineData("/providers/Microsoft.Entra/applications/a/federatedIdentityCredentials")]
    [InlineData("/providers/Microsoft.Entra/conditionalAccess/policies")]
    [InlineData("/providers/Microsoft.Entra/conditionalAccess/a")]
    [InlineData("/providers/Microsoft.Entra/groups/g/federatedIdentityCredentials/f")]
    [InlineData("/providers/Microsoft.Entra/federatedIdentityCredentials/f")]
    public void TryParseResourceId_RejectsMalformedOrUnknownIds(string? id)
    {
        Assert.False(Catalog.TryParseResourceId(id, out var identifier));
        Assert.Null(identifier);
    }

    [Theory]
    [InlineData("microsoft.entra/APPLICATIONS", "Microsoft.Entra/applications")]
    [InlineData("Microsoft.Entra/Applications/FederatedIdentityCredentials", "Microsoft.Entra/applications/federatedIdentityCredentials")]
    [InlineData("microsoft.entra/unknownThings", "Microsoft.Entra/unknownThings")]
    public void CanonicalType_FixesCasingOfKnownTypes(string input, string expected)
    {
        Assert.Equal(expected, Catalog.CanonicalType(input));
    }

    [Fact]
    public void Lookups_AreCaseInsensitiveAndNullSafe()
    {
        Assert.True(Catalog.IsKnownType("MICROSOFT.ENTRA/USERS"));
        Assert.False(Catalog.IsKnownType("Microsoft.Entra/widgets"));
        Assert.False(Catalog.IsKnownType(null));
        Assert.Null(Catalog.Find(null));
        Assert.NotNull(Catalog.Find("/microsoft.entra/groups/"));

        var byLeaf = Catalog.FindByLeaf("POLICIES");
        Assert.Equal("Microsoft.Entra/conditionalAccess/policies", Assert.Single(byLeaf).Type);
        Assert.Empty(Catalog.FindByLeaf("widgets"));
    }

    [Theory]
    [InlineData("https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#", true)]
    [InlineData("https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json", true)]
    [InlineData(" HTTPS://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json# ", true)]
    [InlineData("https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentParameters.json#", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsTemplateSchemaRecognized_AcceptsTheTemplateSchemaWithOrWithoutFragment(string? schema, bool expected)
    {
        Assert.Equal(expected, ResourceTypeCatalog.IsTemplateSchemaRecognized(schema));
    }

    [Fact]
    public void SchemaConstants_MatchTheEmbeddedResources()
    {
        Assert.Equal("2026-10-01", ResourceTypeCatalog.SchemaVersion);
        Assert.Equal("https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json", ResourceTypeCatalog.TemplateSchemaUri);
        Assert.Equal("https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentParameters.json", ResourceTypeCatalog.ParametersSchemaUri);
        Assert.Contains(EmbeddedSchemas.TemplateSchemaResource, EmbeddedSchemas.Names);
        Assert.Contains(EmbeddedSchemas.ParametersSchemaResource, EmbeddedSchemas.Names);
        Assert.Equal(31, EmbeddedSchemas.Names.Count);
        Assert.Contains("\"$id\": \"https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json\"", EmbeddedSchemas.ReadText(EmbeddedSchemas.TemplateSchemaResource));
        Assert.NotEmpty(EmbeddedSchemas.ReadBytes(EmbeddedSchemas.ParametersSchemaResource));
    }

    [Fact]
    public void Overlay_ParsesStructuredSections()
    {
        var applications = Catalog.Find("Microsoft.Entra/applications")!;

        Assert.Contains("owners", applications.Relationships.Select(r => r.Property));
        Assert.True(applications.IsRelationshipProperty("OWNERS"));
        Assert.False(applications.IsRelationshipProperty("displayName"));
        Assert.Contains(applications.Immutable, p => p.ToString() == "uniqueName");
        Assert.False(applications.ImmutableAll);
        Assert.NotEmpty(applications.KeyProperties);

        var passwords = Catalog.Find("Microsoft.Entra/applications/passwordCredentials")!;
        Assert.True(passwords.ImmutableAll);

        var users = Catalog.Find("Microsoft.Entra/users")!;
        var manager = Assert.Single(users.Relationships, r => r.Property == "manager");
        Assert.Equal(RelationshipKind.Manager, manager.Kind);
        Assert.Equal(RelationshipSide.Principal, manager.Side);
        Assert.False(manager.IsAuthoritativeSet);

        var groups = Catalog.Find("Microsoft.Entra/groups")!;
        var members = Assert.Single(groups.Relationships, r => r.Property == "members");
        Assert.Equal(RelationshipSide.Target, members.Side);
        Assert.True(members.IsAuthoritativeSet);
    }

    [Fact]
    public void ParseOverlay_RejectsSchemasWithoutAnOverlay()
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ResourceTypeCatalog.ParseOverlay("{ \"type\": \"object\" }", "test.json"));
        Assert.Contains("x-earm", exception.Message);
    }

    [Theory]
    [InlineData("{ \"x-earm\": { \"graphPath\": \"/x\", \"apiVersions\": { \"2026-10-01-preview\": \"v1.0\" }, \"nameKind\": \"displayName\" } }", "type")]
    [InlineData("{ \"x-earm\": { \"type\": \"Microsoft.Entra/x\", \"apiVersions\": { \"2026-10-01-preview\": \"v1.0\" }, \"nameKind\": \"displayName\" } }", "graphPath")]
    [InlineData("{ \"x-earm\": { \"type\": \"Microsoft.Entra/x\", \"graphPath\": \"/x\", \"nameKind\": \"displayName\" } }", "apiVersions")]
    [InlineData("{ \"x-earm\": { \"type\": \"Microsoft.Entra/x\", \"graphPath\": \"/x\", \"apiVersions\": { \"2026-10-01-preview\": \"v3\" }, \"nameKind\": \"displayName\" } }", "v3")]
    [InlineData("{ \"x-earm\": { \"type\": \"Microsoft.Entra/x\", \"graphPath\": \"/x\", \"apiVersions\": { \"2026-10-01-preview\": \"v1.0\" } } }", "nameKind")]
    public void ParseOverlay_RejectsIncompleteOverlays(string schemaJson, string expectedMention)
    {
        var exception = Assert.Throws<InvalidOperationException>(() => ResourceTypeCatalog.ParseOverlay(schemaJson, "test.json"));
        Assert.Contains(expectedMention, exception.Message);
    }

    [Fact]
    public void FromDefinitions_RejectsUnknownParentsAndDuplicates()
    {
        var orphan = ResourceTypeCatalog.ParseOverlay(
            "{ \"x-earm\": { \"type\": \"Microsoft.Entra/things/parts\", \"parentType\": \"Microsoft.Entra/things\", \"graphPath\": \"/x\", \"apiVersions\": { \"2026-10-01-preview\": \"v1.0\" }, \"nameKind\": \"childName\" } }",
            "orphan.json");
        Assert.Throws<InvalidOperationException>(() => ResourceTypeCatalog.FromDefinitions([orphan]));

        var thing = ResourceTypeCatalog.ParseOverlay(
            "{ \"x-earm\": { \"type\": \"Microsoft.Entra/things\", \"graphPath\": \"/x\", \"apiVersions\": { \"2026-10-01-preview\": \"v1.0\" }, \"nameKind\": \"displayName\" } }",
            "thing.json");
        Assert.Throws<InvalidOperationException>(() => ResourceTypeCatalog.FromDefinitions([thing, thing]));

        var custom = ResourceTypeCatalog.FromDefinitions([thing, orphan]);
        Assert.Equal(2, custom.Types.Count);
        Assert.Equal(2, custom.Find("Microsoft.Entra/things/parts")!.Depth);
        Assert.True(custom.TryParseResourceId("/providers/Microsoft.Entra/things/t/parts/p", out var id));
        Assert.Equal("t/p", id!.Name);
    }

    [Theory]
    [InlineData("v1.0", GraphApi.V1)]
    [InlineData("V1", GraphApi.V1)]
    [InlineData(" beta ", GraphApi.Beta)]
    public void GraphApis_TryParse_AcceptsOverlaySpellings(string text, GraphApi expected)
    {
        Assert.True(GraphApis.TryParse(text, out var api));
        Assert.Equal(expected, api);
    }

    [Fact]
    public void GraphApis_SegmentsAndRejections()
    {
        Assert.Equal("v1.0", GraphApis.Segment(GraphApi.V1));
        Assert.Equal("beta", GraphApis.Segment(GraphApi.Beta));
        Assert.False(GraphApis.TryParse("v2", out _));
        Assert.False(GraphApis.TryParse(null, out _));
    }
}
