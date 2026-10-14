using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Az.Tests.Infrastructure;
using Xunit;

namespace EntraArm.Az.Tests;

/// <summary>README items 3 and 8 plus status inference: <see cref="AzGraphClient"/> over the real pwsh host and the fake az.</summary>
public sealed class AzGraphClientTests
{
    private const string V1 = "https://graph.microsoft.com/v1.0";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task GetAsync_ReturnsNullOnNotFound()
    {
        using var context = new FakeAzContext(nameof(GetAsync_ReturnsNullOnNotFound));
        context.UseScenario(new FakeScenario().Add(FakeResponse.GraphError("notFound")));
        using var runner = context.CreateRunner();
        var delays = new RecordingDelayProvider();
        var client = context.CreateGraphClient(runner, delays);

        var body = await client.GetAsync(new GraphRequest("/users/11111111-2222-3333-4444-555555555555"), Ct);

        Assert.Null(body);
        Assert.Empty(delays.Delays);
        Assert.Single(context.ReadRecords());
    }

    [Fact]
    public async Task GetAsync_NonAsciiBodyRoundTrips()
    {
        using var context = new FakeAzContext(nameof(GetAsync_NonAsciiBodyRoundTrips));
        context.UseFixtureFile("non-ascii-user.json");
        using var runner = context.CreateRunner();
        var client = context.CreateGraphClient(runner, new RecordingDelayProvider());

        var body = await client.GetAsync(new GraphRequest("/users/11111111-2222-3333-4444-555555555555").WithSelect("id", "displayName", "mail"), Ct);

        var user = Assert.IsType<JsonObject>(body);
        Assert.Equal("Ünïcødé 日本語 🎉", user["displayName"]?.GetValue<string>());
        Assert.Equal("ünï@contoso.example", user["mail"]?.GetValue<string>());
        var record = Assert.Single(context.ReadRecords());
        Assert.Equal(["$select=id,displayName,mail"], record.Section("--url-parameters"));
    }

    [Fact]
    public async Task PostAsync_BodyWithIdIsCreatedAndContentTypeIsAdded()
    {
        using var context = new FakeAzContext(nameof(PostAsync_BodyWithIdIsCreatedAndContentTypeIsAdded));
        context.UseScenario(new FakeScenario().Add(FakeResponse.Json("{\"id\":\"app-1\",\"displayName\":\"Created\",\"appId\":\"a1\"}")));
        using var runner = context.CreateRunner();
        var client = context.CreateGraphClient(runner, new RecordingDelayProvider());
        var body = new JsonObject { ["displayName"] = "Created" };

        var response = await client.PostAsync(new GraphRequest("/applications"), body, Ct);

        Assert.Equal(HttpStatusCode.Created, response.Status);
        Assert.True(response.IsCreated);
        Assert.Equal("app-1", response.Body?["id"]?.GetValue<string>());
        var record = Assert.Single(context.ReadRecords());
        Assert.Equal("POST", record.ValueAfter("--method"));
        Assert.Equal(V1 + "/applications", record.ValueAfter("--url"));
        Assert.Contains("Content-Type=application/json", record.Section("--headers"));
        Assert.True(JsonNode.DeepEquals(body, record.BodyJson));
    }

    [Fact]
    public async Task PatchAsync_CreateIfMissingWithIdIsCreated()
    {
        using var context = new FakeAzContext(nameof(PatchAsync_CreateIfMissingWithIdIsCreated));
        context.UseScenario(new FakeScenario().Add(FakeResponse.Json("{\"id\":\"app-2\",\"uniqueName\":\"app-2\"}")));
        using var runner = context.CreateRunner();
        var client = context.CreateGraphClient(runner, new RecordingDelayProvider());
        var request = new GraphRequest(GraphPath.AlternateKey("applications", "uniqueName", "app-2")).WithPrefer("create-if-missing");

        var response = await client.PatchAsync(request, new JsonObject { ["displayName"] = "App 2" }, Ct);

        Assert.Equal(HttpStatusCode.Created, response.Status);
        var record = Assert.Single(context.ReadRecords());
        Assert.Equal("PATCH", record.ValueAfter("--method"));
        Assert.Equal(V1 + "/applications(uniqueName='app-2')", record.ValueAfter("--url"));
        Assert.Contains("Prefer=create-if-missing", record.Section("--headers"));
    }

    [Fact]
    public async Task DeleteAsync_EmptyOutputIsNoContent()
    {
        using var context = new FakeAzContext(nameof(DeleteAsync_EmptyOutputIsNoContent));
        context.UseScenario(new FakeScenario().Add(FakeResponse.Empty()));
        using var runner = context.CreateRunner();
        var client = context.CreateGraphClient(runner, new RecordingDelayProvider());

        var response = await client.SendAsync("DELETE", new GraphRequest("/groups/g1/members/u1/$ref"), null, Ct);

        Assert.Equal(HttpStatusCode.NoContent, response.Status);
        Assert.True(response.IsNoContent);
        Assert.Null(response.Body);
        var record = Assert.Single(context.ReadRecords());
        Assert.Equal("DELETE", record.ValueAfter("--method"));
        Assert.Null(record.BodyFile);
        Assert.DoesNotContain("--headers", record.Argv);
    }

