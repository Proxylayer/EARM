using System.Collections.Generic;
using System.Linq;
using EntraArm.Core;
using EntraArm.Core.Diagnostics;
using EntraArm.Core.Json;
using EntraArm.Expressions.Static;
using EntraArm.Expressions.Validation;
using EntraArm.Expressions.Values;
using Xunit;

namespace EntraArm.Tests;

/// <summary>Expression-dependent validation over small inline templates.</summary>
public sealed class ExpressionValidatorTests
{
    private const string Pw = "\"pw\": { \"type\": \"securestring\" }";

    private static readonly ExpressionValidator Validator = new();

    private static ParameterSet SecureInline => ParameterSet.Empty.WithInline("pw", "s3cret").WithInline("n", "3");

    private static ExpressionAnalysis Analyze(string body, ParameterSet? parameters = null)
    {
        return Validator.Analyze(Expr.ParseBody(body, parameters));
    }

    private static Diagnostic Single(ExpressionAnalysis analysis, string code, string path)
    {
        return Assert.Single(analysis.Diagnostics, d => d.Code == code && d.Path == JsonPath.Parse(path));
    }

    private static void None(ExpressionAnalysis analysis, string path)
    {
        Assert.DoesNotContain(analysis.Diagnostics, d => d.Path == JsonPath.Parse(path));
    }

    private static string Resource(string type, string name, string extra = "")
    {
        return "{ \"type\": \"" + type + "\", \"apiVersion\": \"2026-10-01\", \"name\": \"" + name + "\"" + (extra.Length == 0 ? string.Empty : ", " + extra) + " }";
    }

    // ---- names and ids ----

    [Fact]
    public void DuplicateId_AfterEvaluatingAnExpressionName_IsReported()
    {
        var analysis = Analyze("\"resources\": [ " + Resource("Microsoft.Entra/groups", "g1") + ", " + Resource("Microsoft.Entra/groups", "[concat('g', '1')]") + " ]");

        var error = Single(analysis, DiagnosticCodes.DuplicateResourceId, "resources[1].name");
        Assert.Contains("resources[0]", error.Message);
        Assert.Equal(ResolvedNameKind.Static, analysis.Static.Resources[1].NameKind);
        Assert.Equal("g1", analysis.Static.Resources[1].Name);
    }

    [Fact]
    public void DuplicateSymbolicNames_AreReported()
    {
        var analysis = Analyze("\"resources\": [ " + Resource("Microsoft.Entra/applications", "app") + ", "
            + Resource("Microsoft.Entra/servicePrincipals", "[reference('app').appId]") + ", "
            + Resource("Microsoft.Entra/servicePrincipals", "[reference('app').appId]") + " ]");

        var error = Single(analysis, DiagnosticCodes.DuplicateResourceId, "resources[2].name");
        Assert.Contains("deploy-time name", error.Message);
    }

    [Fact]
    public void ApiVersionExpression_IsRejected()
    {
        var analysis = Analyze("\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"[variables('v')]\", \"name\": \"g\" } ], \"variables\": { \"v\": \"2026-10-01\" }");

        Single(analysis, DiagnosticCodes.ApiVersionMustBeLiteral, "resources[0].apiVersion");
    }

    [Theory]
    [InlineData("Microsoft.Entra/groups", "[createArray()]", DiagnosticCodes.ResourceNameNotString)]
    [InlineData("Microsoft.Entra/groups", "[json('1')]", DiagnosticCodes.ResourceNameNotString)]
    [InlineData("Microsoft.Entra/applications", "[concat('a/b')]", DiagnosticCodes.InvalidResourceName)]
    [InlineData("Microsoft.Entra/applications/federatedIdentityCredentials", "[concat('only-one')]", DiagnosticCodes.InvalidResourceName)]
    [InlineData("Microsoft.Entra/applications", "[concat('a//b')]", DiagnosticCodes.InvalidResourceName)]
    [InlineData("Microsoft.Entra/groups", "[parameters('pw')]", DiagnosticCodes.SecureValueInResourceKey)]
    [InlineData("Microsoft.Entra/groups", "[concat('g-', parameters('pw'))]", DiagnosticCodes.SecureValueInResourceKey)]
    [InlineData("Microsoft.Entra/groups", "[utcNow()]", DiagnosticCodes.NonDeterministicFunctionNotAllowed)]
    [InlineData("Microsoft.Entra/groups", "[concat('g', copyIndex())]", DiagnosticCodes.CopyIndexOutsideCopy)]
    [InlineData("Microsoft.Entra/groups", "[nope()]", DiagnosticCodes.UnknownFunction)]
    [InlineData("Microsoft.Entra/groups", "[concat('a']", DiagnosticCodes.ExpressionSyntaxError)]
    [InlineData("Microsoft.Entra/groups", "[variables('nope')]", DiagnosticCodes.UndefinedVariable)]
    [InlineData("Microsoft.Entra/groups", "[parameters('nope')]", DiagnosticCodes.UndefinedParameter)]
    public void InvalidNames_AreReportedExactlyOnce(string type, string name, string code)
    {
        var analysis = Analyze("\"parameters\": { " + Pw + " }, \"resources\": [ " + Resource(type, name) + " ]", SecureInline);

        var error = Single(analysis, code, "resources[0].name");
        Assert.True(error.IsError);
        Assert.NotNull(error.Location);
        Assert.Single(analysis.Diagnostics, d => d.Path == JsonPath.Parse("resources[0].name"));
        Assert.Equal(ResolvedNameKind.Invalid, analysis.Static.Resources[0].NameKind);
        Assert.Null(analysis.Static.Resources[0].Id);
    }

