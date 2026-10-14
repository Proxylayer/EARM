using System;
using System.Text.Json.Nodes;
using EntraArm.Core.Secrets;
using EntraArm.Expressions.Values;
using Xunit;

namespace EntraArm.Tests.Expressions;

/// <summary>
/// <see cref="JsonValues"/> over element-backed (parsed) and CLR-backed (constructed) nodes: numeric access for every backing
/// type, kind names for messages, value equality with numeric normalisation, and cloning that keeps secret and unknown leaves.
/// </summary>
public sealed class JsonValuesTests
{
    private static JsonNode Parsed(string json)
    {
        return JsonNode.Parse(json)!;
    }

    // ---- integers ----

    [Fact]
    public void TryGetInt64_ReadsEveryIntegralBacking()
    {
        long result;
        Assert.True(JsonValues.TryGetInt64(JsonValue.Create(7), out result));
        Assert.Equal(7, result);
        Assert.True(JsonValues.TryGetInt64(JsonValue.Create(7L), out result));
        Assert.Equal(7, result);
        Assert.True(JsonValues.TryGetInt64(JsonValue.Create((short)7), out result));
        Assert.Equal(7, result);
        Assert.True(JsonValues.TryGetInt64(JsonValue.Create((byte)7), out result));
        Assert.Equal(7, result);
        Assert.True(JsonValues.TryGetInt64(JsonValue.Create(7.0), out result));
        Assert.Equal(7, result);
        Assert.True(JsonValues.TryGetInt64(JsonValue.Create(7.0m), out result));
        Assert.Equal(7, result);
        Assert.True(JsonValues.TryGetInt64(Parsed("-42"), out result));
        Assert.Equal(-42, result);
        Assert.True(JsonValues.TryGetInt64(Parsed("9223372036854775807"), out result));
        Assert.Equal(long.MaxValue, result);
    }

    [Fact]
    public void TryGetInt64_RejectsFractionsHugeValuesAndNonNumbers()
    {
        Assert.False(JsonValues.TryGetInt64(JsonValue.Create(7.5), out _));
        Assert.False(JsonValues.TryGetInt64(JsonValue.Create(7.5m), out _));
        Assert.False(JsonValues.TryGetInt64(JsonValue.Create(1e300), out _));
        Assert.False(JsonValues.TryGetInt64(JsonValue.Create(79228162514264337593543950335m), out _));
        Assert.False(JsonValues.TryGetInt64(Parsed("9223372036854775808"), out _));
        Assert.False(JsonValues.TryGetInt64(Parsed("1.5"), out _));
        Assert.False(JsonValues.TryGetInt64(Parsed("\"7\""), out _));
        Assert.False(JsonValues.TryGetInt64(Parsed("true"), out _));
        Assert.False(JsonValues.TryGetInt64(null, out _));
        Assert.False(JsonValues.TryGetInt64(new JsonObject(), out _));
        Assert.False(JsonValues.TryGetInt64(SecretJson.CreateString("7"), out _));
        Assert.False(JsonValues.TryGetInt64(UnknownJson.Create("x", UnknownOrigins.Reference), out _));
    }

    [Fact]
    public void TryGetDouble_ReadsEveryNumericBacking()
    {
        double result;
        foreach (JsonNode node in new JsonNode[] { JsonValue.Create(2.5), JsonValue.Create(2.5f), JsonValue.Create(2.5m), Parsed("2.5") })
        {
            Assert.True(JsonValues.TryGetDouble(node, out result), node.ToJsonString());
            Assert.Equal(2.5, result);
        }

        Assert.True(JsonValues.TryGetDouble(JsonValue.Create(3), out result));
        Assert.Equal(3.0, result);
        Assert.True(JsonValues.TryGetDouble(JsonValue.Create(3L), out result));
        Assert.True(JsonValues.TryGetDouble(Parsed("1e2"), out result));
        Assert.Equal(100.0, result);
        Assert.False(JsonValues.TryGetDouble(Parsed("\"2.5\""), out _));
        Assert.False(JsonValues.TryGetDouble(null, out _));
        Assert.False(JsonValues.TryGetDouble(new JsonArray(), out _));
        Assert.False(JsonValues.TryGetDouble(SecretJson.CreateString("2.5"), out _));
    }

    // ---- predicates and kinds ----

    [Theory]
    [InlineData("1", true, false)]
    [InlineData("1.5", true, false)]
    [InlineData("\"x\"", false, true)]
    [InlineData("true", false, false)]
    [InlineData("null", false, false)]
    [InlineData("{}", false, false)]
    [InlineData("[]", false, false)]
    public void IsNumber_AndIsString_ClassifyLeaves(string json, bool number, bool text)
    {
        var node = JsonNode.Parse(json);

        Assert.Equal(number, JsonValues.IsNumber(node));
        Assert.Equal(text, JsonValues.IsString(node));
    }

    [Fact]
    public void Describe_NamesEveryKind()
    {
        Assert.Equal("null", JsonValues.Describe(null));
        Assert.Equal("string", JsonValues.Describe(Parsed("\"x\"")));
        Assert.Equal("integer", JsonValues.Describe(Parsed("5")));
        Assert.Equal("number", JsonValues.Describe(Parsed("5.5")));
        Assert.Equal("boolean", JsonValues.Describe(Parsed("true")));
        Assert.Equal("boolean", JsonValues.Describe(Parsed("false")));
        Assert.Equal("object", JsonValues.Describe(Parsed("{}")));
        Assert.Equal("array", JsonValues.Describe(Parsed("[]")));
        Assert.Equal("secure value", JsonValues.Describe(SecretJson.CreateString("x")));
        Assert.Equal("deploy-time value", JsonValues.Describe(UnknownJson.Create("x", UnknownOrigins.Reference)));
    }

