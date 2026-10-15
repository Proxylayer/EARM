using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core.Json;
using EArm.Core.Secrets;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Pure rules of the users provider: UPN shape, mailNickname, secret tainting, password generation, defaults, body clean-ups and licensing views.</summary>
public sealed class UsersRulesTests
{
    private const string Sku = "6fd2c87f-b296-42f0-b197-1e91e994b900";

    [Theory]
    [InlineData("alice@contoso.com", true)]
    [InlineData("alice_gmail.com#EXT#@contoso.onmicrosoft.com", true)]
    [InlineData("not-a-upn", false)]
    [InlineData("@contoso.com", false)]
    [InlineData("alice@", false)]
    [InlineData("a@b@c", false)]
    [InlineData("ali ce@contoso.com", false)]
    public void ValidateUserPrincipalName_RequiresLocalAtDomain(string name, bool valid)
    {
        string? problem = UsersRules.ValidateUserPrincipalName(name);
        Assert.Equal(valid, problem is null);
        if (!valid)
        {
            Assert.Contains(name, problem, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData("alice@contoso.com", "alice")]
    [InlineData("Alice.Smith@contoso.com", "alice-smith")]
    [InlineData("alice_gmail.com#EXT#@contoso.onmicrosoft.com", "alice_gmail-com-ext")]
    [InlineData("---@contoso.com", "user")]
    [InlineData("no-at-sign", "no-at-sign")]
    public void DeriveMailNickname_SanitisesTheLocalPart_AndTrimsDashes(string upn, string expected)
    {
        Assert.Equal(expected, UsersRules.DeriveMailNickname(upn));
    }

    [Fact]
    public void LocalPart_IsEverythingBeforeTheAt()
    {
        Assert.Equal("alice_gmail.com#EXT#", UsersRules.LocalPart("alice_gmail.com#EXT#@contoso.onmicrosoft.com"));
        Assert.Equal("x", UsersRules.LocalPart("x"));
    }

    [Fact]
    public void TaintSecrets_ReplacesPlainLeavesAtSecurePaths_OnlyOnce()
    {
        var properties = (JsonObject)JsonNode.Parse("""{ "displayName": "A", "passwordProfile": { "password": "Plain-1", "forceChangePasswordNextSignIn": true } }""")!;
        var paths = new List<JsonPath> { JsonPath.Of("passwordProfile", "password"), JsonPath.Of("missing", "leaf"), JsonPath.Root };

        UsersRules.TaintSecrets(properties, paths);

        var leaf = properties["passwordProfile"]!["password"];
        Assert.True(SecretJson.IsSecret(leaf, out var secret));
        Assert.Equal("Plain-1", secret.Value);
        Assert.Equal("\"***\"", leaf!.ToJsonString());
        Assert.True(properties["passwordProfile"]!["forceChangePasswordNextSignIn"]!.GetValue<bool>());

        UsersRules.TaintSecrets(properties, paths);
        Assert.True(SecretJson.IsSecret(properties["passwordProfile"]!["password"], out var again));
        Assert.Equal("Plain-1", again.Value);

        var nullPassword = (JsonObject)JsonNode.Parse("""{ "passwordProfile": { "password": null } }""")!;
        UsersRules.TaintSecrets(nullPassword, paths);
        Assert.False(SecretJson.IsSecret(nullPassword["passwordProfile"]!["password"]));
    }

    [Fact]
    public void HasPassword_IsTrueForSecretsAndNonEmptyStrings()
    {
        Assert.True(UsersRules.HasPassword((JsonObject)JsonNode.Parse("""{ "passwordProfile": { "password": "x" } }""")!));
        Assert.True(UsersRules.HasPassword(new JsonObject { ["passwordProfile"] = new JsonObject { ["password"] = SecretJson.CreateString("x") } }));
        Assert.False(UsersRules.HasPassword((JsonObject)JsonNode.Parse("""{ "passwordProfile": { "password": "" } }""")!));
        Assert.False(UsersRules.HasPassword((JsonObject)JsonNode.Parse("""{ "passwordProfile": { "password": null } }""")!));
        Assert.False(UsersRules.HasPassword((JsonObject)JsonNode.Parse("""{ "passwordProfile": { } }""")!));
        Assert.False(UsersRules.HasPassword(null));
    }

    [Fact]
    public void GeneratePassword_HasEveryClass_TheRightLength_NoAmbiguousCharacters_AndIsUnique()
    {
        var passwords = Enumerable.Range(0, 50).Select(_ => UsersRules.GeneratePassword()).ToList();

        Assert.All(passwords, p =>
        {
            Assert.Equal(UsersRules.GeneratedPasswordLength, p.Length);
            Assert.Equal(24, p.Length);
            Assert.Contains(p, char.IsUpper);
            Assert.Contains(p, char.IsLower);
            Assert.Contains(p, char.IsDigit);
            Assert.Contains(p, c => !char.IsLetterOrDigit(c));
            Assert.DoesNotContain(p, c => c is '0' or 'O' or '1' or 'l' or 'I');
            Assert.DoesNotContain(p, char.IsWhiteSpace);
        });
        Assert.Equal(passwords.Count, passwords.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void DeclaredNameConflict_IsCaseSensitive_AndIgnoresSecretOrAbsentValues()
    {
        Assert.Null(UsersRules.DeclaredNameConflict((JsonObject)JsonNode.Parse("""{ "userPrincipalName": "alice@contoso.com" }""")!, "alice@contoso.com", "u"));
        string? problem = UsersRules.DeclaredNameConflict((JsonObject)JsonNode.Parse("""{ "userPrincipalName": "other@contoso.com" }""")!, "alice@contoso.com", "u");
        Assert.NotNull(problem);
        Assert.Contains("NameMappedPropertyConflict", problem, StringComparison.Ordinal);
        Assert.NotNull(UsersRules.DeclaredNameConflict((JsonObject)JsonNode.Parse("""{ "userPrincipalName": "ALICE@contoso.com" }""")!, "alice@contoso.com", "u"));
        Assert.Null(UsersRules.DeclaredNameConflict(new JsonObject(), "alice@contoso.com", "u"));
        Assert.Null(UsersRules.DeclaredNameConflict(new JsonObject { ["userPrincipalName"] = SecretJson.CreateString("x") }, "alice@contoso.com", "u"));
    }

    [Fact]
    public void Defaults_FillMailNicknameForceChangeAndAccountEnabled_WithoutOverriding()
    {
        var target = new JsonObject();
        UsersRules.ApplyCreateDefaults(target, "alice@contoso.com");
        UsersRules.ApplyDesiredDefaults(target);
        Assert.Equal("""{"mailNickname":"alice","passwordProfile":{"forceChangePasswordNextSignIn":true},"accountEnabled":true}""", target.ToJsonString());

        var declared = (JsonObject)JsonNode.Parse("""{ "mailNickname": "custom", "passwordProfile": { "forceChangePasswordNextSignIn": false }, "accountEnabled": false }""")!;
        UsersRules.ApplyCreateDefaults(declared, "alice@contoso.com");
        UsersRules.ApplyDesiredDefaults(declared);
        Assert.Equal("custom", declared["mailNickname"]!.GetValue<string>());
        Assert.False(declared["passwordProfile"]!["forceChangePasswordNextSignIn"]!.GetValue<bool>());
        Assert.False(declared["accountEnabled"]!.GetValue<bool>());
    }

    [Fact]
    public void BodyCleanups_RemoveServerAssigned_NeverPatched_AndMergeWritten()
    {
        var create = (JsonObject)JsonNode.Parse("""{ "id": "x", "displayName": "A" }""")!;
        UsersRules.RemoveServerAssigned(create);
        Assert.Equal("""{"displayName":"A"}""", create.ToJsonString());

        var patch = (JsonObject)JsonNode.Parse("""{ "Id": "x", "UserPrincipalName": "a@b.c", "passwordProfile": { "password": "p" }, "jobTitle": "Dev" }""")!;
        UsersRules.RemoveNeverPatched(patch);
        Assert.Equal("""{"jobTitle":"Dev"}""", patch.ToJsonString());

        var live = (JsonObject)JsonNode.Parse("""{ "id": "x", "displayName": "Live" }""")!;
        var written = (JsonObject)JsonNode.Parse("""{ "displayName": "Written", "jobTitle": "Dev", "passwordProfile": { "password": "p" } }""")!;
        UsersRules.MergeWritten(live, written);
        Assert.Equal("""{"id":"x","displayName":"Live","jobTitle":"Dev"}""", live.ToJsonString());
    }

    [Fact]
    public void UserTypeChangeReason_RendersBothValues()
    {
        string reason = UsersRules.UserTypeChangeReason(JsonValue.Create("Member"), JsonValue.Create("Guest"));
        Assert.StartsWith("userType changes", reason, StringComparison.Ordinal);
        Assert.Contains("Member", reason, StringComparison.Ordinal);
        Assert.Contains("Guest", reason, StringComparison.Ordinal);
    }

    // ---- licensing ----

    [Fact]
    public void DirectAssignments_KeepOnlyStatesWithoutAssignedByGroup_WithPartNumbersFromAssignedLicenses()
    {
        var raw = (JsonObject)JsonNode.Parse("""
            { "assignedLicenses": [ { "skuId": "6fd2c87f-b296-42f0-b197-1e91e994b900", "skuPartNumber": "ENTERPRISEPACK", "disabledPlans": [] }, { "skuId": "22222222-2222-2222-2222-222222222222", "disabledPlans": [] } ],
              "licenseAssignmentStates": [
                { "skuId": "6fd2c87f-b296-42f0-b197-1e91e994b900", "disabledPlans": ["p1"], "assignedByGroup": null },
                { "skuId": "6fd2c87f-b296-42f0-b197-1e91e994b900", "disabledPlans": [], "assignedByGroup": "33333333-3333-3333-3333-333333333333" },
                { "skuId": "22222222-2222-2222-2222-222222222222", "disabledPlans": [], "assignedByGroup": "33333333-3333-3333-3333-333333333333" },
                { "disabledPlans": [] } ] }
            """)!;

        var direct = UsersLicensing.DirectAssignments(raw);

        Assert.Equal("""[{"skuId":"6fd2c87f-b296-42f0-b197-1e91e994b900","skuPartNumber":"ENTERPRISEPACK","disabledPlans":["p1"]}]""", direct.ToJsonString());
    }

    [Fact]
    public void DirectAssignments_WithoutStates_TreatEveryAssignedLicenseAsDirect()
    {
        var raw = (JsonObject)JsonNode.Parse("""{ "assignedLicenses": [ { "skuId": "6fd2c87f-b296-42f0-b197-1e91e994b900", "disabledPlans": [] } ] }""")!;
        Assert.Equal("""[{"skuId":"6fd2c87f-b296-42f0-b197-1e91e994b900","disabledPlans":[]}]""", UsersLicensing.DirectAssignments(raw).ToJsonString());
        Assert.Empty(UsersLicensing.DirectAssignments(null));
        Assert.Empty(UsersLicensing.DirectAssignments(new JsonObject()));
    }

    [Fact]
    public void MissingUsageLocation_DeclaredValueDecides_OtherwiseTheLiveOne()
    {
        var live = (JsonObject)JsonNode.Parse("""{ "usageLocation": "GB" }""")!;
        Assert.False(UsersLicensing.MissingUsageLocation((JsonObject)JsonNode.Parse("""{ "usageLocation": "GB" }""")!, null));
        Assert.True(UsersLicensing.MissingUsageLocation((JsonObject)JsonNode.Parse("""{ "usageLocation": null }""")!, live));
        Assert.False(UsersLicensing.MissingUsageLocation(new JsonObject(), live));
        Assert.True(UsersLicensing.MissingUsageLocation(new JsonObject(), null));
        Assert.True(UsersLicensing.MissingUsageLocation(null, (JsonObject)JsonNode.Parse("""{ "usageLocation": null }""")!));
    }
}
