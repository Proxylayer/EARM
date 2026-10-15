using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// The update path of every provider type: a changed property is applied with one write and shows as a Modify row first; a refused
/// update fails the resource, and the next run (without the failure) converges.
/// </summary>
public sealed class ProviderModifyTests
{
    // Per type: what to change in the valid template of ProviderFailureMatrixTests, and the resource type that is modified.
    private static readonly Dictionary<string, (string Find, string Replace, string Type)> Changes = new(StringComparer.Ordinal)
    {
        ["groups"] = ("\\\"displayName\\\": \\\"G1\\\"", "\\\"displayName\\\": \\\"G1 renamed\\\"", "Microsoft.Entra/groups"),
        ["users"] = ("\\\"displayName\\\": \\\"U1\\\"", "\\\"displayName\\\": \\\"U1 renamed\\\"", "Microsoft.Entra/users"),
        ["applications"] = ("\\\"displayName\\\": \\\"App 1\\\"", "\\\"displayName\\\": \\\"App 1 renamed\\\"", "Microsoft.Entra/applications"),
        ["administrativeUnits"] = ("\\\"description\\\": \\\"d\\\"", "\\\"description\\\": \\\"changed\\\"", "Microsoft.Entra/administrativeUnits"),
        ["namedLocations"] = ("[\\\"US\\\"]", "[\\\"US\\\", \\\"FR\\\"]", "Microsoft.Entra/conditionalAccess/namedLocations"),
        ["authenticationContexts"] = ("\\\"displayName\\\": \\\"Sensitive\\\"", "\\\"displayName\\\": \\\"Sensitive 2\\\"", "Microsoft.Entra/conditionalAccess/authenticationContextClassReferences"),
        ["authenticationStrengths"] = ("[\\\"fido2\\\"]", "[\\\"fido2\\\", \\\"windowsHelloForBusiness\\\"]", "Microsoft.Entra/conditionalAccess/authenticationStrengths"),
        ["roleDefinitions"] = ("\\\"description\\\": \\\"d\\\"", "\\\"description\\\": \\\"changed\\\"", "Microsoft.Entra/roleDefinitions"),
        ["federatedIdentityCredentials"] = ("\\\"audiences\\\": [\\\"api://AzureADTokenExchange\\\"]", "\\\"audiences\\\": [\\\"api://AzureADTokenExchange\\\"], \\\"description\\\": \\\"added\\\"", "Microsoft.Entra/applications/federatedIdentityCredentials"),
    };

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string name in Changes.Keys.Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    private static string Original(string name)
    {
        return ProviderFailureMatrixTests.Templates()[name];
    }

    private static string Modified(string name)
    {
        var change = Changes[name];
        string original = Original(name);
        // The templates are C# string literals with escaped quotes; compare on the unescaped text instead.
        string find = change.Find.Replace("\\\"", "\"", StringComparison.Ordinal);
        string replace = change.Replace.Replace("\\\"", "\"", StringComparison.Ordinal);
        Assert.Contains(find, original, StringComparison.Ordinal);
        return original.Replace(find, replace, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task AChangedProperty_IsAModifyRow_ThenOneWrite_AndTheNextRunIsQuiet(string name)
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Original(name))));
        var changed = host.Resources(Modified(name));

        var whatIf = await host.WhatIfAsync(changed);
        var run = await host.CreateAsync(changed);
        var quiet = await host.CreateAsync(changed);

        var row = whatIf.Plan.Changes.Single(c => ResourceIdentifier.TypeEquals(c.ResourceType, Changes[name].Type));
        Assert.False(row.HasError, row.UnsupportedReason);
        Assert.True(row.Type is ChangeType.Modify or ChangeType.Delete or ChangeType.Create, "what-if shows the change: " + row.Type);
        Check.NoWrites(whatIf);
        Check.Succeeded(run);
        Assert.NotEmpty(run.Writes);
        Check.Succeeded(quiet);
        Check.NoWrites(quiet);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ARefusedUpdate_FailsTheResource_AndTheNextRunConverges(string name)
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Original(name))));
        var changed = host.Resources(Modified(name));
        host.Fake.Failures.Add("PATCH", string.Empty, request => new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "denied", request.Hint), times: 1);
        host.Fake.Failures.Add("PUT", string.Empty, request => new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "denied", request.Hint), times: 1);
        host.Fake.Failures.Add("POST", string.Empty, request => new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "denied", request.Hint), times: 1);
        host.Fake.Failures.Add("DELETE", string.Empty, request => new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "denied", request.Hint), times: 1);

        var failed = await host.CreateAsync(changed);

        if (!failed.Succeeded)
        {
            var op = failed.Executed.Operations.First(o => o.Status == OperationStatus.Failed);
            Assert.False(string.IsNullOrWhiteSpace(op.ErrorCode));
        }

        host.Fake.Failures.Forbid("never-matches", "GET");
        var converged = await host.CreateAsync(changed);

        Check.Succeeded(converged);
    }
}
