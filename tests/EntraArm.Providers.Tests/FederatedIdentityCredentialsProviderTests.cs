using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core;
using EntraArm.Engine;
using EntraArm.Engine.Execution;
using EntraArm.Engine.Planning;
using EntraArm.Engine.Providers;
using EntraArm.Engine.State;
using EntraArm.Providers.Tests.Infrastructure;
using Xunit;
using GraphApi = EntraArm.Az.GraphApi;

namespace EntraArm.Providers.Tests;

/// <summary>The federated identity credentials provider (WI-10) end to end.</summary>
public sealed class FederatedIdentityCredentialsProviderTests
{
    private const string AppType = ApplicationsProvider.ResourceType;
    private const string FicType = FederatedIdentityCredentialsProvider.ResourceType;
    private const string AppName = "test-api";
    private const string Issuer = "https://token.actions.githubusercontent.com";
    private const string AppResourceId = "/providers/Microsoft.Entra/applications/test-api";

    private static string App(string? owners = null, string nested = "")
    {
        string ownersJson = owners is null ? string.Empty : ", \"owners\": [\"" + owners + "\"]";
        return """{ "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "test-api", "properties": { "displayName": "Test API" """ + ownersJson + " }" + nested + " }";
    }

    private static string Fic(string name, string subject, string extra = "", string apiVersion = ProviderHost.ApiVersion, bool flat = true)
    {
        string type = flat ? FicType : "federatedIdentityCredentials";
        string fullName = flat ? AppName + "/" + name : name;
        return "{ \"type\": \"" + type + "\", \"apiVersion\": \"" + apiVersion + "\", \"name\": \"" + fullName + "\", \"properties\": { \"issuer\": \"" + Issuer + "\", \"subject\": \"" + subject + "\"" + extra + " } }";
    }

    private static readonly string NestedTemplate = App(nested: ", \"resources\": [ " + Fic("github-main", "repo:contoso/orders:ref:refs/heads/main", flat: false) + ", " + Fic("github-dev", "repo:contoso/orders:ref:refs/heads/dev", flat: false) + " ]");

    private static readonly string FlatTemplate = App() + ", " + Fic("github-main", "repo:contoso/orders:ref:refs/heads/main") + ", " + Fic("github-dev", "repo:contoso/orders:ref:refs/heads/dev");

    private static string CopyTemplate(int count)
    {
        return App() + """
            , { "type": "Microsoft.Entra/applications/federatedIdentityCredentials", "apiVersion": "2026-10-01-preview",
                "copy": { "name": "fics", "count":
            """ + count + """
            , "mode": "parallel" },
                "name": "[format('test-api/github-{0}', copyIndex('fics'))]",
                "properties": { "issuer": "https://token.actions.githubusercontent.com", "subject": "[format('repo:contoso/orders:ref:refs/heads/branch-{0}', copyIndex('fics'))]" } }
            """;
    }

    private static List<string> Normalized(ProviderHost host, Run run)
    {
        string appId = host.IdOf("/applications", "uniqueName", AppName);
        return run.Requests.Select(r => Run.Describe(r).Replace(appId, "{appId}", StringComparison.Ordinal)).ToList();
    }

    [Fact]
    public void Discovery_RegistersTheProvider()
    {
        var registry = ProviderDiscovery.CreateRegistry(EntraArm.Core.Types.ResourceTypeCatalog.Default);
        Assert.IsType<FederatedIdentityCredentialsProvider>(registry.Get(FicType));
    }

    [Fact]
    public async Task NestedAndFlatTemplates_ProduceIdenticalRequests()
    {
        var nestedHost = new ProviderHost();
        var flatHost = new ProviderHost();

        var nested = await nestedHost.CreateAsync(nestedHost.Resources(NestedTemplate));
        var flat = await flatHost.CreateAsync(flatHost.Resources(FlatTemplate));

        Check.Succeeded(nested);
        Check.Succeeded(flat);
        var a = Normalized(nestedHost, nested);
        var b = Normalized(flatHost, flat);
        Assert.Equal(b, a);
        Assert.Equal(9, a.Count);
        foreach (int i in new[] { 3, 6 })
        {
            Assert.StartsWith("GET /applications/{appId}/federatedIdentityCredentials(name='github-", a[i], StringComparison.Ordinal);
            Assert.StartsWith("GET /applications/{appId}/federatedIdentityCredentials?$select=id,name,issuer,subject", a[i + 1], StringComparison.Ordinal);
            Assert.StartsWith("PATCH /applications/{appId}/federatedIdentityCredentials(name='github-", a[i + 2], StringComparison.Ordinal);
        }

        Assert.Equal(flat.Executed.Operations.Select(o => o.Id.Id), nested.Executed.Operations.Select(o => o.Id.Id));
    }

