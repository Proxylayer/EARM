using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Lifecycle contract of every provider type, driven by one valid template per type: a false condition ignores the
/// resource without a write, a key that carries nothing resolvable is a key error, an unevaluated instance is guarded, and a
/// second identical run changes nothing and records the same state.
/// </summary>
public sealed class ProviderLifecycleContractTests
{
    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string name in ProviderFailureMatrixTests.Templates().Keys.Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    private static string WithCondition(string template, string condition)
    {
        // Every resource of the template (parents and children alike) gets the condition.
        return template.Replace("\"properties\":", "\"condition\": " + condition + ", \"properties\":", StringComparison.Ordinal);
    }

    private static string TypeOf(string name)
    {
        return name switch
        {
            "groups" => "Microsoft.Entra/groups",
            "users" => "Microsoft.Entra/users",
            "applications" => "Microsoft.Entra/applications",
            "administrativeUnits" => "Microsoft.Entra/administrativeUnits",
            "namedLocations" => "Microsoft.Entra/conditionalAccess/namedLocations",
            "authenticationContexts" => "Microsoft.Entra/conditionalAccess/authenticationContextClassReferences",
            "authenticationStrengths" => "Microsoft.Entra/conditionalAccess/authenticationStrengths",
            "roleDefinitions" => "Microsoft.Entra/roleDefinitions",
            "passwordCredentials" => "Microsoft.Entra/applications/passwordCredentials",
            "federatedIdentityCredentials" => "Microsoft.Entra/applications/federatedIdentityCredentials",
            "servicePrincipals" => "Microsoft.Entra/servicePrincipals",
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AFalseCondition_IgnoresTheResource_WithoutAnyWrite(string name)
    {
        if (name == "servicePrincipals")
        {
            // Its name is reference(app).appId: ignoring the application leaves the name unresolvable, which is its own failure (covered by the dependsOn tests).
            return;
        }

        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(WithCondition(ProviderFailureMatrixTests.Templates()[name], "false")));

        Check.Succeeded(run);
        Assert.All(run.Executed.Operations, o => Assert.Equal(OperationStatus.Ignored, o.Status));
        Check.NoWrites(run);
        Assert.Empty(run.State!.Resources);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ARerun_ChangesNothing_AndKeepsTheSameState(string name)
    {
        var host = new ProviderHost();
        var document = host.Resources(ProviderFailureMatrixTests.Templates()[name]);
        var first = await host.CreateAsync(document);
        Check.Succeeded(first);
        string stateAfterFirst = host.StateDocument!;

        var second = await host.CreateAsync(document);

        Check.Succeeded(second);
        Assert.All(second.Executed.Operations.Where(o => ResourceIdentifier.TypeEquals(o.Type, TypeOf(name))), o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(second);
        var before = JsonNode.Parse(stateAfterFirst)!["resources"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
        var after = JsonNode.Parse(host.StateDocument!)!["resources"]!.AsObject().Select(p => p.Key).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(before, after);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AKeyWithNothingInIt_IsAKeyError_NotANullReference(string name)
    {
        var host = new ProviderHost();
        var provider = host.Provider(TypeOf(name));
        var empty = new ResourceKey { Type = TypeOf(name), Values = new Dictionary<string, string>() };

        var ex = await Record.ExceptionAsync(() => provider.ReadAsync(empty, host.Fake, ProviderHost.Ct));

        Assert.NotNull(ex);
        Assert.IsNotType<NullReferenceException>(ex);
        Assert.True(ex is ProviderException or InvalidOperationException or ArgumentException or KeyNotFoundException, ex.GetType().Name + ": " + ex.Message);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AnUnevaluatedInstance_IsGuarded_OnCreate(string name)
    {
        var host = new ProviderHost();
        var provider = host.Provider(TypeOf(name));
        var whatIf = await host.WhatIfAsync(host.Resources(ProviderFailureMatrixTests.Templates()[name]));
        var instance = whatIf.Plan.Instances.First(i => ResourceIdentifier.TypeEquals(i.Type, TypeOf(name)));
        instance.Properties = null;

        var ex = await Record.ExceptionAsync(() => provider.CreateAsync(instance, host.Fake, ProviderHost.Ct));

        Assert.NotNull(ex);
        Assert.IsNotType<NullReferenceException>(ex);
        Assert.Empty(host.Fake.Writes);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task CompleteModeDeletesNothing_AndDeploymentDeleteRemovesWhatTheTemplateDeclares(string name)
    {
        var host = new ProviderHost();
        var document = host.Resources(ProviderFailureMatrixTests.Templates()[name]);
        Check.Succeeded(await host.CreateAsync(document));

        var complete = await host.CreateAsync(host.Resources(string.Empty), EArm.Engine.Planning.DeploymentMode.Complete);
        Check.Succeeded(complete);
        Check.NoWrites(complete);

        host.ClearRequests();
        var deletion = await host.DeleteDeclaredAsync(document);

        Assert.True(deletion.Succeeded, string.Join("; ", deletion.Outcomes.Select(o => o.Target.Instance.Id.Id + "=" + o.Status + " " + o.Error)));
        Assert.Contains(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.DoesNotContain(EArm.Engine.Execution.TemplateDeleter.FindDeclaredObjects((await host.WhatIfAsync(document)).Plan), t => t.Refusal is null);
    }
}
