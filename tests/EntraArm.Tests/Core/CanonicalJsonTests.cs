using System;
using System.Text;
using System.Text.Json.Nodes;
using EntraArm.Core.Json;
using EntraArm.Core.Secrets;
using Xunit;

namespace EntraArm.Tests;

public sealed class CanonicalJsonTests
{
    [Fact]
    public void Serialize_SortsObjectKeysOrdinallyAtEveryLevel()
    {
        var node = JsonNode.Parse("""{ "b": { "z": 1, "a": 2 }, "a": [ { "y": 1, "x": 2 } ], "B": 0 }""");

        Assert.Equal("{\"B\":0,\"a\":[{\"x\":2,\"y\":1}],\"b\":{\"a\":2,\"z\":1}}", CanonicalJson.Serialize(node));
    }

    [Fact]
    public void Serialize_IsIndependentOfKeyOrderAndWhitespace()
    {
        var a = JsonNode.Parse("{ \"x\": 1,   \"y\": [1, 2] }");
        var b = JsonNode.Parse("{\"y\":[1,2],\"x\":1}");

        Assert.Equal(CanonicalJson.Serialize(a), CanonicalJson.Serialize(b));
        Assert.Equal(CanonicalJson.Hash(a), CanonicalJson.Hash(b));
    }

    [Theory]
    [InlineData("2.50", "2.5")]
    [InlineData("1.0", "1")]
    [InlineData("10", "10")]
    [InlineData("-3", "-3")]
    [InlineData("0.125", "0.125")]
    [InlineData("1e2", "100")]
    [InlineData("1E-2", "0.01")]
    [InlineData("9007199254740993", "9007199254740993")]
    public void Serialize_NormalisesNumbers(string literal, string expected)
    {
        var node = JsonNode.Parse("{ \"n\": " + literal + " }");

        Assert.Equal("{\"n\":" + expected + "}", CanonicalJson.Serialize(node));
    }

    [Fact]
    public void Serialize_WritesClrBackedPrimitivesInvariantly()
    {
        var node = new JsonObject
        {
            ["d"] = 2.5,
            ["i"] = 7,
            ["l"] = 9000000000L,
            ["b"] = true,
            ["s"] = "text",
            ["n"] = null,
        };

        Assert.Equal("{\"b\":true,\"d\":2.5,\"i\":7,\"l\":9000000000,\"n\":null,\"s\":\"text\"}", CanonicalJson.Serialize(node));
    }

    [Fact]
    public void Serialize_PreservesNonAsciiBmpCharactersLiterally()
    {
        var node = JsonNode.Parse("""{ "name": "Zoë 日本語 Ωé", "quote": "a\"b\\c", "control": "line\nbreak", "html": "<a&b>" }""");

        string canonical = CanonicalJson.Serialize(node);

        Assert.Equal("{\"control\":\"line\\nbreak\",\"html\":\"<a&b>\",\"name\":\"Zoë 日本語 Ωé\",\"quote\":\"a\\\"b\\\\c\"}", canonical);
        Assert.DoesNotContain("\\u", canonical);
        Assert.Equal(canonical, Encoding.UTF8.GetString(CanonicalJson.SerializeToUtf8(node, [])));
    }

    [Fact]
    public void Serialize_EscapesSupplementaryPlaneCharactersDeterministically()
    {
        // System.Text.Encodings.Web encoders (including UnsafeRelaxedJsonEscaping) always escape code points
        // outside the Basic Multilingual Plane, so an emoji is written as its surrogate pair. The form is
        // stable, which is all hashing needs; the CanonicalJson doc comment's "non-ASCII written literally"
        // holds for BMP characters only.
        var node = JsonNode.Parse("""{ "name": "😀" }""");

        Assert.Equal("{\"name\":\"\\uD83D\\uDE00\"}", CanonicalJson.Serialize(node));
        Assert.Equal(CanonicalJson.Hash(node), CanonicalJson.Hash(JsonNode.Parse("{\"name\":\"\\uD83D\\uDE00\"}")));
    }

    [Fact]
    public void Serialize_WritesSecretLeavesAsTheMask()
    {
        var node = new JsonObject
        {
            ["password"] = SecretJson.CreateString("hunter2"),
            ["pin"] = SecretJson.Create(new SecretValue("1234", System.Text.Json.JsonValueKind.Number)),
            ["user"] = "u",
        };

        string canonical = CanonicalJson.Serialize(node);

        Assert.Equal("{\"password\":\"***\",\"pin\":\"***\",\"user\":\"u\"}", canonical);
        Assert.DoesNotContain("hunter2", canonical);
    }

    [Fact]
    public void Hash_DoesNotDependOnTheSecretText()
    {
        var a = new JsonObject { ["password"] = SecretJson.CreateString("one") };
        var b = new JsonObject { ["password"] = SecretJson.CreateString("two") };

        Assert.Equal(CanonicalJson.Hash(a), CanonicalJson.Hash(b));
    }

    [Fact]
    public void Serialize_OmitsExcludedPropertiesAndElements()
    {
        var node = JsonNode.Parse("""{ "a": 1, "secret": "s", "items": [10, 20, 30], "nested": { "keep": 1, "drop": 2 } }""");
        JsonPath[] exclude = [JsonPath.Parse("secret"), JsonPath.Parse("items[1]"), JsonPath.Parse("nested.drop")];

        Assert.Equal("{\"a\":1,\"items\":[10,30],\"nested\":{\"keep\":1}}", CanonicalJson.Serialize(node, exclude));
        Assert.Equal(CanonicalJson.Sha256("{\"a\":1,\"items\":[10,30],\"nested\":{\"keep\":1}}"), CanonicalJson.Hash(node, exclude));
    }

    [Fact]
    public void Serialize_ExcludingTheRootPathHasNoEffect()
    {
        var node = JsonNode.Parse("{ \"a\": 1 }");

        Assert.Equal("{\"a\":1}", CanonicalJson.Serialize(node, [JsonPath.Root]));
    }

    [Fact]
    public void Serialize_HandlesNullAndScalarsAtTheRoot()
    {
        Assert.Equal("null", CanonicalJson.Serialize(null));
        Assert.Equal("\"x\"", CanonicalJson.Serialize(JsonValue.Create("x")));
        Assert.Equal("[]", CanonicalJson.Serialize(new JsonArray()));
        Assert.Equal("{}", CanonicalJson.Serialize(new JsonObject()));
        Assert.Equal("[null,{\"a\":null}]", CanonicalJson.Serialize(JsonNode.Parse("[null, { \"a\": null }]")));
    }

    [Fact]
    public void Sha256_IsTheLowercaseHexOfTheUtf8Bytes()
    {
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", CanonicalJson.Sha256("abc"));
        Assert.Equal("e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855", CanonicalJson.Sha256(string.Empty));
        Assert.Equal(CanonicalJson.Sha256("{\"a\":1}"), CanonicalJson.Hash(JsonNode.Parse("{\"a\":1}")));
        Assert.Throws<ArgumentNullException>(() => CanonicalJson.Sha256(null!));
    }

    [Fact]
    public void Serialize_RejectsNullExcludeList()
    {
        Assert.Throws<ArgumentNullException>(() => CanonicalJson.Serialize(new JsonObject(), null!));
    }
}
