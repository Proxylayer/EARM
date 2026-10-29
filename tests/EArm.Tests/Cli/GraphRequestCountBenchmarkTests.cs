using System;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Hosting;
using EArm.Core;
using EArm.Graph;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// A request-count benchmark of <c>deployment what-if</c> through the whole CLI over the in-memory directory: 5 applications with their service principals,
/// 20 groups with members and 20 app role assignments to Microsoft Graph. Every request costs 150 ms on the fake wire and the request scheduler paces for
/// real (<see cref="TaskDelayProvider"/>), so the <c>--timings</c> table and the wall time printed to the test output look like a slow tenant.
/// <para>
/// Only the number of Graph requests is asserted: it is deterministic (the directory, the template and the plan do not change between runs), so it is a
/// regression guard for the request count, not a timing test. Tagged <c>Category=Perf</c>, which <c>test.ps1</c> leaves out of every default run; run it
/// with <c>pwsh -File test.ps1 -Target EArm.Tests -Filter "Category=Perf" -Detailed</c> to see the numbers.
/// </para>
/// </summary>
public sealed class GraphRequestCountBenchmarkTests
{
    // The Graph requests one what-if sends for the template below, measured when the benchmark was written. When an optimization lowers the count, the
    // assertion still passes: lower the constant to the new measured number (printed as "Graph requests" in the test output) in the same change, so the gain
    // is locked in. A count above the baseline is a regression: find the extra requests (the output lists them by method and path) before raising it.
    private const int GreenfieldWhatIfBaseline = 89;
    private const int SteadyStateWhatIfBaseline = 184;

    private static readonly TimeSpan WireLatency = TimeSpan.FromMilliseconds(150);

    private static string Template()
    {
        string roles = string.Join(", ", WellKnownIds.GraphAppRoles.Take(4).Select(r => "\"" + r.Value + "\""));
        return """
            "variables": {
              "graphAppId": "[wellKnownApp('MicrosoftGraph')]",
              "graphSpId": "[reference(resourceId('Microsoft.Entra/servicePrincipals', variables('graphAppId')), '2026-10-01-preview').id]",
              "graphRoles": [ __ROLES__ ]
            },
            "resources": [
              {
                "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview",
                "copy": { "name": "apps", "count": 5 },
                "name": "[format('bench-app-{0}', copyIndex('apps'))]",
                "properties": { "displayName": "[format('Bench app {0}', copyIndex('apps'))]", "signInAudience": "AzureADMyOrg" }
              },
              {
                "type": "Microsoft.Entra/servicePrincipals", "apiVersion": "2026-10-01-preview",
                "copy": { "name": "sps", "count": 5 },
                "name": "[reference(format('bench-app-{0}', copyIndex('sps'))).appId]",
                "properties": { "appRoleAssignmentRequired": false },
                "dependsOn": [ "[resourceId('Microsoft.Entra/applications', format('bench-app-{0}', copyIndex('sps')))]" ]
              },
              {
                "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview",
                "copy": { "name": "groups", "count": 20 },
                "name": "[format('bench-group-{0}', copyIndex('groups'))]",
                "properties": {
                  "displayName": "[format('Bench group {0}', copyIndex('groups'))]", "mailEnabled": false,
                  "mailNickname": "[format('bench-group-{0}', copyIndex('groups'))]", "securityEnabled": true,
                  "members": [
                    "[reference(resourceId('Microsoft.Entra/users', format('member{0}@contoso.com', copyIndex('groups'))), '2026-10-01-preview').id]",
                    "[reference(resourceId('Microsoft.Entra/servicePrincipals', reference(format('bench-app-{0}', mod(copyIndex('groups'), 5))).appId)).id]"
                  ]
                },
                "dependsOn": [ "sps" ]
              },
              {
                "type": "Microsoft.Entra/appRoleAssignments", "apiVersion": "2026-10-01-preview",
                "copy": { "name": "grants", "count": 20 },
                "name": "[guid('bench-grant', string(copyIndex('grants')))]",
                "properties": {
                  "principalId": "[reference(resourceId('Microsoft.Entra/servicePrincipals', reference(format('bench-app-{0}', mod(copyIndex('grants'), 5))).appId)).id]",
                  "resourceId": "[variables('graphSpId')]",
                  "appRoleId": "[appRole(variables('graphAppId'), variables('graphRoles')[div(copyIndex('grants'), 5)])]"
                },
                "dependsOn": [ "sps" ]
              }
            ]
            """.Replace("__ROLES__", roles, StringComparison.Ordinal);
    }

    private static FakeDirectory Directory()
    {
        var directory = new FakeDirectory();
        directory.AddServicePrincipal(WellKnownIds.MicrosoftGraphAppId, "Microsoft Graph", sp => sp["appRoles"] = new JsonArray(
            [.. WellKnownIds.GraphAppRoles.Select(r => (JsonNode?)new JsonObject
            {
                ["id"] = r.Id,
                ["value"] = r.Value,
                ["isEnabled"] = true,
                ["allowedMemberTypes"] = new JsonArray("Application"),
            })]));
        for (int i = 0; i < 20; i++)
        {
            directory.AddUser("member" + i.ToString(CultureInfo.InvariantCulture) + "@contoso.com", "Member " + i.ToString(CultureInfo.InvariantCulture));
        }

        return directory;
    }