    [Fact]
    public async Task PostAsync_ForbiddenSurfacesGraphExceptionWithRequestId()
    {
        using var context = new FakeAzContext(nameof(PostAsync_ForbiddenSurfacesGraphExceptionWithRequestId));
        context.UseScenario(new FakeScenario().Add(FakeResponse.GraphError("forbidden")));
        using var runner = context.CreateRunner();
        var delays = new RecordingDelayProvider();
        var client = context.CreateGraphClient(runner, delays);

        var error = await Assert.ThrowsAsync<GraphException>(() => client.PostAsync(new GraphRequest("/applications"), new JsonObject { ["displayName"] = "x" }, Ct));

        Assert.Equal(HttpStatusCode.Forbidden, error.Status);
        Assert.True(error.IsForbidden);
        Assert.Equal("Authorization_RequestDenied", error.Code);
        Assert.Equal("req-403-0001", error.RequestId);
        Assert.Contains("Insufficient privileges", error.GraphMessage);
        Assert.Empty(delays.Delays);
        Assert.Empty(context.LeftoverProtocolFiles());
    }

    [Fact]
    public async Task GetAllPagesAsync_FollowsNextLinksResendingHeadersAndSplittingTheQuery()
    {
        using var context = new FakeAzContext(nameof(GetAllPagesAsync_FollowsNextLinksResendingHeadersAndSplittingTheQuery));
        context.UseFixtureFile("pagination-three-pages.json");
        using var runner = context.CreateRunner();
        var client = context.CreateGraphClient(runner, new RecordingDelayProvider());
        var request = new GraphRequest("/users")
            .WithSelect("id", "displayName")
            .WithTop(2)
            .WithHeader("Prefer", "odata.maxpagesize=2")
            .WithConsistencyLevelEventual();

        var items = new List<JsonNode>();
        await foreach (var item in client.GetAllPagesAsync(request, Ct))
        {
            items.Add(item);
        }

        Assert.Equal(["u1", "u2", "u3", "u4", "u5"], items.Select(static i => i["id"]?.GetValue<string>()));
        Assert.Equal("Ünï 1", items[0]["displayName"]?.GetValue<string>());

        var records = context.ReadRecords();
        Assert.Equal(3, records.Count);
        Assert.All(records, r => Assert.Equal(V1 + "/users", r.ValueAfter("--url")));
        Assert.All(records, r => Assert.Equal("GET", r.ValueAfter("--method")));
        Assert.All(records, r => Assert.DoesNotContain('?', r.ValueAfter("--url")!));

        Assert.Equal(["$select=id,displayName", "$top=2"], records[0].Section("--url-parameters"));

        var second = records[1].Section("--url-parameters");
        Assert.Contains("$skiptoken=X'abc' RFL+page2", second);
        Assert.Contains("$select=id,displayName", second);
        Assert.Contains("$top=2", second);
        Assert.Contains("$skiptoken=page3", records[2].Section("--url-parameters"));

        foreach (var record in records)
        {
            var headers = record.Section("--headers");
            Assert.Contains("Prefer=odata.maxpagesize=2", headers);
            Assert.Contains("ConsistencyLevel=eventual", headers);
        }
    }

    [Fact]
    public async Task GetAllPagesAsync_SingleEntityYieldsItself()
    {
        using var context = new FakeAzContext(nameof(GetAllPagesAsync_SingleEntityYieldsItself));
        context.UseScenario(new FakeScenario().Add(FakeResponse.Json("{\"id\":\"only-one\"}")));
        using var runner = context.CreateRunner();
        var client = context.CreateGraphClient(runner, new RecordingDelayProvider());

        var items = new List<JsonNode>();
        await foreach (var item in client.GetAllPagesAsync(new GraphRequest("/users/only-one"), Ct))
        {
            items.Add(item);
        }

        var single = Assert.Single(items);
        Assert.Equal("only-one", single["id"]?.GetValue<string>());
        Assert.Single(context.ReadRecords());
    }

    [Theory]
    [InlineData("POST", null, "{\"id\":\"1\"}", HttpStatusCode.Created)]
    [InlineData("POST", null, "{\"value\":[]}", HttpStatusCode.OK)]
    [InlineData("PATCH", "create-if-missing", "{\"id\":\"1\"}", HttpStatusCode.Created)]
    [InlineData("PATCH", "return=representation, create-if-missing", "{\"id\":\"1\"}", HttpStatusCode.Created)]
    [InlineData("PATCH", "return=representation", "{\"id\":\"1\"}", HttpStatusCode.OK)]
    [InlineData("PATCH", null, "{\"id\":\"1\"}", HttpStatusCode.OK)]
    [InlineData("PATCH", "create-if-missing", "{\"displayName\":\"no id\"}", HttpStatusCode.OK)]
    [InlineData("GET", null, "{\"id\":\"1\"}", HttpStatusCode.OK)]
    [InlineData("PUT", null, "{\"id\":\"1\"}", HttpStatusCode.OK)]
    [InlineData("DELETE", null, null, HttpStatusCode.NoContent)]
    [InlineData("POST", null, null, HttpStatusCode.NoContent)]
    public void InferStatus_FollowsTheInferenceRules(string method, string? prefer, string? json, HttpStatusCode expected)
    {
        var request = prefer is null ? new GraphRequest("/x") : new GraphRequest("/x").WithPrefer(prefer);
        var body = json is null ? null : JsonNode.Parse(json);

        Assert.Equal(expected, GraphResultMapper.InferStatus(method, request, body));

        var result = new AzResult(0, json ?? string.Empty, string.Empty, TimeSpan.FromMilliseconds(1), false);
        var response = GraphResultMapper.ToGraphResponse(result, method, request);
        Assert.Equal(expected, response.Status);
        Assert.Equal(body is null, response.Body is null);
    }
}