    [Theory]
    [InlineData("[concat('g', parameters('n'))]", "g3")]
    [InlineData("[toLower('G')]", "g")]
    [InlineData("[[literal", "[literal")]
    public void EvaluatedNames_AreStatic(string name, string expected)
    {
        var analysis = Analyze("\"parameters\": { \"n\": { \"type\": \"int\" } }, \"resources\": [ " + Resource("Microsoft.Entra/groups", name) + " ]", SecureInline);

        Assert.False(analysis.HasErrors);
        var resource = Assert.Single(analysis.Static.Resources);
        Assert.Equal(ResolvedNameKind.Static, resource.NameKind);
        Assert.Equal(expected, resource.Name);
        Assert.Equal("/providers/Microsoft.Entra/groups/" + expected, resource.Id?.Id);
    }

    [Fact]
    public void CopyDependentName_WithACopyLoop_IsNotAnError()
    {
        var analysis = Analyze("\"resources\": [ " + Resource("Microsoft.Entra/groups", "[concat('g', copyIndex())]", "\"copy\": { \"name\": \"loop\", \"count\": 2 }") + " ]");

        Assert.False(analysis.HasErrors);
        var resource = Assert.Single(analysis.Static.Resources);
        Assert.Equal(ResolvedNameKind.CopyDependent, resource.NameKind);
        Assert.Equal(2, resource.CopyCount);
        Assert.Equal("loop", resource.CopyName);
        Assert.Empty(analysis.Static.Index.Entries);
        Assert.Equal("loop", Assert.Single(analysis.Static.Index.CopyLoops).Name);
    }

    [Fact]
    public void SymbolicName_IsMatchedByResourceIdAndReferenceOfTheSameComputation()
    {
        var analysis = Analyze("\"resources\": [ " + Resource("Microsoft.Entra/applications", "app") + ", "
            + Resource("Microsoft.Entra/servicePrincipals", "[reference('app').appId]") + ", "
            + Resource("Microsoft.Entra/groups", "g", "\"dependsOn\": [ \"[resourceId('Microsoft.Entra/servicePrincipals', reference('app').appId)]\" ], \"properties\": { \"members\": [ \"[reference(reference('app').appId).id]\", \"[reference(resourceId('Microsoft.Entra/servicePrincipals', reference('app').appId)).id]\" ] }") + " ]");

        Assert.False(analysis.HasErrors, string.Join("\n", analysis.Diagnostics));
        var sp = analysis.Static.Resources[1];
        Assert.Equal(ResolvedNameKind.Symbolic, sp.NameKind);
        Assert.Equal(Expr.AppRef + ".appId", sp.Id!.Name);

        var group = analysis.Static.Resources[2];
        Assert.Equal(sp.Id, Assert.Single(group.DependsOn).Target);

        // Every reference()/resourceId() call is an occurrence, nested ones included: the two outer reference()
        // calls resolve to the service principal, the inner reference('app') calls to the application, and the
        // resourceId() to the service principal's symbolic id.
        var appId = ResourceIdentifier.Create("Microsoft.Entra/applications", "app");
        Assert.Equal(5, group.References.Count);
        Assert.Equal(2, group.References.Count(r => r.Kind == ReferenceOccurrenceKind.Reference && sp.Id.Equals(r.Target)));
        Assert.Equal(2, group.References.Count(r => r.Kind == ReferenceOccurrenceKind.Reference && appId.Equals(r.Target)));
        var viaResourceId = Assert.Single(group.References, r => r.Kind == ReferenceOccurrenceKind.ResourceId);
        Assert.Equal(sp.Id, viaResourceId.Target);
        Assert.Equal([appId, sp.Id], Expr.Evaluator.FindReferences(group.Declaration.Properties, analysis.Static).OrderBy(id => id.Id).ToList());
    }

