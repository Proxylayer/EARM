using System.Collections.Generic;
using System.Linq;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Offline checks of federated identity credential declarations: duplicate literal audiences, and a literal issuer + subject pair declared twice
/// for one application (by two declarations, or by every iteration of a copy loop). Expression values are left to the provider's plan-time check.
/// </summary>
public sealed class FederatedCredentialCheckerTests
{
    private const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";
    private const string Issuer = "https://token.actions.githubusercontent.com";

    private static readonly TemplateLoader Loader = new();
    private static readonly TemplateValidator Validator = new();

    private static IReadOnlyList<Diagnostic> Validate(string resources)
    {
        string json = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"resources\": [ " + resources + " ] }";
        return Validator.Validate(Loader.Parse(json, "inline.entra.json", ParameterSet.Empty));
    }

    private static string App(string nested = "")
    {
        return "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"orders-api\", \"properties\": { \"displayName\": \"Orders\" }" + nested + " }";
    }

    private static string Fic(string name, string subject, string audiences = "[\"api://AzureADTokenExchange\"]", string copy = "", string app = "orders-api/", string type = "Microsoft.Entra/applications/federatedIdentityCredentials")
    {
        return "{ \"type\": \"" + type + "\", \"apiVersion\": \"2026-10-01-preview\", " + copy + "\"name\": \"" + app + name + "\", \"properties\": { \"issuer\": \"" + Issuer + "\", \"subject\": \"" + subject + "\", \"audiences\": " + audiences + " } }";
    }

    private static Diagnostic[] Of(IReadOnlyList<Diagnostic> diagnostics, string code)
    {
        return diagnostics.Where(d => d.Code == code).ToArray();
    }

    [Fact]
    public void DuplicateAudiences_AreAnError_AtTheSecondOccurrence()
    {
        var diagnostics = Validate(App() + ", " + Fic("github-0", "repo:o/r:ref:refs/heads/main", "[\"api://AzureADTokenExchange\", \"api://AzureADTokenExchange\"]"));

        var error = Assert.Single(Of(diagnostics, DiagnosticCodes.DuplicateFederatedCredentialAudience));
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.Equal("resources[1].properties.audiences[1]", error.Path!.ToString());
    }

    [Fact]
    public void DistinctAudiences_AndExpressionAudiences_PassTheCheck()
    {
        var diagnostics = Validate(App() + ", " + Fic("github-0", "repo:o/r:ref:refs/heads/main", "[\"api://AzureADTokenExchange\", \"api://other\", \"[parameters('a')]\", \"[parameters('a')]\"]"));

        Assert.Empty(Of(diagnostics, DiagnosticCodes.DuplicateFederatedCredentialAudience));
    }

    [Fact]
    public void TwoCredentialsOfOneApplication_WithTheSameIssuerAndSubject_AreAnError()
    {
        var diagnostics = Validate(App() + ", " + Fic("github-0", "repo:o/r:ref:refs/heads/main") + ", " + Fic("github-1", "repo:o/r:ref:refs/heads/main"));

        var error = Assert.Single(Of(diagnostics, DiagnosticCodes.DuplicateFederatedCredentialSubject));
        Assert.Equal("resources[2].properties.subject", error.Path!.ToString());
    }

    [Fact]
    public void NestedAndFlatCredentialsOfOneApplication_AreComparedToo()
    {
        string nested = ", \"resources\": [ " + Fic("github-0", "repo:o/r:ref:refs/heads/main", app: string.Empty, type: "federatedIdentityCredentials") + " ]";
        var diagnostics = Validate(App(nested) + ", " + Fic("github-1", "repo:o/r:ref:refs/heads/main"));

        Assert.Single(Of(diagnostics, DiagnosticCodes.DuplicateFederatedCredentialSubject));
    }

    [Fact]
    public void TheSamePair_OnDifferentApplications_OrWithDifferentSubjects_IsFine()
    {
        string otherApp = "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"billing-api\", \"properties\": { \"displayName\": \"Billing\" } }";
        var diagnostics = Validate(App() + ", " + otherApp + ", "
            + Fic("github-0", "repo:o/r:ref:refs/heads/main") + ", "
            + Fic("github-0", "repo:o/r:ref:refs/heads/main", app: "billing-api/") + ", "
            + Fic("github-1", "repo:o/r:ref:refs/heads/Main"));

        Assert.Empty(Of(diagnostics, DiagnosticCodes.DuplicateFederatedCredentialSubject));
    }

    [Fact]
    public void ACopyLoopWithALiteralSubject_IsAnError_BecauseEveryIterationDeclaresTheSamePair()
    {
        string copy = "\"copy\": { \"name\": \"fics\", \"count\": 2 }, ";
        var diagnostics = Validate(App() + ", " + Fic("[format('orders-api/github-{0}', copyIndex('fics'))]", "repo:o/r:ref:refs/heads/main", copy: copy, app: string.Empty));

        var error = Assert.Single(Of(diagnostics, DiagnosticCodes.DuplicateFederatedCredentialSubject));
        Assert.Contains("copyIndex()", error.Message, System.StringComparison.Ordinal);
    }

    [Fact]
    public void ACopyLoopWhoseSubjectUsesCopyIndex_OrOfOneIteration_IsFine()
    {
        string name = "[format('orders-api/github-{0}', copyIndex('fics'))]";
        var byIndex = Validate(App() + ", " + Fic(name, "[format('repo:o/r:ref:refs/heads/branch-{0}', copyIndex('fics'))]", copy: "\"copy\": { \"name\": \"fics\", \"count\": 2 }, ", app: string.Empty));
        var single = Validate(App() + ", " + Fic(name, "repo:o/r:ref:refs/heads/main", copy: "\"copy\": { \"name\": \"fics\", \"count\": 1 }, ", app: string.Empty));

        Assert.Empty(Of(byIndex, DiagnosticCodes.DuplicateFederatedCredentialSubject));
        Assert.Empty(Of(single, DiagnosticCodes.DuplicateFederatedCredentialSubject));
    }
}
