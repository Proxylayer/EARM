using System.Collections.Generic;
using System.Linq;
using EArm.Core.Diagnostics;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// The adopt-only resource extension <c>"x-earm": { "adoptOnly": true, "objectId": "…" }</c> as the loader and the template schema read it (docs/format.md,
/// "Adopt-only resources").
/// </summary>
public sealed class AdoptOnlyDeclarationTests
{
    private const string Id = "11111111-2222-3333-4444-555555555555";

    private static string Group(string extension, string extra = "")
    {
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"ops\", \"properties\": { \"displayName\": \"Ops\", \"mailEnabled\": false, \"securityEnabled\": true }, \"x-earm\": " + extension + extra + " }";
    }

    private static List<Diagnostic> Errors(string resources, string parameters = "")
    {
        return Review1C.Validate(Review1C.Template(parameters + "\"resources\": [ " + resources + " ]")).Where(d => d.IsError).ToList();
    }

    [Theory]
    [InlineData("{ \"adoptOnly\": true }", false)]
    [InlineData("{ \"adoptOnly\": true, \"objectId\": \"" + Id + "\" }", true)]
    [InlineData("{ \"objectId\": \"" + Id + "\" }", true)]
    [InlineData("{ \"objectId\": \"[parameters('opsId')]\" }", true)]
    public void AValidDeclaration_IsLoaded_AndAPinImpliesAdoptOnly(string extension, bool pinned)
    {
        string parameters = "\"parameters\": { \"opsId\": { \"type\": \"string\", \"defaultValue\": \"" + Id + "\" } }, ";
        Assert.Empty(Errors(Group(extension), parameters));

        var resource = Expr.ParseBody(parameters + "\"resources\": [ " + Group(extension) + " ]").Resources.Single();
        Assert.NotNull(resource.AdoptOnly);
        Assert.Equal(pinned, resource.AdoptOnly!.ObjectId is not null);
        Assert.Equal("resources[0]['x-earm']", resource.AdoptOnly.Path.ToString());
    }

    [Fact]
    public void AdoptOnlyFalse_IsAnOrdinaryResource()
    {
        Assert.Empty(Errors(Group("{ \"adoptOnly\": false }")));
        Assert.Null(Expr.ParseBody("\"resources\": [ " + Group("{ \"adoptOnly\": false }") + " ]").Resources.Single().AdoptOnly);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("{ \"adoptOnly\": \"yes\" }")]
    [InlineData("{ \"adoptOnly\": true, \"pin\": \"x\" }")]
    [InlineData("{ \"objectId\": \"ops\" }")]
    [InlineData("{ \"objectId\": \"\" }")]
    [InlineData("{ \"adoptOnly\": false, \"objectId\": \"" + Id + "\" }")]
    public void AMalformedDeclaration_IsInvalidAdoptOnly(string extension)
    {
        Assert.Contains(Errors(Group(extension)), d => d.Code == DiagnosticCodes.InvalidAdoptOnly);
    }

    [Fact]
    public void AdoptOnly_CannotBeCombinedWithExisting()
    {
        string body = "\"languageVersion\": \"2.0\", \"resources\": { \"ops\": { \"existing\": true, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"ops\", \"x-earm\": { \"adoptOnly\": true } } }";

        Assert.Contains(Review1C.Validate(Review1C.Template(body)), d => d.Code == DiagnosticCodes.InvalidAdoptOnly && d.IsError);
    }

    [Fact]
    public void ANestedChild_MayBeAdoptOnly()
    {
        string child = "{ \"type\": \"federatedIdentityCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"github-main\", \"properties\": { \"issuer\": \"https://token.actions.githubusercontent.com\", \"subject\": \"repo:o/r:ref:refs/heads/main\", \"audiences\": [\"api://AzureADTokenExchange\"] }, \"x-earm\": { \"adoptOnly\": true } }";
        string app = "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app\", \"properties\": { \"displayName\": \"App\" }, \"resources\": [ " + child + " ] }";

        Assert.Empty(Errors(app));
        Assert.NotNull(Expr.ParseBody("\"resources\": [ " + app + " ]").Resources.Single(r => r.IsNested).AdoptOnly);
    }
}