    // ---- dependsOn ----

    [Fact]
    public void DependsOn_ResolvesEveryFormAndReportsBadEntries()
    {
        const string dependsOn = """
            "dependsOn": [
              "[resourceId('Microsoft.Entra/applications', 'app')]",
              "/providers/Microsoft.Entra/applications/app",
              "Microsoft.Entra/applications/app",
              "APP",
              "loop",
              "g",
              "dup",
              "/providers/Microsoft.Entra/bogus/x",
              "nope",
              "[resourceId('Microsoft.Entra/groups', 'nope')]",
              "[parameters('pw')]",
              "[reference('app').id]",
              "[createArray()]"
            ]
            """;
        var analysis = Analyze("\"parameters\": { " + Pw + " }, \"resources\": [ "
            + Resource("Microsoft.Entra/applications", "app") + ", "
            + Resource("Microsoft.Entra/groups", "g", "\"copy\": { \"name\": \"loop\", \"count\": 2 }, \"dependsOn\": [ \"[resourceId('Microsoft.Entra/groups', concat('x', copyIndex()))]\" ]") + ", "
            + Resource("Microsoft.Entra/groups", "dup") + ", "
            + Resource("Microsoft.Entra/roleDefinitions", "dup") + ", "
            + Resource("Microsoft.Entra/users", "u@contoso.com", dependsOn) + " ]", SecureInline);

        var appId = ResourceIdentifier.Create("Microsoft.Entra/applications", "app");
        var user = analysis.Static.Resources[4];
        Assert.Equal(13, user.DependsOn.Count);
        Assert.All(user.DependsOn, d => Assert.Equal(DependencySource.Explicit, d.Source));
        Assert.All(user.DependsOn.Take(4), d => Assert.Equal(appId, d.Target));
        Assert.Equal("loop", user.DependsOn[4].CopyName);
        Assert.True(user.DependsOn[4].IsResolved);
        Assert.Equal(ResourceIdentifier.Create("Microsoft.Entra/groups", "g"), user.DependsOn[5].Target);
        Assert.All(user.DependsOn.Skip(6), d => Assert.False(d.IsResolved));

        Single(analysis, DiagnosticCodes.AmbiguousDependsOn, "resources[4].dependsOn[6]");
        Single(analysis, DiagnosticCodes.InvalidResourceId, "resources[4].dependsOn[7]");
        Single(analysis, DiagnosticCodes.DependsOnTargetNotFound, "resources[4].dependsOn[8]");
        Single(analysis, DiagnosticCodes.DependsOnTargetNotFound, "resources[4].dependsOn[9]");
        Single(analysis, DiagnosticCodes.SecureValueInResourceKey, "resources[4].dependsOn[10]");
        Single(analysis, DiagnosticCodes.DependsOnTargetNotFound, "resources[4].dependsOn[11]");
        Single(analysis, DiagnosticCodes.DependsOnTargetNotFound, "resources[4].dependsOn[12]");
        Assert.Equal(7, analysis.Diagnostics.Count(d => d.Path!.ToString().StartsWith("resources[4].dependsOn", System.StringComparison.Ordinal)));

        var deferred = Assert.Single(analysis.Static.Resources[1].DependsOn);
        Assert.True(deferred.Deferred);
        Assert.False(deferred.IsResolved);
        None(analysis, "resources[1].dependsOn[0]");
        Assert.DoesNotContain(analysis.Diagnostics, d => d.Message.Contains("s3cret", System.StringComparison.Ordinal));
    }

    // ---- copy ----