    [Fact]
    public async Task Create_UpsertsAfterTheApp_WithReplicationHint_AndRerunIsNoChange()
    {
        var host = new ProviderHost();
        var document = host.Resources(CopyTemplate(2));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Equal(3, run.Executed.Operations.Count);
        Assert.All(run.Executed.Operations, o => Assert.Equal(ChangeType.Create, o.ChangeType));
        var writes = run.Writes;
        Assert.Equal(3, writes.Count);
        string appId = host.IdOf("/applications", "uniqueName", AppName);
        Assert.Contains("applications(uniqueName='test-api')", writes[0].Path, StringComparison.Ordinal);
        foreach (var upsert in writes.Skip(1))
        {
            Assert.Equal("PATCH", upsert.Method);
            Assert.StartsWith("/applications/" + appId + "/federatedIdentityCredentials(name='github-", upsert.Path, StringComparison.Ordinal);
            Assert.True(upsert.HasHeader("Prefer", "create-if-missing"));
            Assert.Equal(RetryHint.ExpectReplicationLag, upsert.Hint);
            var body = Check.Body(upsert);
            Assert.StartsWith("github-", body["name"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.Equal("api://AzureADTokenExchange", Assert.Single(body["audiences"]!.AsArray())!.GetValue<string>());
            Assert.Equal(Issuer, body["issuer"]!.GetValue<string>());
            Assert.StartsWith("repo:contoso/orders:ref:refs/heads/branch-", body["subject"]!.GetValue<string>(), StringComparison.Ordinal);
            Assert.False(body.ContainsKey("id"));
        }

        var reads = run.Requests.Where(r => r.Method == "GET" && r.Path.Contains("federatedIdentityCredentials", StringComparison.Ordinal)).ToList();
        Assert.All(reads, r => Assert.StartsWith("/applications/" + appId + "/", r.Path, StringComparison.Ordinal));
        Assert.Contains(reads, r => r.Path.EndsWith("/federatedIdentityCredentials", StringComparison.Ordinal));
        Assert.Equal(2, host.Directory.Count("/applications/" + appId + "/federatedIdentityCredentials"));

        var rerun = await host.CreateAsync(document);
        Check.Succeeded(rerun);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task WhatIf_ShowsThreeCreateRows_AndWritesNothing()
    {
        var host = new ProviderHost();

        var run = await host.WhatIfAsync(host.Resources(CopyTemplate(2)));

        Assert.Equal(3, run.Plan.Changes.Count);
        Assert.All(run.Plan.Changes, c => Assert.Equal(ChangeType.Create, c.Type));
        Assert.All(run.Plan.Changes, c => Assert.False(c.HasError));
        Check.NoWrites(run);
    }

    [Fact]
    public async Task SubjectDrift_IsOneUpsertPatch_WithOnlyTheSubject()
    {
        var host = new ProviderHost();
        string appId = host.Directory.AddApplication(AppName, "Test API")["id"]!.GetValue<string>();
        host.Directory.Add("/applications/" + appId + "/federatedIdentityCredentials", new JsonObject { ["name"] = "github-main", ["issuer"] = Issuer, ["subject"] = "repo:contoso/orders:ref:refs/heads/old", ["audiences"] = new JsonArray("api://AzureADTokenExchange"), ["description"] = null });
        var document = host.Resources(App() + ", " + Fic("github-main", "repo:contoso/orders:ref:refs/heads/main"));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Modify, run.Op("github-main").ChangeType);
        var write = Assert.Single(run.Writes);
        Assert.Equal("PATCH", write.Method);
        Assert.Equal("/applications/" + appId + "/federatedIdentityCredentials(name='github-main')", write.Path);
        Assert.True(write.HasHeader("Prefer", "create-if-missing"));
        Assert.Equal("""{"subject":"repo:contoso/orders:ref:refs/heads/main"}""", write.Body!.ToJsonString());
        Assert.Equal(RetryHint.Default, write.Hint);

        var rerun = await host.CreateAsync(document);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task AudiencesDrift_IsPatchedWhole()
    {
        var host = new ProviderHost();
        string appId = host.Directory.AddApplication(AppName, "Test API")["id"]!.GetValue<string>();
        host.Directory.Add("/applications/" + appId + "/federatedIdentityCredentials", new JsonObject { ["name"] = "github-main", ["issuer"] = Issuer, ["subject"] = "repo:x", ["audiences"] = new JsonArray("api://AzureADTokenExchange", "api://other") });

        var run = await host.CreateAsync(host.Resources(App() + ", " + Fic("github-main", "repo:x", ", \"audiences\": [\"api://AzureADTokenExchange\"]")));

        Check.Succeeded(run);
        var write = Assert.Single(run.Writes);
        Assert.Equal("""{"audiences":["api://AzureADTokenExchange"]}""", write.Body!.ToJsonString());
    }

    [Fact]
    public async Task Upsert204_AdoptsTheExistingCredential()
    {
        var host = new ProviderHost();
        string appId = host.Directory.AddApplication(AppName, "Test API")["id"]!.GetValue<string>();
        var seeded = host.Directory.Add("/applications/" + appId + "/federatedIdentityCredentials", new JsonObject { ["name"] = "github-main", ["issuer"] = Issuer, ["subject"] = "repo:x", ["audiences"] = new JsonArray("api://AzureADTokenExchange") });
        host.Fake.Failures.ReplicationLag("federatedIdentityCredentials(name='github-main')", times: 1, method: "GET");

        var run = await host.CreateAsync(host.Resources(App() + ", " + Fic("github-main", "repo:x")));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Create, run.Op("github-main").ChangeType);
        Assert.Equal(seeded["id"]!.GetValue<string>(), run.Op("github-main").GraphId);
        Assert.Equal(1, host.Directory.Count("/applications/" + appId + "/federatedIdentityCredentials"));
    }

    [Fact]
    public async Task Rename_DetachesInIncremental_AndDeletesInComplete()
    {
        var host = new ProviderHost();
        var first = await host.CreateAsync(host.Resources(App() + ", " + Fic("github-old", "repo:contoso/orders:ref:refs/heads/old")));
        Check.Succeeded(first);
        string appId = host.IdOf("/applications", "uniqueName", AppName);
        var oldEntry = first.Entry(AppResourceId + "/federatedIdentityCredentials/github-old");
        Assert.Equal(appId, oldEntry.ParentGraphId);
        Assert.Equal("github-old", oldEntry.Key["name"]);
        Assert.Contains(AppResourceId, oldEntry.DependsOn);

        var incremental = await host.CreateAsync(host.Resources(App() + ", " + Fic("github-new", "repo:contoso/orders:ref:refs/heads/new")));
        Check.Succeeded(incremental);
        Assert.Equal(ChangeType.Create, incremental.Op("github-new").ChangeType);
        Assert.Equal(OperationStatus.Detached, incremental.Op("github-old").Status);
        Assert.Equal(2, host.Directory.Count("/applications/" + appId + "/federatedIdentityCredentials"));
        Assert.False(incremental.State!.Resources.ContainsKey(oldEntry.Id.Id));

        var complete = new ProviderHost();
        await complete.CreateAsync(complete.Resources(App() + ", " + Fic("github-old", "repo:contoso/orders:ref:refs/heads/old")));
        appId = complete.IdOf("/applications", "uniqueName", AppName);

        var run = await complete.CreateAsync(complete.Resources(App() + ", " + Fic("github-new", "repo:contoso/orders:ref:refs/heads/new")), DeploymentMode.Complete);

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Delete, run.Op("github-old").ChangeType);
        Assert.Equal(OperationStatus.Succeeded, run.Op("github-old").Status);
        var writes = run.Writes;
        Assert.Equal(2, writes.Count);
        Assert.Equal("PATCH", writes[0].Method);
        Assert.EndsWith("(name='github-new')", writes[0].Path, StringComparison.Ordinal);
        Assert.Equal("DELETE", writes[1].Method);
        Assert.Equal("/applications/" + appId + "/federatedIdentityCredentials(name='github-old')", writes[1].Path);
        Assert.Equal("github-new", Assert.Single(complete.Directory.Objects("/applications/" + appId + "/federatedIdentityCredentials"))["name"]!.GetValue<string>());
    }

    [Fact]
    public async Task RenameKeepingThePair_IsUnsupportedUntilTheOldCredentialIsGone()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(App() + ", " + Fic("github-old", "repo:contoso/orders:ref:refs/heads/main")));
        var renamed = host.Resources(App() + ", " + Fic("github-new", "repo:contoso/orders:ref:refs/heads/main"));

