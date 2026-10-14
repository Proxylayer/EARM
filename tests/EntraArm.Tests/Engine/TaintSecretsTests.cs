using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Core.Json;
using EntraArm.Core.Secrets;
using EntraArm.Engine.Planning;
using EntraArm.Engine.WhatIf;
using EntraArm.Expressions.Values;
using Xunit;

namespace EntraArm.Tests.Engine;

/// <summary><see cref="InstanceEvaluator.TaintSecrets"/>: the overlay's <c>secure</c> paths are tainted like a <c>securestring</c> parameter would be.</summary>
public sealed class TaintSecretsTests
{
    private static JsonPath P(string text)
    {
        return JsonPath.Parse(text);
    }

    private static string Revealed(JsonNode? node)
    {
        Assert.True(SecretJson.IsSecret(node, out var secret), "expected a secret leaf but got " + (node?.ToJsonString() ?? "null"));
        return secret.Value;
    }

    [Fact]
    public void PlainLeaf_BecomesASecret_AndRendersAsMask()
    {
        var properties = new JsonObject { ["displayName"] = "u", ["passwordProfile"] = new JsonObject { ["password"] = "hunter2", ["forceChangePasswordNextSignIn"] = true } };

        InstanceEvaluator.TaintSecrets(properties, [P("passwordProfile.password")]);

        Assert.Equal("hunter2", Revealed(properties["passwordProfile"]!["password"]));
        Assert.Equal("\"***\"", properties["passwordProfile"]!["password"]!.ToJsonString());
        Assert.DoesNotContain("hunter2", properties.ToJsonString(), System.StringComparison.Ordinal);
        Assert.Equal("u", properties["displayName"]!.GetValue<string>());
        Assert.True(properties["passwordProfile"]!["forceChangePasswordNextSignIn"]!.GetValue<bool>());
    }

    [Theory]
    [InlineData("passwordprofile.PASSWORD")]
    [InlineData("PasswordProfile.Password")]
    public void PropertyNames_MatchCaseInsensitively(string path)
    {
        var properties = new JsonObject { ["passwordProfile"] = new JsonObject { ["password"] = "p" } };

        InstanceEvaluator.TaintSecrets(properties, [P(path)]);

        Assert.Equal("p", Revealed(properties["passwordProfile"]!["password"]));
    }

    [Fact]
    public void NonStringLeaves_KeepTheirKind()
    {
        var properties = new JsonObject { ["n"] = 42, ["b"] = true, ["s"] = "x" };

        InstanceEvaluator.TaintSecrets(properties, [P("n"), P("b"), P("s")]);

        Assert.True(SecretJson.IsSecret(properties["n"], out var number));
        Assert.Equal(JsonValueKind.Number, number.Kind);
        Assert.Equal("42", number.Value);
        Assert.True(SecretJson.IsSecret(properties["b"], out var flag));
        Assert.Equal(JsonValueKind.True, flag.Kind);
        Assert.True(SecretJson.IsSecret(properties["s"], out var text));
        Assert.Equal(JsonValueKind.String, text.Kind);
    }

    [Fact]
    public void AnUnknownLeaf_KeepsItsSymbol_AndBecomesTainted()
    {
        var properties = new JsonObject { ["secretText"] = UnknownJson.Create("ref(/providers/Microsoft.Entra/applications/a).x", UnknownOrigins.Reference) };

        InstanceEvaluator.TaintSecrets(properties, [P("secretText")]);

        Assert.True(UnknownJson.IsUnknown(properties["secretText"], out var unknown));
        Assert.True(unknown.Tainted);
        Assert.Equal("ref(/providers/Microsoft.Entra/applications/a).x", unknown.Symbol);
        Assert.Equal(UnknownOrigins.Reference, unknown.Origins);
        Assert.Equal("\"(deploy-time)\"", properties["secretText"]!.ToJsonString());
    }

    [Fact]
    public void SecretsTaintedLeavesAndNulls_AreLeftAlone()
    {
        var secret = SecretJson.CreateString("already");
        var tainted = UnknownJson.Create("sym", UnknownOrigins.Reference, tainted: true);
        var properties = new JsonObject { ["a"] = secret, ["b"] = tainted, ["c"] = null };

        InstanceEvaluator.TaintSecrets(properties, [P("a"), P("b"), P("c")]);

        Assert.Same(secret, properties["a"]);
        Assert.Same(tainted, properties["b"]);
        Assert.Null(properties["c"]);
        Assert.True(properties.ContainsKey("c"));
    }

    [Fact]
    public void CallingItTwice_IsANoOp()
    {
        var properties = new JsonObject { ["passwordProfile"] = new JsonObject { ["password"] = "p" }, ["t"] = UnknownJson.Create("s", UnknownOrigins.Copy) };
        InstanceEvaluator.TaintSecrets(properties, [P("passwordProfile.password"), P("t")]);
        var once = properties["passwordProfile"]!["password"];
        var unknownOnce = properties["t"];

        InstanceEvaluator.TaintSecrets(properties, [P("passwordProfile.password"), P("t")]);

        Assert.Same(once, properties["passwordProfile"]!["password"]);
        Assert.Same(unknownOnce, properties["t"]);
        Assert.Equal("p", Revealed(once));
    }

    [Fact]
    public void ObjectsAndArraysUnderASecurePath_AreTaintedLeafByLeaf()
    {
        var properties = new JsonObject
        {
            ["blob"] = new JsonObject { ["a"] = "1", ["nested"] = new JsonArray("x", 2), ["nothing"] = null },
        };

        InstanceEvaluator.TaintSecrets(properties, [P("blob")]);

        var blob = properties["blob"]!.AsObject();
        Assert.Equal("1", Revealed(blob["a"]));
        Assert.Equal("x", Revealed(blob["nested"]![0]));
        Assert.Equal("2", Revealed(blob["nested"]![1]));
        Assert.Null(blob["nothing"]);
        Assert.Equal("{\"a\":\"***\",\"nested\":[\"***\",\"***\"],\"nothing\":null}", blob.ToJsonString());
    }