    [Theory]
    [InlineData("\"[parameters('n')]\"", 3L, null)]
    [InlineData("3", 3L, null)]
    [InlineData("\"[add(parameters('n'), 1)]\"", 4L, null)]
    [InlineData("\"[copyIndex()]\"", null, DiagnosticCodes.InvalidCopy)]
    [InlineData("-1", null, DiagnosticCodes.InvalidCopy)]
    [InlineData("\"[sub(0, 5)]\"", null, DiagnosticCodes.InvalidCopy)]
    [InlineData("\"abc\"", null, DiagnosticCodes.InvalidCopy)]
    [InlineData("\"[json('1.5')]\"", null, DiagnosticCodes.InvalidCopy)]
    [InlineData("\"[parameters('pw')]\"", null, DiagnosticCodes.SecureValueInResourceKey)]
    [InlineData("\"[div(1, 0)]\"", null, DiagnosticCodes.ExpressionEvaluationFailed)]
    [InlineData("\"[reference('other').n]\"", null, null)]
    public void CopyCount_IsEvaluatedStatically(string count, long? expectedCount, string? code)
    {
        var analysis = Analyze("\"parameters\": { " + Pw + ", \"n\": { \"type\": \"int\" } }, \"resources\": [ "
            + Resource("Microsoft.Entra/groups", "g", "\"copy\": { \"name\": \"loop\", \"count\": " + count + " }") + ", "
            + Resource("Microsoft.Entra/applications", "other") + " ]", SecureInline);

        var resource = analysis.Static.Resources[0];
        Assert.Equal(expectedCount, resource.CopyCount);
        if (code is null)
        {
            Assert.False(analysis.HasErrors, string.Join("\n", analysis.Diagnostics));
        }
        else
        {
            Single(analysis, code, "resources[0].copy.count");
        }
    }

    // ---- condition ----

    [Theory]
    [InlineData("\"[concat('a')]\"", null, false, DiagnosticCodes.ConditionNotBoolean)]
    [InlineData("\"[json('1')]\"", null, false, DiagnosticCodes.ConditionNotBoolean)]
    [InlineData("\"[equals(reference('other').id, 'x')]\"", null, true, null)]
    [InlineData("\"[parameters('pw')]\"", null, false, DiagnosticCodes.SecureValueInResourceKey)]
    [InlineData("\"[equals(parameters('pw'), 'x')]\"", null, false, DiagnosticCodes.SecureValueInResourceKey)]
    [InlineData("\"[true()]\"", true, false, null)]
    [InlineData("false", false, false, null)]
    [InlineData("\"[not(equals(parameters('n'), 3))]\"", false, false, null)]
    [InlineData("\"[nope()]\"", null, false, DiagnosticCodes.UnknownFunction)]
    public void Condition_IsEvaluatedStatically(string condition, bool? expected, bool deployTime, string? code)
    {
        var analysis = Analyze("\"parameters\": { " + Pw + ", \"n\": { \"type\": \"int\" } }, \"resources\": [ "
            + Resource("Microsoft.Entra/groups", "g", "\"condition\": " + condition) + ", "
            + Resource("Microsoft.Entra/applications", "other") + " ]", SecureInline);

        var resource = analysis.Static.Resources[0];
        Assert.Equal(expected, resource.Condition);
        Assert.Equal(deployTime, resource.ConditionIsDeployTime);
        if (code is null)
        {
            Assert.False(analysis.HasErrors, string.Join("\n", analysis.Diagnostics));
        }
        else
        {
            Single(analysis, code, "resources[0].condition");
        }
    }

    // ---- outputs ----

    [Fact]
    public void Outputs_CheckSecureFlowAndConditions()
    {
        var analysis = Analyze("\"parameters\": { " + Pw + " }, \"resources\": [], \"outputs\": { "
            + "\"plain\": { \"type\": \"string\", \"value\": \"[parameters('pw')]\" }, "
            + "\"nested\": { \"type\": \"object\", \"value\": { \"k\": \"[toUpper(parameters('pw'))]\" } }, "
            + "\"secure\": { \"type\": \"securestring\", \"value\": \"[parameters('pw')]\" }, "
            + "\"secureObj\": { \"type\": \"secureObject\", \"value\": { \"k\": \"[parameters('pw')]\" } }, "
            + "\"cond\": { \"type\": \"string\", \"value\": \"x\", \"condition\": \"[concat('a')]\" }, "
            + "\"ok\": { \"type\": \"string\", \"value\": \"[concat('a')]\", \"condition\": \"[true()]\" }, "
            + "\"broken\": { \"type\": \"string\", \"value\": \"[nope()]\" } }", SecureInline);

        Single(analysis, DiagnosticCodes.SecureValueInPlainOutput, "outputs.plain.value");
        Single(analysis, DiagnosticCodes.SecureValueInPlainOutput, "outputs.nested.value");
        None(analysis, "outputs.secure.value");
        None(analysis, "outputs.secureObj.value");
        Single(analysis, DiagnosticCodes.ConditionNotBoolean, "outputs.cond.condition");
        Single(analysis, DiagnosticCodes.UnknownFunction, "outputs.broken.value");

        Assert.True(analysis.Static.Outputs["secure"].IsTainted);
        Assert.True(analysis.Static.Outputs["plain"].IsTainted);
        Assert.True(analysis.Static.Outputs["ok"].Condition);
        Assert.Equal("a", Expr.AssertString(analysis.Static.Outputs["ok"].Value));
        Assert.Null(analysis.Static.Outputs["cond"].Condition);
        Assert.Null(analysis.Static.Outputs["broken"].Value);
        Assert.DoesNotContain(analysis.Diagnostics, d => d.Message.Contains("s3cret", System.StringComparison.Ordinal));
    }