    [Fact]
    public void TryGetString_AndTryGetBool_RefuseSecretsUnknownsAndOtherKinds()
    {
        Assert.True(JsonValues.TryGetString(JsonValue.Create("x"), out var text));
        Assert.Equal("x", text);
        Assert.False(JsonValues.TryGetString(JsonValue.Create(1), out text));
        Assert.Equal(string.Empty, text);
        Assert.False(JsonValues.TryGetString(SecretJson.CreateString("x"), out _));
        Assert.False(JsonValues.TryGetString(UnknownJson.Create("x", UnknownOrigins.Reference), out _));
        Assert.True(JsonValues.TryGetBool(JsonValue.Create(true), out bool yes));
        Assert.True(yes);
        Assert.True(JsonValues.TryGetBool(JsonValue.Create(false), out bool no));
        Assert.False(no);
        Assert.False(JsonValues.TryGetBool(JsonValue.Create("true"), out _));
        Assert.False(JsonValues.TryGetBool(null, out _));
        Assert.False(JsonValues.TryGetBool(SecretJson.CreateString("true"), out _));
    }

    // ---- equality ----

    [Theory]
    [InlineData("1", "1.0", true)]
    [InlineData("1", "1", true)]
    [InlineData("1", "2", false)]
    [InlineData("\"a\"", "\"a\"", true)]
    [InlineData("\"a\"", "\"A\"", false)]
    [InlineData("\"1\"", "1", false)]
    [InlineData("true", "true", true)]
    [InlineData("true", "false", false)]
    [InlineData("null", "null", true)]
    [InlineData("[1,2]", "[1,2]", true)]
    [InlineData("[1,2]", "[2,1]", false)]
    [InlineData("[1,2]", "[1,2,3]", false)]
    [InlineData("[]", "{}", false)]
    [InlineData("{\"a\":1,\"b\":[true]}", "{\"b\":[true],\"a\":1}", true)]
    [InlineData("{\"a\":1}", "{\"a\":1,\"b\":2}", false)]
    [InlineData("{\"a\":1}", "{\"A\":1}", false)]
    [InlineData("{\"a\":{\"b\":{\"c\":[1,{\"d\":null}]}}}", "{\"a\":{\"b\":{\"c\":[1,{\"d\":null}]}}}", true)]
    [InlineData("{\"a\":1}", "[1]", false)]
    [InlineData("\"x\"", "{\"x\":1}", false)]
    public void AreEqual_ComparesValues_WithNumericNormalisation(string left, string right, bool expected)
    {
        Assert.Equal(expected, JsonValues.AreEqual(JsonNode.Parse(left), JsonNode.Parse(right)));
        Assert.Equal(expected, JsonValues.AreEqual(JsonNode.Parse(right), JsonNode.Parse(left)));
    }

    [Fact]
    public void AreEqual_TreatsNullsAndMixedBackingsConsistently()
    {
        Assert.True(JsonValues.AreEqual(null, null));
        Assert.False(JsonValues.AreEqual(null, Parsed("1")));
        Assert.False(JsonValues.AreEqual(Parsed("1"), null));
        Assert.True(JsonValues.AreEqual(JsonValue.Create(1), Parsed("1")));
        Assert.True(JsonValues.AreEqual(JsonValue.Create(1L), JsonValue.Create(1.0)));
        Assert.True(JsonValues.AreEqual(JsonValue.Create(1.5m), Parsed("1.5")));
        Assert.False(JsonValues.AreEqual(JsonValue.Create("1"), JsonValue.Create(1)));
    }

    // ---- clone and render ----

    [Fact]
    public void Clone_KeepsSecretAndUnknownLeaves_AndDetachesTheCopy()
    {
        var original = new JsonObject
        {
            ["plain"] = "x",
            ["secret"] = SecretJson.CreateString("hunter2"),
            ["unknown"] = UnknownJson.Create("ref(a).b", UnknownOrigins.Reference),
            ["nested"] = new JsonArray(1, SecretJson.CreateString("deep"), new JsonObject { ["u"] = UnknownJson.Create("c", UnknownOrigins.Reference) }),
        };

        var clone = (JsonObject)JsonValues.Clone(original)!;

        Assert.NotSame(original, clone);
        Assert.True(SecretJson.IsSecret(clone["secret"]));
        Assert.True(UnknownJson.IsUnknown(clone["unknown"]));
        Assert.True(SecretJson.IsSecret(clone["nested"]![1]));
        Assert.True(UnknownJson.IsUnknown(clone["nested"]![2]!["u"]));
        clone["plain"] = "changed";
        Assert.Equal("x", original["plain"]!.GetValue<string>());
        Assert.Null(JsonValues.Clone(null));
    }

    [Fact]
    public void Render_NeverShowsASecretOrAnUnknownRaw()
    {
        var node = new JsonObject { ["s"] = SecretJson.CreateString("hunter2-canary"), ["u"] = UnknownJson.Create("ref(a).b", UnknownOrigins.Reference), ["n"] = 5 };

        string text = JsonValues.Render(node);

        Assert.DoesNotContain("hunter2-canary", text, StringComparison.Ordinal);
        Assert.Contains("5", text, StringComparison.Ordinal);
        Assert.Equal("null", JsonValues.Render(null));
    }
}
