using System;
using EArm.Core;
using Xunit;

namespace EArm.Tests;

public sealed class ResourceIdentifierTests
{
    private const string FicType = "Microsoft.Entra/applications/federatedIdentityCredentials";

    [Theory]
    [InlineData("Microsoft.Entra/applications", "orders-api", "/providers/Microsoft.Entra/applications/orders-api")]
    [InlineData("Microsoft.Entra/users", "bg-orders@contoso.com", "/providers/Microsoft.Entra/users/bg-orders@contoso.com")]
    [InlineData("Microsoft.Entra/conditionalAccess/policies", "CA-1", "/providers/Microsoft.Entra/conditionalAccess/policies/CA-1")]
    [InlineData("Microsoft.Entra/conditionalAccess/authenticationContextClassReferences", "c10", "/providers/Microsoft.Entra/conditionalAccess/authenticationContextClassReferences/c10")]
    [InlineData(FicType, "orders-api/github-main", "/providers/Microsoft.Entra/applications/orders-api/federatedIdentityCredentials/github-main")]
    [InlineData("Microsoft.Entra/applications/passwordCredentials", "orders-api/ci-secret", "/providers/Microsoft.Entra/applications/orders-api/passwordCredentials/ci-secret")]
    [InlineData("microsoft.entra/applications", "x", "/providers/Microsoft.Entra/applications/x")]
    public void Create_RendersTheTenantScopeId(string type, string name, string expectedId)
    {
        var identifier = ResourceIdentifier.Create(type, name);

        Assert.Equal(expectedId, identifier.Id);
        Assert.Equal(expectedId, identifier.ToString());
        Assert.Equal(name, identifier.Name);
    }

    [Fact]
    public void Segments_AreExposedForTypeAndName()
    {
        var identifier = ResourceIdentifier.Create(FicType, "orders-api/github-main");

        Assert.Equal(FicType, identifier.Type);
        Assert.Equal(["applications", "federatedIdentityCredentials"], identifier.TypeSegments);
        Assert.Equal("federatedIdentityCredentials", identifier.TypeLeaf);
        Assert.Equal(["orders-api", "github-main"], identifier.NameSegments);
        Assert.Equal("github-main", identifier.NameLeaf);
    }

    [Fact]
    public void Parent_OfAChild_IsTheParentIdentity()
    {
        var child = ResourceIdentifier.Create(FicType, "orders-api/github-main");

        var parent = child.Parent;

        Assert.NotNull(parent);
        Assert.Equal(ResourceIdentifier.Create("Microsoft.Entra/applications", "orders-api"), parent);
        Assert.Equal("/providers/Microsoft.Entra/applications/orders-api", parent.Id);
        Assert.Null(parent.Parent);
    }

    [Theory]
    [InlineData("Microsoft.Entra/applications", "orders-api")]
    [InlineData("Microsoft.Entra/conditionalAccess/policies", "CA-1")]
    public void Parent_OfATopLevelResource_IsNull(string type, string name)
    {
        Assert.Null(ResourceIdentifier.Create(type, name).Parent);
    }

    [Fact]
    public void Equality_IsCaseInsensitiveForTypesAndNames_BecauseGraphMatchesItsKeysThatWay()
    {
        var a = ResourceIdentifier.Create("Microsoft.Entra/applications", "orders-api");
        var sameTypeDifferentCase = ResourceIdentifier.Create("MICROSOFT.ENTRA/Applications", "orders-api");
        var differentNameCase = ResourceIdentifier.Create("Microsoft.Entra/applications", "Orders-Api");

        Assert.Equal(a, sameTypeDifferentCase);
        Assert.True(a.Equals((object)sameTypeDifferentCase));
        Assert.Equal(a.GetHashCode(), sameTypeDifferentCase.GetHashCode());

        // Review finding E-01: Graph alternate keys and filters are case-insensitive, so ids differing only by case are the same resource.
        Assert.Equal(a, differentNameCase);
        Assert.Equal(a.GetHashCode(), differentNameCase.GetHashCode());
        Assert.False(a.Equals(null));
        Assert.False(a.Equals("not an identifier"));
        Assert.NotEqual(a, ResourceIdentifier.Create("Microsoft.Entra/groups", "orders-api"));
    }

    [Fact]
    public void Equality_ConsidersEveryNameSegment()
    {
        var a = ResourceIdentifier.Create(FicType, "app/fic");
        var b = ResourceIdentifier.Create(FicType, "app/FIC");
        var c = ResourceIdentifier.Create(FicType, "APP/fic");

        Assert.Equal(a, b);
        Assert.Equal(a, c);
        Assert.NotEqual(a, ResourceIdentifier.Create(FicType, "app/other"));
        Assert.NotEqual(a, ResourceIdentifier.Create(FicType, "other/fic"));
        Assert.Equal(a, ResourceIdentifier.Create(FicType, "app/fic"));
    }

