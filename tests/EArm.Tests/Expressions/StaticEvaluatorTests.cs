using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Loading;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Static;
using EArm.Expressions.Validation;
using EArm.Expressions.Values;
using Xunit;

namespace EArm.Tests;

/// <summary>Static evaluation of the two sample templates (nested and flat forms).</summary>
public sealed partial class StaticEvaluatorTests
{
    private const string AppType = "Microsoft.Entra/applications";
    private const string SpType = "Microsoft.Entra/servicePrincipals";
    private const string GroupType = "Microsoft.Entra/groups";
    private const string FicType = "Microsoft.Entra/applications/federatedIdentityCredentials";
    private const string PasswordType = "Microsoft.Entra/applications/passwordCredentials";
    private const string AppId = "/providers/Microsoft.Entra/applications/contoso-prod-api";
    private const string GroupId = "/providers/Microsoft.Entra/groups/contoso-prod-orders-admins";
    private const string SpSymbol = "ref(" + AppId + ").appId";

    private static readonly ExpressionValidator Validator = new();

    private static async Task<ExpressionAnalysis> AnalyzeSampleAsync(string fileName, bool withParameters)
    {
        var parameters = withParameters ? ParameterSet.FromFile(TestPaths.Sample("orders-api.parameters.json")) : ParameterSet.Empty;
        var document = await new TemplateLoader().LoadAsync(TestPaths.Sample(fileName), parameters, TestContext.Current.CancellationToken);
        return Validator.Analyze(document);
    }

    private static ResolvedResource Find(StaticContext context, string type)
    {
        return Assert.Single(context.Resources, r => r.Declaration.Type == type);
    }

    [GeneratedRegex(@"/providers/[^\s'""]*")]
    private static partial Regex ResourceIdInMessage();

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NestedAndFlatSamples_ResolveToIdenticalIdsAndNameKinds(bool withParameters)
    {
        var nested = await AnalyzeSampleAsync("orders-api.entra.json", withParameters);
        var flat = await AnalyzeSampleAsync("orders-api.flat.entra.json", withParameters);

        static List<(string? Type, ResolvedNameKind Kind, string? Id, string? Name)> Shape(StaticContext context)
        {
            return context.Resources.Select(r => (r.Declaration.Type, r.NameKind, r.Id?.Id, r.Name)).OrderBy(t => t.Type).ThenBy(t => t.Id).ToList();
        }

        Assert.False(nested.HasErrors, string.Join("\n", nested.Diagnostics.Where(d => d.IsError)));
        Assert.False(flat.HasErrors, string.Join("\n", flat.Diagnostics.Where(d => d.IsError)));
        Assert.Equal(15, nested.Static.Resources.Count);
        Assert.Equal(Shape(nested.Static), Shape(flat.Static));
        Assert.Equal(nested.Static.Index.Ids, flat.Static.Index.Ids);
        Assert.Equal(nested.Static.Outputs.Keys.Order(), flat.Static.Outputs.Keys.Order());
    }

    [Fact]
    public async Task ServicePrincipal_IsSymbolic_AndTheGroupDependsOnItsSymbolicId()
    {
        var analysis = await AnalyzeSampleAsync("orders-api.entra.json", withParameters: true);
        var sp = Find(analysis.Static, SpType);
        var group = Find(analysis.Static, GroupType);

        Assert.Equal(ResolvedNameKind.Symbolic, sp.NameKind);
        Assert.False(sp.IsStatic);
        Assert.NotNull(sp.Id);
        Assert.Equal(SpSymbol, sp.Id.Name);
        Assert.Equal(SpSymbol, Expr.AssertUnknown(sp.NameValue).Symbol);
        Assert.True(analysis.Static.Index.TryGet(sp.Id, out var entry));
        Assert.True(entry.IsSymbolic);

        var dependency = Assert.Single(group.DependsOn);
        Assert.Equal(DependencySource.Explicit, dependency.Source);
        Assert.True(dependency.IsResolved);
        Assert.Equal(sp.Id, dependency.Target);
        Assert.False(dependency.Deferred);
        Assert.Equal(GroupId, group.Id?.Id);
        Assert.Equal(ResolvedNameKind.Static, group.NameKind);
    }

