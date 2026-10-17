using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The password credentials provider end to end: one-time secrets, rotation, expiry, deletes, external parents, hygiene.</summary>
public sealed partial class PasswordCredentialsProviderTests
{
    private const string AppType = ApplicationsProvider.ResourceType;
    private const string SecretType = PasswordCredentialsProvider.ResourceType;
    private const string AppName = "test-api";
    private const string SecretName = "ci-secret";
    private const string SecretId = "/providers/Microsoft.Entra/applications/test-api/passwordCredentials/ci-secret";
    private const string AppId = "/providers/Microsoft.Entra/applications/test-api";

    private const string Outputs = """
        "ciSecret": { "type": "securestring", "value": "[reference(resourceId('Microsoft.Entra/applications/passwordCredentials', 'test-api', 'ci-secret')).secretText]" },
        "ciKeyId": { "type": "string", "value": "[reference(resourceId('Microsoft.Entra/applications/passwordCredentials', 'test-api', 'ci-secret')).keyId]" }
        """;

    private static string App(string nested = "")
    {
        return """{ "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "test-api", "properties": { "displayName": "Test API" }""" + nested + " }";
    }

    private static string Secret(string properties = "", bool flat = true, string name = SecretName)
    {
        string type = flat ? SecretType : "passwordCredentials";
        string fullName = flat ? AppName + "/" + name : name;
        return "{ \"type\": \"" + type + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + fullName + "\", \"properties\": { " + properties + " } }";
    }

    private static string Template(string secretProperties = "")
    {
        return App() + ", " + Secret(secretProperties);
    }

    private static ProviderHost SeededHost(DateTimeOffset endDateTime, out string appId, out string keyId)
    {
        var host = new ProviderHost();
        keyId = Guid.NewGuid().ToString("D");
        string seededKeyId = keyId;
        var app = host.Directory.AddApplication(AppName, "Test API", configure: a => a["passwordCredentials"] = new JsonArray(new JsonObject
        {
            ["keyId"] = seededKeyId,
            ["displayName"] = SecretName,
            ["startDateTime"] = endDateTime.AddDays(-170).ToString("O", CultureInfo.InvariantCulture),
            ["endDateTime"] = endDateTime.ToString("O", CultureInfo.InvariantCulture),
            ["hint"] = "see",
            ["customKeyIdentifier"] = null,
        }));
        appId = app["id"]!.GetValue<string>();
        return host;
    }

    [Fact]
    public void Discovery_RegistersTheProvider()
    {
        var registry = ProviderDiscovery.CreateRegistry(EArm.Core.Types.ResourceTypeCatalog.Default);
        Assert.IsType<PasswordCredentialsProvider>(registry.Get(SecretType));
    }

