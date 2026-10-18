using System;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The live-side secret masking helpers of <see cref="JsonDiff"/>: <c>RenderLive</c>, <c>RenderLiveObject</c> and <c>MaskSecurePaths</c>.</summary>
public sealed class JsonDiffSecretRenderingTests
{
    private const string Plain = "live-plain-text";

    private static JsonObject Obj(string json)
    {
        return (JsonObject)JsonNode.Parse(json)!;
    }

    private static JsonNode Secret(string value = "s3cret")
    {
        return SecretJson.CreateString(value);
    }

    [Fact]
    public void RenderLive_ASecretDesiredLeaf_MasksTheLiveLeaf()
    {
        var rendered = JsonDiff.RenderLive(JsonValue.Create(Plain), Secret());

        Assert.Equal("\"***\"", rendered!.ToJsonString());
    }

    [Fact]
    public void RenderLive_WithoutASecret_RendersTheLiveValueAsIs()
    {
        var live = Obj("""{ "a": "x", "b": [1, 2], "c": { "d": null } }""");

        Assert.Equal(live.ToJsonString(), JsonDiff.RenderLive(live, Obj("""{ "a": "other" }"""))!.ToJsonString());
        Assert.Equal(live.ToJsonString(), JsonDiff.RenderLive(live, null)!.ToJsonString());
        Assert.Null(JsonDiff.RenderLive(null, Secret()));
    }

    [Fact]
    public void RenderLive_ASecretBearingArray_MasksEveryLiveLeaf()
    {
        var desired = new JsonArray(JsonValue.Create("a"), Secret());
        var live = new JsonArray(JsonValue.Create("a"), JsonValue.Create("b"), JsonValue.Create("c"));

        Assert.Equal("""["***","***","***"]""", JsonDiff.RenderLive(live, desired)!.ToJsonString());

        var nested = Obj("""{ "items": [ { "id": 1 }, { "id": 2 } ] }""");
        var desiredNested = new JsonObject { ["items"] = new JsonArray(new JsonObject { ["id"] = Secret("x") }) };
        Assert.Equal("""{"items":[{"id":"***"},{"id":"***"}]}""", JsonDiff.RenderLive(nested, desiredNested)!.ToJsonString());
    }

    [Fact]
    public void RenderLive_ATypeMismatch_MasksTheWholeLiveSide()
    {
        var desiredObject = new JsonObject { ["k"] = Secret() };

        Assert.Equal("\"***\"", JsonDiff.RenderLive(JsonValue.Create(Plain), desiredObject)!.ToJsonString());
        Assert.Equal("""["***","***"]""", JsonDiff.RenderLive(new JsonArray(JsonValue.Create("a"), JsonValue.Create("b")), desiredObject)!.ToJsonString());
        Assert.Equal("""{"x":"***","y":{"z":"***"}}""", JsonDiff.RenderLive(Obj("""{ "x": "1", "y": { "z": "2" } }"""), Secret())!.ToJsonString());
    }

    [Fact]
    public void RenderLive_MatchesObjectKeysCaseInsensitively_AndMasksOnlyTheSecretProperty()
    {
        var desired = new JsonObject { ["Notes"] = Secret(), ["OTHER"] = "x" };
        var live = Obj("""{ "notes": "live-secret", "other": "visible", "extra": "also visible", "empty": null }""");

        var rendered = JsonDiff.RenderLive(live, desired)!;

        Assert.Equal("""{"notes":"***","other":"visible","extra":"also visible","empty":null}""", rendered.ToJsonString());
        Assert.Equal("live-secret", live["notes"]!.GetValue<string>());
    }

    [Fact]
    public void RenderLive_NullLiveLeavesStayNull_WhenTheContainerIsMasked()
    {
        Assert.Equal("""{"a":null,"b":"***"}""", JsonDiff.RenderLive(Obj("""{ "a": null, "b": "x" }"""), Secret())!.ToJsonString());
    }

    [Fact]
    public void RenderLiveObject_ReturnsAnObjectCopy_OrNull()
    {
        var live = Obj("""{ "notes": "live-secret", "name": "n" }""");
        var desired = new JsonObject { ["notes"] = Secret() };

        var rendered = JsonDiff.RenderLiveObject(live, desired);

        Assert.NotNull(rendered);
        Assert.NotSame(live, rendered);
        Assert.Equal("""{"notes":"***","name":"n"}""", rendered.ToJsonString());
        Assert.Null(JsonDiff.RenderLiveObject(null, desired));
    }

    [Fact]
    public void MaskSecurePaths_MasksANestedPath()
    {
        var rendered = Obj("""{ "passwordProfile": { "password": "p", "forceChangePasswordNextSignIn": true }, "displayName": "d" }""");

        JsonDiff.MaskSecurePaths(rendered, [JsonPath.Of("passwordProfile", "password")]);

        Assert.Equal("""{"passwordProfile":{"password":"***","forceChangePasswordNextSignIn":true},"displayName":"d"}""", rendered.ToJsonString());
    }

    [Fact]
    public void MaskSecurePaths_MasksAnArrayIndex_AndEverythingUnderAMaskedObject()
    {
        var rendered = Obj("""{ "keys": [ { "value": "a", "id": 1 }, { "value": "b", "id": 2 } ], "blob": { "x": "1", "y": { "z": "2" } } }""");

        JsonDiff.MaskSecurePaths(rendered, [JsonPath.Of("keys").Append(1).Append("value"), JsonPath.Of("blob")]);

        Assert.Equal("""{"keys":[{"value":"a","id":1},{"value":"***","id":2}],"blob":{"x":"***","y":{"z":"***"}}}""", rendered.ToJsonString());
    }

    [Fact]
    public void MaskSecurePaths_SkipsMissingSegments_RootPaths_AndNullInput()
    {
        var rendered = Obj("""{ "a": { "b": "x" }, "list": ["i"] }""");
        string before = rendered.ToJsonString();

        JsonDiff.MaskSecurePaths(rendered, [JsonPath.Of("missing", "leaf"), JsonPath.Of("a", "missing"), JsonPath.Of("list").Append(5), JsonPath.Of("a", "b", "deeper"), JsonPath.Root]);
        JsonDiff.MaskSecurePaths(null, [JsonPath.Of("a")]);

        Assert.Equal(before, rendered.ToJsonString());
        Assert.Throws<ArgumentNullException>(() => JsonDiff.MaskSecurePaths(rendered, null!));
    }

    [Fact]
    public void MaskSecurePaths_MatchesPropertyNamesCaseInsensitively()
    {
        var rendered = Obj("""{ "PasswordProfile": { "Password": "p" } }""");

        JsonDiff.MaskSecurePaths(rendered, [JsonPath.Of("passwordProfile", "password")]);

        Assert.Equal("""{"PasswordProfile":{"Password":"***"}}""", rendered.ToJsonString());
    }
}