    [Theory]
    [InlineData("Microsoft.Entra/applications", "a/b")]
    [InlineData("Microsoft.Entra/applications", "a/b/c")]
    [InlineData(FicType, "a/b/c")]
    public void Create_RejectsMoreNameSegmentsThanTypeLevels(string type, string name)
    {
        var exception = Assert.Throws<ArgumentException>(() => ResourceIdentifier.Create(type, name));
        Assert.Equal("nameSegments", exception.ParamName);
    }

    [Theory]
    [InlineData("Microsoft.Entra/applications", "")]
    [InlineData(FicType, "a/")]
    [InlineData(FicType, "/b")]
    public void Create_RejectsEmptyNameSegments(string type, string name)
    {
        Assert.Throws<ArgumentException>(() => ResourceIdentifier.Create(type, name));
    }

    [Theory]
    [InlineData("Microsoft.Entra")]
    [InlineData("Microsoft.Entra/")]
    [InlineData(" ")]
    public void Create_RejectsTypesWithoutATypeSegment(string type)
    {
        Assert.Throws<ArgumentException>(() => ResourceIdentifier.Create(type, "x"));
    }

    [Fact]
    public void Create_AcceptsFewerNameSegmentsThanTypeLevels()
    {
        // The generic identity allows it (conditionalAccess/* types have two type segments and one name);
        // the catalog decides how many segments a known type really has.
        var identifier = ResourceIdentifier.Create(FicType, "only-child");

        Assert.Equal("/providers/Microsoft.Entra/applications/federatedIdentityCredentials/only-child", identifier.Id);
        Assert.Null(identifier.Parent);
    }

    [Fact]
    public void Constructor_RejectsNullOrEmptySegmentLists()
    {
        Assert.Throws<ArgumentNullException>(() => new ResourceIdentifier("Microsoft.Entra/applications", null!));
        Assert.Throws<ArgumentException>(() => new ResourceIdentifier("Microsoft.Entra/applications", []));
        Assert.Throws<ArgumentNullException>(() => ResourceIdentifier.Create("Microsoft.Entra/applications", null!));
    }

    [Theory]
    [InlineData(null, "x", false)]
    [InlineData("", "x", false)]
    [InlineData("Microsoft.Entra/applications", null, false)]
    [InlineData("Microsoft.Entra/applications", "", false)]
    [InlineData("Microsoft.Entra/applications", "a/b", false)]
    [InlineData("Microsoft.Entra/applications", "a", true)]
    [InlineData(FicType, "a/b", true)]
    public void TryCreate_ReturnsFalseInsteadOfThrowing(string? type, string? name, bool expected)
    {
        bool created = ResourceIdentifier.TryCreate(type, name, out var identifier);

        Assert.Equal(expected, created);
        Assert.Equal(expected, identifier is not null);
    }

    [Theory]
    [InlineData("microsoft.entra/applications", "Microsoft.Entra/applications")]
    [InlineData("MICROSOFT.ENTRA/Applications/FederatedIdentityCredentials", "Microsoft.Entra/Applications/FederatedIdentityCredentials")]
    [InlineData("/Microsoft.Entra/groups/", "Microsoft.Entra/groups")]
    [InlineData("  Microsoft.Entra/groups  ", "Microsoft.Entra/groups")]
    [InlineData("microsoft.entra", "Microsoft.Entra")]
    [InlineData("Other.Provider/things", "Other.Provider/things")]
    public void NormalizeType_CanonicalisesTheNamespaceAndTrims(string input, string expected)
    {
        Assert.Equal(expected, ResourceIdentifier.NormalizeType(input));
    }

    [Theory]
    [InlineData("Microsoft.Entra/applications", "microsoft.entra/APPLICATIONS", true)]
    [InlineData("Microsoft.Entra/applications", "/Microsoft.Entra/applications/", true)]
    [InlineData("Microsoft.Entra/applications", "Microsoft.Entra/groups", false)]
    [InlineData("Microsoft.Entra/applications", null, false)]
    [InlineData(null, null, false)]
    public void TypeEquals_IsCaseInsensitive(string? left, string? right, bool expected)
    {
        Assert.Equal(expected, ResourceIdentifier.TypeEquals(left, right));
    }

    [Fact]
    public void Constants_AreTheArmProviderConventions()
    {
        Assert.Equal("Microsoft.Entra", ResourceIdentifier.ProviderNamespace);
        Assert.Equal("/providers/", ResourceIdentifier.ProvidersPrefix);
    }
}
