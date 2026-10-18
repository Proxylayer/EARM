using System;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core.Diagnostics;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Rotation tests of <see cref="PasswordCredentialsProviderTests"/>: rotation tags, the tag separator, expiry thresholds and declared end dates.</summary>
public sealed partial class PasswordCredentialsProviderTests
{
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
        Assert.Equal(newKeyId, rotated.GraphIdOf(SecretId));
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
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Template("\"rotationTag\": \"2026Q3\""), Outputs)));

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
        var host = new ProviderHost();
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
        var host = new ProviderHost();
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
}
