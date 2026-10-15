using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Secrets;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Failure and edge paths of the password credentials provider: malformed <c>addPassword</c> answers (and the proof the
/// secret in them never reaches a message), normalisation of a sparse answer, rotation and deletion over several
/// same-name credentials, and the key / parent errors.
/// </summary>
public sealed class PasswordCredentialFailureTests
{
    private const string AppType = ApplicationsProvider.ResourceType;
    private const string SecretType = PasswordCredentialsProvider.ResourceType;
    private const string AppName = "test-api";
    private const string SecretName = "ci-secret";
    private const string LeakCanary = "S3CR3T-CANARY-9f2b";

    private static string Template(string secretProperties = "")
    {
        return """{ "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "test-api", "properties": { "displayName": "Test API" } }"""
            + ", { \"type\": \"" + SecretType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"test-api/ci-secret\", \"properties\": { " + secretProperties + " } }";
    }

    private static async Task<(ProviderHost Host, string AppId, ResourceInstance Instance, PasswordCredentialsProvider Provider)> ArrangeAsync(Action<JsonObject>? configureApp = null)
    {
        var host = new ProviderHost();
        string appId = host.Directory.AddApplication(AppName, "Test API", configure: configureApp)["id"]!.GetValue<string>();
        var run = await host.WhatIfAsync(host.Resources(Template()));
        var instance = run.Plan.Instances.Single(i => i.Type == SecretType);
        instance.ParentGraphId = appId;
        return (host, appId, instance, host.Provider<PasswordCredentialsProvider>(SecretType));
    }

    private static JsonObject Seeded(string keyId, string displayName, DateTimeOffset end)
    {
        return new JsonObject
        {
            ["keyId"] = keyId,
            ["displayName"] = displayName,
            ["startDateTime"] = end.AddDays(-30).ToString("O", CultureInfo.InvariantCulture),
            ["endDateTime"] = end.ToString("O", CultureInfo.InvariantCulture),
            ["hint"] = "see",
            ["customKeyIdentifier"] = null,
        };
    }

    // ---- malformed addPassword answers ----

    [Theory]
    [InlineData("no-body", "returned no credential object")]
    [InlineData("no-secret", "returned no secretText")]
    [InlineData("no-keyid", "returned no keyId")]
    public async Task AddPassword_MalformedAnswers_FailWithoutLeakingTheSecret(string shape, string expected)
    {
        var (host, _, instance, provider) = await ArrangeAsync();
        var graph = new InterceptingGraphClient(host.Fake)
        {
            PostOverride = (request, _) => !request.Path.EndsWith("/addPassword", StringComparison.Ordinal)
                ? null
                : shape switch
                {
                    "no-body" => new GraphResponse(HttpStatusCode.OK, null),
                    "no-secret" => new GraphResponse(HttpStatusCode.OK, new JsonObject { ["keyId"] = Guid.NewGuid().ToString("D"), ["displayName"] = SecretName }),
                    _ => new GraphResponse(HttpStatusCode.OK, new JsonObject { ["secretText"] = LeakCanary, ["displayName"] = SecretName }),
                },
        };

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(instance, graph, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, ex.Code);
        Assert.Contains(expected, ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(LeakCanary, ex.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(host.Logs.Lines, line => line.Contains(LeakCanary, StringComparison.Ordinal));
    }

    [Fact]
    public async Task AddPassword_ASparseAnswer_GetsTheRequestedEndDate_AndTheCredentialName_AndTheSecretStaysASecretLeaf()
    {
        var (host, _, instance, provider) = await ArrangeAsync();
        string keyId = Guid.NewGuid().ToString("D");
        var graph = new InterceptingGraphClient(host.Fake)
        {
            PostOverride = (request, _) => request.Path.EndsWith("/addPassword", StringComparison.Ordinal)
                ? new GraphResponse(HttpStatusCode.OK, new JsonObject { ["keyId"] = keyId, ["secretText"] = LeakCanary, ["displayName"] = "something else" })
                : null,
        };

        var applied = await provider.CreateAsync(instance, graph, ProviderHost.Ct);

        Assert.Equal(keyId, applied.GraphId);
        Assert.Null(applied.Extra);
        Assert.False(applied.Live.ContainsKey("secretText") && applied.Live["secretText"] is JsonValue plain && plain.TryGetValue<string>(out _), "the live view carries the secret only as a secret leaf");
        Assert.True(SecretJson.IsSecret(applied.Live["secretText"]));
        Assert.Equal(SecretName, applied.Live["displayName"]!.GetValue<string>());
        Assert.NotNull(applied.Live["endDateTime"]);
    }

    // ---- create preconditions ----

    [Fact]
    public async Task Create_WhenTheParentApplicationDoesNotExist_Fails_AndAnInstanceWithoutAParentIsRejected()
    {
        var (host, _, instance, provider) = await ArrangeAsync();
        instance.ParentGraphId = null;
        host.Directory.SoftDelete(host.IdOf("/applications", "uniqueName", AppName));

        var missing = await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(instance, host.Fake, ProviderHost.Ct));
        Assert.Contains("does not exist, so no password credential can be added", missing.Message, StringComparison.Ordinal);

        var orphanId = instance.Id;
        instance.Id = ResourceIdentifier.Create(SecretType, "no-parent-here");
        try
        {
            var orphan = await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(instance, host.Fake, ProviderHost.Ct));
            Assert.Equal(EngineDiagnosticCodes.ResourceKeyUnresolved, orphan.Code);
        }
        finally
        {
            instance.Id = orphanId;
        }
    }

    [Fact]
    public async Task Create_WithPropertiesTheRulesRefuse_IsAProviderException_BeforeAnyWrite()
    {
        var (host, _, instance, provider) = await ArrangeAsync();
        instance.Properties = (JsonObject)JsonNode.Parse("{ \"endDateTime\": \"not-a-date\" }")!;
        var before = host.Fake.Writes.Count;

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.CreateAsync(instance, host.Fake, ProviderHost.Ct));
        instance.Properties = null;
        var unevaluated = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.CreateAsync(instance, host.Fake, ProviderHost.Ct));

        Assert.Contains("endDateTime", ex.Message, StringComparison.Ordinal);
        Assert.Contains("must be evaluated", unevaluated.Message, StringComparison.Ordinal);
        Assert.Equal(before, host.Fake.Writes.Count);
    }

    // ---- read ----

    [Fact]
    public async Task Read_NeedsADisplayNameAndAParent_AndAnAbsentParentIsNull()
    {
        var host = new ProviderHost();
        var provider = host.Provider<PasswordCredentialsProvider>(SecretType);
        var parent = new ResourceKey { Type = AppType, Values = new Dictionary<string, string> { ["uniqueName"] = AppName } };

        var noName = await Assert.ThrowsAsync<ProviderException>(() => provider.ReadAsync(new ResourceKey { Type = SecretType, Values = new Dictionary<string, string>(), ParentKey = parent }, host.Fake, ProviderHost.Ct));
        var noParent = await Assert.ThrowsAsync<ProviderException>(() => provider.ReadAsync(new ResourceKey { Type = SecretType, Values = new Dictionary<string, string> { ["displayName"] = SecretName } }, host.Fake, ProviderHost.Ct));
        var absent = await provider.ReadAsync(new ResourceKey { Type = SecretType, Values = new Dictionary<string, string> { ["displayName"] = SecretName }, ParentKey = parent }, host.Fake, ProviderHost.Ct);

        Assert.Equal(EngineDiagnosticCodes.ResourceKeyUnresolved, noName.Code);
        Assert.Contains("carries no credential displayName", noName.Message, StringComparison.Ordinal);
        Assert.Contains("parent application is not known", noParent.Message, StringComparison.Ordinal);
        Assert.Null(absent);
    }

    // ---- plan ----

    [Fact]
    public async Task Plan_WithADeployTimeValue_IsUnknownOnAnExistingCredential_AndAPlainCreateOtherwise()
    {
        string keyId = Guid.NewGuid().ToString("D");
        var (host, _, instance, provider) = await ArrangeAsync(app => app["passwordCredentials"] = new JsonArray(Seeded(keyId, SecretName, DateTimeOffset.UtcNow.AddDays(300))));
        instance.Properties = (JsonObject)JsonNode.Parse("{ }")!;
        instance.HasUnknownProperties = true;
        var existing = await provider.ReadAsync(provider.ResolveKey(instance), host.Fake, ProviderHost.Ct);
        Assert.NotNull(existing);

        var change = provider.Plan(instance, existing, PlanOptions.Default);
        var fresh = provider.Plan(instance, null, PlanOptions.Default);

        Assert.True(change.IsUnknown);
        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.Equal(keyId, change.GraphId);
        Assert.Contains("only known at deployment time", change.Reasons.Single(), StringComparison.Ordinal);
        Assert.Equal(ChangeType.Create, fresh.Type);
    }

    // ---- rotation and deletion over several same-name credentials ----

    [Fact]
    public async Task Rotation_RemovesEverySameNameCredentialTheReadSaw()
    {
        var host = new ProviderHost();
        var end = DateTimeOffset.UtcNow.AddDays(300);
        string older = Guid.NewGuid().ToString("D");
        string newer = Guid.NewGuid().ToString("D");
        string other = Guid.NewGuid().ToString("D");
        // An untagged leftover (older EARM) and the newest one, tagged 'old': the declared tag differs from the live one, so the secret rotates.
        string appId = host.Directory.AddApplication(AppName, "Test API", configure: a => a["passwordCredentials"] = new JsonArray(
            Seeded(older, SecretName, end.AddDays(-20)),
            Seeded(newer, SecretName + "#old", end),
            Seeded(other, "unrelated", end)))["id"]!.GetValue<string>();

        var run = await host.CreateAsync(host.Resources(Template("\"rotationTag\": \"rotate-now\"")));

        Check.Succeeded(run);
        var removed = run.Writes.Where(w => w.Path.EndsWith("/removePassword", StringComparison.Ordinal)).Select(w => Check.Body(w)["keyId"]!.GetValue<string>()).ToList();
        Assert.Contains(older, removed);
        Assert.Contains(newer, removed);
        Assert.DoesNotContain(other, removed);
        var remaining = host.Directory.FindById(appId)!["passwordCredentials"]!.AsArray().Select(c => c!["displayName"]!.GetValue<string>()).Order(StringComparer.Ordinal).ToList();
        Assert.Equal(["ci-secret#rotate-now", "unrelated"], remaining);
    }

    [Fact]
    public async Task DeleteAll_RemovesEverySameNameCredential_ButNotOthers()
    {
        var host = new ProviderHost();
        var end = DateTimeOffset.UtcNow.AddDays(300);
        string duplicate = Guid.NewGuid().ToString("D");
        string newest = Guid.NewGuid().ToString("D");
        string unrelated = Guid.NewGuid().ToString("D");
        host.Directory.AddApplication(AppName, "Test API", configure: a => a["passwordCredentials"] = new JsonArray(
            Seeded(duplicate, SecretName, end.AddDays(-40)),
            Seeded(newest, SecretName, end),
            Seeded(unrelated, "unrelated", end)));
        Check.Succeeded(await host.CreateAsync(host.Resources(Template())));
        host.ClearRequests();

        // No adoption distinction any more: 'deployment delete' deletes what the template declares, found by key.
        var result = await host.DeleteDeclaredAsync(host.Resources(Template()));

        Assert.Equal(0, result.FailedCount);
        var removed = host.Fake.Writes.Where(w => w.Path.EndsWith("/removePassword", StringComparison.Ordinal)).Select(w => Check.Body(w)["keyId"]!.GetValue<string>()).ToList();
        Assert.Contains(duplicate, removed);
        Assert.Contains(newest, removed);
        Assert.DoesNotContain(unrelated, removed);
    }

    [Fact]
    public async Task Delete_WithoutAnyParentInformation_Throws()
    {
        var host = new ProviderHost();
        var provider = host.Provider<PasswordCredentialsProvider>(SecretType);
        var entry = new EArm.Engine.State.StateEntry
        {
            Id = ResourceIdentifier.Create(SecretType, "flat-name-without-parent"),
            Type = SecretType,
            ApiVersion = ProviderHost.ApiVersion,
            GraphId = Guid.NewGuid().ToString("D"),
        };

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.DeleteAsync(DeleteTarget.KeyOf(entry), DeleteTarget.LiveOf(entry), host.Fake, ProviderHost.Ct));

        Assert.Equal(EngineDiagnosticCodes.ResourceKeyUnresolved, ex.Code);
    }
}