    [Theory]
    [InlineData("orders-api.entra.json", DependencySource.Inferred)]
    [InlineData("orders-api.flat.entra.json", DependencySource.Explicit)]
    public async Task FederatedCredential_IsCopyDependentAndDependsOnItsParent(string fileName, DependencySource source)
    {
        var analysis = await AnalyzeSampleAsync(fileName, withParameters: true);
        var fic = Find(analysis.Static, FicType);
        var password = Find(analysis.Static, PasswordType);

        Assert.Equal(ResolvedNameKind.CopyDependent, fic.NameKind);
        Assert.Null(fic.Id);
        Assert.True(fic.HasCopy);
        Assert.Equal("fics", fic.CopyName);
        Assert.Equal(2, fic.CopyCount);
        Assert.Equal(UnknownOrigins.Copy, Expr.AssertUnknown(fic.NameValue).Origins);

        var dependency = Assert.Single(fic.DependsOn);
        Assert.Equal(source, dependency.Source);
        Assert.Equal(AppId, dependency.Target?.Id);
        Assert.Equal("[resourceId('Microsoft.Entra/applications', variables('apiName'))]", dependency.Text);

        Assert.Equal(ResolvedNameKind.Static, password.NameKind);
        Assert.Equal(AppId + "/passwordCredentials/ci-secret", password.Id?.Id);
        Assert.Equal("contoso-prod-api/ci-secret", password.Name);
        Assert.Equal(AppId, Assert.Single(password.DependsOn).Target?.Id);
    }

    [Fact]
    public async Task StaticResources_HaveConcreteIdsConditionsAndDependencies()
    {
        var analysis = await AnalyzeSampleAsync("orders-api.entra.json", withParameters: true);
        var context = analysis.Static;

        var app = Find(context, AppType);
        Assert.Equal(ResolvedNameKind.Static, app.NameKind);
        Assert.Equal(AppId, app.Id?.Id);
        Assert.Equal("contoso-prod-api", app.Name);
        Assert.Null(app.Condition);
        Assert.False(app.ConditionIsDeployTime);
        Assert.Null(app.CopyCount);
        Assert.Equal("/providers/Microsoft.Entra/users/bg-orders@contoso.com", Assert.Single(app.DependsOn).Target?.Id);

        var policy = Find(context, "Microsoft.Entra/conditionalAccess/policies");
        Assert.True(policy.Condition);
        Assert.Equal(3, policy.DependsOn.Count);
        Assert.All(policy.DependsOn, d => Assert.True(d.IsResolved));
        Assert.Equal(
            [GroupId, "/providers/Microsoft.Entra/conditionalAccess/namedLocations/Contoso offices", "/providers/Microsoft.Entra/conditionalAccess/authenticationContextClassReferences/c10"],
            policy.DependsOn.Select(d => d.Target!.Id).ToList());

        var users = Find(context, "Microsoft.Entra/users");
        Assert.Empty(users.DependsOn);
        var same = users.References.Where(r => r.IsSameTemplate).Select(r => r.Target!.Id).ToHashSet();
        Assert.Contains("/providers/Microsoft.Entra/administrativeUnits/Orders AU", same);
        Assert.Contains(GroupId, same);
        Assert.Contains(AppId, same);
        var existing = users.References.Where(r => r.Kind == ReferenceOccurrenceKind.ExistingReference).Select(r => r.Target!.Id).ToList();
        Assert.Contains("/providers/Microsoft.Entra/users/cio@contoso.com", existing);
        Assert.Contains("/providers/Microsoft.Entra/groups/contoso-all-staff", existing);
        Assert.All(users.References, r => Assert.StartsWith("resources[2].properties", r.Path.ToString()));
        Assert.All(users.References, r => Assert.StartsWith("reference(", r.ExpressionText));

        Assert.Equal(3, context.Parameters.Count);
        Assert.Equal(5, context.Variables.Count);
        Assert.Equal("contoso-prod-api", Expr.AssertString(context.Variables.Get("apiName")));
        Assert.Equal(EvaluationScope.Resource, context.Context.Scope);
        Assert.True(context.Index.Contains(app.Id!));
    }

    [Fact]
    public async Task Outputs_EvaluateToPlaceholders()
    {
        var analysis = await AnalyzeSampleAsync("orders-api.entra.json", withParameters: true);

        var apiAppId = analysis.Static.Outputs["apiAppId"];
        Assert.Equal(SpSymbol, Expr.AssertUnknown(apiAppId.Value).Symbol);
        Assert.False(apiAppId.IsTainted);
        Assert.Null(apiAppId.Condition);
        Assert.Equal("apiAppId", apiAppId.Definition.Name);

        var ciSecret = analysis.Static.Outputs["CISECRET"];
        Assert.Equal("ref(" + AppId + "/passwordCredentials/ci-secret).secretText", Expr.AssertUnknown(ciSecret.Value).Symbol);
        Assert.True(ciSecret.Definition.IsSecure);
        Assert.Equal(2, analysis.Static.Outputs.Count);
    }

