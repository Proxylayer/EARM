using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Secrets;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

public sealed partial class Review4K_ExecutorTests
{
    private sealed record Tenant(ProviderHost Host, string ResourceId, string UserId);

    private static Tenant NewTenant()
    {
        var host = new ProviderHost();
        string resourceId = host.Directory.AddServicePrincipal(ResourceAppId, "Orders API", sp => sp["appRoles"] = new JsonArray(
            Role(UserRoleId, "Orders.UserOnly"),
            Role(OtherRoleId, "Orders.Other"),
            Role(BothRoleId, "Orders.Both")))["id"]!.GetValue<string>();
        string user = host.Directory.AddUser("bob@contoso.com", "Bob")["id"]!.GetValue<string>();
        return new Tenant(host, resourceId, user);
    }

    private static JsonObject Role(string id, string value)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["value"] = value,
            ["isEnabled"] = true,
            ["allowedMemberTypes"] = new JsonArray("User", "Application"),
        };
    }

    private static string Assignment(Tenant t, string name, string roleId)
    {
        return Templates.Resource(AssignmentType, name, "{ \"principalId\": \"" + t.UserId + "\", \"resourceId\": \"" + t.ResourceId + "\", \"appRoleId\": \"" + roleId + "\" }");
    }

    private static string AssignedTo(string resourceId)
    {
        return "/servicePrincipals/" + resourceId + "/appRoleAssignedTo";
    }

    private static string AssignmentResourceId(string name)
    {
        return "/providers/" + AssignmentType + "/" + name;
    }

    private static async Task TamperAsync(ProviderHost host, string resourceId, Func<StateEntry, StateEntry> change)
    {
        var state = (await host.LoadStateAsync())!;
        state.Upsert(change(state.Find(resourceId)!));
        await host.Store.SaveAsync(state, Ct);
    }

    // ---- DE18 / DE20 / DE21: a superseded object is replaced only when this deployment recorded it ----

    [Fact]
    public async Task ASupersededObject_IsReplaced_AndItsEntryStaysCreated()
    {
        var t = NewTenant();
        string name = Guid.NewGuid().ToString("D");
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(Assignment(t, name, UserRoleId))));
        string oldId = t.Host.Directory.Objects(AssignedTo(t.ResourceId)).Single()["id"]!.GetValue<string>();

        var run = await t.Host.CreateAsync(t.Host.Resources(Assignment(t, name, BothRoleId)));

        Check.Succeeded(run);
        Assert.Contains(run.Writes, w => w.Method == "DELETE" && w.Path.EndsWith(oldId, StringComparison.Ordinal));
        var remaining = Assert.Single(t.Host.Directory.Objects(AssignedTo(t.ResourceId)));
        Assert.Equal(BothRoleId, remaining["appRoleId"]!.GetValue<string>());
        var entry = run.Entry(AssignmentResourceId(name));
        Assert.Equal(StateProvenance.Created, entry.Provenance);
        Assert.Equal(remaining["id"]!.GetValue<string>(), entry.GraphId);
    }

    [Fact]
    public async Task AnEntryWhoseRecordedKey_TheLiveObjectDoesNotCarry_NeverSupersedesIt_TheObjectIsLeftAlone()
    {
        var t = NewTenant();
        string name = Guid.NewGuid().ToString("D");
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(Assignment(t, name, UserRoleId))));
        string oldId = t.Host.Directory.Objects(AssignedTo(t.ResourceId)).Single()["id"]!.GetValue<string>();

        // The entry now says the recorded app role was another one: whatever the pinned id points at is not what this deployment recorded.
        await TamperAsync(t.Host, AssignmentResourceId(name), e => e with { Key = new Dictionary<string, string>(e.Key, StringComparer.OrdinalIgnoreCase) { ["appRoleId"] = OtherRoleId } });

        var run = await t.Host.CreateAsync(t.Host.Resources(Assignment(t, name, BothRoleId)));

        Check.Succeeded(run);
        Assert.DoesNotContain(run.Writes, w => w.Method == "DELETE");
        Assert.Equal(2, t.Host.Directory.Objects(AssignedTo(t.ResourceId)).Count);
        Assert.NotNull(t.Host.Directory.Objects(AssignedTo(t.ResourceId)).SingleOrDefault(o => o["id"]!.GetValue<string>() == oldId));
    }

    [Fact]
    public async Task AnAdoptedEntry_NeverSupersedesTheObject_ItIsNotDeletedWithoutDeleteAdopted()
    {
        var t = NewTenant();
        string name = Guid.NewGuid().ToString("D");
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(Assignment(t, name, UserRoleId))));
        await TamperAsync(t.Host, AssignmentResourceId(name), e => e with { Provenance = StateProvenance.Adopted });

        var run = await t.Host.CreateAsync(t.Host.Resources(Assignment(t, name, BothRoleId)));

        Check.Succeeded(run);
        Assert.DoesNotContain(run.Writes, w => w.Method == "DELETE");
        Assert.Equal(2, t.Host.Directory.Objects(AssignedTo(t.ResourceId)).Count);
    }

    // ---- DE26: the replacement exists, the old object cannot be deleted ----

    [Fact]
    public async Task AReplacementThatWasCreated_WhoseOldObjectCannotBeDeleted_IsRecorded_AndTheResourceFails()
    {
        var t = NewTenant();
        string name = Guid.NewGuid().ToString("D");
        Check.Succeeded(await t.Host.CreateAsync(t.Host.Resources(Assignment(t, name, UserRoleId))));
        string oldId = t.Host.Directory.Objects(AssignedTo(t.ResourceId)).Single()["id"]!.GetValue<string>();
        t.Host.Fake.Failures.Forbid("appRoleAssignedTo", "DELETE");

        var run = await t.Host.CreateAsync(t.Host.Resources(Assignment(t, name, BothRoleId)));

        Assert.False(run.Succeeded);
        var operation = run.Op(name);
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Contains("the replacement was created", operation.Error, StringComparison.Ordinal);
        Assert.Contains("could not be deleted", operation.Error, StringComparison.Ordinal);
        Assert.Contains(oldId, operation.Error, StringComparison.Ordinal);
        var newObject = t.Host.Directory.Objects(AssignedTo(t.ResourceId)).Single(o => o["appRoleId"]!.GetValue<string>() == BothRoleId);
        Assert.Equal(newObject["id"]!.GetValue<string>(), run.Entry(AssignmentResourceId(name)).GraphId);
        Assert.Equal(2, t.Host.Directory.Objects(AssignedTo(t.ResourceId)).Count);
    }

    // ---- DE22 / DE24: replacing an adopted object ----

    [Fact]
    public async Task AnAdoptedObject_IsReplacedOnlyWithDeleteAdopted_AndTheNewEntryIsCreated()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(IpLocation())));
        string oldId = host.IdOf(LocPath, "displayName", "Contoso offices");
        await TamperAsync(host, "/providers/" + LocType + "/Contoso offices", e => e with { Provenance = StateProvenance.Adopted });

        var refused = await ExecuteAsync(host, host.Resources(CountryLocation()));

        var refusedOperation = Assert.Single(refused.Result.Operations);
        Assert.Equal(OperationStatus.Failed, refusedOperation.Status);
        Assert.Equal(EngineDiagnosticCodes.AdoptedObjectNotDeleted, refusedOperation.ErrorCode);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Equal(oldId, host.IdOf(LocPath, "displayName", "Contoso offices"));

        var replaced = await ExecuteAsync(host, host.Resources(CountryLocation()), execute: new ExecuteOptions { Parallelism = 1, DeleteAdopted = true });

        Assert.True(replaced.Result.Succeeded, string.Join("\n", replaced.Result.Operations.Select(o => o.Error)));
        string newId = host.IdOf(LocPath, "displayName", "Contoso offices");
        Assert.NotEqual(oldId, newId);
        var entry = (await StateOfAsync(host)).Find("/providers/" + LocType + "/Contoso offices")!;
        Assert.Equal(newId, entry.GraphId);
        Assert.Equal(StateProvenance.Created, entry.Provenance);
    }

    [Fact]
    public async Task ADependentOfAReplacedObject_IsWrittenUnderTheReplicationLagHint()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(IpLocation())));
        host.ClearRequests();
        var document = host.Resources(CountryLocation() + ", " + Group("dep", extraResource: Templates.DependsOn(LocType, "Contoso offices")));

        var outcome = await ExecuteAsync(host, document);

        Assert.True(outcome.Result.Succeeded, string.Join("\n", outcome.Result.Operations.Select(o => o.Error)));
        Assert.Contains(host.Fake.Writes, w => w.Method == "DELETE");
        var dependent = Assert.Single(host.Fake.Writes, w => w.Method == "PATCH" && w.Path.Contains("uniqueName='dep'", StringComparison.Ordinal));
        Assert.Equal(RetryHint.ExpectReplicationLag, dependent.Hint);
    }

    // ---- DE31: the entry describes the object only when it pins this very id ----

    [Fact]
    public async Task AnEntryThatPinsAnotherId_DoesNotVouchForTheObjectFound_SoItIsNotReplacedWithoutDeleteAdopted()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(IpLocation())));
        await TamperAsync(host, "/providers/" + LocType + "/Contoso offices", e => e with { GraphId = Guid.NewGuid().ToString("D") });

        var outcome = await ExecuteAsync(host, host.Resources(CountryLocation()));

        var operation = Assert.Single(outcome.Result.Operations);
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Equal(EngineDiagnosticCodes.AdoptedObjectNotDeleted, operation.ErrorCode);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Equal("#microsoft.graph.ipNamedLocation", host.Directory.Find(LocPath, "displayName", "Contoso offices")!["@odata.type"]!.GetValue<string>());
    }

    // ---- DE32 / DE33: the signed-in principal is never replaced, whatever the case of its id ----

    [Fact]
    public async Task TheSignedInPrincipal_IsNeverReplaced_WhateverTheCaseOfItsId()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(IpLocation())));
        host.SignedInPrincipalId = host.IdOf(LocPath, "displayName", "Contoso offices").ToUpperInvariant();

        var run = await host.CreateAsync(host.Resources(CountryLocation()));

        Assert.False(run.Succeeded);
        var operation = run.OpOfType(LocType);
        Assert.Equal(EngineDiagnosticCodes.ProtectedObject, operation.ErrorCode);
        Assert.Contains("Nothing was changed", operation.Error, StringComparison.Ordinal);
        Assert.DoesNotContain(run.Writes, w => w.Method is "DELETE" or "POST");
    }

    // ---- DE34: an old object that is already gone is no failure ----

    [Fact]
    public async Task AnOldObjectThatVanishedBeforeTheReplacement_IsNoFailure_AndIsNotDeletedAgain()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(IpLocation())));
        string oldId = host.IdOf(LocPath, "displayName", "Contoso offices");
        host.ClearRequests();
        int deletes = 0;
        var logger = new HookLogger<DeploymentExecutor>(id =>
        {
            // "Applying <id>: Modify (Recreate)" is written after the plan and before the replacement starts: someone deletes the object right then.
            if (id.Id == 2020 && Interlocked.Increment(ref deletes) == 1)
            {
                host.Fake.DeleteAsync(new GraphRequest(LocPath + "/" + oldId, GraphApi.V1), Ct).GetAwaiter().GetResult();
            }
        });

        var outcome = await ExecuteAsync(host, host.Resources(CountryLocation()), logger: logger);

        Assert.True(outcome.Result.Succeeded, string.Join("\n", outcome.Result.Operations.Select(o => o.Error)));
        Assert.Single(host.Fake.Writes, w => w.Method == "DELETE");
        Assert.Equal("#microsoft.graph.countryNamedLocation", host.Directory.Find(LocPath, "displayName", "Contoso offices")!["@odata.type"]!.GetValue<string>());
    }

    // ---- DE28 / DE54: the look for an object after an uncertain create ----

    private static string AmbiguousLocationDocument()
    {
        return IpLocation();
    }

    private static HookedGraphClient CommitThenTimeout(ProviderHost host, Action? afterCommit = null)
    {
        return new HookedGraphClient(host.Fake)
        {
            After = (method, request) =>
            {
                if (method == "POST" && request.Path == LocPath)
                {
                    afterCommit?.Invoke();
                    throw new GraphTransportException(GraphTransportFailure.Timeout, "the request to graph.microsoft.com did not answer in time.", ambiguous: true);
                }

                return Task.CompletedTask;
            },
        };
    }

    [Fact]
    public async Task TheLookAfterAnUncertainCreate_WaitsOutReplicationLag()
    {
        var host = new ProviderHost();
        var hooked = CommitThenTimeout(host, () => host.Fake.Failures.Add("GET", "namedLocations", _ => new GraphException(HttpStatusCode.NotFound, "Request_ResourceNotFound", "Resource does not exist or one of its queried reference-property objects are not present.", RetryHint.ExpectReplicationLag), 1));

        var outcome = await ExecuteAsync(host, host.Resources(AmbiguousLocationDocument()), graph: hooked);

        Assert.True(outcome.Result.Succeeded, string.Join("\n", outcome.Result.Operations.Select(o => o.Error)));
        Assert.Contains(outcome.Result.Diagnostics, d => d.Code == EngineDiagnosticCodes.CreateOutcomeUncertain);
        Assert.Equal(1, host.Directory.Count(LocPath));
        Assert.Equal(host.IdOf(LocPath, "displayName", "Contoso offices"), outcome.State.Find("/providers/" + LocType + "/Contoso offices")!.GraphId);
    }

    [Fact]
    public async Task AFailureOfTheLookItself_LeavesTheOriginalAmbiguousFailureAsTheOneReported()
    {
        var host = new ProviderHost();
        var hooked = CommitThenTimeout(host, () => host.Fake.Failures.Add("GET", "namedLocations", _ => new ProviderException(ProviderException.AmbiguousMatch, "the look exploded"), -1));

        var outcome = await ExecuteAsync(host, host.Resources(AmbiguousLocationDocument()), graph: hooked);

        var operation = Assert.Single(outcome.Result.Operations);
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Contains("did not answer in time", operation.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("the look exploded", operation.Error, StringComparison.Ordinal);
    }

    // ---- DE36 / DE37: the secure outputs file in the result ----

    private static string GeneratedPasswordUser()
    {
        return DirectoryTemplates.Alice();
    }

    [Fact]
    public async Task TheResult_NamesTheSecureOutputsFile_WhenOnlyAOneTimeSecretWasWritten()
    {
        var host = new ProviderHost();
        using var directory = new TestDirectory();
        string path = directory.File("secure.json");

        var run = await host.CreateAsync(host.Resources(GeneratedPasswordUser()), secureOutputsFile: path);

        Check.Succeeded(run);
        Assert.Equal(path, run.Executed.SecureOutputsFile);
        Assert.Contains("generatedPassword", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResult_NamesTheSecureOutputsFile_WhenASecureOutputWasWritten()
    {
        var host = new ProviderHost();
        using var directory = new TestDirectory();
        string path = directory.File("secure.json");
        string outputs = "\"initial\": { \"type\": \"securestring\", \"value\": \"[reference('alice@contoso.com').passwordProfile.password]\" }";

        var run = await host.CreateAsync(host.Resources(GeneratedPasswordUser(), outputs), secureOutputsFile: path);

        Check.Succeeded(run);
        Assert.Equal(path, run.Executed.SecureOutputsFile);
        Assert.Contains("\"initial\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheResult_NamesNoSecureOutputsFile_WhenNothingSecretWasWritten()
    {
        var host = new ProviderHost();
        using var directory = new TestDirectory();
        string path = directory.File("secure.json");

        var run = await host.CreateAsync(host.Resources(Group("plain")), secureOutputsFile: path);

        Check.Succeeded(run);
        Assert.Null(run.Executed.SecureOutputsFile);
        Assert.False(File.Exists(path));
    }

    // ---- DE38: an output that carries a secret is secure whatever it is declared as ----

    [Fact]
    public async Task AnOutputDerivedFromASecret_IsSecure_EvenWhenDeclaredPlain()
    {
        var host = new ProviderHost();
        using var directory = new TestDirectory();
        string path = directory.File("secure.json");
        string outputs = "\"initial\": { \"type\": \"string\", \"value\": \"[reference('alice@contoso.com').passwordProfile.password]\" }";

        var run = await host.CreateAsync(host.Resources(GeneratedPasswordUser(), outputs), secureOutputsFile: path);

        Check.Succeeded(run);
        Assert.Equal("***", run.Executed.Outputs["initial"]!["value"]!.GetValue<string>());
        Assert.NotNull(run.SecureOutput("initial"));
        Assert.Contains("\"initial\"", File.ReadAllText(path), StringComparison.Ordinal);
    }

    // ---- DE45: one object claimed twice under another spelling of its id ----

    [Fact]
    public async Task TwoDeclarationsThatResolveToOneObject_AreADuplicate_WhateverTheCaseOfItsId()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(Group("a"))));
        var original = host.Directory.Find("/groups", "uniqueName", "a")!;
        string aId = original["id"]!.GetValue<string>();
        var hooked = new HookedGraphClient(host.Fake)
        {
            RewriteGet = (request, result) =>
            {
                if (!request.Path.Contains("uniqueName='b'", StringComparison.Ordinal))
                {
                    return result;
                }

                var alias = (JsonObject)original.DeepClone();
                alias["id"] = aId.ToUpperInvariant();
                alias["uniqueName"] = "b";
                return alias;
            },
        };

        var outcome = await ExecuteAsync(host, host.Resources(Group("a") + ", " + Group("b")), graph: hooked);

        Assert.Contains(outcome.Result.Operations, o => o.Status == OperationStatus.Failed && o.ErrorCode == EngineDiagnosticCodes.DuplicateGraphObject);
    }

    // ---- DE47 / DE48: values derived from a secret, and secrets handed back by a provider, are masked everywhere ----

    [Fact]
    public async Task AValueDerivedFromASecureParameter_IsMaskedInTheErrorOfTheResourceThatEchoesIt()
    {
        using var scope = SecretScrubber.Begin();
        var host = new ProviderHost();
        const string Derived = "AB12-XYZ-9TQ";
        host.Fake.Failures.Add("PATCH", "uniqueName='leaky'", request => new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "denied for " + Derived, request.Hint), -1);
        var document = host.Resources(
            Templates.Resource(GroupType, "leaky", "{ \"displayName\": \"[toUpper(parameters('pw'))]\", \"mailEnabled\": false, \"securityEnabled\": true }"),
            parameters: "\"pw\": { \"type\": \"securestring\" }",
            values: [("pw", "ab12-xyz-9tq")]);

        var outcome = await ExecuteAsync(host, document);

        var operation = Assert.Single(outcome.Result.Operations);
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.DoesNotContain(Derived, operation.Error, StringComparison.Ordinal);
        Assert.Contains("***", operation.Error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ASecretThatAProviderHandedBack_IsMaskedInTheErrorOfALaterResource()
    {
        using var scope = SecretScrubber.Begin();
        var host = new ProviderHost();
        string? secret = null;
        var hooked = new HookedGraphClient(host.Fake) { OnPostResponse = (request, response) => secret = request.Path.EndsWith("/addPassword", StringComparison.Ordinal) ? response.Body?["secretText"]?.GetValue<string>() : secret };
        host.Fake.Failures.Add("PATCH", "uniqueName='later'", request => new GraphException(HttpStatusCode.Forbidden, "Authorization_RequestDenied", "denied for " + secret, request.Hint), -1);
        string password = Templates.Resource("Microsoft.Entra/applications/passwordCredentials", "first/ci", "{ }");
        var document = host.Resources(App("first") + ", " + password + ", " + Group("later", extraResource: ", \"dependsOn\": [\"[resourceId('Microsoft.Entra/applications/passwordCredentials', 'first', 'ci')]\"]"));

        var outcome = await ExecuteAsync(host, document, graph: hooked);

        Assert.NotNull(secret);
        var operation = outcome.Result.Operations.Single(o => o.Id.Id == GroupResourceId("later"));
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.DoesNotContain(secret, operation.Error, StringComparison.Ordinal);
        Assert.Contains("***", operation.Error, StringComparison.Ordinal);
    }
}
