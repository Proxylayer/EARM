using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Graph;
using EArm.Engine;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Applications, service principals, federated identity credentials and client secrets against the real tenant:
/// create, what-if (NoChange), drift, Complete mode (report only, never deletes), deployment delete, secure outputs. Skipped without <c>EARM_IT_TENANT</c>.
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class ApplicationScenarioTests : IntegrationTestBase
{
    public ApplicationScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_Applications_RoundTripWithServicePrincipalFicAndSecret()
    {
        Fixture.Require();
        var run = Fixture.NewRun("apps", "app-roundtrip.entra.json");
        string appName = Fixture.Name("app");
        string appResource = "/providers/Microsoft.Entra/applications/" + appName;
        string secretFile = Path.Combine(Fixture.ScratchDirectory, "apps.secrets.json");

        // 1. Create: application + SP + FIC + client secret.
        var created = await Fixture.Cli.CreateAsync(run, Ct, "--secure-outputs-file", secretFile);
        AssertCreated(created, "create");
        Assert.Equal("***", (string?)created.Json["outputs"]?["clientSecret"]?["value"]);
        var secrets = SecureOutputs(secretFile);
        Assert.False(string.IsNullOrWhiteSpace((string?)secrets["clientSecret"]?["value"]), "The secure outputs file holds no clientSecret.");

        var application = await ApplicationByNameAsync(appName, "id", "appId", "displayName", "uniqueName", "identifierUris", "appRoles");
        Assert.NotNull(application);
        string appObjectId = application.RequireId();
        string appId = application.Text("appId")!;
        Assert.Equal(appName, application.Text("displayName"));
        Assert.Contains("api://" + appId, application.Strings("identifierUris"));
        Assert.Equal(appId, (string?)created.Json["outputs"]?["appId"]?["value"]);

        var servicePrincipal = await FindOneEventuallyAsync("/servicePrincipals", "appId", appId, found => found is not null, "id", "appId", "displayName", "appRoleAssignmentRequired");
        Assert.NotNull(servicePrincipal);
        Assert.Equal(servicePrincipal.RequireId(), (string?)created.Json["outputs"]?["servicePrincipalId"]?["value"]);

        var credentials = await Graph.GetObjectAsync(new GraphRequest("/applications/" + appObjectId).WithSelect("id", "passwordCredentials"), Ct);
        // The provider writes the rotation tag into the displayName ("ci-secret#v1").
        Assert.Single(
            credentials!["passwordCredentials"]!.AsArray().OfType<JsonObject>(),
            c => c.Text("displayName") is { } displayName
                && PasswordCredentialName.Parse(displayName) is { Base: "ci-secret", Tag: "v1" });
        var federated = await Graph.ListAsync(new GraphRequest($"/applications/{appObjectId}/federatedIdentityCredentials"), Ct);
        Assert.Contains(federated, f => f.Text("name") == "github-main");

        // 2. What-if right after the create: nothing to do.
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        // 3. A rerun succeeds, rotates nothing and reports the one-time secret as unavailable.
        var rerun = await Fixture.Cli.CreateAsync(run, Ct);
        AssertCreated(rerun, "rerun");
        Assert.True(rerun.HasDiagnostic(EngineDiagnosticCodes.SecretNotAvailableOnRerun), "The rerun did not report SecretNotAvailableOnRerun: " + Tail(rerun.StandardOutput));
        Assert.Null(rerun.Json["outputs"]?["clientSecret"]?["value"]);

        // 4. Drift: change the live application behind EARM's back; what-if reports Modify, create repairs it.
        await Graph.PatchAsync(new GraphRequest("/applications/" + appObjectId), new JsonObject { ["displayName"] = appName + "-drifted" }, Ct);
        AssertChange(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), appResource, "Modify", "what-if after drift");
        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create after drift");
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after repair");
        Assert.Equal(appName, (await ApplicationByNameAsync(appName, "displayName"))!.Text("displayName"));

        // 5. Complete mode with the application alone never deletes: the service principal, the credential and the FIC stay (their types are not declared).
        var appOnly = TemplateEditor.Retain(TemplateEditor.Load(run.TemplateFile), r => TemplateEditor.IsType(r, "Microsoft.Entra/applications"));
        var completeRun = run.WithTemplate(TemplateEditor.Write(Path.Combine(Fixture.ScratchDirectory, "variants"), "apps-app-only.entra.json", appOnly));
        var preview = await Fixture.Cli.WhatIfAsync(completeRun, Ct, "--mode", "Complete", "--exit-code-on-changes");
        AssertExit(0, preview, "Complete what-if");
        Assert.DoesNotContain(preview.Changes, c => (string?)c["changeType"] == "Delete");
        Assert.Equal("NoChange", preview.ChangeTypeOf(appResource));

        var complete = await Fixture.Cli.CreateAsync(completeRun, Ct, "--mode", "Complete");
        AssertCreated(complete, "Complete create");
        Assert.NotNull(await FindOneAsync("/servicePrincipals", "appId", appId, "id"));

        // 6. deployment delete removes what the full template declares, children first.
        var deleted = await Fixture.Cli.DeleteAllAsync(run, Ct);
        AssertExit(0, deleted, "deployment delete");
        Assert.Null(await ApplicationByNameAsync(appName, "id"));
        Assert.Null(await FindOneEventuallyAsync("/servicePrincipals", "appId", appId, found => found is null, "id"));
    }
}
