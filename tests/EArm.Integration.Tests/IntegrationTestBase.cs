using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>Shared plumbing of the real-tenant test classes: the fixture, the cancellation token and the assertions with useful messages.</summary>
public abstract class IntegrationTestBase
{
    protected IntegrationTestBase(IntegrationFixture fixture)
    {
        Fixture = fixture ?? throw new ArgumentNullException(nameof(fixture));
    }

    internal IntegrationFixture Fixture { get; }

    internal IGraphClient Graph => Fixture.Graph;

    protected static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Writes a diagnostic line that the test runner shows next to the test.</summary>
    protected static void Observe(string message)
    {
        TestContext.Current.SendDiagnosticMessage(message);
    }

    /// <summary>A password that satisfies the tenant complexity rules (upper, lower, digit, symbol, 36 characters).</summary>
    protected static string NewPassword()
    {
        return "Aa1!" + Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
    }

    /// <summary>Re-reads until <paramref name="done"/> accepts the value or the time is up (Graph is eventually consistent); returns the last value read.</summary>
    protected static async Task<T> EventuallyAsync<T>(Func<Task<T>> read, Func<T, bool> done, int seconds = 45)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        T value = await read();
        while (!done(value) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), Ct);
            value = await read();
        }

        return value;
    }

    // ---- CLI assertions ----

    internal static void AssertExit(int expected, CliResult result, string step)
    {
        if (result.ExitCode == expected)
        {
            return;
        }

        // The failing operations first: the output tail alone cuts off the head of the document, where they are.
        string? failures = result.DescribeFailures();
        string detail = failures is null ? string.Empty : $"Failed operations:{Environment.NewLine}{failures}{Environment.NewLine}";
        Assert.Fail($"{step}: expected exit code {expected} but got {result.ExitCode}.{Environment.NewLine}{detail}{Tail(result.Combined)}");
    }

    /// <summary>Exit 0, status Succeeded and no failed or skipped operation.</summary>
    internal static void AssertCreated(CliResult result, string step)
    {
        AssertExit(0, result, step);
        Assert.True(
            string.Equals((string?)result.Json["status"], "Succeeded", StringComparison.Ordinal) && result.Operations.All(o => (string?)o["status"] is "Succeeded" or "Ignored" or "Detached"),
            $"{step}: the deployment did not succeed.{Environment.NewLine}Failed operations:{Environment.NewLine}{result.DescribeFailures() ?? "(none reported)"}{Environment.NewLine}All operations: {result.DescribeOperations()}{Environment.NewLine}{Tail(result.Combined)}");
    }

    /// <summary>Exit 0 (no changes) and every row NoChange or Ignore, with no edge to add or remove.</summary>
    internal static void AssertNoChanges(CliResult whatIf, string step)
    {
        AssertExit(0, whatIf, step);
        Assert.True(
            whatIf.Changes.All(c => (string?)c["changeType"] is "NoChange" or "Ignore"),
            $"{step}: expected only NoChange rows but got: {whatIf.DescribeChanges()}{Environment.NewLine}{Tail(DescribeDeltas(whatIf))}");
        Assert.Equal(0, EdgeCount(whatIf, "add"));
        Assert.Equal(0, EdgeCount(whatIf, "remove"));
    }

    /// <summary>Exit 5 (changes found) and the named row has the expected change type.</summary>
    internal static void AssertChange(CliResult whatIf, string resourceIdSuffix, string expectedChangeType, string step)
    {
        AssertExit(5, whatIf, step);
        string? actual = whatIf.ChangeTypeOf(resourceIdSuffix);
        Assert.True(
            string.Equals(actual, expectedChangeType, StringComparison.Ordinal),
            $"{step}: expected {expectedChangeType} for '{resourceIdSuffix}' but the rows are: {whatIf.DescribeChanges()}");
    }

    /// <summary><c>summary.edges.add</c> / <c>.remove</c> of a what-if document.</summary>
    internal static int EdgeCount(CliResult whatIf, string kind)
    {
        return (int?)whatIf.Json["summary"]?["edges"]?[kind] ?? 0;
    }

    internal static JsonObject SecureOutputs(string file)
    {
        Assert.True(File.Exists(file), $"The secure outputs file '{file}' was not written.");
        return JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? throw new InvalidOperationException("The secure outputs file is not a JSON object.");
    }

    private static string DescribeDeltas(CliResult whatIf)
    {
        var lines = new List<string>();
        foreach (var change in whatIf.Changes.Where(c => (string?)c["changeType"] is not ("NoChange" or "Ignore")))
        {
            lines.Add((string?)change["resourceId"] + ": " + (change["delta"]?.ToJsonString() ?? "(no delta)"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    internal static string Tail(string text)
    {
        const int Limit = 3500;
        return text.Length <= Limit ? text : "..." + text[^Limit..];
    }

    // ---- Graph lookups ----

    /// <summary>The single object of a collection whose <paramref name="property"/> equals the value (null when none; fails when several).</summary>
    internal async Task<JsonObject?> FindOneAsync(string collectionPath, string property, string value, params string[] select)
    {
        var request = new GraphRequest(collectionPath).WithFilter($"{property} eq '{value.Replace("'", "''", StringComparison.Ordinal)}'");
        if (select.Length > 0)
        {
            request = request.WithSelect(select);
        }

        var matches = await Graph.ListAsync(request, Ct);
        Assert.True(matches.Count <= 1, $"{collectionPath}: {matches.Count} objects have {property} '{value}'; expected at most one.");
        return matches.FirstOrDefault();
    }

    /// <summary>
    /// <see cref="FindOneAsync"/> re-read until <paramref name="done"/> accepts the result: the name-keyed list endpoints
    /// can omit a just-created object (or still return a just-deleted or stale one) for a few seconds.
    /// </summary>
    internal Task<JsonObject?> FindOneEventuallyAsync(string collectionPath, string property, string value, Func<JsonObject?, bool> done, params string[] select)
    {
        return EventuallyAsync(() => FindOneAsync(collectionPath, property, value, select), done);
    }

    /// <summary>Ids of the directory objects in a navigation collection (<c>/groups/{id}/members</c>).</summary>
    internal async Task<HashSet<string>> IdsOfAsync(string collectionPath)
    {
        var items = await Graph.ListAsync(new GraphRequest(collectionPath).WithSelect("id"), Ct);
        return new HashSet<string>(items.Select(i => i.RequireId()), StringComparer.OrdinalIgnoreCase);
    }

    internal async Task<JsonObject?> ApplicationByNameAsync(string uniqueName, params string[] select)
    {
        var request = new GraphRequest(GraphPath.AlternateKey("/applications", "uniqueName", uniqueName));
        if (select.Length > 0)
        {
            request = request.WithSelect(select);
        }

        return await Graph.GetObjectAsync(request, Ct);
    }

    internal Task<JsonObject?> GroupByNameAsync(string displayName, params string[] select)
    {
        return FindOneAsync("/groups", "displayName", displayName, select);
    }

    internal async Task<JsonObject?> UserAsync(string userPrincipalName, params string[] select)
    {
        var request = new GraphRequest("/users/" + GraphPath.Segment(userPrincipalName));
        if (select.Length > 0)
        {
            request = request.WithSelect(select);
        }

        return await Graph.GetObjectAsync(request, Ct);
    }
}
