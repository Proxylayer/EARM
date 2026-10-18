using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Engine.Execution;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// One valid template per provider type, run against scripted Graph failures: forbidden, throttled, unavailable and a
/// vanished object, on writes and on reads. Contract for every type: no exception escapes the engine, a failed write
/// is a failed operation with an error code and a message, nothing half-applied is reported as applied, the retry policy
/// is bounded, and no secret appears in any error text.
/// </summary>
public sealed class ProviderFailureMatrixTests
{
    private const string Canary = "Hunter2-Canary-9a7c";

    /// <summary>The canary with its secret half replaced: put in Graph's error message, which is not a secret and is relayed as is.</summary>
    private const string NearMiss = "x-Canary-9a7c";

    public static TheoryData<string> Cases()
    {
        var data = new TheoryData<string>();
        foreach (string name in Templates().Keys.Order(StringComparer.Ordinal))
        {
            data.Add(name);
        }

        return data;
    }

    internal static Dictionary<string, string> Templates()
    {
        return new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["groups"] = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g1\", \"properties\": { \"displayName\": \"G1\" } }",
            ["users"] = "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"u1@contoso.com\", \"properties\": { \"displayName\": \"U1\" } }",
            ["applications"] = "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app1\", \"properties\": { \"displayName\": \"App 1\" } }",
            ["administrativeUnits"] = "{ \"type\": \"Microsoft.Entra/administrativeUnits\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"AU 1\", \"properties\": { \"description\": \"d\" } }",
            ["namedLocations"] = "{ \"type\": \"Microsoft.Entra/conditionalAccess/namedLocations\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"Offices\", \"properties\": { \"@odata.type\": \"#microsoft.graph.countryNamedLocation\", \"countriesAndRegions\": [\"US\"] } }",
            ["authenticationContexts"] = "{ \"type\": \"Microsoft.Entra/conditionalAccess/authenticationContextClassReferences\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"c5\", \"properties\": { \"displayName\": \"Sensitive\" } }",
            ["authenticationStrengths"] = "{ \"type\": \"Microsoft.Entra/conditionalAccess/authenticationStrengths\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"Strong\", \"properties\": { \"displayName\": \"Strong\", \"allowedCombinations\": [\"fido2\"] } }",
            ["roleDefinitions"] = "{ \"type\": \"Microsoft.Entra/roleDefinitions\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"Custom Role\", \"properties\": { \"description\": \"d\", \"rolePermissions\": [{ \"allowedResourceActions\": [\"microsoft.directory/users/standard/read\"] }] } }",
            ["passwordCredentials"] = "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app2\", \"properties\": { \"displayName\": \"App 2\" } }, "
                + "{ \"type\": \"Microsoft.Entra/applications/passwordCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app2/secret\", \"properties\": { } }",
            ["federatedIdentityCredentials"] = "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app3\", \"properties\": { \"displayName\": \"App 3\" } }, "
                + "{ \"type\": \"Microsoft.Entra/applications/federatedIdentityCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app3/github-main\", \"properties\": { \"issuer\": \"https://token.actions.githubusercontent.com\", \"subject\": \"repo:o/r:ref:refs/heads/main\", \"audiences\": [\"api://AzureADTokenExchange\"] } }",
            ["servicePrincipals"] = "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app4\", \"properties\": { \"displayName\": \"App 4\" } }, "
                + "{ \"type\": \"Microsoft.Entra/servicePrincipals\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[reference('app4').appId]\", \"properties\": { }, \"dependsOn\": [\"[resourceId('Microsoft.Entra/applications', 'app4')]\"] }",
        };
    }

    /// <summary>
    /// The shared template of <paramref name="name"/> with the <see cref="Canary"/> declared as a secret where the type
    /// takes one: the users case declares it as the initial password (the shared <see cref="Templates"/> stay as they are
    /// for the other contract tests).
    /// </summary>
    private static string Template(string name)
    {
        string template = Templates()[name];
        return name == "users"
            ? template.Replace("\"displayName\": \"U1\"", "\"displayName\": \"U1\", \"passwordProfile\": { \"password\": \"" + Canary + "\" }", StringComparison.Ordinal)
            : template;
    }

    /// <summary>A host whose fake answers every <c>addPassword</c> with the <see cref="Canary"/> as its <c>secretText</c>.</summary>
    private static ProviderHost NewHost()
    {
        var host = new ProviderHost();
        host.Directory.SecretText = Canary;
        return host;
    }

    /// <summary>
    /// Every text a user or a log can see: the plan's and the result's diagnostics, the rows' reasons and errors, the
    /// failed operations' messages and the rendered what-if (text and JSON).
    /// </summary>
    private static string VisibleText(Run run)
    {
        var parts = new List<string?>();
        parts.AddRange(run.Plan.Diagnostics.Select(d => d.Message));
        parts.AddRange(run.Plan.Changes.SelectMany(c => c.Reasons.Append(c.UnsupportedReason)));
        if (run.Result is { } result)
        {
            parts.AddRange(result.Diagnostics.Select(d => d.Message));
            parts.AddRange(result.Operations.Select(o => o.Error));
            parts.AddRange(result.Changes.SelectMany(c => c.Reasons.Append(c.UnsupportedReason)));
        }

        parts.Add(run.WhatIfTextOutput);
        parts.Add(run.WhatIfJsonText);
        return string.Join(" | ", parts.Where(p => p is not null));
    }

    /// <summary>The <see cref="Canary"/> is in no error text, no diagnostic, no rendered what-if and no log line.</summary>
    private static void AssertNoLeak(ProviderHost host, Run run)
    {
        Assert.DoesNotContain(Canary, VisibleText(run), StringComparison.Ordinal);
        Assert.DoesNotContain(host.Logs.Lines, l => l.Contains(Canary, StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ForbiddenWrites_FailTheResource_WithAMessage(string name)
    {
        var host = NewHost();
        host.Fake.Failures.Add("POST", string.Empty, request => new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges " + NearMiss, request.Hint), times: -1);
        host.Fake.Failures.Add("PATCH", string.Empty, request => new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges", request.Hint), times: -1);
        host.Fake.Failures.Add("PUT", string.Empty, request => new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges", request.Hint), times: -1);

        var run = await host.CreateAsync(host.Resources(Template(name)));

        Assert.False(run.Succeeded, name + " unexpectedly succeeded");
        var failed = run.Executed.Operations.Where(o => o.Status == OperationStatus.Failed).ToList();
        Assert.NotEmpty(failed);
        Assert.All(failed, o =>
        {
            Assert.False(string.IsNullOrWhiteSpace(o.ErrorCode));
            Assert.False(string.IsNullOrWhiteSpace(o.Error));
        });
        Assert.DoesNotContain(failed, o => o.Error!.Contains("Unhandled", StringComparison.OrdinalIgnoreCase));
        Assert.All(failed, o => Assert.Null(o.GraphId));
        Assert.Empty(host.Directory.Objects("/oauth2PermissionGrants"));
        AssertNoLeak(host, run);

        // Graph's own message is relayed verbatim (it carries only a near-miss of the canary), which proves the leak
        // check reads the text a user sees.
        if (host.Fake.Requests.Any(r => r.Method == "POST"))
        {
            Assert.Contains(NearMiss, VisibleText(run), StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task PersistentUnavailability_IsRetriedAFixedNumberOfTimes_ThenFailsTheResource(string name)
    {
        var host = NewHost();
        host.Fake.Failures.ServerError(string.Empty, times: -1, method: "POST");
        host.Fake.Failures.ServerError(string.Empty, times: -1, method: "PATCH");
        host.Fake.Failures.ServerError(string.Empty, times: -1, method: "PUT");

        var run = await host.CreateAsync(host.Resources(Template(name)));

        Assert.False(run.Succeeded);
        Assert.Contains(run.Executed.Operations, o => o.Status == OperationStatus.Failed);

        // Review round 2 (S2-07): a create that is a POST is never repeated blindly by the executor's retry policy (a 5xx may have followed a commit; the object is
        // re-read by key instead), so it waits for nothing; the upserts (a PATCH with create-if-missing: applications, groups, authentication contexts, federated credentials, and what depends on them) are still retried, bounded.
        bool firstWriteIsAnUpsert = name is "groups" or "applications" or "authenticationContexts" or "federatedIdentityCredentials" or "passwordCredentials" or "servicePrincipals";
        if (firstWriteIsAnUpsert)
        {
            Assert.True(host.Delays.Count > 0, "the policy waited between attempts");
        }
        else
        {
            Assert.Equal(0, host.Delays.Count);
            Assert.Single(host.Fake.Writes, w => w.Method == "POST");
        }

        Assert.True(host.Delays.Count <= 60, "retries are bounded: " + host.Delays.Count);
        AssertNoLeak(host, run);
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ThrottlingThatClearsAfterAFewAttempts_StillSucceeds_AfterRetrying(string name)
    {
        var host = NewHost();
        host.Fake.Failures.Throttle(string.Empty, times: 2, retryAfter: TimeSpan.FromSeconds(3), method: "POST");
        host.Fake.Failures.Throttle(string.Empty, times: 2, retryAfter: TimeSpan.FromSeconds(3), method: "PATCH");
        host.Fake.Failures.Throttle(string.Empty, times: 2, retryAfter: TimeSpan.FromSeconds(3), method: "PUT");

        var run = await host.CreateAsync(host.Resources(Template(name)));

        Check.Succeeded(run);
        Assert.True(host.Delays.Count >= 2, "two throttled attempts were retried");
        AssertNoLeak(host, run);

        // The canary was really in play: sent as the declared password, or handed out by addPassword.
        if (name == "users")
        {
            Assert.Contains(host.Fake.Writes, w => w.Body?.ToJsonString().Contains(Canary, StringComparison.Ordinal) == true);
        }
        else if (name == "passwordCredentials")
        {
            Assert.Contains(host.Fake.Writes, w => w.Path.EndsWith("/addPassword", StringComparison.Ordinal));
        }
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public async Task ForbiddenReads_AreReportedAsAFailure_NeverAnUnhandledException_AndNothingIsWritten(string name)
    {
        var host = NewHost();
        host.Fake.Failures.Add("GET", string.Empty, request => new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "Insufficient privileges", request.Hint), times: -1);

        var whatIf = await host.WhatIfAsync(host.Resources(Template(name)));

        Assert.True(host.Fake.Requests.All(r => !r.IsWrite), "what-if never writes");
        var unreadable = whatIf.Plan.Changes.Where(c => c.HasError).ToList();
        Assert.NotEmpty(unreadable);
        Assert.All(unreadable, c => Assert.Contains("403", c.UnsupportedReason, StringComparison.Ordinal));
        AssertNoLeak(host, whatIf);
    }
}
