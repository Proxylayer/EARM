using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers.Tests;

/// <summary>The federated identity credentials provider end to end.</summary>
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
        var registry = ProviderDiscovery.CreateRegistry(EArm.Core.Types.ResourceTypeCatalog.Default);
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
    public async Task Rename_KeepsTheOldCredential_AndCompleteModeReportsIt()
    {
        var host = new ProviderHost();
        var first = await host.CreateAsync(host.Resources(App() + ", " + Fic("github-old", "repo:contoso/orders:ref:refs/heads/old")));
        Check.Succeeded(first);
        string appId = host.IdOf("/applications", "uniqueName", AppName);
        Assert.Equal(1, host.Directory.Count("/applications/" + appId + "/federatedIdentityCredentials"));

        var incremental = await host.CreateAsync(host.Resources(App() + ", " + Fic("github-new", "repo:contoso/orders:ref:refs/heads/new")));
        Check.Succeeded(incremental);
        Assert.Equal(ChangeType.Create, incremental.Op("github-new").ChangeType);
        Assert.Equal(2, host.Directory.Count("/applications/" + appId + "/federatedIdentityCredentials"));
        Assert.Empty(incremental.Executed.Orphans);

        var complete = new ProviderHost();
        await complete.CreateAsync(complete.Resources(App() + ", " + Fic("github-old", "repo:contoso/orders:ref:refs/heads/old")));
        appId = complete.IdOf("/applications", "uniqueName", AppName);
        string oldId = complete.Directory.Objects("/applications/" + appId + "/federatedIdentityCredentials").Single()["id"]!.GetValue<string>();

        var run = await complete.CreateAsync(complete.Resources(App() + ", " + Fic("github-new", "repo:contoso/orders:ref:refs/heads/new")), DeploymentMode.Complete);

        Check.Succeeded(run);
        Assert.DoesNotContain(run.Writes, w => w.Method == "DELETE");
        Assert.Equal(2, complete.Directory.Count("/applications/" + appId + "/federatedIdentityCredentials"));
        var orphan = Assert.Single(run.Executed.Orphans);
        Assert.Equal(OrphanReason.Undeclared, orphan.Orphan);
        Assert.Equal(oldId, orphan.GraphId);
        Assert.Equal(AppResourceId + "/federatedIdentityCredentials/github-old", orphan.Id.Id, StringComparer.OrdinalIgnoreCase);
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

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.CreateAsync(host.Resources(App() + ", " + Fic("_bad", "repo:x"))));
        Assert.Contains(ex.Diagnostics, d => d.Message.Contains("_bad", StringComparison.Ordinal));

        var expression = host.Resources(App() + """, { "type": "Microsoft.Entra/applications/federatedIdentityCredentials", "apiVersion": "2026-10-01-preview", "name": "[concat('test-api/_', 'bad')]", "properties": { "issuer": "https://token.actions.githubusercontent.com", "subject": "repo:x" } }""");
        // A name built from literals is folded and validated like a literal by the planner (a ValidationFailedException); a name that is only known
        // when it is evaluated reaches the provider as an unsupported row. Either way nothing is written.
        var early = await Record.ExceptionAsync(() => host.WhatIfAsync(expression));
        if (early is null)
        {
            var whatIf = await host.WhatIfAsync(expression);
            var row = whatIf.ChangeOfType(FicType);
            Assert.True(row.HasError);
            Assert.Contains("is not valid", row.UnsupportedReason, StringComparison.Ordinal);

            var run = await host.CreateAsync(expression);
            Assert.Equal(DeploymentStatus.Failed, run.Executed.Status);
            Assert.DoesNotContain(run.Writes, w => w.Path.Contains("federatedIdentityCredentials", StringComparison.Ordinal));
        }
        else
        {
            Assert.IsType<ValidationFailedException>(early);
        }

        Assert.DoesNotContain(host.Fake.Writes, w => w.Path.Contains("federatedIdentityCredentials", StringComparison.Ordinal));
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

        var deletion = await host.DeleteDeclaredAsync(host.Resources(FlatTemplate));

        Assert.True(deletion.Succeeded, string.Join("; ", deletion.Outcomes.Select(o => o.Target.Instance.Id.Id + "=" + o.Status + " " + o.Error)));
        var writes = host.Fake.Writes;
        Assert.Equal(3, writes.Count);
        Assert.All(writes, w => Assert.Equal("DELETE", w.Method));
        Assert.StartsWith("/applications/" + appId + "/federatedIdentityCredentials(name='github-", writes[0].Path, StringComparison.Ordinal);
        Assert.StartsWith("/applications/" + appId + "/federatedIdentityCredentials(name='github-", writes[1].Path, StringComparison.Ordinal);
        Assert.Equal("/applications/" + appId, writes[2].Path);
        Assert.Equal(0, host.Directory.Count("/applications"));

        // Review 3I (intentional change): a parent known only by its uniqueName is resolved to its object id first (Learn documents the credential endpoints
        // under /applications/{id} and /applications(appId=...), not under the uniqueName key), so a parent that is gone means nothing to delete and no write.
        var provider = host.Provider(FicType);
        var entry = new DeleteTarget { Id = ResourceIdentifier.Create(FicType, AppName + "/github-main"), Type = FicType, ApiVersion = ProviderHost.ApiVersion, Key = new Dictionary<string, string> { ["name"] = "github-main" }, GraphId = "whatever" };
        host.ClearRequests();
        await provider.DeleteAsync(entry.ResourceKey, entry.Live, host.Fake, ProviderHost.Ct);
        Assert.Empty(host.Fake.Writes);
        Assert.Contains(host.Fake.Requests, r => r.Method == "GET" && r.Path == "/applications(uniqueName='test-api')");
    }

    [Fact]
    public async Task ExternalParent_UsesAlternateKeyPaths_AndRecordsTheParentId()
    {
        var host = new ProviderHost();
        string appId = host.Directory.AddApplication(AppName, "Seeded API")["id"]!.GetValue<string>();
        var document = host.Resources(Fic("github-main", "repo:contoso/orders:ref:refs/heads/main"));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        // Review 3I (intentional change): the parent known only by uniqueName is resolved to its object id first; every credential call then uses
        // /applications/{id}/..., a form Learn documents.
        var requests = run.Requests;
        Assert.Equal("GET", requests[0].Method);
        Assert.Equal("/applications(uniqueName='test-api')", requests[0].Path);
        Assert.Equal("id", requests[0].UrlParameters!["$select"]);
        string parentPath = "/applications/" + appId;
        Assert.Equal("GET", requests[1].Method);
        Assert.Equal(parentPath + "/federatedIdentityCredentials(name='github-main')", requests[1].Path);
        Assert.Equal("id,name,issuer,subject,audiences,description", requests[1].UrlParameters!["$select"]);
        Assert.Equal("GET", requests[2].Method);
        Assert.Equal(parentPath + "/federatedIdentityCredentials", requests[2].Path);
        var upsert = Assert.Single(run.Writes);
        Assert.Equal("PATCH", upsert.Method);
        Assert.Equal(parentPath + "/federatedIdentityCredentials(name='github-main')", upsert.Path);
        Assert.True(upsert.HasHeader("Prefer", "create-if-missing"));
        Assert.Equal(RetryHint.Default, upsert.Hint);
        Assert.DoesNotContain(requests, r => r.Path.Contains("(uniqueName='test-api')/", StringComparison.Ordinal));
        string ficId = run.GraphIdOf(AppResourceId + "/federatedIdentityCredentials/github-main");
        Assert.Equal(1, host.Directory.Count("/applications/" + appId + "/federatedIdentityCredentials"));

        var rerun = await host.CreateAsync(document);
        Assert.Equal(ChangeType.NoChange, rerun.Executed.Operations[0].ChangeType);
        Check.NoWrites(rerun);
        Assert.Contains(rerun.Requests, r => r.Path == parentPath + "/federatedIdentityCredentials(name='github-main')");
        Assert.DoesNotContain(rerun.Requests, r => r.Path == parentPath + "/federatedIdentityCredentials/" + ficId);
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

    /// <summary>
    /// Graph refuses concurrent writes to the credentials of one application (a real-tenant run got "Request contains a property with duplicate values"
    /// for the second of two parallel upserts with different subjects): the fake refuses an overlapping write the same way, and the provider must
    /// write the credentials of one application one at a time even when the executor applies them in parallel.
    /// </summary>
    [Fact]
    public async Task CopyLoopCredentials_OfOneApplication_AreWrittenOneAtATime_EvenInParallel()
    {
        var host = new ProviderHost();
        int inFlight = 0;
        int maxInFlight = 0;
        var hooked = new HookedGraphClient(host.Fake)
        {
            Before = async (method, request) =>
            {
                if (method != "PATCH" || !request.Path.Contains("/" + FederatedIdentityCredentialsProvider.ChildSegment, StringComparison.Ordinal))
                {
                    return;
                }

                int now = System.Threading.Interlocked.Increment(ref inFlight);
                if (now > 1)
                {
                    System.Threading.Interlocked.Decrement(ref inFlight);
                    throw new GraphException(System.Net.HttpStatusCode.BadRequest, "Request_BadRequest", "Request contains a property with duplicate values.", request.Hint);
                }

                maxInFlight = Math.Max(maxInFlight, now);
                await Task.Delay(TimeSpan.FromMilliseconds(40));
            },
            After = (method, request) =>
            {
                if (method == "PATCH" && request.Path.Contains("/" + FederatedIdentityCredentialsProvider.ChildSegment, StringComparison.Ordinal))
                {
                    System.Threading.Interlocked.Decrement(ref inFlight);
                }

                return Task.CompletedTask;
            },
        };

        var plan = await host.Planner().PlanAsync(host.Resources(CopyTemplate(4)), DeploymentMode.Incremental, PlanMode.Create, ProviderHost.Ct);
        var executor = new DeploymentExecutor(host.Registry, hooked, host.Logs.For<DeploymentExecutor>(), new ReplicationWait { Delay = host.ReplicationDelays }, host.AbsenceCheck);
        var result = await executor.ExecuteAsync(plan, new ExecuteOptions { Parallelism = 4 }, ProviderHost.Ct);

        Assert.True(result.Succeeded, string.Join("; ", result.Operations.Where(o => o.Error is not null).Select(o => o.Id.Id + ": " + o.Error)));
        Assert.Equal(1, maxInFlight);
        Assert.Equal(4, host.Directory.Objects("/applications/" + host.IdOf("/applications", "uniqueName", AppName) + "/federatedIdentityCredentials").Count);
    }
}