    [Fact]
    [Trait("Category", "Perf")]
    public async Task WhatIf_OfTwentyGroupsFiveAppsAndTwentyGraphGrants_StaysWithinTheRequestBaseline()
    {
        using var sandbox = new CliSandbox(nameof(WhatIf_OfTwentyGroupsFiveAppsAndTwentyGraphGrants_StaysWithinTheRequestBaseline));
        sandbox.Handler.Directory = Directory();
        string template = sandbox.WriteTemplate("bench.entra.json", Template());

        // 1. Greenfield: nothing of the template exists yet.
        var greenfield = await MeasureAsync(sandbox, template, "greenfield what-if");
        Assert.Equal(0, greenfield.ExitCode);
        Assert.Equal(50, greenfield.Json["summary"]!["create"]!.GetValue<int>());

        // 2. Deploy it (not measured: no latency, no pacing), then 3. the steady state: everything exists and nothing changes.
        var create = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "-o", "json");
        Assert.True(create.ExitCode == 0, create.StandardOutput + create.StandardError);
        var steady = await MeasureAsync(sandbox, template, "steady-state what-if");
        Assert.Equal(0, steady.ExitCode);
        Assert.Equal(0, steady.Json["summary"]!["create"]!.GetValue<int>());
        Assert.Equal(0, steady.Json["summary"]!["modify"]!.GetValue<int>());

        Assert.True(greenfield.Requests <= GreenfieldWhatIfBaseline, $"greenfield what-if sent {greenfield.Requests} Graph requests, above the baseline of {GreenfieldWhatIfBaseline}");
        Assert.True(steady.Requests <= SteadyStateWhatIfBaseline, $"steady-state what-if sent {steady.Requests} Graph requests, above the baseline of {SteadyStateWhatIfBaseline}");
    }

    private sealed record Measured(int ExitCode, JsonObject Json, int Requests);

    private static async Task<Measured> MeasureAsync(CliSandbox sandbox, string template, string label)
    {
        var latency = new LatencyHandler(sandbox.Handler, WireLatency);
        int before = sandbox.RecordedRequests().Count;
        CliResult result;
        var clock = Stopwatch.StartNew();
        using (ServiceOverrides.UseAmbient(new ServiceOverrides
        {
            Handler = latency,
            Tokens = sandbox.Tokens,
            Delay = TaskDelayProvider.Instance,
            RecordStore = sandbox.Overrides.RecordStore,
            TokenCacheDirectories = sandbox.Overrides.TokenCacheDirectories,
        }))
        {
            result = await CliRunner.RunAsync("deployment", "what-if", "-f", template, "--timings", "-o", "json");
        }

        clock.Stop();
        var requests = sandbox.RecordedRequests().Skip(before).ToList();
        var output = TestContext.Current.TestOutputHelper;
        output?.WriteLine($"== {label}: exit {result.ExitCode}, {requests.Count} Graph requests, at most {latency.MaxConcurrency} in flight on the wire, wall time {clock.ElapsedMilliseconds} ms");
        output?.WriteLine(string.Join(Environment.NewLine, result.StandardError.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').SkipWhile(l => l != "Timings")));
        foreach (var group in requests.GroupBy(r => r.Method + " " + Shape(r.Uri.AbsolutePath)).OrderByDescending(g => g.Count()).ThenBy(g => g.Key, StringComparer.Ordinal))
        {
            output?.WriteLine($"   {group.Count(),4}  {group.Key}");
        }

        Assert.True(result.ExitCode is 0, result.StandardOutput + result.StandardError);
        return new Measured(result.ExitCode, result.Json, requests.Count);
    }

    /// <summary>The path with ids and keys replaced by placeholders, so the request list groups by endpoint.</summary>
    private static string Shape(string path)
    {
        var segments = path.Split('/');
        for (int i = 0; i < segments.Length; i++)
        {
            if (Guid.TryParse(segments[i], out _))
            {
                segments[i] = "{id}";
            }
            else if (segments[i].Contains('(', StringComparison.Ordinal))
            {
                segments[i] = segments[i][..segments[i].IndexOf('(', StringComparison.Ordinal)] + "({key})";
            }
            else if (segments[i].Contains('@', StringComparison.Ordinal))
            {
                segments[i] = "{upn}";
            }
        }

        return string.Join('/', segments);
    }

    /// <summary>Adds a fixed latency to every request before it reaches the fake directory and tracks the most requests in flight at once.</summary>
    private sealed class LatencyHandler(HttpMessageHandler inner, TimeSpan latency) : DelegatingHandler(inner)
    {
        private int _inFlight;
        private int _max;

        public int MaxConcurrency => Volatile.Read(ref _max);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int now = Interlocked.Increment(ref _inFlight);
            int seen = Volatile.Read(ref _max);
            while (now > seen && Interlocked.CompareExchange(ref _max, now, seen) != seen)
            {
                seen = Volatile.Read(ref _max);
            }

            try
            {
                await Task.Delay(latency, cancellationToken).ConfigureAwait(false);
                return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref _inFlight);
            }
        }
    }
}
