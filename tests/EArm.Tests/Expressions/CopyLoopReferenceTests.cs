using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Validation;
using EArm.Expressions.Static;
using EArm.Expressions.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Offline validation of what ARM allows around copy loops: <c>reference()</c> of one copy-loop instance by its computed name from
/// anywhere (a single resource, another loop, a property copy, an output copy), <c>dependsOn</c> on a loop name, property iteration
/// building a list of references, output iteration, the flattened cartesian loop that replaces nested loops, the clear errors when a name
/// is only known at deployment, and the ARM refusal of lambda variables inside <c>reference()</c>.
/// </summary>
public sealed class CopyLoopReferenceTests
{
    private const string Scopes = "\"scopes\": { \"type\": \"array\", \"defaultValue\": [ \"dev\", \"uat\", \"prd\" ] }";

    private const string Approvers = "{ \"copy\": { \"name\": \"approvers\", \"count\": \"[length(parameters('scopes'))]\" }, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", "
        + "\"name\": \"[format('pg-{0}-approvers', parameters('scopes')[copyIndex()])]\", \"properties\": { \"displayName\": \"[format('PG {0}', parameters('scopes')[copyIndex()])]\", \"mailEnabled\": false, \"securityEnabled\": true } }";

    private static readonly TemplateValidator Core = new();
    private static readonly ExpressionValidator Expressions = new();

    private static (ExpressionAnalysis Analysis, IReadOnlyList<Diagnostic> All) Validate(string resources, string? outputs = null, string parameters = Scopes, string? variables = null, ParameterSet? values = null)
    {
        string body = "\"parameters\": { " + parameters + " }, "
            + (variables is null ? string.Empty : "\"variables\": { " + variables + " }, ")
            + "\"resources\": [ " + resources + " ]"
            + (outputs is null ? string.Empty : ", \"outputs\": { " + outputs + " }");
        var document = Expr.ParseBody(body, values);
        var analysis = Expressions.Analyze(document);
        return (analysis, Core.Validate(document).Concat(analysis.Diagnostics).ToList());
    }

    private static string User(string properties, string name = "bg@contoso.com", string extra = "")
    {
        return "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\"" + extra + ", \"properties\": { " + properties + " } }";
    }

    private static void NoErrors(IReadOnlyList<Diagnostic> diagnostics)
    {
        Assert.True(diagnostics.All(d => !d.IsError), string.Join(Environment.NewLine, diagnostics.Where(d => d.IsError).Select(d => d.ToString())));
    }

    private static ResolvedResource Resource(ExpressionAnalysis analysis, string type)
    {
        return analysis.Static.Resources.Single(r => ResourceIdentifier.TypeEquals(r.Declaration.Type!, type));
    }

    private static ResourceIdentifier Group(string name)
    {
        return new ResourceIdentifier("Microsoft.Entra/groups", [name]);
    }

    // ---- reference() of a copy-loop instance ----

    [Fact]
    public void AReferenceToOneInstance_FromASingleResource_ResolvesToThatInstance()
    {
        var (analysis, all) = Validate(Approvers + ", " + User("\"memberOf\": [ \"[reference(format('pg-{0}-approvers', parameters('scopes')[2])).id]\" ]"));

        NoErrors(all);
        var reference = Assert.Single(Resource(analysis, "Microsoft.Entra/users").References);
        Assert.Equal(ReferenceOccurrenceKind.Reference, reference.Kind);
        Assert.Equal(Group("pg-prd-approvers"), reference.Target);
    }

    [Fact]
    public void AReferenceByResourceId_AndByAComputedName_ResolveToTheInstance()
    {
        var (analysis, all) = Validate(
            Approvers + ", " + User("\"memberOf\": [ \"[reference(resourceId('Microsoft.Entra/groups', 'pg-dev-approvers')).id]\", \"[reference(concat(variables('prefix'), parameters('scopes')[1], '-approvers')).id]\" ]"),
            variables: "\"prefix\": \"pg-\"");

        NoErrors(all);
        Assert.Equal([Group("pg-dev-approvers"), Group("pg-uat-approvers")], Resource(analysis, "Microsoft.Entra/users").References.Where(r => r.Kind == ReferenceOccurrenceKind.Reference).Select(r => r.Target));
    }

