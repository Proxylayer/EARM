using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using EntraArm.Core;
using EntraArm.Core.Json;
using EntraArm.Core.Loading;
using EntraArm.Core.Secrets;
using Xunit;

namespace EntraArm.Tests;

public sealed class SecretValueTests
{
    private const string Plain = "hunter2-s3cret-text";

    private static JsonObject TreeWithSecret()
    {
        return new JsonObject
        {
            ["displayName"] = "Break Glass",
            ["passwordProfile"] = new JsonObject
            {
                ["password"] = SecretJson.CreateString(Plain),
                ["forceChangePasswordNextSignIn"] = true,
            },
            ["tags"] = new JsonArray("a", SecretJson.CreateString(Plain)),
        };
    }

    [Fact]
    public void ToString_IsTheMask()
    {
        var secret = new SecretValue(Plain);

        Assert.Equal("***", secret.ToString());
        Assert.Equal("***", SecretValue.Mask);
        Assert.Equal(Plain, secret.Value);
        Assert.Equal(JsonValueKind.String, secret.Kind);
    }

    [Fact]
    public void Serialize_Directly_WritesTheMask()
    {
        var secret = new SecretValue(Plain);

        Assert.Equal("\"***\"", JsonSerializer.Serialize(secret));
        Assert.Equal("\"***\"", JsonSerializer.Serialize(secret, SecretJsonContext.Default.SecretValue));
        Assert.Equal("\"***\"", JsonSerializer.Serialize(secret, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.DoesNotContain(Plain, JsonSerializer.Serialize(new { Password = secret }));
    }

    [Fact]
    public void Serialize_InsideAJsonNodeTree_WritesTheMask()
    {
        var tree = TreeWithSecret();

        string[] renderings =
        [
            tree.ToJsonString(),
            tree.ToJsonString(new JsonSerializerOptions { WriteIndented = true, TypeInfoResolver = new DefaultJsonTypeInfoResolver() }),
            JsonSerializer.Serialize(tree),
            JsonSerializer.Serialize(tree, new JsonSerializerOptions { WriteIndented = true }),
            JsonSerializer.Serialize<JsonNode>(tree),
            tree["passwordProfile"]!["password"]!.ToJsonString(),
            tree["passwordProfile"]!["password"]!.ToString(),
            tree.ToString(),
            CanonicalJson.Serialize(tree),
        ];

        Assert.All(renderings, text => Assert.DoesNotContain(Plain, text));
        Assert.All(renderings, text => Assert.Contains("***", text));
        Assert.Equal("{\"displayName\":\"Break Glass\",\"passwordProfile\":{\"password\":\"***\",\"forceChangePasswordNextSignIn\":true},\"tags\":[\"a\",\"***\"]}", tree.ToJsonString());
        Assert.Equal("{\"displayName\":\"Break Glass\",\"passwordProfile\":{\"forceChangePasswordNextSignIn\":true,\"password\":\"***\"},\"tags\":[\"a\",\"***\"]}", CanonicalJson.Serialize(tree));
    }

    [Fact]
    public void TemplateJsonHelpers_NeverReadTheSecretText()
    {
        var leaf = SecretJson.CreateString(Plain);

        Assert.Null(TemplateJson.AsString(leaf));
        Assert.Null(TemplateJson.LiteralString(leaf));
        Assert.False(TemplateJson.IsExpression(leaf));
        Assert.DoesNotContain(Plain, leaf.ToJsonString());
    }

    [Fact]
    public void Reveal_ExposesTheValueOnlyThroughTheRevealingApis()
    {
        var tree = TreeWithSecret();
        var secret = new SecretValue(Plain);

        var revealed = Assert.IsType<JsonObject>(SecretJson.Reveal(tree));
        Assert.Equal(Plain, revealed["passwordProfile"]!["password"]!.GetValue<string>());
        Assert.Equal(Plain, revealed["tags"]![1]!.GetValue<string>());
        Assert.False(SecretJson.ContainsSecret(revealed));

        Assert.Equal("\"" + Plain + "\"", JsonSerializer.Serialize(secret, SecretJson.RevealingOptions));
        Assert.Contains(Plain, tree.ToJsonString(SecretJson.RevealingOptions));
        Assert.Contains(Plain, JsonSerializer.Serialize<JsonNode>(tree, SecretJson.RevealingOptions));

        // The original tree is untouched.
        Assert.True(SecretJson.ContainsSecret(tree));
        Assert.DoesNotContain(Plain, tree.ToJsonString());
    }

    [Fact]
    public void RevealingOptions_AreReadOnlyWithAResolver_AndRevealOnFirstUseFromJsonNode()
    {
        Assert.True(SecretJson.RevealingOptions.IsReadOnly);
        Assert.NotNull(SecretJson.RevealingOptions.TypeInfoResolver);
        Assert.Contains(SecretJson.RevealingOptions.Converters, c => c is RevealingJsonConverter);

        // A tainted leaf is a customized JsonValue; JsonNode.WriteTo(writer, options) makes foreign options read-only
        // without populating a resolver, so RevealingOptions must carry one from construction for this to work as
        // the first use of the instance in the process.
        Assert.Contains(Plain, TreeWithSecret().ToJsonString(SecretJson.RevealingOptions));
        Assert.Contains(Plain, TreeWithSecret()["passwordProfile"]!["password"]!.ToJsonString(SecretJson.RevealingOptions));
    }

    [Fact]
    public void FreshOptions_RevealThroughJsonNodeOnlyWhenTheyCarryAResolver()
    {
        // Caller-supplied options need a TypeInfoResolver for JsonNode.ToJsonString (STJ limitation, not fixable here);
        // JsonSerializer.Serialize populates the default resolver itself, so it works either way.
        var withResolver = new JsonSerializerOptions(JsonSerializerDefaults.General) { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };
        withResolver.Converters.Add(new RevealingJsonConverter());
        Assert.Contains(Plain, TreeWithSecret().ToJsonString(withResolver));

        var resolverless = new JsonSerializerOptions(JsonSerializerDefaults.General);
        resolverless.Converters.Add(new RevealingJsonConverter());
        Assert.Contains(Plain, JsonSerializer.Serialize<JsonNode>(TreeWithSecret(), resolverless));
    }

    [Fact]
    public void RevealingOptions_SerialiseDtosAndDictionaries()
    {
        var dto = new SecretDto("bg", new SecretValue(Plain), TreeWithSecret(), new SecretValue("4321", JsonValueKind.Number));

        string revealed = JsonSerializer.Serialize(dto, SecretJson.RevealingOptions);
        Assert.Contains("\"Secret\":\"" + Plain + "\"", revealed);
        Assert.Contains("\"password\":\"" + Plain + "\"", revealed);
        Assert.Contains("\"Pin\":4321", revealed);
        Assert.Contains("\"Name\":\"bg\"", revealed);

        string masked = JsonSerializer.Serialize(dto);
        Assert.DoesNotContain(Plain, masked);
        Assert.DoesNotContain("4321", masked);
        Assert.Contains("\"Secret\":\"***\"", masked);

        var dictionary = new Dictionary<string, JsonNode?> { ["tree"] = TreeWithSecret(), ["leaf"] = SecretJson.CreateString(Plain), ["plain"] = "visible" };
        string revealedDictionary = JsonSerializer.Serialize(dictionary, SecretJson.RevealingOptions);
        Assert.Contains("\"leaf\":\"" + Plain + "\"", revealedDictionary);
        Assert.Contains("\"plain\":\"visible\"", revealedDictionary);
        Assert.DoesNotContain(Plain, JsonSerializer.Serialize(dictionary));
    }

    private sealed record SecretDto(string Name, SecretValue Secret, JsonNode? Tree, SecretValue Pin);

    [Fact]
    public void RevealingOptions_WriteNonStringKindsAsTheirJson()
    {
        var number = new SecretValue("1234", JsonValueKind.Number);
        var flag = new SecretValue("true", JsonValueKind.True);

        Assert.Equal("1234", JsonSerializer.Serialize(number, SecretJson.RevealingOptions));
        Assert.Equal("true", JsonSerializer.Serialize(flag, SecretJson.RevealingOptions));
        Assert.Equal("\"***\"", JsonSerializer.Serialize(number));
    }

    [Fact]
    public void Clone_PreservesTaint_ButDeepCloneYieldsThePlainMask()
    {
        var tree = TreeWithSecret();

        var clone = Assert.IsType<JsonObject>(SecretJson.Clone(tree));
        Assert.NotSame(tree, clone);
        Assert.True(SecretJson.IsSecret(clone["passwordProfile"]!["password"], out var cloned));
        Assert.Equal(Plain, cloned.Value);
        Assert.True(SecretJson.IsSecret(clone["tags"]![1]));
        Assert.Equal("Break Glass", TemplateJson.AsString(clone["displayName"]));

        var deepClone = tree.DeepClone().AsObject();
        var leaf = deepClone["passwordProfile"]!["password"];
        Assert.False(SecretJson.IsSecret(leaf));
        Assert.Equal("***", TemplateJson.AsString(leaf));
        Assert.DoesNotContain(Plain, deepClone.ToJsonString(SecretJson.RevealingOptions));
    }

    [Fact]
    public void Taint_AndReveal_RoundTripEveryValueKindOfASecureObject()
    {
        var original = JsonNode.Parse("""{ "user": "u", "pin": 1234, "ok": true, "off": false, "ratio": 1.5, "nothing": null, "list": [1, "a", { "deep": "d" }] }""")!;

        var tainted = Assert.IsType<JsonObject>(SecretJson.Taint(original));

        Assert.Equal("{\"user\":\"***\",\"pin\":\"***\",\"ok\":\"***\",\"off\":\"***\",\"ratio\":\"***\",\"nothing\":null,\"list\":[\"***\",\"***\",{\"deep\":\"***\"}]}", tainted.ToJsonString());
        Assert.True(SecretJson.IsSecret(tainted["pin"], out var pin));
        Assert.Equal(JsonValueKind.Number, pin.Kind);
        Assert.Equal("1234", pin.Value);
        Assert.True(SecretJson.IsSecret(tainted["ok"], out var ok));
        Assert.Equal(JsonValueKind.True, ok.Kind);
        Assert.True(SecretJson.IsSecret(tainted["off"], out var off));
        Assert.Equal(JsonValueKind.False, off.Kind);
        Assert.True(SecretJson.IsSecret(tainted["user"], out var user));
        Assert.Equal(JsonValueKind.String, user.Kind);
        Assert.Equal("u", user.Value);
        Assert.Null(tainted["nothing"]);
        Assert.True(SecretJson.IsSecret(tainted["list"]![2]!["deep"]));

        var revealed = SecretJson.Reveal(tainted);
        Assert.True(JsonNode.DeepEquals(original, revealed));
        Assert.Equal(JsonValueKind.Number, revealed!["pin"]!.GetValueKind());
        Assert.Equal(1234, revealed["pin"]!.GetValue<int>());
        Assert.Equal(JsonValueKind.True, revealed["ok"]!.GetValueKind());
        Assert.Equal(1.5, revealed["ratio"]!.GetValue<double>());
    }

    [Fact]
    public void Taint_IsIdempotent()
    {
        var once = SecretJson.Taint(JsonValue.Create(Plain));
        var twice = SecretJson.Taint(once);

        Assert.True(SecretJson.IsSecret(twice, out var secret));
        Assert.Equal(Plain, secret.Value);
        Assert.Equal(JsonValueKind.String, secret.Kind);
        Assert.Null(SecretJson.Taint(null));
    }

    [Fact]
    public void FromLeaf_WrapsLeavesOnly()
    {
        Assert.Null(SecretValue.FromLeaf(null));
        Assert.Throws<ArgumentException>(() => SecretValue.FromLeaf(new JsonObject()));
        Assert.Throws<ArgumentException>(() => SecretValue.FromLeaf(new JsonArray()));

        var fromString = SecretValue.FromLeaf(JsonValue.Create("abc"))!;
        Assert.Equal("abc", fromString.Value);
        Assert.Equal(JsonValueKind.String, fromString.Kind);

        var fromNumber = SecretValue.FromLeaf(JsonValue.Create(42))!;
        Assert.Equal("42", fromNumber.Value);
        Assert.Equal(JsonValueKind.Number, fromNumber.Kind);

        var existing = new SecretValue(Plain);
        Assert.Same(existing, SecretValue.FromLeaf(SecretJson.Create(existing)));
    }

    [Fact]
    public void ToRevealedNode_RebuildsTheOriginalKind()
    {
        Assert.Equal("abc", new SecretValue("abc").ToRevealedNode()!.GetValue<string>());
        Assert.Equal(JsonValueKind.Number, new SecretValue("7", JsonValueKind.Number).ToRevealedNode()!.GetValueKind());
        Assert.Equal(JsonValueKind.False, new SecretValue("false", JsonValueKind.False).ToRevealedNode()!.GetValueKind());
    }

    [Fact]
    public void ContainsSecret_FindsNestedLeavesAndNothingElse()
    {
        Assert.True(SecretJson.ContainsSecret(TreeWithSecret()));
        Assert.True(SecretJson.ContainsSecret(new JsonArray(new JsonObject { ["x"] = SecretJson.CreateString("s") })));
        Assert.False(SecretJson.ContainsSecret(JsonNode.Parse("""{ "a": [1, { "b": "***" }] }""")));
        Assert.False(SecretJson.ContainsSecret(null));
        Assert.False(SecretJson.IsSecret(JsonValue.Create("***")));
        Assert.False(SecretJson.IsSecret(null, out var none));
        Assert.Null(none);
    }

    [Fact]
    public void Redact_ReplacesSecretsWithPlainMaskStrings()
    {
        var redacted = Assert.IsType<JsonObject>(SecretJson.Redact(TreeWithSecret()));

        var leaf = redacted["passwordProfile"]!["password"];
        Assert.False(SecretJson.IsSecret(leaf));
        Assert.Equal("***", TemplateJson.AsString(leaf));
        Assert.False(SecretJson.ContainsSecret(redacted));
        Assert.DoesNotContain(Plain, redacted.ToJsonString(SecretJson.RevealingOptions));
    }

    [Fact]
    public void DeepEquals_CannotTellSecretsApart_DocumentedCaveat()
    {
        var a = SecretJson.CreateString("one");
        var b = SecretJson.CreateString("two");

        Assert.True(JsonNode.DeepEquals(a, b));
        Assert.False(JsonNode.DeepEquals(SecretJson.Reveal(a), SecretJson.Reveal(b)));
    }

    [Fact]
    public void Converters_ReadAStringBackAsASecret()
    {
        var redacting = JsonSerializer.Deserialize<SecretValue>("\"abc\"");
        Assert.NotNull(redacting);
        Assert.Equal("abc", redacting.Value);
        Assert.Null(JsonSerializer.Deserialize<SecretValue>("null"));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SecretValue>("42"));

        var revealing = JsonSerializer.Deserialize<SecretValue>("\"abc\"", SecretJson.RevealingOptions);
        Assert.NotNull(revealing);
        Assert.Equal("abc", revealing.Value);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SecretValue>("true", SecretJson.RevealingOptions));
    }

