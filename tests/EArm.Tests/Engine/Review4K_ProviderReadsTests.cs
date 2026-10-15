using System;
using System.Collections.Generic;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using EArm.Graph;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// <see cref="ProviderReads"/>: the check that a state-pinned Graph id still names the object the key says it should, and the classification of a write
/// failure whose outcome is unknown. Each case here pins one decision (an empty recorded key, a secret leaf, trimming, case, the 5xx boundary, every
/// transport failure kind) that a refactor must not flip silently.
/// </summary>
public sealed class ProviderReadsTests
{
    private static ResourceKey Key(params (string Name, string Value)[] values)
    {
        var dictionary = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in values)
        {
            dictionary[name] = value;
        }

        return new ResourceKey { Type = "Microsoft.Entra/groups", Values = dictionary };
    }

    private static ExistingResource Live(string id, JsonObject raw)
    {
        raw["id"] = id;
        return new ExistingResource(id, raw, raw);
    }

    private static JsonObject Obj(string json)
    {
        return (JsonObject)JsonNode.Parse(json)!;
    }

    // ---- ReadVerifiedAsync ----

    [Fact]
    public async Task WithoutAPinnedId_TheReadResultIsUsedAsIs_EvenWhenItsKeyDiffers()
    {
        var other = Live("A1", Obj("{ \"uniqueName\": \"somebody-else\" }"));
        var calls = new List<string?>();

        var verified = await ProviderReads.ReadVerifiedAsync(Key(("uniqueName", "wanted")), null, id =>
        {
            calls.Add(id);
            return Task.FromResult<ExistingResource?>(other);
        });

        Assert.Same(other, verified.Existing);
        Assert.Null(verified.Mismatch);
        Assert.False(verified.Superseded);
        Assert.Single(calls);
    }

    [Fact]
    public async Task APinnedObjectThatIsTheKeysObject_IsUsed_WithoutASecondRead()
    {
        var match = Live("A1", Obj("{ \"uniqueName\": \"wanted\" }"));
        int reads = 0;

        var verified = await ProviderReads.ReadVerifiedAsync(Key(("uniqueName", "wanted")), "A1", _ =>
        {
            reads++;
            return Task.FromResult<ExistingResource?>(match);
        });

        Assert.Same(match, verified.Existing);
        Assert.Null(verified.Mismatch);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ASupersededPinnedObject_IsKeptAsTheOneToReplace_WhenNoObjectHasTheNewKey()
    {
        var pinned = Live("A1", Obj("{ \"principalId\": \"old\" }"));

        var verified = await ProviderReads.ReadVerifiedAsync(
            Key(("principalId", "new")),
            "A1",
            id => Task.FromResult<ExistingResource?>(id is null ? null : pinned),
            acceptSuperseded: _ => true);

        Assert.True(verified.Superseded);
        Assert.Same(pinned, verified.Existing);
        Assert.Null(verified.Mismatch);
        Assert.False(verified.Renamed);
    }

    [Fact]
    public async Task ASupersededPinnedObject_YieldsToAnObjectThatHasTheNewKey()
    {
        var pinned = Live("A1", Obj("{ \"principalId\": \"old\" }"));
        var current = Live("A2", Obj("{ \"principalId\": \"new\" }"));

        var verified = await ProviderReads.ReadVerifiedAsync(
            Key(("principalId", "new")),
            "A1",
            id => Task.FromResult<ExistingResource?>(id is null ? current : pinned),
            acceptSuperseded: _ => true);

        Assert.False(verified.Superseded);
        Assert.Same(current, verified.Existing);
        Assert.Contains("the object the key names was used instead", verified.Mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASupersededPredicate_ThatDeclines_FallsBackToTheKeyLookup()
    {
        var pinned = Live("A1", Obj("{ \"principalId\": \"old\" }"));

        var verified = await ProviderReads.ReadVerifiedAsync(
            Key(("principalId", "new")),
            "A1",
            id => Task.FromResult<ExistingResource?>(id is null ? null : pinned),
            acceptSuperseded: _ => false);

        Assert.False(verified.Superseded);
        Assert.Null(verified.Existing);
        Assert.Contains("the pinned id was ignored", verified.Mismatch, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARenamedNameKeyedObject_IsReportedRenamed_NotCreated()
    {
        var pinned = Live("A1", Obj("{ \"displayName\": \"Old name\" }"));

        var verified = await ProviderReads.ReadVerifiedAsync(Key(("displayName", "New name")), "A1", id => Task.FromResult<ExistingResource?>(id is null ? null : pinned), keyCanBeRenamed: true);

        Assert.True(verified.Renamed);
        Assert.Null(verified.Existing);
    }

    [Fact]
    public async Task ARecordedFingerprint_ThatTheLiveObjectDoesNotCarry_RejectsThePinnedObject()
    {
        var pinned = Live("A1", Obj("{ \"uniqueName\": \"wanted\", \"createdDateTime\": \"2026-01-01T00:00:00Z\" }"));

        var verified = await ProviderReads.ReadVerifiedAsync(
            Key(("uniqueName", "wanted")),
            "A1",
            id => Task.FromResult<ExistingResource?>(id is null ? null : pinned),
            recordedCreated: new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        Assert.Null(verified.Existing);
        Assert.Contains("was created", verified.Mismatch, StringComparison.Ordinal);
    }

    // ---- RecordedKeyMatches ----

    [Fact]
    public void AnEmptyRecordedKey_VerifiesNothing_AndMatchesNothing()
    {
        Assert.False(ProviderReads.RecordedKeyMatches(new Dictionary<string, string>(), Obj("{ \"uniqueName\": \"x\" }")));
    }

    [Fact]
    public void ARecordedKeyValue_ThatIsASecretLeaf_IsNeverTreatedAsVerified()
    {
        var live = new JsonObject { ["uniqueName"] = SecretJson.CreateString("wanted") };

        Assert.False(ProviderReads.RecordedKeyMatches(new Dictionary<string, string> { ["uniqueName"] = "wanted" }, live));
    }

    [Fact]
    public void ARecordedKeyThePropertyOfWhichTheLiveObjectDoesNotExpose_DoesNotMatch()
    {
        Assert.False(ProviderReads.RecordedKeyMatches(new Dictionary<string, string> { ["uniqueName"] = "wanted" }, Obj("{ \"displayName\": \"wanted\" }")));
    }

    [Theory]
    [InlineData("  wanted ", "wanted")]
    [InlineData("wanted", "  wanted\t")]
    [InlineData("WANTED", "wanted")]
    [InlineData("wanted", "WaNtEd")]
    public void RecordedKeyValues_AreComparedTrimmedAndCaseInsensitively(string live, string recorded)
    {
        var raw = new JsonObject { ["uniqueName"] = live };

        Assert.True(ProviderReads.RecordedKeyMatches(new Dictionary<string, string> { ["uniqueName"] = recorded }, raw));
        Assert.True(ProviderReads.KeyMatches(Key(("uniqueName", recorded)), raw, out string? reason));
        Assert.Null(reason);
    }

    [Fact]
    public void ARecordedKeyThatDiffersInTheValue_DoesNotMatch()
    {
        Assert.False(ProviderReads.RecordedKeyMatches(new Dictionary<string, string> { ["uniqueName"] = "wanted" }, Obj("{ \"uniqueName\": \"wanted2\" }")));
    }

    // ---- KeyMatches ----

    [Fact]
    public void AKeyThatNoLivePropertyExposes_CannotBeVerified_AndTheReasonNamesTheKey()
    {
        bool matches = ProviderReads.KeyMatches(Key(("uniqueName", "wanted")), Obj("{ \"displayName\": \"anything\" }"), out string? reason);

        Assert.False(matches);
        Assert.Contains("does not expose the key (uniqueName)", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeyOfSeveralProperties_IsVerifiedByTheOnesTheLiveObjectExposes()
    {
        var key = Key(("principalId", "p"), ("resourceId", "r"), ("appRoleId", "a"));

        Assert.True(ProviderReads.KeyMatches(key, Obj("{ \"principalId\": \"P\", \"resourceId\": \"r\" }"), out _));
        Assert.False(ProviderReads.KeyMatches(key, Obj("{ \"principalId\": \"p\", \"resourceId\": \"other\" }"), out string? reason));
        Assert.Contains("resourceId 'other' instead of 'r'", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEmptyKey_HasNothingToVerify_AndMatches()
    {
        Assert.True(ProviderReads.KeyMatches(Key(), Obj("{ \"x\": 1 }"), out string? reason));
        Assert.Null(reason);
    }

    [Fact]
    public void ASecretLeafOfTheLiveObject_IsSkipped_AndALoneSecretKeyCannotBeVerified()
    {
        var onlySecret = new JsonObject { ["uniqueName"] = SecretJson.CreateString("wanted") };
        Assert.False(ProviderReads.KeyMatches(Key(("uniqueName", "wanted")), onlySecret, out string? reason));
        Assert.Contains("does not expose", reason, StringComparison.Ordinal);

        // With another verifiable property the secret one is simply skipped, whatever it holds.
        var mixed = new JsonObject { ["uniqueName"] = SecretJson.CreateString("different"), ["displayName"] = "d" };
        Assert.True(ProviderReads.KeyMatches(Key(("uniqueName", "wanted"), ("displayName", "d")), mixed, out _));
    }

    [Fact]
    public void ANonStringLiveValue_IsComparedThroughItsJsonText()
    {
        Assert.True(ProviderReads.KeyMatches(Key(("version", "3")), Obj("{ \"version\": 3 }"), out _));
        Assert.False(ProviderReads.KeyMatches(Key(("version", "4")), Obj("{ \"version\": 3 }"), out _));
    }

    // ---- IsAmbiguousWriteFailure ----

    [Theory]
    [InlineData(HttpStatusCode.InternalServerError, true)]
    [InlineData(HttpStatusCode.BadGateway, true)]
    [InlineData(HttpStatusCode.ServiceUnavailable, true)]
    [InlineData((HttpStatusCode)499, false)]
    [InlineData(HttpStatusCode.BadRequest, false)]
    [InlineData(HttpStatusCode.TooManyRequests, false)]
    [InlineData(HttpStatusCode.NotFound, false)]
    public void AGraphError_IsAmbiguous_FromStatus500Up(HttpStatusCode status, bool expected)
    {
        Assert.Equal(expected, ProviderReads.IsAmbiguousWriteFailure(new GraphException(status, "code", "message")));
    }

    [Fact]
    public void AGraphError_FlaggedAmbiguousByTheClient_IsAmbiguous_WhateverItsStatus()
    {
        var flagged = new GraphException(HttpStatusCode.BadRequest, "code", "message") { IsAmbiguous = true };

        Assert.True(ProviderReads.IsAmbiguousWriteFailure(flagged));
        Assert.False(ProviderReads.IsAmbiguousWriteFailure(new GraphException(HttpStatusCode.BadRequest, "code", "message")));
    }

    [Fact]
    public void ATimeoutException_IsAmbiguous()
    {
        Assert.True(ProviderReads.IsAmbiguousWriteFailure(new TimeoutException()));
        Assert.False(ProviderReads.IsAmbiguousWriteFailure(new InvalidOperationException()));
        Assert.False(ProviderReads.IsAmbiguousWriteFailure(new OperationCanceledException()));
    }

    [Theory]
    [InlineData(GraphTransportFailure.Network, true)]
    [InlineData(GraphTransportFailure.Timeout, true)]
    [InlineData(GraphTransportFailure.InvalidResponse, true)]
    [InlineData(GraphTransportFailure.ResponseTooLarge, true)]
    [InlineData(GraphTransportFailure.NotAuthenticated, false)]
    [InlineData(GraphTransportFailure.UnsafeUrl, false)]
    [InlineData(GraphTransportFailure.RequestTooLarge, false)]
    [InlineData(GraphTransportFailure.PagingLimitExceeded, false)]
    public void ATransportFailure_IsAmbiguous_ByItsKind(GraphTransportFailure failure, bool expected)
    {
        Assert.Equal(expected, ProviderReads.IsAmbiguousWriteFailure(new GraphTransportException(failure, "failed")));
    }

    [Fact]
    public void ATransportFailure_FlaggedAmbiguous_IsAmbiguous_WhateverItsKind()
    {
        Assert.True(ProviderReads.IsAmbiguousWriteFailure(new GraphTransportException(GraphTransportFailure.NotAuthenticated, "failed", ambiguous: true)));
    }

    [Fact]
    public void EveryTransportFailureKind_IsCoveredByTheTable()
    {
        // A new kind must be classified on purpose: this fails when GraphTransportFailure grows.
        Assert.Equal(8, Enum.GetValues<GraphTransportFailure>().Length);
    }
}
