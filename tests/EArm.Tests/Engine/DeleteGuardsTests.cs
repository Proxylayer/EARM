using System;
using System.Text.Json.Nodes;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>The objects no delete may remove (<see cref="DeleteGuards"/>): the signed-in principal and service principals of other organizations.</summary>
public sealed class DeleteGuardsTests
{
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private const string SpType = "Microsoft.Entra/servicePrincipals";

    private static ExistingResource Live(string id, JsonObject? raw = null)
    {
        raw ??= new JsonObject { ["id"] = id };
        return new ExistingResource(id, raw, raw);
    }

    [Fact]
    public void TheSignedInPrincipal_IsRefused_WhateverTheCaseOfItsId()
    {
        string id = Guid.NewGuid().ToString("D");

        string? refusal = DeleteGuards.Refusal("/providers/Microsoft.Entra/users/me@contoso.com", "Microsoft.Entra/users", Live(id), id.ToUpperInvariant(), Tenant);

        Assert.NotNull(refusal);
        Assert.Contains("signed-in principal", refusal, StringComparison.Ordinal);
        Assert.Null(DeleteGuards.Refusal("/providers/Microsoft.Entra/users/other@contoso.com", "Microsoft.Entra/users", Live(id), Guid.NewGuid().ToString("D"), Tenant));
    }

    [Theory]
    [InlineData("f8cdef31-a31e-4b4a-93e4-5f571e91255a", true)]
    [InlineData(Tenant, false)]
    [InlineData(null, false)]
    public void AServicePrincipalOfAnotherOrganization_IsRefused(string? owner, bool refused)
    {
        var raw = new JsonObject { ["id"] = "sp-1", ["appOwnerOrganizationId"] = owner };

        string? refusal = DeleteGuards.Refusal("/providers/" + SpType + "/x", SpType, Live("sp-1", raw), null, Tenant);

        Assert.Equal(refused, refusal is not null);
        Assert.Equal(refused, DeleteGuards.IsFirstPartyServicePrincipal(SpType, raw, Tenant));
    }

    [Fact]
    public void AnotherOrganizationAsOwner_OfAnObjectThatIsNotAServicePrincipal_OrWithoutAKnownTenant_IsNotRefused()
    {
        var raw = new JsonObject { ["id"] = "x", ["appOwnerOrganizationId"] = "f8cdef31-a31e-4b4a-93e4-5f571e91255a" };

        Assert.False(DeleteGuards.IsFirstPartyServicePrincipal("Microsoft.Entra/applications", raw, Tenant));
        Assert.False(DeleteGuards.IsFirstPartyServicePrincipal(SpType, raw, null));
        Assert.False(DeleteGuards.IsFirstPartyServicePrincipal(SpType, null, Tenant));
    }
}