    [Fact]
    public void Create_RejectsNull()
    {
        Assert.Throws<ArgumentNullException>(() => SecretJson.Create(null!));
        Assert.Throws<ArgumentNullException>(() => SecretJson.CreateString(null!));
    }

    [Fact]
    public void SecureParameters_InALoadedDocument_NeverSerializeTheirText()
    {
        const string template = """
            {
              "$schema": "https://entraarm.dev/schemas/2026-10-01/deploymentTemplate.json#",
              "contentVersion": "1.0.0.0",
              "parameters": { "pw": { "type": "securestring" }, "blob": { "type": "secureObject" }, "plain": { "type": "string" } },
              "resources": []
            }
            """;
        var parameters = ParameterSet
            .Parse("""{ "parameters": { "pw": { "value": "FILE-SECRET" }, "blob": { "value": { "pin": 4321, "note": "OBJ-SECRET" } } } }""", "values.parameters.json")
            .WithInline("plain", "visible");

        var document = new TemplateLoader().Parse(template, "t.entra.json", parameters);

        var pw = document.ParameterValues["pw"].Value;
        Assert.True(SecretJson.IsSecret(pw, out var pwSecret));
        Assert.Equal("FILE-SECRET", pwSecret.Value);
        Assert.Equal("\"***\"", pw!.ToJsonString());

        var blob = Assert.IsType<JsonObject>(document.ParameterValues["blob"].Value);
        Assert.True(SecretJson.IsSecret(blob["pin"], out var pin));
        Assert.Equal(JsonValueKind.Number, pin.Kind);
        Assert.Equal("{\"pin\":\"***\",\"note\":\"***\"}", blob.ToJsonString());

        var values = new Dictionary<string, JsonNode?>();
        foreach (var pair in document.ParameterValues)
        {
            values[pair.Key] = pair.Value.Value;
        }

        string serialized = JsonSerializer.Serialize(values);
        Assert.DoesNotContain("FILE-SECRET", serialized);
        Assert.DoesNotContain("OBJ-SECRET", serialized);
        Assert.DoesNotContain("4321", serialized);
        Assert.Contains("visible", serialized);

        string canonical = CanonicalJson.Serialize(new JsonObject { ["pw"] = SecretJson.Clone(pw), ["blob"] = SecretJson.Clone(blob) });
        Assert.Equal("{\"blob\":{\"note\":\"***\",\"pin\":\"***\"},\"pw\":\"***\"}", canonical);

        string revealed = JsonSerializer.Serialize(values, SecretJson.RevealingOptions);
        Assert.Contains("FILE-SECRET", revealed);
        Assert.Contains("\"pin\":4321", revealed);
    }
}