    [Fact]
    public async Task WithoutParameterValues_TheCiSecretOutputIsDeferredWithAnInfo()
    {
        var analysis = await AnalyzeSampleAsync("orders-api.entra.json", withParameters: false);

        Assert.False(analysis.HasErrors);
        var info = Assert.Single(analysis.Diagnostics, d => d.Code == DiagnosticCodes.ReferenceDependsOnMissingParameter);
        Assert.Equal(DiagnosticSeverity.Info, info.Severity);
        Assert.Equal(JsonPath.Parse("outputs.ciSecret.value"), info.Path);
        Assert.Contains("(at offset", info.Message);
        Assert.NotNull(info.Location);

        var app = Find(analysis.Static, AppType);
        Assert.Equal(ResolvedNameKind.Symbolic, app.NameKind);
        Assert.Contains("parameters('env')", app.Id!.Name);

        var ciSecret = Expr.AssertUnknown(analysis.Static.Outputs["ciSecret"].Value);
        Assert.True((ciSecret.Origins & UnknownOrigins.Parameter) != 0);
        // WI-16: no function of the default registry is a stub any more, so the Info is the only finding.
        Assert.Equal(0, analysis.Diagnostics.Count(d => d.Code == DiagnosticCodes.NotImplementedYet));
        Assert.Single(analysis.Diagnostics);
    }

    [Theory]
    [InlineData("orders-api.entra.json", true)]
    [InlineData("orders-api.entra.json", false)]
    [InlineData("orders-api.flat.entra.json", false)]
    public async Task NoDiagnosticMessage_ContainsExpressionTextInsideAResourceId(string fileName, bool withParameters)
    {
        var analysis = await AnalyzeSampleAsync(fileName, withParameters);

        foreach (var diagnostic in analysis.Diagnostics)
        {
            foreach (Match match in ResourceIdInMessage().Matches(diagnostic.Message))
            {
                Assert.DoesNotContain("(", match.Value);
            }
        }
    }

    [Fact]
    public async Task Diagnostics_AreSortedAndCarryLocationsAndOffsets()
    {
        var analysis = await AnalyzeSampleAsync("orders-api.entra.json", withParameters: false);
        var diagnostics = analysis.Diagnostics;

        Assert.NotEmpty(diagnostics);
        for (int i = 1; i < diagnostics.Count; i++)
        {
            Assert.True(DiagnosticOrder.Instance.Compare(diagnostics[i - 1], diagnostics[i]) <= 0, $"{diagnostics[i - 1]} should sort before {diagnostics[i]}");
        }

        Assert.All(diagnostics, d =>
        {
            Assert.NotNull(d.Location);
            Assert.Contains("(at offset ", d.Message);
            Assert.Matches(@"\(\d+:\d+\)", d.ToString());
            Assert.Equal(TestPaths.Sample("orders-api.entra.json"), d.Source);
        });
    }

    [Fact]
    public async Task FindReferences_ReturnsTheSameTemplateTargetsOfATree()
    {
        var analysis = await AnalyzeSampleAsync("orders-api.entra.json", withParameters: true);
        var group = Find(analysis.Static, GroupType);
        var sp = Find(analysis.Static, SpType);

        var references = Expr.Evaluator.FindReferences(group.Declaration.Properties, analysis.Static);

        Assert.Contains(sp.Id!, references);
        Assert.Contains(ResourceIdentifier.Create("Microsoft.Entra/administrativeUnits", "Orders AU"), references);
        Assert.Contains(ResourceIdentifier.Create("Microsoft.Entra/roleDefinitions", "Orders Support"), references);
        Assert.Empty(Expr.Evaluator.FindReferences(null, analysis.Static));
        Assert.Equal(references, group.References.Where(r => r.IsSameTemplate).Select(r => r.Target!).ToHashSet());
    }

    [Fact]
    public async Task StaticContext_FindsDeclarationsAndScopesContexts()
    {
        var analysis = await AnalyzeSampleAsync("orders-api.entra.json", withParameters: true);
        var context = analysis.Static;
        var fic = Find(context, FicType);
        var app = Find(context, AppType);

        Assert.Same(fic, context.Find(fic.Declaration));
        Assert.Same(app, context.Find(app.Declaration));

        var copyContext = context.ContextFor(fic.Declaration);
        Assert.NotNull(copyContext.CurrentCopy);
        Assert.Equal("fics", copyContext.CurrentCopy.Name);
        Assert.False(copyContext.CurrentCopy.IsConcrete);
        Assert.Null(context.ContextFor(app.Declaration).CurrentCopy);
        Assert.Same(context.Document, app.Declaration.Source.Root is null ? context.Document : context.Document);
        Assert.Equal(context.Resources.Count, context.Document.Resources.Count);
    }

    [Fact]
    public async Task Validate_ReturnsTheAnalysisDiagnostics()
    {
        var parameters = ParameterSet.FromFile(TestPaths.Sample("orders-api.parameters.json"));
        var document = await new TemplateLoader().LoadAsync(TestPaths.Sample("orders-api.entra.json"), parameters, TestContext.Current.CancellationToken);

        var diagnostics = Validator.Validate(document);

        // With every parameter supplied the sample is clean (WI-16 removed the NotImplementedYet warnings).
        Assert.Empty(diagnostics);
    }
}