    // ---- variables and parameter defaults ----

    [Fact]
    public void Variables_MayOnlyReferenceExistingResources()
    {
        var analysis = Analyze("\"variables\": { "
            + "\"v\": \"[reference('app')]\", "
            + "\"w\": \"[reference(resourceId('Microsoft.Entra/groups', 'x'), '2026-10-01').id]\", "
            + "\"cyc\": \"[variables('cyc')]\", "
            + "\"undef\": \"[variables('nope')]\", "
            + "\"ok\": \"[concat('a', 'b')]\", "
            + "\"now\": \"[utcNow()]\", "
            + "\"ci\": \"[copyIndex()]\" }, "
            + "\"resources\": [ " + Resource("Microsoft.Entra/applications", "app") + " ]");

        Single(analysis, DiagnosticCodes.ReferenceNotAllowedHere, "variables.v");
        None(analysis, "variables.w");
        Single(analysis, DiagnosticCodes.VariableCycle, "variables.cyc");
        Single(analysis, DiagnosticCodes.UndefinedVariable, "variables.undef");
        Single(analysis, DiagnosticCodes.NonDeterministicFunctionNotAllowed, "variables.now");
        Single(analysis, DiagnosticCodes.CopyIndexOutsideCopy, "variables.ci");
        None(analysis, "variables.ok");
        Assert.Equal(5, analysis.Diagnostics.Count);

        Assert.Equal("ab", Expr.AssertString(analysis.Static.Variables.Get("ok")));
        Assert.Equal("ref(/providers/Microsoft.Entra/groups/x, '2026-10-01').id", Expr.AssertUnknown(analysis.Static.Variables.Get("w")).Symbol);
    }

    [Fact]
    public void ParameterDefaults_AllowNonDeterminismButNotVariablesOrReferences()
    {
        var analysis = Analyze("\"parameters\": { "
            + "\"ok\": { \"type\": \"string\", \"defaultValue\": \"[utcNow('yyyy')]\" }, "
            + "\"bad\": { \"type\": \"string\", \"defaultValue\": \"[variables('x')]\" }, "
            + "\"ref\": { \"type\": \"string\", \"defaultValue\": \"[reference('app')]\" }, "
            + "\"cyc\": { \"type\": \"string\", \"defaultValue\": \"[parameters('cyc')]\" }, "
            + "\"deep\": { \"type\": \"object\", \"defaultValue\": { \"k\": \"[newGuid()]\", \"bad\": \"[variables('x')]\" } } }, "
            + "\"variables\": { \"x\": 1 }, \"resources\": [ " + Resource("Microsoft.Entra/applications", "app") + " ]");

        None(analysis, "parameters.ok.defaultValue");
        Single(analysis, DiagnosticCodes.FunctionNotAllowedHere, "parameters.bad.defaultValue");
        Single(analysis, DiagnosticCodes.ReferenceNotAllowedHere, "parameters.ref.defaultValue");
        Single(analysis, DiagnosticCodes.ParameterDefaultCycle, "parameters.cyc.defaultValue");
        None(analysis, "parameters.deep.defaultValue.k");
        Single(analysis, DiagnosticCodes.FunctionNotAllowedHere, "parameters.deep.defaultValue.bad");
        Assert.Matches("^[0-9]{4}$", Expr.AssertString(analysis.Static.Parameters.Get("ok")));
    }

    // ---- functions ----