    [Fact]
    public async Task Create_CallsAddPassword_RevealsTheSecretOnlyInSecureOutputs_AndRecordsNoSecret()
    {
        using var directory = new TestDirectory();
        string secureFile = directory.File("secure-outputs.json");
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Template(), Outputs), secureOutputsFile: secureFile);

        Check.Succeeded(run);
        string appId = host.IdOf("/applications", "uniqueName", AppName);
        var writes = run.Writes;
        Assert.Equal(2, writes.Count);
        Assert.Equal("PATCH", writes[0].Method);
        Assert.Contains("applications(uniqueName='test-api')", writes[0].Path, StringComparison.Ordinal);
        var add = writes[1];
        Assert.Equal("POST", add.Method);
        Assert.Equal("/applications/" + appId + "/addPassword", add.Path);
        Assert.Equal(RetryHint.ExpectReplicationLag, add.Hint);
        var body = Check.Body(add);
        var credential = Assert.IsType<JsonObject>(body["passwordCredential"]);
        Assert.Equal(["passwordCredential"], body.Select(p => p.Key).ToList());
        Assert.Equal(["displayName", "endDateTime"], credential.Select(p => p.Key).ToList());
        Assert.Equal(SecretName, credential["displayName"]!.GetValue<string>());
        var end = DateTimeOffset.Parse(credential["endDateTime"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        Assert.True((end - DateTimeOffset.UtcNow.AddDays(180)).Duration() < TimeSpan.FromMinutes(1), "endDateTime defaults to now + 180 days");
        var read = Assert.Single(run.Requests, r => r.Method == "GET" && r.Path.StartsWith("/applications/" + appId, StringComparison.Ordinal));
        Assert.Equal("id,passwordCredentials", read.UrlParameters!["$select"]);

        Assert.Equal("\"***\"", run.Output("ciSecret")!.ToJsonString());
        string? revealed = run.SecureOutput("ciSecret")?.GetValue<string>();
        Assert.NotNull(revealed);
        Assert.StartsWith("fake-secret-", revealed, StringComparison.Ordinal);
        Assert.Equal(secureFile, run.Executed.SecureOutputsFile);
        Assert.Contains(revealed, File.ReadAllText(secureFile), StringComparison.Ordinal);
        string keyId = run.Output("ciKeyId")!.GetValue<string>();
        Assert.Equal(run.Op(SecretName).GraphId, keyId);
        Assert.Empty(run.Executed.Diagnostics);
        Assert.Equal(keyId, run.GraphIdOf(SecretId));
    }

    [Fact]
    public async Task Rerun_IsNoChange_TheSecretOutputIsNull_WithExactlyOneWarning()
    {
        using var directory = new TestDirectory();
        var host = new ProviderHost();
        var first = await host.CreateAsync(host.Resources(Template(), Outputs));
        Check.Succeeded(first);
        string secureFile = directory.File("secure-outputs-rerun.json");

        var rerun = await host.CreateAsync(host.Resources(Template(), Outputs), secureOutputsFile: secureFile);

        Check.Succeeded(rerun);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
        Assert.True(rerun.Executed.Outputs.ContainsKey("ciSecret"));
        Assert.Null(rerun.Output("ciSecret"));
        Assert.False(rerun.Executed.SecureOutputs.ContainsKey("ciSecret"));
        Assert.False(File.Exists(secureFile), "a rerun without secure values leaves the secure outputs file alone (review finding E-08), it does not write {}");
        var warning = Assert.Single(rerun.Executed.Diagnostics);
        Assert.Equal(EngineDiagnosticCodes.SecretNotAvailableOnRerun, warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains(SecretId, warning.Message, StringComparison.Ordinal);
        Assert.Equal(first.Op(SecretName).GraphId, rerun.Output("ciKeyId")!.GetValue<string>());
        string appId = host.IdOf("/applications", "uniqueName", AppName);
        Assert.Contains(rerun.Requests, r => r.Method == "GET" && r.Path == "/applications/" + appId && r.UrlParameters!["$select"] == "id,passwordCredentials");
    }

    [Fact]
    public async Task WhatIf_OnARerun_IsNoChange_AndNeverCallsAddPassword()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(Template(), Outputs));

        var whatIf = await host.WhatIfAsync(host.Resources(Template(), Outputs));

        Assert.Equal(2, whatIf.Plan.Changes.Count);
        Assert.All(whatIf.Plan.Changes, c => Assert.Equal(ChangeType.NoChange, c.Type));
        Assert.All(whatIf.Plan.Changes, c => Assert.False(c.HasError));
        Check.NoWrites(whatIf);
        Assert.DoesNotContain(whatIf.Requests, r => r.Path.Contains("addPassword", StringComparison.OrdinalIgnoreCase));
        var row = whatIf.ChangeOfType(SecretType);
        string before = row.Before!.ToJsonString();
        Assert.DoesNotContain("secretText", before, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("fake-secret", before, StringComparison.OrdinalIgnoreCase);

        var fresh = await new ProviderHost().WhatIfAsync(host.Resources(Template(), Outputs));
        Assert.Equal(2, fresh.Plan.Changes.Count);
        Assert.All(fresh.Plan.Changes, c => Assert.Equal(ChangeType.Create, c.Type));
        Check.NoWrites(fresh);
    }

    [Fact]
    public async Task NestedAndFlatTemplates_ProduceIdenticalRequests()
    {
        var nestedHost = new ProviderHost();
        var flatHost = new ProviderHost();

        var nested = await nestedHost.CreateAsync(nestedHost.Resources(App(nested: ", \"resources\": [ " + Secret(flat: false) + " ]"), Outputs));
        var flat = await flatHost.CreateAsync(flatHost.Resources(Template(), Outputs));

        Check.Succeeded(nested);
        Check.Succeeded(flat);
        Assert.Equal(Normalize(flatHost, flat), Normalize(nestedHost, nested));
        Assert.Equal(flat.Executed.Operations.Select(o => o.Id.Id), nested.Executed.Operations.Select(o => o.Id.Id));

        static List<string> Normalize(ProviderHost host, Run run)
        {
            string appId = host.IdOf("/applications", "uniqueName", AppName);
            return run.Requests.Select(r =>
            {
                string body = r.Body is null ? string.Empty : " " + string.Join(",", ((JsonObject)r.Body).Select(p => p.Key + (p.Value is JsonObject o ? "{" + string.Join(",", o.Select(q => q.Key)) + "}" : string.Empty)));
                return (r + " [" + r.Hint + "]" + body).Replace(appId, "{appId}", StringComparison.Ordinal);
            }).ToList();
        }
    }

    [Fact]
    public async Task TheSecret_NeverReachesState_WhatIfRows_OrOutputsJson_AndTheLiveObjectIsMarked()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Template(), Outputs));

        Check.Succeeded(run);
        string secret = run.SecureOutput("ciSecret")!.GetValue<string>();
        Assert.Contains("***", run.Executed.Outputs.ToJsonString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, run.Executed.Outputs.ToJsonString(), StringComparison.Ordinal);
        Assert.All(run.Executed.Changes, c =>
        {
            Assert.DoesNotContain(secret, c.After?.ToJsonString() ?? string.Empty, StringComparison.Ordinal);
            Assert.DoesNotContain(secret, c.Before?.ToJsonString() ?? string.Empty, StringComparison.Ordinal);
        });
        Assert.Contains("ci-secret", run.ChangeOfType(SecretType).After!.ToJsonString(), StringComparison.Ordinal);
        Assert.False(host.Logs.Contains(secret));

        Assert.True(run.Plan.Resolver.TryGetLive(ResourceIdentifier.Create(SecretType, AppName + "/" + SecretName), out var live, out _));
        Assert.Contains("\"secretText\":\"***\"", live.ToJsonString(), StringComparison.Ordinal);
        Assert.True(live["secretTextAvailable"]!.GetValue<bool>());
    }

    [Fact]
    public void LiveSecrets_ContainsNull_FindsNestedNulls_ButNotSecretOrUnknownLeaves()
    {
        Assert.True(LiveSecrets.ContainsNull(null));
        Assert.True(LiveSecrets.ContainsNull(JsonNode.Parse("""{ "a": { "b": [ 1, { "c": null } ] } }""")));
        Assert.False(LiveSecrets.ContainsNull(JsonNode.Parse("""{ "a": { "b": [ 1, { "c": "x" } ] } }""")));
        Assert.False(LiveSecrets.ContainsNull(new JsonObject { ["s"] = EArm.Core.Secrets.SecretJson.CreateString("x") }));
        Assert.True(LiveSecrets.ContainsNull(new JsonArray(JsonValue.Create("x"), null)));
    }
}
