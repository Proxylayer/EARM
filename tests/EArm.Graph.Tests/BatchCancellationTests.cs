using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// A <c>$batch</c> of several chunks cancelled by the caller between two chunks keeps the per-item results of the chunks already sent (their
/// sub-requests may have committed) and reports the chunks not sent as <c>BatchCancelled</c>, instead of throwing every result away.
/// </summary>
public sealed class BatchCancellationTests
{
    private static string BatchReply(int first, int count, int status = 204)
    {
        var responses = new JsonArray();
        for (var i = first; i < first + count; i++)
        {
            responses.Add(new JsonObject { ["id"] = i.ToString(CultureInfo.InvariantCulture), ["status"] = status });
        }

        return new JsonObject { ["responses"] = responses }.ToJsonString();
    }

    private static List<BatchRequest> Posts(int count)
    {
        return Enumerable.Range(1, count)
            .Select(i => new BatchRequest("POST", "/groups/g/members/$ref", new JsonObject { ["@odata.id"] = "https://graph.microsoft.com/v1.0/directoryObjects/" + i }, Id: "m" + i))
            .ToList();
    }

    [Fact]
    public async Task ACancellationBetweenChunks_KeepsTheResultsOfTheChunksAlreadySent()
    {
        using var cancel = new CancellationTokenSource();
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Json(BatchReply(1, 20))));
        rig.Scripted!.Fallback = _ =>
        {
            // The second envelope is already on the wire (a write is never abandoned): it completes; the third is never sent.
            cancel.Cancel();
            return ScriptedResponse.Json(BatchReply(21, 20));
        };

        var results = await rig.Client.BatchAsync(Posts(45), GraphApi.V1, RetryHint.Default, cancel.Token);

        Assert.Equal(45, results.Count);
        Assert.All(results.Take(40), static r => Assert.True(r.Succeeded));
        Assert.All(results.Skip(40), static r =>
        {
            Assert.False(r.Succeeded);
            Assert.Equal("BatchCancelled", r.Error!.Code);
            Assert.False(r.Error.IsAmbiguous);
            Assert.Equal((HttpStatusCode)499, r.Status);
        });
        Assert.Equal(2, rig.Scripted.Requests.Count);
        Assert.Equal("m41", results[40].Id);
    }

    [Fact]
    public async Task ACancellationBeforeTheFirstChunk_StillThrows()
    {
        using var cancel = new CancellationTokenSource();
        await cancel.CancelAsync();
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Json(BatchReply(1, 20))));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => rig.Client.BatchAsync(Posts(25), GraphApi.V1, RetryHint.Default, cancel.Token));
        Assert.Empty(rig.Scripted!.Requests);
    }

    [Fact]
    public async Task ACancellationBeforeAnIndividualReSend_KeepsEveryResultAlreadyKnown()
    {
        using var cancel = new CancellationTokenSource();
        var reply = new JsonObject
        {
            ["responses"] = new JsonArray(
                new JsonObject { ["id"] = "1", ["status"] = 204 },
                new JsonObject { ["id"] = "2", ["status"] = 429, ["headers"] = new JsonObject { ["Retry-After"] = "1" }, ["body"] = new JsonObject { ["error"] = new JsonObject { ["code"] = "TooManyRequests", ["message"] = "slow down" } } }),
        }.ToJsonString();
        using var rig = new Review1BRig(new ScriptedHttpHandler());
        rig.Scripted!.Enqueue(_ =>
        {
            // The envelope completes; the caller cancels before the throttled item is re-sent on its own.
            cancel.Cancel();
            return ScriptedResponse.Json(reply);
        });

        var results = await rig.Client.BatchAsync(Posts(2), GraphApi.V1, RetryHint.Default, cancel.Token);

        Assert.True(results[0].Succeeded);
        Assert.Equal(HttpStatusCode.TooManyRequests, results[1].Error!.Status);
        Assert.Single(rig.Scripted.Requests);
    }
}
