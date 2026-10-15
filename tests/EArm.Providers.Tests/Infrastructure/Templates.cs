using System;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests.Infrastructure;

/// <summary>Inline template builders shared by the provider test classes.</summary>
internal static class Templates
{
    public const string Schema = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";

    public static string Envelope(string resources, string? outputs = null, string? parameters = null)
    {
        string parametersJson = parameters is null ? string.Empty : "\"parameters\": { " + parameters + " }, ";
        string outputsJson = outputs is null ? string.Empty : ", \"outputs\": { " + outputs + " }";
        return "{ \"$schema\": \"" + Schema + "\", \"contentVersion\": \"1.0.0.0\", " + parametersJson + "\"resources\": [ " + resources + " ]" + outputsJson + " }";
    }

    /// <summary>One resource declaration; <paramref name="extra"/> is appended inside the resource object (<c>, "dependsOn": [...]</c>).</summary>
    public static string Resource(string type, string name, string properties, string apiVersion = ProviderHost.ApiVersion, string? extra = null)
    {
        return "{ \"type\": \"" + type + "\", \"apiVersion\": \"" + apiVersion + "\", \"name\": \"" + name + "\", \"properties\": " + properties + (extra ?? string.Empty) + " }";
    }

    public static string DependsOn(string type, string name)
    {
        return ", \"dependsOn\": [\"[resourceId('" + type + "', '" + name + "')]\"]";
    }

    /// <summary>A <c>securestring</c> parameter declaration.</summary>
    public static string SecureParameter(string name)
    {
        return "\"" + name + "\": { \"type\": \"securestring\" }";
    }
}

/// <summary>Assertion helpers shared by the provider test classes.</summary>
internal static class Check
{
    public static string Json(JsonNode? node)
    {
        return node?.ToJsonString() ?? "null";
    }

    public static JsonObject Body(FakeGraphRequest request)
    {
        return Assert.IsType<JsonObject>(request.Body);
    }

    public static void Succeeded(Run run)
    {
        Assert.True(run.Succeeded, "expected a succeeded run: " + run.Errors + "\n" + run.DescribeRequests());
    }

    public static void NoWrites(Run run)
    {
        Assert.True(run.Writes.Count == 0, "expected no writes:\n" + string.Join("\n", run.Writes.Select(Run.Describe)));
    }

    public static Diagnostic Diagnostic(Run run, string code)
    {
        var diagnostic = run.Executed.Diagnostics.FirstOrDefault(d => d.Code == code);
        Assert.True(diagnostic is not null, $"expected diagnostic {code}; got: " + string.Join(" | ", run.Executed.Diagnostics));
        return diagnostic;
    }

    public static void NoDiagnostic(Run run, string code)
    {
        Assert.DoesNotContain(run.Executed.Diagnostics, d => d.Code == code);
    }
}

/// <summary>
/// The secret-leak sweep: a secret string may appear in the bodies the test names as intended (and nowhere else in a
/// request), never in a path / query / header, the state document, the what-if JSON or text, any change row, any
/// diagnostic, the public outputs, or any log line.
/// </summary>
internal static class SecretSweep
{
    public static void AssertNoLeak(ProviderHost host, Run? whatIf, Run create, string secret, Func<FakeGraphRequest, bool> intendedBody, int expectedIntendedBodies, bool checkChangeRows = true)
    {
        int intended = 0;
        foreach (var request in host.Fake.Requests)
        {
            string body = request.Body?.ToJsonString() ?? string.Empty;
            if (body.Contains(secret, StringComparison.Ordinal))
            {
                Assert.True(intendedBody(request), "the secret reached an unintended request body: " + Run.Describe(request));
                intended++;
            }

            Assert.DoesNotContain(secret, request.Path, StringComparison.Ordinal);
            if (request.UrlParameters is not null)
            {
                Assert.All(request.UrlParameters, p => Assert.DoesNotContain(secret, p.Value, StringComparison.Ordinal));
            }

            if (request.Headers is not null)
            {
                Assert.All(request.Headers, h => Assert.DoesNotContain(secret, h.Value, StringComparison.Ordinal));
            }
        }

        if (expectedIntendedBodies >= 0)
        {
            Assert.Equal(expectedIntendedBodies, intended);
        }

        Assert.DoesNotContain(secret, host.StateDocument ?? string.Empty, StringComparison.Ordinal);
        if (whatIf is not null)
        {
            Assert.DoesNotContain(secret, whatIf.WhatIfJsonText, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, whatIf.WhatIfTextOutput, StringComparison.Ordinal);
            Assert.All(whatIf.Plan.Diagnostics, d => Assert.DoesNotContain(secret, d.Message, StringComparison.Ordinal));
        }

        foreach (var change in checkChangeRows ? create.Executed.Changes.Concat(create.Plan.Changes) : [])
        {
            Assert.DoesNotContain(secret, change.Before?.ToJsonString() ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, change.After?.ToJsonString() ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, string.Join(" ", change.Reasons), StringComparison.Ordinal);
            Assert.DoesNotContain(secret, change.UnsupportedReason ?? string.Empty, StringComparison.Ordinal);
        }

        Assert.All(create.Executed.Diagnostics.Concat(create.Plan.Diagnostics), d => Assert.DoesNotContain(secret, d.Message, StringComparison.Ordinal));
        Assert.All(create.Executed.Operations, o => Assert.DoesNotContain(secret, o.Error ?? string.Empty, StringComparison.Ordinal));
        Assert.DoesNotContain(secret, create.Executed.Outputs.ToJsonString(), StringComparison.Ordinal);
        Assert.False(host.Logs.Contains(secret), "the secret reached a log line:\n" + string.Join("\n", host.Logs.Lines.Where(l => l.Contains(secret, StringComparison.Ordinal))));
    }
}
