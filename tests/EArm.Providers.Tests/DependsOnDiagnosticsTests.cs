using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine;
using EArm.Engine.Planning;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// <c>dependsOn</c> entries the dependency graph cannot resolve, and the cycles it refuses: every shape is reported as a
/// diagnostic with a stable code (never an exception of another kind, never a silent drop), before any Graph call.
/// </summary>
public sealed class DependsOnDiagnosticsTests
{
    private static string Group(string name, string? dependsOn = null, string? extraProperties = null)
    {
        return "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"displayName\": \"" + name + "\"" + (extraProperties is null ? string.Empty : ", " + extraProperties) + " }"
            + (dependsOn is null ? string.Empty : ", \"dependsOn\": [" + dependsOn + "]") + " }";
    }

    private static string User(string upn, string? dependsOn = null)
    {
        return "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + upn + "\", \"properties\": { \"displayName\": \"U\" }"
            + (dependsOn is null ? string.Empty : ", \"dependsOn\": [" + dependsOn + "]") + " }";
    }

    private static async Task<ValidationFailedException> Fail(string resources)
    {
        var host = new ProviderHost();
        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.ExpandAsync(host.Resources(resources)));
        Assert.Empty(host.Fake.Requests);
        return ex;
    }

    [Theory]
    [InlineData("\"[resourceId('Microsoft.Entra/groups', 'missing')]\"", DiagnosticCodes.DependsOnTargetNotFound, "does not name a resource of this template")]
    [InlineData("\"Microsoft.Entra/groups/missing\"", DiagnosticCodes.DependsOnTargetNotFound, "does not name a resource")]
    [InlineData("\"missing\"", DiagnosticCodes.DependsOnTargetNotFound, "does not name a resource")]
    [InlineData("\"/providers/Microsoft.Entra/bogus/x\"", DiagnosticCodes.InvalidResourceId, "not a valid resource id")]
    [InlineData("\"[1]\"", DiagnosticCodes.DependsOnTargetNotFound, "must evaluate to a string")]
    [InlineData("\"[createArray('a')]\"", DiagnosticCodes.DependsOnTargetNotFound, "must evaluate to a string")]
    [InlineData("\"[reference('app-x').id]\"", "ReferenceTargetNotFound", "no resource named 'app-x'")]
    public async Task UnresolvableDependsOnEntries_AreReportedWithTheirCode(string entry, string code, string fragment)
    {
        var ex = await Fail(Group("g1", entry));

        var diagnostic = ex.Diagnostics.FirstOrDefault(d => d.Code == code);
        Assert.True(diagnostic is not null, "expected " + code + "; got " + string.Join(" | ", ex.Diagnostics.Select(d => d.Code + ": " + d.Message)));
        Assert.True(diagnostic.Message.Contains(fragment, StringComparison.Ordinal), diagnostic.Message);
        Assert.NotNull(diagnostic.Path);
        Assert.Contains("dependsOn", diagnostic.Path.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ABrokenExpression_InDependsOn_IsAnExpressionDiagnostic_NotAnException()
    {
        var ex = await Fail(Group("g1", "\"[concat('a', \""));

        Assert.NotEmpty(ex.Diagnostics);
        Assert.All(ex.Diagnostics, d => Assert.False(string.IsNullOrEmpty(d.Code)));
    }

    [Fact]
    public async Task ABareNameThatTwoResourcesShare_IsAmbiguous()
    {
        // A user and a group both named "shared@contoso.com" / "shared@contoso.com" cannot be told apart by name alone.
        string resources = string.Join(",", Group("shared@contoso.com"), User("shared@contoso.com"), Group("dependent", "\"shared@contoso.com\""));

        var ex = await Fail(resources);

        var diagnostic = Assert.Single(ex.Diagnostics, d => d.Code == DiagnosticCodes.AmbiguousDependsOn);
        Assert.Contains("names 2 resources", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASelfDependency_AndATwoResourceCycle_AreDependencyCycles()
    {
        var self = await Fail(Group("a", "\"[resourceId('Microsoft.Entra/groups', 'a')]\""));
        var cycle = await Fail(string.Join(",", Group("a", "\"[resourceId('Microsoft.Entra/groups', 'b')]\""), Group("b", "\"[resourceId('Microsoft.Entra/groups', 'a')]\"")));

        Assert.Contains(self.Diagnostics, d => d.Code == EngineDiagnosticCodes.DependencyCycle);
        Assert.Contains(cycle.Diagnostics, d => d.Code == EngineDiagnosticCodes.DependencyCycle);
    }

    [Fact]
    public async Task ALongCycle_NamesTheResourcesInvolved()
    {
        string resources = string.Join(",", Enumerable.Range(0, 6).Select(i => Group("g" + i, "\"[resourceId('Microsoft.Entra/groups', 'g" + ((i + 1) % 6) + "')]\"")));

        var ex = await Fail(resources);

        var diagnostic = ex.Diagnostics.First(d => d.Code == EngineDiagnosticCodes.DependencyCycle);
        Assert.Contains("g0", diagnostic.Message, StringComparison.Ordinal);
        Assert.Contains("g3", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ValidDependsOnForms_AllResolveToTheSameEdge()
    {
        var host = new ProviderHost();
        string[] forms =
        [
            "\"[resourceId('Microsoft.Entra/groups', 'base')]\"",
            "\"/providers/Microsoft.Entra/groups/base\"",
            "\"Microsoft.Entra/groups/base\"",
            "\"base\"",
            "\"[format('{0}', 'base')]\"",
        ];

        foreach (string form in forms)
        {
            var plan = await host.ExpandAsync(host.Resources(string.Join(",", Group("base"), Group("child", form))));

            var child = plan.Instances.Single(i => i.Id.NameLeaf == "child");
            Assert.True(child.Dependencies.Any(d => d.NameLeaf == "base"), "form " + form);
        }
    }
}
