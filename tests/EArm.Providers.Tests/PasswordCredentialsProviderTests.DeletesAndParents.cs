using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>Delete, parent and validation tests of <see cref="PasswordCredentialsProviderTests"/>: Complete mode and delete-all, tolerated delete errors, external parents, permissions and validation rows.</summary>
public sealed partial class PasswordCredentialsProviderTests
{
    [Fact]
    public async Task CompleteMode_RemovesTheCredentialOnly_AndDeleteAllRemovesItBeforeTheApplication()
    {
        var host = new ProviderHost();
        var run = await host.CreateAsync(host.Resources(Template(), Outputs));
        Check.Succeeded(run);
        string appId = host.IdOf("/applications", "uniqueName", AppName);
        string keyId = run.Op(SecretName).GraphId!;

        // Complete mode never deletes: the credential the template no longer declares stays (its type is not declared, so it is not even listed).
        var complete = await host.CreateAsync(host.Resources(App()), DeploymentMode.Complete);
        Check.Succeeded(complete);
        Check.NoWrites(complete);
        Assert.Contains(host.Directory.FindById(appId)!["passwordCredentials"]!.AsArray(), c => c!["keyId"]!.GetValue<string>() == keyId);

        var deleteHost = new ProviderHost();
        var declared = deleteHost.Resources(Template(), Outputs);
        var deployed = await deleteHost.CreateAsync(declared);
        appId = deleteHost.IdOf("/applications", "uniqueName", AppName);
        keyId = deployed.Op(SecretName).GraphId!;
        deleteHost.ClearRequests();

        var deletion = await deleteHost.DeleteDeclaredAsync(declared);

        Assert.True(deletion.Succeeded, string.Join("; ", deletion.Outcomes.Select(o => o.Target.Instance.Id.Id + "=" + o.Status + " " + o.Error)));
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
        var entry = new DeleteTarget { Id = ResourceIdentifier.Create(SecretType, AppName + "/" + SecretName), Type = SecretType, ApiVersion = ProviderHost.ApiVersion, Key = new Dictionary<string, string> { ["displayName"] = SecretName }, GraphId = "00000000-0000-0000-0000-00000000dead", ParentGraphId = appId };

        await provider.DeleteAsync(entry.ResourceKey, entry.Live, host.Fake, ProviderHost.Ct);
        Assert.Equal("/applications/" + appId + "/removePassword", Assert.Single(host.Fake.Writes).Path);

        host.ClearRequests();
        host.Fake.Failures.Add("POST", "/removePassword", r => new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource not found.", r.Hint));
        await provider.DeleteAsync(entry.ResourceKey, entry.Live, host.Fake, ProviderHost.Ct);
        Assert.Single(host.Fake.Writes);

        host.ClearRequests();
        host.Fake.Failures.Add("POST", "/removePassword", r => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "No password credential found with keyId 00000000-0000-0000-0000-00000000dead.", r.Hint));
        await provider.DeleteAsync(entry.ResourceKey, entry.Live, host.Fake, ProviderHost.Ct);
        Assert.Single(host.Fake.Writes);

        host.ClearRequests();
        host.Fake.Failures.Add("POST", "/removePassword", r => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "Tenant policy forbids this.", r.Hint));
        await Assert.ThrowsAsync<GraphException>(() => provider.DeleteAsync(entry.ResourceKey, entry.Live, host.Fake, ProviderHost.Ct));

        host.ClearRequests();
        var orphan = new DeleteTarget { Id = entry.Id, Type = SecretType, ApiVersion = ProviderHost.ApiVersion, Key = entry.Key, GraphId = "whatever" };
        await provider.DeleteAsync(orphan.ResourceKey, orphan.Live, host.Fake, ProviderHost.Ct);
        Assert.Equal(2, host.Fake.Requests.Count);
        Assert.Equal("/applications(uniqueName='test-api')", host.Fake.Requests[0].Path);
        Assert.Equal("/applications/" + appId + "/removePassword", Assert.Single(host.Fake.Writes).Path);

        host.Directory.SoftDelete(appId);
        host.ClearRequests();
        await provider.DeleteAsync(entry.ResourceKey, entry.Live, host.Fake, ProviderHost.Ct);
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
        Assert.Equal("/applications/" + appId + "/addPassword", Assert.Single(run.Writes).Path);
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
}
