using System;
using System.Linq;
using System.Text.Json.Nodes;
using EntraArm.Core.Json;
using EntraArm.Core.Secrets;
using EntraArm.Engine.Providers;
using EntraArm.Expressions.Values;
using Xunit;

namespace EntraArm.Tests.Engine;

/// <summary>The small JSON helpers every provider leans on: case-insensitive access, absence rules, path removal, dotted get / set-default, GUID-aware key comparison.</summary>
public sealed class JsonObjectsTests
{
    private static JsonObject Obj(string json)
    {
        return (JsonObject)JsonNode.Parse(json)!;
    }

    // ---- access ----

    [Fact]
    public void TryGetIgnoreCase_FindsExactAndDifferentlyCasedKeys_AndToleratesNull()
    {
        var obj = Obj("{ \"DisplayName\": \"x\", \"displayname\": \"y\" }");

        Assert.True(JsonObjects.TryGetIgnoreCase(obj, "displayName", out var value));
        Assert.Contains(value!.GetValue<string>(), new[] { "x", "y" });
        Assert.True(JsonObjects.TryGetIgnoreCase(obj, "DisplayName", out value));
        Assert.Equal("x", value!.GetValue<string>());
        Assert.False(JsonObjects.TryGetIgnoreCase(obj, "other", out value));
        Assert.Null(value);
        Assert.False(JsonObjects.TryGetIgnoreCase(null, "any", out value));
    }

    [Fact]
    public void FindKey_ReturnsTheStoredSpelling()
    {
        var obj = Obj("{ \"AppId\": 1 }");

        Assert.Equal("AppId", JsonObjects.FindKey(obj, "appid"));
        Assert.Equal("AppId", JsonObjects.FindKey(obj, "AppId"));
        Assert.Null(JsonObjects.FindKey(obj, "missing"));
        Assert.Null(JsonObjects.FindKey(null, "x"));
        Assert.Throws<ArgumentNullException>(() => JsonObjects.FindKey(obj, null!));
    }

    [Fact]
    public void GetString_AndGetId_OnlyReadPlainStrings()
    {
        var obj = new JsonObject
        {
            ["id"] = "abc",
            ["number"] = 5,
            ["secret"] = SecretJson.CreateString("hunter2"),
            ["unknown"] = UnknownJson.Create("ref(x).y", UnknownOrigins.Reference),
            ["nothing"] = null,
        };

        Assert.Equal("abc", JsonObjects.GetId(obj));
        Assert.Equal("abc", JsonObjects.GetString(obj, "ID"));
        Assert.Null(JsonObjects.GetString(obj, "number"));
        Assert.Null(JsonObjects.GetString(obj, "secret"));
        Assert.Null(JsonObjects.GetString(obj, "unknown"));
        Assert.Null(JsonObjects.GetString(obj, "nothing"));
        Assert.Null(JsonObjects.GetString(obj, "missing"));
        Assert.Null(JsonObjects.GetId(null));
    }

    [Fact]
    public void IsAbsent_IsNullNullLeafOrEmptyArray_Only()
    {
        Assert.True(JsonObjects.IsAbsent(null));
        Assert.True(JsonObjects.IsAbsent(JsonNode.Parse("[]")));
        Assert.True(JsonObjects.IsAbsent(JsonValue.Create((string?)null)));
        Assert.False(JsonObjects.IsAbsent(JsonNode.Parse("{}")));
        Assert.False(JsonObjects.IsAbsent(JsonNode.Parse("[0]")));
        Assert.False(JsonObjects.IsAbsent(JsonValue.Create(0)));
        Assert.False(JsonObjects.IsAbsent(JsonValue.Create(false)));
        Assert.False(JsonObjects.IsAbsent(JsonValue.Create(string.Empty)));
        Assert.False(JsonObjects.IsAbsent(SecretJson.CreateString("x")));
        Assert.False(JsonObjects.IsAbsent(UnknownJson.Create("a", UnknownOrigins.Reference)));
    }

    // ---- remove ----

    [Fact]
    public void Remove_DeletesPropertiesAndArrayElements_AtAnyDepth_IgnoringCase()
    {
        var obj = Obj("{ \"a\": { \"B\": { \"c\": [10, 20, { \"d\": 1, \"e\": 2 }] } } }");

        Assert.True(JsonObjects.Remove(obj, JsonPath.Parse("a.b.c[2].D")));
        Assert.True(JsonObjects.Remove(obj, JsonPath.Parse("a.b.c[0]")));
        Assert.Equal("{\"a\":{\"B\":{\"c\":[20,{\"e\":2}]}}}", obj.ToJsonString());
        Assert.True(JsonObjects.Remove(obj, JsonPath.Parse("a.B")));
        Assert.Equal("{\"a\":{}}", obj.ToJsonString());
    }

    [Theory]
    [InlineData("")]
    [InlineData("missing")]
    [InlineData("a.missing")]
    [InlineData("a.b.c")]
    [InlineData("a[0]")]
    [InlineData("list[9]")]
    [InlineData("list[0].x")]
    [InlineData("list[5].x")]
    [InlineData("scalar.x")]
    [InlineData("list.x")]
    public void Remove_OfAPathThatIsNotThere_IsFalse_AndChangesNothing(string path)
    {
        var obj = Obj("{ \"a\": { \"b\": 1 }, \"list\": [1, 2], \"scalar\": 5 }");
        string before = obj.ToJsonString();

        Assert.False(JsonObjects.Remove(obj, JsonPath.Parse(path)));
        Assert.Equal(before, obj.ToJsonString());
    }