        var whatIf = await host.WhatIfAsync(renamed, DeploymentMode.Complete);
        var row = whatIf.Change("github-new");
        Assert.True(row.HasError);
        Assert.Contains("already used by federated identity credential 'github-old'", row.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(renamed, DeploymentMode.Complete);
        Assert.Equal(DeploymentStatus.Failed, run.Executed.Status);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task DuplicatePair_AcrossTwoCredentials_IsOneUnsupportedRow()
    {
        var host = new ProviderHost();
        var document = host.Resources(App() + ", " + Fic("github-a", "repo:contoso/orders:ref:refs/heads/main") + ", " + Fic("github-b", "repo:contoso/orders:ref:refs/heads/main"));

        var whatIf = await host.WhatIfAsync(document);
        var error = Assert.Single(whatIf.Plan.Changes, c => c.HasError);
        Assert.Equal(FicType, error.ResourceType);
        Assert.Contains("also declared by federated identity credential", error.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(document);
        Assert.Equal(DeploymentStatus.Failed, run.Executed.Status);
        var ficOps = run.Executed.Operations.Where(o => o.Type == FicType).ToList();
        Assert.Equal(1, ficOps.Count(o => o.Status == OperationStatus.Failed));
        Assert.Equal(1, ficOps.Count(o => o.Status == OperationStatus.Succeeded));
        Assert.Equal(1, run.Writes.Count(w => w.Path.Contains("federatedIdentityCredentials", StringComparison.Ordinal)));

        var rerun = await host.CreateAsync(document);
        var failed = rerun.Executed.Operations.First(o => o.Status == OperationStatus.Failed);
        Assert.Contains("already used by federated identity credential", failed.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvalidName_LiteralIsAValidationError_ExpressionIsAnUnsupportedRow()
    {
        var host = new ProviderHost();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.CreateAsync(host.Resources(App() + ", " + Fic("1bad", "repo:x"))));
        Assert.Contains(ex.Diagnostics, d => d.Message.Contains("1bad", StringComparison.Ordinal));

        var expression = host.Resources(App() + """, { "type": "Microsoft.Entra/applications/federatedIdentityCredentials", "apiVersion": "2026-10-01-preview", "name": "[concat('test-api/1', 'bad')]", "properties": { "issuer": "https://token.actions.githubusercontent.com", "subject": "repo:x" } }""");
        var whatIf = await host.WhatIfAsync(expression);
        var row = whatIf.ChangeOfType(FicType);
        Assert.True(row.HasError);
        Assert.Contains("is not valid", row.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(expression);
        Assert.Equal(DeploymentStatus.Failed, run.Executed.Status);
        Assert.DoesNotContain(run.Writes, w => w.Path.Contains("federatedIdentityCredentials", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TwentyOneCredentials_TheLastIsRefused()
    {
        var host = new ProviderHost();
        var document = host.Resources(CopyTemplate(21));

        var whatIf = await host.WhatIfAsync(document);
        var error = Assert.Single(whatIf.Plan.Changes, c => c.HasError);
        Assert.Contains("at most 20", error.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(document);
        Assert.Equal(DeploymentStatus.Failed, run.Executed.Status);
        var ficOps = run.Executed.Operations.Where(o => o.Type == FicType).ToList();
        Assert.Equal(20, ficOps.Count(o => o.Status == OperationStatus.Succeeded));
        Assert.Equal(1, ficOps.Count(o => o.Status == OperationStatus.Failed));
        string appId = host.IdOf("/applications", "uniqueName", AppName);
        Assert.Equal(20, host.Directory.Count("/applications/" + appId + "/federatedIdentityCredentials"));

        var extra = await host.WhatIfAsync(host.Resources(App() + ", " + Fic("github-extra", "repo:contoso/orders:ref:refs/heads/extra")));
        var extraRow = extra.Change("github-extra");
        Assert.True(extraRow.HasError);
        Assert.Contains("already has 20", extraRow.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeleteAll_RemovesCredentialsBeforeTheApplication_AndToleratesNotFound()
    {
        var host = new ProviderHost();
        var run = await host.CreateAsync(host.Resources(FlatTemplate));
        Check.Succeeded(run);
        string appId = host.IdOf("/applications", "uniqueName", AppName);
        host.ClearRequests();

        var deletion = await host.DeleteAllAsync();

        Assert.True(deletion.Succeeded, string.Join("; ", deletion.Outcomes.Select(o => o.Entry.Id.Id + "=" + o.Status + " " + o.Error)));
        var writes = host.Fake.Writes;
        Assert.Equal(3, writes.Count);
        Assert.All(writes, w => Assert.Equal("DELETE", w.Method));
        Assert.StartsWith("/applications/" + appId + "/federatedIdentityCredentials(name='github-", writes[0].Path, StringComparison.Ordinal);
        Assert.StartsWith("/applications/" + appId + "/federatedIdentityCredentials(name='github-", writes[1].Path, StringComparison.Ordinal);
        Assert.Equal("/applications/" + appId, writes[2].Path);
        Assert.Equal(0, host.Directory.Count("/applications"));

        var provider = host.Provider(FicType);
        var entry = new StateEntry { Id = ResourceIdentifier.Create(FicType, AppName + "/github-main"), Type = FicType, ApiVersion = ProviderHost.ApiVersion, Key = new Dictionary<string, string> { ["name"] = "github-main" }, GraphId = "whatever" };
        host.ClearRequests();
        await provider.DeleteAsync(entry, host.Fake, ProviderHost.Ct);
        var write = Assert.Single(host.Fake.Writes);
        Assert.Equal("/applications(uniqueName='test-api')/federatedIdentityCredentials(name='github-main')", write.Path);
    }

    [Fact]
    public async Task ExternalParent_UsesAlternateKeyPaths_AndRecordsTheParentId()
    {
        var host = new ProviderHost();
        string appId = host.Directory.AddApplication(AppName, "Seeded API")["id"]!.GetValue<string>();
        var document = host.Resources(Fic("github-main", "repo:contoso/orders:ref:refs/heads/main"));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var requests = run.Requests;
        Assert.Equal("GET", requests[0].Method);
        Assert.Equal("/applications(uniqueName='test-api')/federatedIdentityCredentials(name='github-main')", requests[0].Path);
        Assert.Equal("id,name,issuer,subject,audiences,description", requests[0].UrlParameters!["$select"]);
        Assert.Equal("GET", requests[1].Method);
        Assert.Equal("/applications(uniqueName='test-api')/federatedIdentityCredentials", requests[1].Path);
        var upsert = Assert.Single(run.Writes);
        Assert.Equal("PATCH", upsert.Method);
        Assert.Equal("/applications(uniqueName='test-api')/federatedIdentityCredentials(name='github-main')", upsert.Path);
        Assert.True(upsert.HasHeader("Prefer", "create-if-missing"));
        Assert.Equal(RetryHint.Default, upsert.Hint);
        Assert.Contains(requests, r => r.Method == "GET" && r.Path == "/applications(uniqueName='test-api')" && r.UrlParameters!["$select"] == "id");
        var entry = run.Entry(AppResourceId + "/federatedIdentityCredentials/github-main");
        Assert.Equal(appId, entry.ParentGraphId);
        Assert.Equal(1, host.Directory.Count("/applications/" + appId + "/federatedIdentityCredentials"));

        var rerun = await host.CreateAsync(document);
        Assert.Equal(ChangeType.NoChange, rerun.Executed.Operations[0].ChangeType);
        Check.NoWrites(rerun);
        Assert.Equal("/applications(uniqueName='test-api')/federatedIdentityCredentials/" + entry.GraphId, rerun.Requests[0].Path);

        var missing = await new ProviderHost().CreateAsync(host.Resources(Fic("github-main", "repo:x")));
        Assert.Equal(DeploymentStatus.Failed, missing.Executed.Status);
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, missing.Executed.Operations[0].ErrorCode);
    }

    [Fact]
    public async Task RequiredPermissions_FollowTheParentApplication()
    {
        var host = new ProviderHost();
        Assert.Equal(["Application.ReadWrite.All"], host.Provider(FicType).RequiredPermissions(null));

        var owned = await host.WhatIfAsync(host.Resources(App("00000000-0000-0000-0000-00000000aaaa") + ", " + Fic("github-main", "repo:x")));
        Assert.Equal(["Application.ReadWrite.OwnedBy"], owned.Plan.RequiredPermissions.Order());

        var unowned = await host.WhatIfAsync(host.Resources(App() + ", " + Fic("github-main", "repo:x")));
        Assert.Equal(["Application.ReadWrite.All", "Application.ReadWrite.OwnedBy"], unowned.Plan.RequiredPermissions.Order());

        var external = new ProviderHost();
        external.Directory.AddApplication(AppName, "Seeded");
        var externalRun = await external.WhatIfAsync(external.Resources(Fic("github-main", "repo:x")));
        Assert.Equal(["Application.ReadWrite.All"], externalRun.Plan.RequiredPermissions.Order());
    }

    [Fact]
    public async Task Validation_IssuerSubjectAndPreviewOnlyClaims()
    {
        var host = new ProviderHost();

        async Task<ResourceChange> RowAsync(string resources)
        {
            var run = await host.WhatIfAsync(host.Resources(resources));
            return run.ChangeOfType(FicType);
        }

        var http = await RowAsync(App() + """, { "type": "Microsoft.Entra/applications/federatedIdentityCredentials", "apiVersion": "2026-10-01-preview", "name": "test-api/github-main", "properties": { "issuer": "http://insecure", "subject": "repo:x" } }""");
        Assert.True(http.HasError);
        Assert.Contains("https", http.UnsupportedReason, StringComparison.Ordinal);

        const string emptySubject = """, { "type": "Microsoft.Entra/applications/federatedIdentityCredentials", "apiVersion": "2026-10-01-preview", "name": "test-api/github-main", "properties": { "issuer": "https://ok", "subject": "SUBJECT" } }""";
        var literal = await Assert.ThrowsAsync<ValidationFailedException>(() => RowAsync(App() + emptySubject.Replace("SUBJECT", string.Empty, StringComparison.Ordinal)));
        Assert.Contains(literal.Diagnostics, d => d.Code == "SchemaViolation" && d.Message.Contains("minLength", StringComparison.Ordinal));
        var empty = await RowAsync(App() + emptySubject.Replace("SUBJECT", "[concat('', '')]", StringComparison.Ordinal));
        Assert.True(empty.HasError);
        Assert.Contains("subject", empty.UnsupportedReason, StringComparison.Ordinal);

        var missing = await RowAsync(App() + """, { "type": "Microsoft.Entra/applications/federatedIdentityCredentials", "apiVersion": "2026-10-01-preview", "name": "test-api/github-main", "properties": { "issuer": "https://ok" } }""");
        Assert.True(missing.HasError);
        Assert.Contains("subject", missing.UnsupportedReason, StringComparison.Ordinal);

        const string claims = """, "claimsMatchingExpression": { "value": "claims['sub'] eq 'x'", "languageVersion": 1 }""";
        var v1 = await RowAsync(App() + ", " + Fic("github-main", "repo:x", claims));
        Assert.True(v1.HasError);
        Assert.Contains("beta", v1.UnsupportedReason, StringComparison.Ordinal);

        var beta = await host.CreateAsync(host.Resources(App() + ", " + Fic("github-main", "repo:x", claims, apiVersion: ProviderHost.PreviewApiVersion)));
        Check.Succeeded(beta);
        var betaRead = beta.Requests.First(r => r.Method == "GET" && r.Path.EndsWith("(name='github-main')", StringComparison.Ordinal));
        Assert.Equal(GraphApi.Beta, betaRead.Api);
        Assert.Contains("claimsMatchingExpression", betaRead.UrlParameters!["$select"], StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeclaredName_MustEqualTheCredentialName()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(App() + ", " + Fic("github-main", "repo:x", """, "name": "[concat('other', '-name')]" """)));
        Assert.Equal(DeploymentStatus.Failed, run.Executed.Status);
        Assert.Contains("conflicts with the credential name", run.Op("github-main").Error, StringComparison.Ordinal);

        var equal = await new ProviderHost().CreateAsync(host.Resources(App() + ", " + Fic("github-main", "repo:x", """, "name": "[concat('github', '-main')]" """)));
        Check.Succeeded(equal);
    }
}