    [Fact]
    public void IndexSegments_AddressArrayMembers()
    {
        var properties = new JsonObject { ["keys"] = new JsonArray(new JsonObject { ["v"] = "a" }, new JsonObject { ["v"] = "b" }) };

        InstanceEvaluator.TaintSecrets(properties, [P("keys[1].v")]);

        Assert.Equal("a", properties["keys"]![0]!["v"]!.GetValue<string>());
        Assert.Equal("b", Revealed(properties["keys"]![1]!["v"]));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("passwordProfile.missing")]
    [InlineData("passwordProfile.password.deeper")]
    [InlineData("displayName.child")]
    [InlineData("keys[5]")]
    [InlineData("keys.v")]
    public void PathsThatDoNotExist_AreSkipped(string path)
    {
        var properties = new JsonObject { ["displayName"] = "d", ["passwordProfile"] = new JsonObject { ["password"] = "p" }, ["keys"] = new JsonArray("k") };
        string before = properties.ToJsonString();

        InstanceEvaluator.TaintSecrets(properties, [P(path)]);

        Assert.Equal("d", properties["displayName"]!.GetValue<string>());
        Assert.Equal("k", properties["keys"]![0]!.GetValue<string>());
        Assert.Equal(before.Replace("\"p\"", "\"***\"", System.StringComparison.Ordinal), properties.ToJsonString().Replace("\"p\"", "\"***\"", System.StringComparison.Ordinal));
    }

    [Fact]
    public void RootPathAndEmptyLists_DoNothing()
    {
        var properties = new JsonObject { ["a"] = "1" };

        InstanceEvaluator.TaintSecrets(properties, [JsonPath.Root]);
        InstanceEvaluator.TaintSecrets(properties, []);

        Assert.Equal("1", properties["a"]!.GetValue<string>());
        Assert.Throws<System.ArgumentNullException>(() => InstanceEvaluator.TaintSecrets(null!, []));
        Assert.Throws<System.ArgumentNullException>(() => InstanceEvaluator.TaintSecrets(properties, null!));
    }

    // ---- through the engine ----

    private const string UserWithLiteralPassword = "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01\", \"name\": \"u@contoso.com\", \"properties\": { \"displayName\": \"U\", \"passwordProfile\": { \"password\": \"Literal-P@ss-1\", \"forceChangePasswordNextSignIn\": true } } }";

    [Fact]
    public async Task EvaluateProperties_TaintsALiteralPasswordFromTheOverlay()
    {
        var host = new EngineHost();
        var plan = await host.ExpandAsync(Templates.Resources(UserWithLiteralPassword));

        var instance = EngineHost.Evaluated(plan, "/providers/Microsoft.Entra/users/u@contoso.com");

        Assert.Equal("Literal-P@ss-1", Revealed(instance.Properties!["passwordProfile"]!["password"]));
        Assert.True(instance.Properties["passwordProfile"]!["forceChangePasswordNextSignIn"]!.GetValue<bool>());
        Assert.Equal("\"***\"", instance.Properties["passwordProfile"]!["password"]!.ToJsonString());
    }

    [Fact]
    public async Task ALiteralPassword_NeverReachesWhatIfOutputOrTheStateHash()
    {
        var host = new EngineHost();
        var plan = await host.WhatIfAsync(Templates.Resources(UserWithLiteralPassword));
        var report = WhatIfReport.From(plan);
        using var writer = new System.IO.StringWriter();
        WhatIfText.Render(report, writer);

        string json = WhatIfJson.Serialize(WhatIfJson.Build(report), prettyPrint: false);

        var user = Assert.Single(report.Rows);
        Assert.Equal("***", user.After!["properties"]!["passwordProfile"]!["password"]!.GetValue<string>());
        Assert.DoesNotContain("Literal-P@ss-1", writer.ToString(), System.StringComparison.Ordinal);
        Assert.DoesNotContain("Literal-P@ss-1", json, System.StringComparison.Ordinal);
        Assert.Contains("password: \"***\"", writer.ToString());

        var instance = EngineHost.Instance(plan, "/providers/Microsoft.Entra/users/u@contoso.com");
        var other = await host.ExpandAsync(Templates.Resources(UserWithLiteralPassword.Replace("Literal-P@ss-1", "Another-P@ss-2", System.StringComparison.Ordinal)));
        Assert.Equal(
            EntraArm.Engine.State.StateHashing.ContentHash(instance.Properties),
            EntraArm.Engine.State.StateHashing.ContentHash(EngineHost.Evaluated(other, "/providers/Microsoft.Entra/users/u@contoso.com").Properties));
    }

    [Fact]
    public async Task ACreate_StillSendsTheClearPassword_ToGraph()
    {
        var host = new EngineHost();
        host.SeedExisting();

        var (_, result) = await host.CreateAsync(Templates.Resources(UserWithLiteralPassword));

        Assert.True(result.Succeeded);
        var write = Assert.Single(host.Fake.Writes, w => w.Path == "/users");
        Assert.Equal("Literal-P@ss-1", write.Body!["passwordProfile"]!["password"]!.GetValue<string>());
        Assert.DoesNotContain("Literal-P@ss-1", result.Outputs.ToJsonString(), System.StringComparison.Ordinal);
    }
}