    // ---- dotted ----

    [Fact]
    public void TryGetDotted_PrefersAWholeDottedKey_ThenWalksTheSegments()
    {
        var obj = Obj("{ \"@odata.type\": \"#t\", \"conditions\": { \"users\": { \"include\": [1] } }, \"flat.key\": 7 }");

        Assert.True(JsonObjects.TryGetDotted(obj, "@odata.type", out var odata));
        Assert.Equal("#t", odata!.GetValue<string>());
        Assert.True(JsonObjects.TryGetDotted(obj, "Conditions.Users.include", out var include));
        Assert.IsType<JsonArray>(include);
        Assert.True(JsonObjects.TryGetDotted(obj, "flat.key", out var flat));
        Assert.Equal(7, flat!.GetValue<int>());
        Assert.False(JsonObjects.TryGetDotted(obj, "conditions.apps", out var none));
        Assert.Null(none);
        Assert.False(JsonObjects.TryGetDotted(obj, "conditions.users.include.deeper", out _));
        Assert.False(JsonObjects.TryGetDotted(null, "a.b", out _));
        Assert.ThrowsAny<ArgumentException>(() => JsonObjects.TryGetDotted(obj, " ", out _));
    }

    [Fact]
    public void SetDefault_CreatesMissingObjects_NeverOverwrites_AndRespectsANonObjectOnTheWay()
    {
        var obj = Obj("{ \"keep\": { \"x\": 1 }, \"scalar\": 5, \"Existing\": { \"Leaf\": 2 } }");

        Assert.True(JsonObjects.SetDefault(obj, "passwordProfile.force", JsonValue.Create(true)));
        Assert.True(JsonObjects.SetDefault(obj, "keep.y", JsonValue.Create(2)));
        Assert.False(JsonObjects.SetDefault(obj, "keep.x", JsonValue.Create(99)));
        Assert.False(JsonObjects.SetDefault(obj, "scalar.deeper", JsonValue.Create(1)));
        Assert.False(JsonObjects.SetDefault(obj, "existing.leaf", JsonValue.Create(3)));
        Assert.True(JsonObjects.SetDefault(obj, "topLevel", JsonValue.Create("v")));

        Assert.Equal("{\"keep\":{\"x\":1,\"y\":2},\"scalar\":5,\"Existing\":{\"Leaf\":2},\"passwordProfile\":{\"force\":true},\"topLevel\":\"v\"}", obj.ToJsonString());
        Assert.Throws<ArgumentNullException>(() => JsonObjects.SetDefault(null!, "a", null));
        Assert.ThrowsAny<ArgumentException>(() => JsonObjects.SetDefault(obj, "", null));
    }

    [Fact]
    public void SetDefault_StoresACopy_ThatKeepsSecretLeaves()
    {
        var obj = new JsonObject();
        var secret = SecretJson.CreateString("hunter2");
        var original = new JsonObject { ["p"] = secret };

        Assert.True(JsonObjects.SetDefault(obj, "a.b", original));
        ((JsonObject)original)["p"] = "changed";

        var stored = obj["a"]!["b"]!;
        Assert.True(SecretJson.IsSecret(stored["p"]));
        Assert.NotSame(original, stored);
    }

    // ---- keys and unknowns ----

    [Theory]
    [InlineData("6D2F1C0E-2B7A-4C1E-9A1B-4F3F1F6A0001", "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001", true)]
    [InlineData("6d2f1c0e2b7a4c1e9a1b4f3f1f6a0001", "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001", true)]
    [InlineData("Alice@Contoso.com", "alice@contoso.com", true)]
    [InlineData("alice", "bob", false)]
    [InlineData("6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001", "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002", false)]
    [InlineData(null, null, true)]
    [InlineData(null, "x", false)]
    [InlineData("x", null, false)]
    [InlineData("", "", true)]
    public void KeyEquals_IsCaseInsensitive_AndGuidAware(string? left, string? right, bool expected)
    {
        Assert.Equal(expected, JsonObjects.KeyEquals(left, right));
        Assert.Equal(expected, JsonObjects.KeyEquals(right, left));
    }

    [Fact]
    public void UnknownLeaves_ListsEveryPlaceholderWithItsPath()
    {
        var document = new JsonObject
        {
            ["a"] = UnknownJson.Create("ref(a).id", UnknownOrigins.Reference),
            ["list"] = new JsonArray(1, UnknownJson.Create("ref(b).id", UnknownOrigins.Reference), new JsonObject { ["deep"] = UnknownJson.Create("ref(c).id", UnknownOrigins.Reference) }),
            ["plain"] = "x",
        };

        var leaves = JsonObjects.UnknownLeaves(document, JsonPath.Of("properties"));

        Assert.Equal(["properties.a", "properties.list[1]", "properties.list[2].deep"], leaves.Select(l => l.Path.ToString()).Order(StringComparer.Ordinal).ToArray());
        Assert.Equal(["ref(a).id", "ref(b).id", "ref(c).id"], leaves.Select(l => l.Symbol).Order(StringComparer.Ordinal).ToArray());
        Assert.Empty(JsonObjects.UnknownLeaves(null, JsonPath.Root));
        Assert.Throws<ArgumentNullException>(() => JsonObjects.UnknownLeaves(document, null!));
    }
}