    [Fact]
    public void UserFunctions_AreCheckedForDeclarationAndArity()
    {
        var analysis = Analyze("\"functions\": [ { \"namespace\": \"c\", \"members\": { "
            + "\"slug\": { \"parameters\": [ { \"name\": \"s\", \"type\": \"string\" } ], \"output\": { \"type\": \"string\", \"value\": \"[toLower(parameters('s'))]\" } }, "
            + "\"bad\": { \"output\": { \"type\": \"string\", \"value\": \"[variables('x')]\" } }, "
            + "\"worse\": { \"output\": { \"type\": \"string\", \"value\": \"[reference('app')]\" } } } } ], "
            + "\"variables\": { \"x\": 1 }, "
            + "\"resources\": [ " + Resource("Microsoft.Entra/applications", "app", "\"properties\": { \"a\": \"[x.f()]\", \"b\": \"[c.nope()]\", \"c\": \"[c.slug()]\", \"d\": \"[c.slug('x')]\", \"e\": \"[c.bad()]\" }") + " ]");

        None(analysis, "functions[0].members.slug.output.value");
        Single(analysis, DiagnosticCodes.FunctionNotAllowedHere, "functions[0].members.bad.output.value");
        Single(analysis, DiagnosticCodes.ReferenceNotAllowedHere, "functions[0].members.worse.output.value");
        Single(analysis, DiagnosticCodes.UnknownFunction, "resources[0].properties.a");
        Single(analysis, DiagnosticCodes.UnknownFunction, "resources[0].properties.b");
        Single(analysis, DiagnosticCodes.InvalidFunctionArguments, "resources[0].properties.c");
        // WI-16: a well-formed user-function call is clean; the broken body of c.bad is reported where it is declared, not at the call.
        None(analysis, "resources[0].properties.d");
        None(analysis, "resources[0].properties.e");
    }

    [Theory]
    [InlineData("[concat('x']", DiagnosticCodes.ExpressionSyntaxError, 11)]
    [InlineData("[true]", DiagnosticCodes.ExpressionSyntaxError, 5)]
    [InlineData("[nope()]", DiagnosticCodes.UnknownFunction, 1)]
    [InlineData("[concat('a', nope())]", DiagnosticCodes.UnknownFunction, 13)]
    [InlineData("[resourceGroup()]", DiagnosticCodes.UnsupportedArmFunction, 1)]
    [InlineData("[listKeys('a', 'b')]", DiagnosticCodes.UnsupportedArmFunction, 1)]
    [InlineData("[toLower()]", DiagnosticCodes.InvalidFunctionArguments, 1)]
    [InlineData("[utcNow()]", DiagnosticCodes.NonDeterministicFunctionNotAllowed, 1)]
    [InlineData("[reference(reference('app').displayName)]", DiagnosticCodes.ReferenceNotStaticallyResolvable, 1)]
    [InlineData("[reference('dup').id]", DiagnosticCodes.AmbiguousReference, 1)]
    [InlineData("[reference('nope').id]", DiagnosticCodes.ReferenceTargetNotFound, 1)]
    [InlineData("[reference('/providers/Microsoft.Entra/bogus/x')]", DiagnosticCodes.InvalidResourceId, 1)]
    [InlineData("[reference(resourceId('Microsoft.Entra/groups', 'nope'))]", DiagnosticCodes.ReferenceTargetNotFound, 1)]
    [InlineData("[copyIndex()]", DiagnosticCodes.CopyIndexOutsideCopy, 1)]
    public void PropertyExpressions_ReportFunctionAndReferenceProblemsWithOffsets(string expression, string code, int offset)
    {
        var analysis = Analyze("\"resources\": [ "
            + Resource("Microsoft.Entra/applications", "app") + ", "
            + Resource("Microsoft.Entra/groups", "dup") + ", "
            + Resource("Microsoft.Entra/roleDefinitions", "dup") + ", "
            + Resource("Microsoft.Entra/users", "u@contoso.com", "\"properties\": { \"x\": " + System.Text.Json.JsonSerializer.Serialize(expression) + " }") + " ]");

        var error = Single(analysis, code, "resources[3].properties.x");
        Assert.EndsWith($"(at offset {offset})", error.Message);
        Assert.NotNull(error.Location);
        Assert.Equal("inline.entra.json", error.Source);
        Assert.Matches(@"\(\d+:\d+\)", error.ToString());
        Assert.DoesNotContain(analysis.Diagnostics, d => d.Message.Contains("/providers/Microsoft.Entra/applications/app).", System.StringComparison.Ordinal) && d.Code != DiagnosticCodes.ReferenceNotStaticallyResolvable);
    }

    [Fact]
    public void ExpressionTooLong_IsReportedAtOffsetZero()
    {
        string expression = "[concat('" + new string('a', 24570) + "')]";
        var analysis = Analyze("\"resources\": [ " + Resource("Microsoft.Entra/groups", "g", "\"properties\": { \"displayName\": \"" + expression + "\" }") + " ]");

        var error = Single(analysis, DiagnosticCodes.ExpressionTooLong, "resources[0].properties.displayName");
        Assert.EndsWith("(at offset 0)", error.Message);
    }