    [Fact]
    public void EveryInstanceOfALoop_IsIndexed_SoAnotherLoopCanReferenceIt()
    {
        string other = "{ \"copy\": { \"name\": \"others\", \"count\": 3 }, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[format('other-{0}', copyIndex())]\", "
            + "\"properties\": { \"displayName\": \"O\", \"mailEnabled\": false, \"securityEnabled\": true, \"description\": \"[reference(format('pg-{0}-approvers', parameters('scopes')[copyIndex()])).id]\" } }";

        var (analysis, all) = Validate(Approvers + ", " + other);

        NoErrors(all);
        foreach (var scope in new[] { "dev", "uat", "prd" })
        {
            Assert.True(analysis.Static.Index.TryGet(Group($"pg-{scope}-approvers"), out var entry));
            Assert.Equal("approvers", entry.CopyName);
        }
    }

    [Fact]
    public void ANameNoInstanceProduces_IsStillReferenceTargetNotFound()
    {
        var (_, all) = Validate(Approvers + ", " + User("\"memberOf\": [ \"[reference('pg-test-approvers').id]\" ]"));

        Assert.Contains(all, d => d.Code == DiagnosticCodes.ReferenceTargetNotFound && d.Message.Contains("pg-test-approvers", StringComparison.Ordinal));
    }

