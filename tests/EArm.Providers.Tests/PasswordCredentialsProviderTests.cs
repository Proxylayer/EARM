using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The password credentials provider end to end: one-time secrets, rotation, expiry, deletes, external parents, hygiene.</summary>
public sealed class PasswordCredentialsProviderTests
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

        var entry = run.Entry(SecretId);
        Assert.Equal(keyId, entry.GraphId);
        Assert.Equal(appId, entry.ParentGraphId);
        Assert.Equal(SecretName, entry.Key["displayName"]);
        Assert.Null(entry.Extra);
        Assert.DoesNotContain(revealed, host.StateDocument, StringComparison.Ordinal);
        Assert.DoesNotContain("secretText", host.StateDocument, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(AppId, entry.DependsOn);
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
    public async Task RotationTagChange_AddsThenRemoves_AndRevealsTheNewSecret()
    {
        var host = new ProviderHost();
        var first = await host.CreateAsync(host.Resources(Template("\"rotationTag\": \"2026Q3\""), Outputs));
        Check.Succeeded(first);
        string oldKeyId = first.Op(SecretName).GraphId!;
        Assert.Equal("ci-secret#2026Q3", Check.Body(first.Writes[1])["passwordCredential"]!["displayName"]!.GetValue<string>());
        string appId = host.IdOf("/applications", "uniqueName", AppName);

        var same = await host.CreateAsync(host.Resources(Template("\"rotationTag\": \"2026Q3\""), Outputs));
        Assert.Equal(ChangeType.NoChange, same.Op(SecretName).ChangeType);
        Check.NoWrites(same);

        var whatIf = await host.WhatIfAsync(host.Resources(Template("\"rotationTag\": \"2026Q4\""), Outputs));
        var row = whatIf.ChangeOfType(SecretType);
        Assert.Equal(ChangeType.Modify, row.Type);
        Assert.False(row.HasError);
        var delta = Assert.Single(row.Deltas, d => d.Path.ToString() == "properties.rotationTag");
        Assert.Equal(PropertyChangeType.Modify, delta.Kind);
        Assert.Equal("2026Q3", delta.Before!.GetValue<string>());
        Assert.Equal("2026Q4", delta.After!.GetValue<string>());
        Check.NoWrites(whatIf);

        var rotated = await host.CreateAsync(host.Resources(Template("\"rotationTag\": \"2026Q4\""), Outputs));
        Check.Succeeded(rotated);
        Assert.Equal(ChangeType.Modify, rotated.Op(SecretName).ChangeType);
        Assert.Equal(ChangeMode.Patch, rotated.Op(SecretName).Mode);
        var writes = rotated.Writes;
        Assert.Equal(2, writes.Count);
        Assert.Equal("POST", writes[0].Method);
        Assert.Equal("/applications/" + appId + "/addPassword", writes[0].Path);
        Assert.Equal("POST", writes[1].Method);
        Assert.Equal("/applications/" + appId + "/removePassword", writes[1].Path);
        Assert.Equal(oldKeyId, Check.Body(writes[1])["keyId"]!.GetValue<string>());
        Assert.All(writes, w => Assert.Equal(RetryHint.Default, w.Hint));
        string newKeyId = rotated.Op(SecretName).GraphId!;
        Assert.NotEqual(oldKeyId, newKeyId);
        Assert.Equal(newKeyId, rotated.Entry(SecretId).GraphId);
        Assert.Equal("ci-secret#2026Q4", Check.Body(writes[0])["passwordCredential"]!["displayName"]!.GetValue<string>());
        Assert.StartsWith("fake-secret-", rotated.SecureOutput("ciSecret")!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Empty(rotated.Executed.Diagnostics);
        var live = host.Directory.Find("/applications", "uniqueName", AppName)!["passwordCredentials"]!.AsArray();
        Assert.Equal(newKeyId, Assert.Single(live)!["keyId"]!.GetValue<string>());
        Assert.Equal("ci-secret#2026Q4", live[0]!["displayName"]!.GetValue<string>());

        var untagged = await host.WhatIfAsync(host.Resources(Template(), Outputs));
        var untaggedRow = untagged.ChangeOfType(SecretType);
        Assert.Equal(ChangeType.Modify, untaggedRow.Type);
        Assert.Contains(untaggedRow.Deltas, d => d.Path.ToString() == "properties.rotationTag" && d.Kind == PropertyChangeType.Delete);
    }

    [Fact]
    public async Task SameTag_IsNoChange_WithoutAnyLocalState()
    {
        var host = new ProviderHost { TrackState = false };
        Check.Succeeded(await host.CreateAsync(host.Resources(Template("\"rotationTag\": \"2026Q3\""), Outputs)));
        Assert.Null(host.StateDocument);

        var whatIf = await host.WhatIfAsync(host.Resources(Template("\"rotationTag\": \"2026Q3\""), Outputs));
        var row = whatIf.ChangeOfType(SecretType);
        Assert.Equal(ChangeType.NoChange, row.Type);
        Assert.Equal("2026Q3", row.Before!["rotationTag"]!.GetValue<string>());
        Assert.Equal("ci-secret#2026Q3", row.Before!["displayName"]!.GetValue<string>());
        Assert.Equal("ci-secret#2026Q3", row.After!["displayName"]!.GetValue<string>());

        var rerun = await host.CreateAsync(host.Resources(Template("\"rotationTag\": \"2026Q3\""), Outputs));
        Assert.Equal(ChangeType.NoChange, rerun.Op(SecretName).ChangeType);
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task ChangedTag_Rotates_WithoutAnyLocalState()
    {
        var host = new ProviderHost { TrackState = false };
        var first = await host.CreateAsync(host.Resources(Template("\"rotationTag\": \"2026Q3\""), Outputs));
        string oldKeyId = first.Op(SecretName).GraphId!;

        var whatIf = await host.WhatIfAsync(host.Resources(Template("\"rotationTag\": \"2026Q4\""), Outputs));
        var row = whatIf.ChangeOfType(SecretType);
        Assert.Equal(ChangeType.Modify, row.Type);
        Assert.Contains(row.Reasons, r => r.Contains("rotationTag changed ('2026Q3' → '2026Q4')", StringComparison.Ordinal));

        var rotated = await host.CreateAsync(host.Resources(Template("\"rotationTag\": \"2026Q4\""), Outputs));
        Assert.Equal(ChangeType.Modify, rotated.Op(SecretName).ChangeType);
        Assert.Equal("ci-secret#2026Q4", Check.Body(rotated.Writes[0])["passwordCredential"]!["displayName"]!.GetValue<string>());
        Assert.Equal(oldKeyId, Check.Body(rotated.Writes[1])["keyId"]!.GetValue<string>());
        Assert.NotNull(rotated.SecureOutput("ciSecret"));
    }

    [Fact]
    public async Task LegacyUntaggedCredential_WithADeclaredTag_IsNotRotated_AndWarns()
    {
        var host = SeededHost(DateTimeOffset.UtcNow.AddDays(100), out _, out string seededKeyId);
        host.TrackState = false;

        var whatIf = await host.WhatIfAsync(host.Resources(Template("\"rotationTag\": \"2026Q4\""), Outputs));
        var row = whatIf.ChangeOfType(SecretType);
        Assert.Equal(ChangeType.NoChange, row.Type);
        var planned = Assert.Single(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.RotationTagNotStamped);
        Assert.Equal(DiagnosticSeverity.Warning, planned.Severity);
        Assert.Contains("'ci-secret#2026Q4'", planned.Message, StringComparison.Ordinal);

        var run = await host.CreateAsync(host.Resources(Template("\"rotationTag\": \"2026Q4\""), Outputs));
        Assert.Equal(ChangeType.NoChange, run.Op(SecretName).ChangeType);
        Assert.Equal(seededKeyId, run.Op(SecretName).GraphId);
        Check.NoWrites(run);
        Check.Diagnostic(run, EngineDiagnosticCodes.RotationTagNotStamped);
    }

    [Fact]
    public async Task SeveralSameBaseCredentials_TheOneWithTheDeclaredTagIsChosen()
    {
        var host = new ProviderHost { TrackState = false };
        var end = DateTimeOffset.UtcNow.AddDays(300);
        string matching = Guid.NewGuid().ToString("D");
        string newerOther = Guid.NewGuid().ToString("D");
        host.Directory.AddApplication(AppName, "Test API", configure: a => a["passwordCredentials"] = new JsonArray(
            Credential(matching, SecretName + "#q4", end.AddDays(-50)),
            Credential(newerOther, SecretName + "#q3", end)));

        var run = await host.CreateAsync(host.Resources(Template("\"rotationTag\": \"q4\""), Outputs));

        Assert.Equal(ChangeType.NoChange, run.Op(SecretName).ChangeType);
        Assert.Equal(matching, run.Op(SecretName).GraphId);
        Check.NoWrites(run);

        static JsonObject Credential(string keyId, string displayName, DateTimeOffset end) => new()
        {
            ["keyId"] = keyId,
            ["displayName"] = displayName,
            ["startDateTime"] = end.AddDays(-170).ToString("O", CultureInfo.InvariantCulture),
            ["endDateTime"] = end.ToString("O", CultureInfo.InvariantCulture),
            ["hint"] = "see",
        };
    }

    [Fact]
    public async Task TheTagSeparator_IsRejected_InALiteralNameOffline_AndInAComputedNameOrTag()
    {
        var host = new ProviderHost();

        var literal = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(host.Resources(App() + ", " + Secret(name: "ci#secret"))));
        var diagnostic = Assert.Single(literal.Diagnostics, d => d.Code == "InvalidResourceName");
        Assert.Contains("contains '#'", diagnostic.Message, StringComparison.Ordinal);

        var literalTag = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(host.Resources(Template("\"rotationTag\": \"a#b\""))));
        Assert.Contains(literalTag.Diagnostics, d => d.Code == "InvalidResourceName" && d.Message.Contains("rotationTag", StringComparison.Ordinal));

        var tooLong = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(host.Resources(Template("\"rotationTag\": \"" + new string('t', 250) + "\""))));
        Assert.Contains(tooLong.Diagnostics, d => d.Code == "InvalidResourceName" && d.Message.Contains("256", StringComparison.Ordinal));

        var computedTag = await host.WhatIfAsync(host.Resources(Template("\"rotationTag\": \"[concat('a', '#b')]\"")));
        var row = computedTag.ChangeOfType(SecretType);
        Assert.True(row.HasError);
        Assert.Contains("contains '#'", row.UnsupportedReason, StringComparison.Ordinal);

        var computedName = await host.CreateAsync(host.Resources(App() + ", { \"type\": \"" + SecretType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[concat('test-api/ci', '#secret')]\", \"properties\": { } }"));
        Assert.Equal(DeploymentStatus.Failed, computedName.Executed.Status);
        Assert.DoesNotContain(computedName.Writes, w => w.Path.EndsWith("/addPassword", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Expiry_WithinTheThreshold_Rotates_RelaxedThresholdAndFarExpiryDoNot()
    {
        var host = SeededHost(DateTimeOffset.UtcNow.AddDays(10), out string appId, out string seededKeyId);
        var whatIf = await host.WhatIfAsync(host.Resources(Template(), Outputs));
        var row = whatIf.ChangeOfType(SecretType);
        Assert.Equal(ChangeType.Modify, row.Type);
        Assert.Contains(row.Reasons, r => r.Contains("expires on", StringComparison.Ordinal) && r.Contains("fewer than rotateBeforeDays (30)", StringComparison.Ordinal));
        Assert.Contains(row.Deltas, d => d.Path.ToString() == "properties.endDateTime" && d.Kind == PropertyChangeType.Modify);

        var run = await host.CreateAsync(host.Resources(Template(), Outputs));
        Check.Succeeded(run);
        Assert.Equal(ChangeType.Modify, run.Op(SecretName).ChangeType);
        var writes = run.Writes;
        Assert.Equal(2, writes.Count);
        Assert.EndsWith("/addPassword", writes[0].Path, StringComparison.Ordinal);
        Assert.EndsWith("/removePassword", writes[1].Path, StringComparison.Ordinal);
        Assert.Equal(seededKeyId, Check.Body(writes[1])["keyId"]!.GetValue<string>());
        Assert.NotNull(run.SecureOutput("ciSecret"));
        var live = host.Directory.FindById(appId)!["passwordCredentials"]!.AsArray();
        Assert.NotEqual(seededKeyId, Assert.Single(live)!["keyId"]!.GetValue<string>());

        var relaxedHost = SeededHost(DateTimeOffset.UtcNow.AddDays(10), out _, out _);
        var relaxed = await relaxedHost.CreateAsync(relaxedHost.Resources(Template("\"rotateBeforeDays\": 5"), Outputs));
        Assert.Equal(ChangeType.NoChange, relaxed.Op(SecretName).ChangeType);
        Check.NoWrites(relaxed);
        Assert.Null(relaxed.Output("ciSecret"));
        Check.Diagnostic(relaxed, EngineDiagnosticCodes.SecretNotAvailableOnRerun);

        var fineHost = SeededHost(DateTimeOffset.UtcNow.AddDays(100), out _, out _);
        var fine = await fineHost.CreateAsync(fineHost.Resources(Template(), Outputs));
        Assert.Equal(ChangeType.NoChange, fine.Op(SecretName).ChangeType);
        Check.NoWrites(fine);
    }

    [Fact]
    public async Task ExpiredCredential_IsRotated_AndWhatIfSaysExpired()
    {
        var host = SeededHost(DateTimeOffset.UtcNow.AddDays(-1), out _, out string seededKeyId);

        var whatIf = await host.WhatIfAsync(host.Resources(Template(), Outputs));
        Assert.Contains(whatIf.ChangeOfType(SecretType).Reasons, r => r.Contains("expired on", StringComparison.Ordinal));

        var run = await host.CreateAsync(host.Resources(Template(), Outputs));
        Assert.Equal(ChangeType.Modify, run.Op(SecretName).ChangeType);
        Assert.Equal(2, run.Writes.Count);
        Assert.Equal(seededKeyId, Check.Body(run.Writes[1])["keyId"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeclaredEndDateTime_IsWrittenAsGiven_ComparedByInstant_AndTheDefaultStaysStable()
    {
        var host = new ProviderHost();
        var first = await host.CreateAsync(host.Resources(Template("\"endDateTime\": \"2028-01-01T00:00:00Z\""), Outputs));
        Check.Succeeded(first);
        Assert.Equal("2028-01-01T00:00:00Z", Check.Body(first.Writes[1])["passwordCredential"]!["endDateTime"]!.GetValue<string>());

        var sameOtherFormat = await host.CreateAsync(host.Resources(Template("\"endDateTime\": \"2028-01-01T02:00:00+02:00\""), Outputs));
        Assert.Equal(ChangeType.NoChange, sameOtherFormat.Op(SecretName).ChangeType);
        Check.NoWrites(sameOtherFormat);

        var changed = await host.CreateAsync(host.Resources(Template("\"endDateTime\": \"2028-06-01T00:00:00Z\""), Outputs));
        Assert.Equal(ChangeType.Modify, changed.Op(SecretName).ChangeType);
        Assert.Equal(2, changed.Writes.Count);
        Assert.EndsWith("/addPassword", changed.Writes[0].Path, StringComparison.Ordinal);
        Assert.Equal("2028-06-01T00:00:00Z", Check.Body(changed.Writes[0])["passwordCredential"]!["endDateTime"]!.GetValue<string>());

        var stableHost = new ProviderHost();
        var created = await stableHost.CreateAsync(stableHost.Resources(Template(), Outputs));
        string defaulted = Check.Body(created.Writes[1])["passwordCredential"]!["endDateTime"]!.GetValue<string>();
        for (int i = 0; i < 3; i++)
        {
            var again = await stableHost.CreateAsync(stableHost.Resources(Template(), Outputs));
            Assert.Equal(ChangeType.NoChange, again.Op(SecretName).ChangeType);
            Check.NoWrites(again);
        }

        var live = stableHost.Directory.Find("/applications", "uniqueName", AppName)!["passwordCredentials"]!.AsArray();
        Assert.Equal(defaulted, PasswordCredentialsRules.Format(DateTimeOffset.Parse(Assert.Single(live)!["endDateTime"]!.GetValue<string>(), CultureInfo.InvariantCulture)));
    }

    [Fact]
    public async Task CompleteMode_RemovesTheCredentialOnly_AndDeleteAllRemovesItBeforeTheApplication()
    {
        var host = new ProviderHost();
        var run = await host.CreateAsync(host.Resources(Template(), Outputs));
        Check.Succeeded(run);
        string appId = host.IdOf("/applications", "uniqueName", AppName);
        string keyId = run.Op(SecretName).GraphId!;

        var complete = await host.CreateAsync(host.Resources(App()), DeploymentMode.Complete);
        Check.Succeeded(complete);
        Assert.Equal(ChangeType.Delete, complete.Op(SecretName).ChangeType);
        Assert.Equal(OperationStatus.Succeeded, complete.Op(SecretName).Status);
        var write = Assert.Single(complete.Writes);
        Assert.Equal("POST", write.Method);
        Assert.Equal("/applications/" + appId + "/removePassword", write.Path);
        Assert.Equal(keyId, Check.Body(write)["keyId"]!.GetValue<string>());
        Assert.Empty(host.Directory.FindById(appId)!["passwordCredentials"]!.AsArray());
        Assert.Equal(1, host.Directory.Count("/applications"));
        Assert.False(complete.State!.Resources.ContainsKey(SecretId));

        var deleteHost = new ProviderHost();
        var deployed = await deleteHost.CreateAsync(deleteHost.Resources(Template(), Outputs));
        appId = deleteHost.IdOf("/applications", "uniqueName", AppName);
        keyId = deployed.Op(SecretName).GraphId!;
        deleteHost.ClearRequests();

        var deletion = await deleteHost.DeleteAllAsync();

        Assert.True(deletion.Succeeded, string.Join("; ", deletion.Outcomes.Select(o => o.Entry.Id.Id + "=" + o.Status + " " + o.Error)));
        var writes = deleteHost.Fake.Writes;
        Assert.Equal(2, writes.Count);
        Assert.Equal("POST", writes[0].Method);
        Assert.Equal("/applications/" + appId + "/removePassword", writes[0].Path);
        Assert.Equal(keyId, Check.Body(writes[0])["keyId"]!.GetValue<string>());
        Assert.Equal("DELETE", writes[1].Method);
        Assert.Equal("/applications/" + appId, writes[1].Path);
    }

    [Fact]
    public async Task Delete_ToleratesUnknownKeyIds_AndAMissingParent_ButPropagatesOtherErrors()
    {
        var host = new ProviderHost();
        var provider = host.Provider(SecretType);
        var app = host.Directory.AddApplication(AppName, "Seeded");
        string appId = app["id"]!.GetValue<string>();
        var entry = new StateEntry { Id = ResourceIdentifier.Create(SecretType, AppName + "/" + SecretName), Type = SecretType, ApiVersion = ProviderHost.ApiVersion, Key = new Dictionary<string, string> { ["displayName"] = SecretName }, GraphId = "00000000-0000-0000-0000-00000000dead", ParentGraphId = appId };

        await provider.DeleteAsync(DeleteTarget.KeyOf(entry), DeleteTarget.LiveOf(entry), host.Fake, ProviderHost.Ct);
        Assert.Equal("/applications/" + appId + "/removePassword", Assert.Single(host.Fake.Writes).Path);

        host.ClearRequests();
        host.Fake.Failures.Add("POST", "/removePassword", r => new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource not found.", r.Hint));
        await provider.DeleteAsync(DeleteTarget.KeyOf(entry), DeleteTarget.LiveOf(entry), host.Fake, ProviderHost.Ct);
        Assert.Single(host.Fake.Writes);

        host.ClearRequests();
        host.Fake.Failures.Add("POST", "/removePassword", r => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "No password credential found with keyId 00000000-0000-0000-0000-00000000dead.", r.Hint));
        await provider.DeleteAsync(DeleteTarget.KeyOf(entry), DeleteTarget.LiveOf(entry), host.Fake, ProviderHost.Ct);
        Assert.Single(host.Fake.Writes);

        host.ClearRequests();
        host.Fake.Failures.Add("POST", "/removePassword", r => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "Tenant policy forbids this.", r.Hint));
        await Assert.ThrowsAsync<GraphException>(() => provider.DeleteAsync(DeleteTarget.KeyOf(entry), DeleteTarget.LiveOf(entry), host.Fake, ProviderHost.Ct));

        host.ClearRequests();
        var orphan = new StateEntry { Id = entry.Id, Type = SecretType, ApiVersion = ProviderHost.ApiVersion, Key = entry.Key, GraphId = "whatever" };
        await provider.DeleteAsync(DeleteTarget.KeyOf(orphan), DeleteTarget.LiveOf(orphan), host.Fake, ProviderHost.Ct);
        Assert.Equal(2, host.Fake.Requests.Count);
        Assert.Equal("/applications(uniqueName='test-api')", host.Fake.Requests[0].Path);
        Assert.Equal("/applications/" + appId + "/removePassword", Assert.Single(host.Fake.Writes).Path);

        host.Directory.SoftDelete(appId);
        host.ClearRequests();
        await provider.DeleteAsync(DeleteTarget.KeyOf(entry), DeleteTarget.LiveOf(entry), host.Fake, ProviderHost.Ct);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task ExternalParent_ReadsByAlternateKey_LooksUpTheId_AndCallsAddPasswordById()
    {
        var host = new ProviderHost();
        string appId = host.Directory.AddApplication(AppName, "Seeded API")["id"]!.GetValue<string>();

        var run = await host.CreateAsync(host.Resources(Secret(), Outputs));

        Check.Succeeded(run);
        var requests = run.Requests;
        Assert.Equal("GET", requests[0].Method);
        Assert.Equal("/applications(uniqueName='test-api')", requests[0].Path);
        Assert.Equal("id,passwordCredentials", requests[0].UrlParameters!["$select"]);
        Assert.Equal("GET", requests[1].Method);
        Assert.Equal("/applications(uniqueName='test-api')", requests[1].Path);
        Assert.Equal("id", requests[1].UrlParameters!["$select"]);
        Assert.Equal("POST", requests[2].Method);
        Assert.Equal("/applications/" + appId + "/addPassword", requests[2].Path);
        Assert.Equal(RetryHint.Default, requests[2].Hint);
        Assert.Single(run.Writes);
        Assert.Equal(appId, run.Entry(SecretId).ParentGraphId);
        Assert.NotNull(run.SecureOutput("ciSecret"));

        var rerun = await host.CreateAsync(host.Resources(Secret(), Outputs));
        Assert.Equal(ChangeType.NoChange, rerun.Executed.Operations[0].ChangeType);
        Check.NoWrites(rerun);
        Assert.Equal("/applications(uniqueName='test-api')", rerun.Requests[0].Path);

        var missing = await new ProviderHost().CreateAsync(host.Resources(Secret()));
        Assert.Equal(DeploymentStatus.Failed, missing.Executed.Status);
        Assert.Contains("does not exist", missing.Executed.Operations[0].Error, StringComparison.Ordinal);
        Check.NoWrites(missing);
    }

    [Fact]
    public async Task RequiredPermissions_FollowTheParentApplication()
    {
        var host = new ProviderHost();
        Assert.Equal(["Application.ReadWrite.All"], host.Provider(SecretType).RequiredPermissions(null));

        var withParent = await host.WhatIfAsync(host.Resources(Template()));
        Assert.Equal(["Application.ReadWrite.All", "Application.ReadWrite.OwnedBy"], withParent.Plan.RequiredPermissions.Order());

        var external = new ProviderHost();
        external.Directory.AddApplication(AppName, "Seeded");
        var externalRun = await external.WhatIfAsync(external.Resources(Secret()));
        Assert.Equal(["Application.ReadWrite.All"], externalRun.Plan.RequiredPermissions.Order());
    }

    [Fact]
    public async Task Validation_RowsAreUnsupported_ForBadDates_NegativeThreshold_NameConflict_AndMissingParentSegment()
    {
        var host = new ProviderHost();

        var bad = await host.WhatIfAsync(host.Resources(Template("\"endDateTime\": \"not-a-date\"")));
        var row = bad.ChangeOfType(SecretType);
        Assert.True(row.HasError);
        Assert.Contains("endDateTime", row.UnsupportedReason, StringComparison.Ordinal);
        var badRun = await host.CreateAsync(host.Resources(Template("\"endDateTime\": \"not-a-date\"")));
        Assert.Equal(DeploymentStatus.Failed, badRun.Executed.Status);
        Assert.DoesNotContain(badRun.Writes, w => w.Path.Contains("Password", StringComparison.OrdinalIgnoreCase));

        var literalNegative = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(host.Resources(Template("\"rotateBeforeDays\": -1"))));
        Assert.Contains(literalNegative.Diagnostics, d => d.Code == "SchemaViolation" && d.Message.Contains("minimum", StringComparison.Ordinal));
        var negative = await host.WhatIfAsync(host.Resources(Template("\"rotateBeforeDays\": \"[int('-1')]\"")));
        row = negative.ChangeOfType(SecretType);
        Assert.True(row.HasError);
        Assert.Contains("rotateBeforeDays", row.UnsupportedReason, StringComparison.Ordinal);

        var conflict = await new ProviderHost().CreateAsync(host.Resources(Template("\"displayName\": \"[concat('other', '-name')]\"")));
        Assert.Equal(DeploymentStatus.Failed, conflict.Executed.Status);
        Assert.Contains("conflicts with the credential name", conflict.Op(SecretName).Error, StringComparison.Ordinal);

        var equal = await new ProviderHost().CreateAsync(host.Resources(Template("\"displayName\": \"[concat('ci', '-secret')]\"")));
        Check.Succeeded(equal);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.CreateAsync(host.Resources(App() + """, { "type": "Microsoft.Entra/applications/passwordCredentials", "apiVersion": "2026-10-01-preview", "name": "ci-secret", "properties": { } }""")));
        Assert.Contains(ex.Diagnostics, d => d.Code == "InvalidResourceName");
    }

    [Fact]
    public async Task TheSecret_NeverReachesState_WhatIfRows_OrOutputsJson_AndTheLiveObjectIsMarked()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(Template(), Outputs));

        Check.Succeeded(run);
        string secret = run.SecureOutput("ciSecret")!.GetValue<string>();
        Assert.DoesNotContain(secret, host.StateDocument, StringComparison.Ordinal);
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