    [Fact]
    public void ReferencesInsideProperties_AreCollectedPerOccurrence()
    {
        var analysis = Analyze("\"resources\": [ "
            + Resource("Microsoft.Entra/applications", "app") + ", "
            + Resource("Microsoft.Entra/groups", "g", "\"properties\": { \"owners\": [ \"[reference('app').id]\", \"[reference(resourceId('Microsoft.Entra/applications', 'app'), '2026-10-01').id]\" ], \"tag\": \"[resourceId('Microsoft.Entra/applications', 'app')]\", \"ext\": \"[resourceId('Microsoft.Entra/groups', 'elsewhere')]\" }") + " ]");

        Assert.False(analysis.HasErrors);
        var references = analysis.Static.Resources[1].References;
        Assert.Equal(4, references.Count);
        var appId = ResourceIdentifier.Create("Microsoft.Entra/applications", "app");

        Assert.Equal(ReferenceOccurrenceKind.Reference, references[0].Kind);
        Assert.Equal(appId, references[0].Target);
        Assert.Equal(JsonPath.Parse("resources[1].properties.owners[0]"), references[0].Path);
        Assert.Equal("reference('app')", references[0].ExpressionText);
        Assert.Equal(1, references[0].Offset);

        // reference(resourceId(...), apiVersion): the outer call is an existing-resource lookup (no dependency),
        // the inner resourceId() still names a template resource and is recorded as such.
        Assert.Equal(ReferenceOccurrenceKind.ExistingReference, references[1].Kind);
        Assert.Equal(appId, references[1].Target);
        Assert.False(references[1].IsSameTemplate);
        Assert.Equal(ReferenceOccurrenceKind.ResourceId, references[2].Kind);
        Assert.Equal(JsonPath.Parse("resources[1].properties.owners[1]"), references[2].Path);
        Assert.Equal(11, references[2].Offset);

        Assert.Equal(ReferenceOccurrenceKind.ResourceId, references[3].Kind);
        Assert.Equal(appId, references[3].Target);
        Assert.True(references[3].IsSameTemplate);
        Assert.Equal(JsonPath.Parse("resources[1].properties.tag"), references[3].Path);
    }

    [Fact]
    public void MissingParameterValues_DeferTargetsWithAnInfoInsteadOfAnError()
    {
        var analysis = Analyze("\"parameters\": { \"env\": { \"type\": \"string\" } }, \"resources\": [ "
            + Resource("Microsoft.Entra/groups", "[parameters('env')]") + ", "
            + Resource("Microsoft.Entra/applications", "app", "\"dependsOn\": [ \"[resourceId('Microsoft.Entra/groups', concat(parameters('env'), 'x'))]\" ], \"properties\": { \"a\": \"[reference(concat(parameters('env'), '-x')).id]\", \"b\": \"[reference(parameters('env')).id]\" }") + " ]");

        Assert.False(analysis.HasErrors, string.Join("\n", analysis.Diagnostics));
        var infos = analysis.Diagnostics.Where(d => d.Code == DiagnosticCodes.ReferenceDependsOnMissingParameter).ToList();
        Assert.Equal(2, infos.Count);
        Assert.All(infos, d => Assert.Equal(DiagnosticSeverity.Info, d.Severity));
        Assert.Contains(infos, d => d.Path == JsonPath.Parse("resources[1].dependsOn[0]"));
        Assert.Contains(infos, d => d.Path == JsonPath.Parse("resources[1].properties.a"));

        var group = analysis.Static.Resources[0];
        Assert.Equal(ResolvedNameKind.Symbolic, group.NameKind);
        Assert.Equal("parameters('env')", group.Id!.Name);

        var app = analysis.Static.Resources[1];
        Assert.True(Assert.Single(app.DependsOn).Deferred);
        var matched = Assert.Single(app.References, r => r.Path == JsonPath.Parse("resources[1].properties.b"));
        Assert.Equal(ReferenceOccurrenceKind.Reference, matched.Kind);
        Assert.Equal(group.Id, matched.Target);
        var deferred = Assert.Single(app.References, r => r.Path == JsonPath.Parse("resources[1].properties.a"));
        Assert.Equal(ReferenceOccurrenceKind.Deferred, deferred.Kind);
        Assert.Null(deferred.Target);
    }

