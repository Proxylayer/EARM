using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Expressions.Evaluation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// <c>reference(nameOrId, apiVersion, 'Full')</c> (https://learn.microsoft.com/azure/azure-resource-manager/templates/template-functions-resource#reference):
/// the full resource object, reduced to what applies to Entra.
/// </summary>
public sealed class ReferenceFullTests
{
    private static readonly ResourceIdentifier Group = ResourceIdentifier.Create("Microsoft.Entra/groups", "g1");

    [Fact]
    public void Full_OfATemplateResource_WrapsTheLiveObject()
    {
        var resolver = new InMemoryReferenceResolver();
        resolver.Publish(Group, new JsonObject { ["id"] = "0000-1", ["displayName"] = "G1" }, "0000-1");
        var context = Expr.Context(resources: Expr.Index(("Microsoft.Entra/groups", "g1")), references: resolver);

        var full = Expr.Eval("[reference('g1', '2026-10-01-preview', 'Full')]", context)!.AsObject();

        Assert.Equal("2026-10-01-preview", full["apiVersion"]!.GetValue<string>());
        Assert.Equal(Group.Id, full["resourceId"]!.GetValue<string>());
        Assert.Equal("G1", full["properties"]!["displayName"]!.GetValue<string>());
        Assert.Equal("G1", Expr.AssertString(Expr.Eval("[reference('g1', '2026-10-01-preview', 'full').properties.displayName]", context)));
    }

    [Fact]
    public void Full_OfAnExistingResource_WrapsTheLookup()
    {
        var resolver = new InMemoryReferenceResolver();
        var existing = ResourceIdentifier.Create("Microsoft.Entra/groups", "outside");
        resolver.Publish(existing, new JsonObject { ["id"] = "0000-2" }, "0000-2");

        var full = Expr.Eval("[reference(resourceId('Microsoft.Entra/groups', 'outside'), '2026-10-01-preview', 'Full')]", Expr.Context(references: resolver))!.AsObject();

        Assert.Equal(existing.Id, full["resourceId"]!.GetValue<string>());
        Assert.Equal("0000-2", full["properties"]!["id"]!.GetValue<string>());
    }

    [Fact]
    public void Full_Offline_KeepsTheResourceIdKnown_AndThePropertiesDeployTime()
    {
        var context = Expr.Context(resources: Expr.Index(("Microsoft.Entra/groups", "g1")));

        Assert.Equal(Group.Id, Expr.AssertString(Expr.Eval("[reference('g1', '2026-10-01-preview', 'Full').resourceId]", context)));
        Expr.AssertUnknown(Expr.Eval("[reference('g1', '2026-10-01-preview', 'Full').properties.id]", context));
    }

    [Fact]
    public void AThirdArgumentOtherThanFull_IsRefused()
    {
        var context = Expr.Context(resources: Expr.Index(("Microsoft.Entra/groups", "g1")));

        Expr.Fails("[reference('g1', '2026-10-01-preview', 'Properties')]", DiagnosticCodes.InvalidFunctionArguments, context);
    }
}
