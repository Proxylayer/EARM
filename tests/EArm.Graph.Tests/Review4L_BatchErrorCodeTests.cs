using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph.Tests.Infrastructure;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Review 4L: the two error codes a failed <c>$batch</c> envelope puts on its items are string literals, not constants, so no
/// docs-versus-constants test sees them. <c>BatchEnvelopeFailed</c> is the code of an item whose own envelope failed at the transport level (no Graph
/// error code to copy); <c>BatchNotSent</c> is the code of the items of later chunks that were never sent. Scripts and CI logs match on both.
/// </summary>
public sealed class Review4L_BatchErrorCodeTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string BatchReply(int count)
    {
        var responses = new JsonArray();
        for (var i = 1; i <= count; i++)
        {
            responses.Add(new JsonObject { ["id"] = i.ToString(System.Globalization.CultureInfo.InvariantCulture), ["status"] = 204 });
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
    public async Task ATransportFailureOfALaterEnvelope_IsCodedBatchEnvelopeFailed_AndTheChunksAfterItBatchNotSent()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(ScriptedResponse.Json(BatchReply(20))));
        rig.Scripted!.Fallback = _ => new ScriptedResponse { Throw = new HttpRequestException(HttpRequestError.ConnectionError, "refused") };

        var results = await rig.Client.BatchAsync(Posts(45), GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal(45, results.Count);
        Assert.All(results.Take(20), static r => Assert.True(r.Succeeded));

        // The 20 items of the envelope that failed: the envelope's own failure, not a copy of a Graph error code.
        Assert.All(results.Skip(20).Take(20), static r =>
        {
            Assert.False(r.Succeeded);
            Assert.Equal("BatchEnvelopeFailed", r.Error!.Code);
            Assert.Contains("$batch envelope failed", r.Error.Message, StringComparison.Ordinal);
        });

        // The 5 items after it were never sent: a different code, and never ambiguous (nothing was written for them).
        Assert.All(results.Skip(40), static r =>
        {
            Assert.False(r.Succeeded);
            Assert.Equal("BatchNotSent", r.Error!.Code);
            Assert.False(r.Error.IsAmbiguous);
            Assert.Contains("Not sent", r.Error.Message, StringComparison.Ordinal);
            Assert.Equal(HttpStatusCode.BadGateway, r.Status);
        });
    }

    [Fact]
    public async Task AGraphErrorOfALaterEnvelope_KeepsItsOwnCode_NotBatchEnvelopeFailed()
    {
        using var rig = new Review1BRig(new ScriptedHttpHandler(
            ScriptedResponse.Json(BatchReply(20)),
            ScriptedResponse.Error(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "no")));

        var results = await rig.Client.BatchAsync(Posts(21), GraphApi.V1, RetryHint.Default, Ct);

        Assert.Equal("Authorization_RequestDenied", results[20].Error!.Code);
        Assert.DoesNotContain(results, static r => r.Error?.Code == "BatchEnvelopeFailed");
    }
}