    [Fact]
    public void Diagnostics_AreSortedByPositionAndDeduplicated()
    {
        const string json = """
            {
              "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#",
              "contentVersion": "1.0.0.0",
              "outputs": { "o": { "type": "string", "value": "[nope()]" } },
              "resources": [
                { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01", "name": "[utcNow()]" },
                { "type": "Microsoft.Entra/users", "apiVersion": "2026-10-01", "name": "u@contoso.com", "properties": { "x": "[concat('a']" } }
              ]
            }
            """;

        var analysis = Validator.Analyze(Expr.Parse(json));

        Assert.Equal(
            [DiagnosticCodes.UnknownFunction, DiagnosticCodes.NonDeterministicFunctionNotAllowed, DiagnosticCodes.ExpressionSyntaxError],
            analysis.Diagnostics.Select(d => d.Code).ToList());
        Assert.Equal([4, 6, 7], analysis.Diagnostics.Select(d => d.Location!.Value.Line).ToList());
        Assert.True(analysis.HasErrors);
        Assert.Equal(analysis.Diagnostics, Validator.Validate(Expr.Parse(json)));
    }

    [Fact]
    public void NestedChild_WithASymbolicParent_ResolvesTheInferredDependency()
    {
        var analysis = Analyze("\"resources\": [ "
            + Resource("Microsoft.Entra/applications", "app") + ", "
            + "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01\", \"name\": \"[concat(reference('app').appId, '-child-host')]\", "
            + "\"resources\": [ { \"type\": \"passwordCredentials\", \"apiVersion\": \"2026-10-01\", \"name\": \"s\" } ] } ]");

        Assert.False(analysis.HasErrors, string.Join("\n", analysis.Diagnostics));
        var host = analysis.Static.Resources[1];
        Assert.Equal(ResolvedNameKind.Symbolic, host.NameKind);
        var child = analysis.Static.Resources[2];
        Assert.Equal(ResolvedNameKind.Symbolic, child.NameKind);
        var inferred = Assert.Single(child.DependsOn);
        Assert.Equal(DependencySource.Inferred, inferred.Source);
        Assert.Equal(host.Id, inferred.Target);
    }

    [Fact]
    public void HasErrors_IgnoresWarningsAndInfos()
    {
        // WI-16: wellKnownApp() is implemented, so this template is entirely clean and the value is the Graph appId.
        var clean = Analyze("\"resources\": [ " + Resource("Microsoft.Entra/groups", "g", "\"properties\": { \"x\": \"[wellKnownApp('MicrosoftGraph')]\" }") + " ]");

        Assert.False(clean.HasErrors);
        Assert.Empty(clean.Diagnostics);
        Assert.Equal(WellKnownIds.MicrosoftGraphAppId, Expr.AssertString(Expr.Evaluator.EvaluateTree(clean.Static.Resources[0].Declaration.Properties, clean.Static.Context)!["x"]));

        // An Info-only analysis (a reference() target that depends on a parameter without an offline value) is not an error.
        var infoOnly = Analyze("\"parameters\": { \"n\": { \"type\": \"string\" } }, \"resources\": [ " + Resource("Microsoft.Entra/groups", "g", "\"properties\": { \"x\": \"[reference(parameters('n')).id]\" }") + " ]");

        Assert.False(infoOnly.HasErrors);
        var info = Assert.Single(infoOnly.Diagnostics);
        Assert.Equal(DiagnosticCodes.ReferenceDependsOnMissingParameter, info.Code);
        Assert.Equal(DiagnosticSeverity.Info, info.Severity);
    }

    [Fact]
    public void Analyze_RejectsNull()
    {
        Assert.Throws<System.ArgumentNullException>(() => Validator.Analyze(null!));
        Assert.Throws<System.ArgumentNullException>(() => new ExpressionValidator(null!));
    }

    private static IEnumerable<string> Messages(ExpressionAnalysis analysis)
    {
        return analysis.Diagnostics.Select(d => d.Message);
    }

    [Fact]
    public void SecureValues_NeverAppearInDiagnosticMessages()
    {
        var analysis = Analyze("\"parameters\": { " + Pw + " }, \"resources\": [ "
            + Resource("Microsoft.Entra/applications", "app") + ", "
            + Resource("Microsoft.Entra/groups", "[parameters('pw')]", "\"condition\": \"[parameters('pw')]\", \"dependsOn\": [ \"[parameters('pw')]\" ], \"properties\": { \"x\": \"[reference(resourceId('Microsoft.Entra/applications', parameters('pw'))).id]\" }") + " ]", SecureInline);

        Assert.All(Messages(analysis), m => Assert.DoesNotContain("s3cret", m));
    }
}
