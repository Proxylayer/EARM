using System;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// Secure values on an existing object (<see cref="JsonDiff"/>): a secure scalar Graph reads back is compared like any value (a masked
/// <c>Modify</c> when it differs, nothing when it is equal), so it updates the object; a write-only secret (no live value, a path the type
/// declares <c>secure</c>, a secure array) stays <c>NoEffect</c>, so reruns neither PATCH it nor count as changes.
/// </summary>
public sealed class SecureValueDiffTests
{
    private const string GuidLower = "6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0002";

    private static JsonObject J(string json) => JsonNode.Parse(json)!.AsObject();

    private static string Text(JsonNode? node) => node is null ? "null" : node.ToJsonString();

    [Fact]
    public void A_secure_scalar_that_differs_from_the_live_value_is_a_masked_modify_and_is_sent()
    {
        var desired = new JsonObject { ["state"] = SecretJson.CreateString("enabled") };
        var live = J("{\"state\":\"disabled\"}");

        var delta = Assert.Single(JsonDiff.Diff(desired, live));

        Assert.Equal(PropertyChangeType.Modify, delta.Kind);
        Assert.True(delta.HasEffect);
        Assert.True(delta.IsSecret);
        Assert.Equal("\"***\"", Text(delta.Before));
        Assert.Equal("\"***\"", Text(delta.After));
        var body = JsonDiff.BuildPatchBody(desired, live, [delta]);
        Assert.True(SecretJson.IsSecret(body["state"]));
        Assert.Equal("\"enabled\"", Text(SecretJson.Reveal(body["state"])));
    }

    [Theory]
    [InlineData("enabled", "{\"state\":\"enabled\"}")]
    [InlineData(GuidLower, "{\"state\":\"6D2F1C0E-2B7A-4C1E-9A1B-4F3F1F6A0002\"}")]
    public void A_secure_scalar_equal_to_the_live_value_is_no_change(string secret, string liveJson)
    {
        Assert.Empty(JsonDiff.Diff(new JsonObject { ["state"] = SecretJson.CreateString(secret) }, J(liveJson)));
    }

    [Fact]
    public void A_secure_scalar_inside_an_object_is_compared_and_the_container_stays_masked()
    {
        var desired = new JsonObject { ["settings"] = new JsonObject { ["mode"] = SecretJson.CreateString("strict"), ["level"] = 2 } };

        var delta = Assert.Single(JsonDiff.Diff(desired, J("{\"settings\":{\"mode\":\"lax\",\"level\":2}}")));

        Assert.Equal(PropertyChangeType.Modify, delta.Kind);
        Assert.True(delta.IsSecret);
        Assert.Contains(delta.Children!, c => c.Path.ToString() == "properties.settings.mode" && c.Kind == PropertyChangeType.Modify && c.IsSecret);
        Assert.DoesNotContain("strict", Text(delta.After), StringComparison.Ordinal);
        Assert.DoesNotContain("lax", Text(delta.Before), StringComparison.Ordinal);
    }

    [Fact]
    public void A_secure_scalar_without_a_live_value_is_write_only_and_no_effect()
    {
        var delta = Assert.Single(JsonDiff.Diff(new JsonObject { ["state"] = SecretJson.CreateString("enabled") }, J("{}")));

        Assert.Equal(PropertyChangeType.NoEffect, delta.Kind);
        Assert.False(delta.HasEffect);
        Assert.True(delta.IsSecret);
    }

    [Fact]
    public void A_path_the_type_declares_secure_is_never_compared_even_with_a_live_value()
    {
        var desired = new JsonObject { ["passwordProfile"] = new JsonObject { ["password"] = SecretJson.CreateString("hunter2") } };
        var options = new JsonDiffOptions { Secure = [JsonPath.Of("passwordProfile", "password")] };

        var delta = Assert.Single(JsonDiff.Diff(desired, J("{\"passwordProfile\":{\"password\":\"something-else\"}}"), options));

        Assert.Equal(PropertyChangeType.NoEffect, delta.Kind);
        Assert.False(delta.HasEffect);
        Assert.DoesNotContain("something-else", Text(delta.Before), StringComparison.Ordinal);
    }

    [Fact]
    public void A_secure_array_is_not_judged_whole()
    {
        var desired = new JsonObject { ["tags"] = new JsonArray(SecretJson.CreateString("a")) };

        var delta = Assert.Single(JsonDiff.Diff(desired, J("{\"tags\":[\"b\"]}")));

        Assert.Equal(PropertyChangeType.NoEffect, delta.Kind);
        Assert.False(delta.HasEffect);
    }

    [Fact]
    public void ForDefinition_carries_the_type_secure_paths()
    {
        var users = EArm.Core.Types.ResourceTypeCatalog.Default.Find("Microsoft.Entra/users")!;

        var options = JsonDiffOptions.ForDefinition(users, existing: true);

        Assert.Contains(options.Secure, p => p.ToString() == "passwordProfile.password");
    }
}