    [Fact]
    public void ALoopWhoseCountIsOnlyKnownAtDeployment_CannotBeReferencedByName_WithAClearError()
    {
        string loop = "{ \"copy\": { \"name\": \"late\", \"count\": \"[length(reference('seed').displayName)]\" }, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[format('late-{0}', copyIndex())]\", \"properties\": { \"displayName\": \"L\" } }";
        string seed = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"seed\", \"properties\": { \"displayName\": \"S\" } }";

        var (_, all) = Validate(seed + ", " + loop + ", " + User("\"memberOf\": [ \"[reference('late-0').id]\" ]"));

        var error = Assert.Single(all, d => d.Code == DiagnosticCodes.ReferenceTargetNotFound);
        Assert.Contains("'late'", error.Message, StringComparison.Ordinal);
        Assert.Contains("only known at deployment", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ALoopWhoseCountWaitsForAMissingParameter_DefersTheReference_OfflineOnly()
    {
        string parameters = "\"scopes\": { \"type\": \"array\" }";

        var (_, all) = Validate(Approvers + ", " + User("\"memberOf\": [ \"[reference('pg-dev-approvers').id]\" ]"), parameters: parameters);

        NoErrors(all);
        Assert.Contains(all, d => d.Code == DiagnosticCodes.ReferenceDependsOnMissingParameter && d.Message.Contains("copy loop", StringComparison.Ordinal));
    }

    // ---- dependsOn ----

    [Fact]
    public void DependsOn_NamesAWholeLoop_OrOneInstance()
    {
        var (analysis, all) = Validate(Approvers + ", " + User("\"displayName\": \"BG\"", extra: ", \"dependsOn\": [ \"approvers\", \"pg-uat-approvers\" ]"));

        NoErrors(all);
        var dependsOn = Resource(analysis, "Microsoft.Entra/users").DependsOn;
        Assert.Equal("approvers", dependsOn[0].CopyName);
        Assert.Equal(Group("pg-uat-approvers"), dependsOn[1].Target);
    }

    // ---- property iteration ----

    [Fact]
    public void APropertyCopy_BuildsAListOfReferences_OneTargetPerElement()
    {
        string copy = "\"copy\": [ { \"name\": \"memberOf\", \"count\": \"[length(parameters('scopes'))]\", \"input\": \"[reference(format('pg-{0}-approvers', parameters('scopes')[copyIndex('memberOf')])).id]\" } ]";

        var (analysis, all) = Validate(Approvers + ", " + User(copy));

        NoErrors(all);
        var references = Resource(analysis, "Microsoft.Entra/users").References;
        Assert.Equal([Group("pg-dev-approvers"), Group("pg-uat-approvers"), Group("pg-prd-approvers")], references.Select(r => r.Target));
        Assert.Equal(["resources[1].properties.memberOf[0]", "resources[1].properties.memberOf[1]", "resources[1].properties.memberOf[2]"], references.Select(r => r.Path.ToString()));
    }

    [Fact]
    public void APropertyCopy_InsideAResourceLoop_UsesBothLoopsByName()
    {
        string users = "{ \"copy\": { \"name\": \"breakGlass\", \"count\": 2 }, \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[format('bg{0}@contoso.com', copyIndex())]\", "
            + "\"properties\": { \"copy\": [ { \"name\": \"memberOf\", \"count\": \"[length(parameters('scopes'))]\", \"input\": \"[reference(format('pg-{0}-approvers', parameters('scopes')[copyIndex('memberOf')])).id]\" } ] } }";

        var (_, all) = Validate(Approvers + ", " + users);

        NoErrors(all);
    }

    [Fact]
    public void CopyIndexWithoutAName_InsideAPropertyCopy_IsRefused()
    {
        string copy = "\"copy\": [ { \"name\": \"memberOf\", \"count\": 1, \"input\": \"[reference(format('pg-{0}-approvers', parameters('scopes')[copyIndex()])).id]\" } ]";

        var (_, all) = Validate(Approvers + ", " + User(copy));

        Assert.Contains(all, d => d.Code == DiagnosticCodes.CopyIndexOutsideCopy && d.Message.Contains("copyIndex('memberOf')", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("{ \"count\": 1, \"input\": \"x\" }", "literal 'name'")]
    [InlineData("{ \"name\": \"memberOf\", \"input\": \"x\" }", "'count'")]
    [InlineData("{ \"name\": \"memberOf\", \"count\": 1 }", "'input'")]
    [InlineData("{ \"name\": \"memberOf\", \"count\": -1, \"input\": \"x\" }", "negative")]
    public void AMalformedPropertyCopyEntry_IsInvalidCopy(string entry, string fragment)
    {
        var (_, all) = Validate(User("\"copy\": [ " + entry + " ]"));

        Assert.Contains(all, d => d.Code == DiagnosticCodes.InvalidCopy && d.Message.Contains(fragment, StringComparison.Ordinal));
    }

    [Fact]
    public void APropertyCopy_ThatAlsoDeclaresThePropertyDirectly_IsInvalidCopy()
    {
        var (_, all) = Validate(User("\"memberOf\": [], \"copy\": [ { \"name\": \"memberOf\", \"count\": 0, \"input\": \"x\" } ]"));

        Assert.Contains(all, d => d.Code == DiagnosticCodes.InvalidCopy && d.Message.Contains("also declared directly", StringComparison.Ordinal));
    }

    [Fact]
    public void APropertyCopyCountOnlyKnownAtDeployment_IsReported()
    {
        string seed = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"seed\", \"properties\": { \"displayName\": \"S\" } }";

        var (_, all) = Validate(seed + ", " + User("\"copy\": [ { \"name\": \"memberOf\", \"count\": \"[length(reference('seed').displayName)]\", \"input\": \"[reference('seed').id]\" } ]"));

        Assert.Contains(all, d => d.Code == DiagnosticCodes.InvalidCopy && d.Path == JsonPath.Parse("resources[1].properties.copy[0].count") && d.Message.Contains("before deployment", StringComparison.Ordinal));
    }

    // ---- output iteration ----

    [Fact]
    public void AnOutputCopy_BuildsAnArray_ReferencingEachInstance()
    {
        string outputs = "\"approverIds\": { \"type\": \"array\", \"copy\": { \"count\": \"[length(parameters('scopes'))]\", \"input\": { \"scope\": \"[parameters('scopes')[copyIndex()]]\", \"id\": \"[reference(format('pg-{0}-approvers', parameters('scopes')[copyIndex()])).id]\" } } }";

        var (analysis, all) = Validate(Approvers, outputs);

        NoErrors(all);
        var value = Assert.IsType<JsonArray>(analysis.Static.Outputs["approverIds"].Value);
        Assert.Equal(["dev", "uat", "prd"], value.Select(e => e!["scope"]!.GetValue<string>()));
    }

    [Fact]
    public void AnOutputWithBothValueAndCopy_IsInvalidCopy()
    {
        var (_, all) = Validate(Approvers, "\"o\": { \"type\": \"array\", \"value\": [], \"copy\": { \"count\": 1, \"input\": \"x\" } }");

        Assert.Contains(all, d => d.Code == DiagnosticCodes.InvalidCopy && d.Path == JsonPath.Parse("outputs.o.value"));
    }

    [Fact]
    public void AnOutputCopyThatNamesNoInstance_IsReferenceTargetNotFound_AtTheInput()
    {
        var (_, all) = Validate(Approvers, "\"o\": { \"type\": \"array\", \"copy\": { \"count\": 4, \"input\": \"[reference(format('pg-{0}-approvers', copyIndex())).id]\" } }");

        Assert.Contains(all, d => d.Code == DiagnosticCodes.ReferenceTargetNotFound && d.Path == JsonPath.Parse("outputs.o.copy.input"));
    }

    // ---- the nested-loop idiom ----

    [Fact]
    public void AFlattenedScopesTimesTiersLoop_IndexesEveryInstance()
    {
        string access = "{ \"copy\": { \"name\": \"access\", \"count\": \"[mul(length(parameters('scopes')), length(variables('tiers')))]\" }, \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", "
            + "\"name\": \"[format('pg-{0}-{1}-access', parameters('scopes')[div(copyIndex(), length(variables('tiers')))], variables('tiers')[mod(copyIndex(), length(variables('tiers')))])]\", \"properties\": { \"displayName\": \"A\" } }";
        string outputs = "\"prdReader\": { \"type\": \"string\", \"value\": \"[reference('pg-prd-reader-access').id]\" }";

        var (analysis, all) = Validate(access, outputs, variables: "\"tiers\": [ \"privileged\", \"reader\" ]");

        NoErrors(all);
        var ids = analysis.Static.Index.Entries.Select(e => e.Id.Name).Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(["pg-dev-privileged-access", "pg-dev-reader-access", "pg-prd-privileged-access", "pg-prd-reader-access", "pg-uat-privileged-access", "pg-uat-reader-access"], ids);
    }

    // ---- lambdas (ARM: lambda variables are not supported inside reference()) ----

    [Fact]
    public void AReferenceWhoseTargetUsesALambdaVariable_IsRefused_PointingAtCopyLoops()
    {
        var (_, all) = Validate(Approvers + ", " + User("\"memberOf\": \"[map(parameters('scopes'), lambda('s', reference(format('pg-{0}-approvers', lambdaVariables('s'))).id))]\""));

        var error = Assert.Single(all, d => d.Code == DiagnosticCodes.ReferenceNotStaticallyResolvable);
        Assert.Contains("lambda variable", error.Message, StringComparison.Ordinal);
        Assert.Contains("property copy loop", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AReferenceInsideALambda_ThatUsesNoLambdaVariable_Resolves()
    {
        var (analysis, all) = Validate(Approvers + ", " + User("\"memberOf\": \"[map(range(0, 1), lambda('i', reference('pg-dev-approvers').id))]\""));

        NoErrors(all);
        Assert.Equal(Group("pg-dev-approvers"), Assert.Single(Resource(analysis, "Microsoft.Entra/users").References).Target);
    }
}
