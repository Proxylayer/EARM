using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core;
using EntraArm.Core.Secrets;
using EntraArm.Engine;
using EntraArm.Engine.Execution;
using EntraArm.Engine.Planning;
using EntraArm.Engine.Providers;
using EntraArm.TestSupport;
using Xunit;

namespace EntraArm.Tests.Engine;

/// <summary>Execution (plan section 2.4, steps 6-7) of the engine fixture against <see cref="FakeGraphClient"/> through <see cref="EchoProvider"/>.</summary>
public sealed class DeploymentExecutorTests
{
    private static ResourceOperation Operation(DeploymentResult result, string resourceId)
    {
        var operation = result.Operations.FirstOrDefault(o => o.Id.Id == resourceId);
        Assert.NotNull(operation);
        return operation;
    }

    private static ResourceOperation OperationOfType(DeploymentResult result, string type)
    {
        return Assert.Single(result.Operations, o => o.Type == type);
    }

    private static string Value(JsonObject outputs, string name)
    {
        return outputs[name]!["value"]!.GetValue<string>();
    }

    // ---- full create ----

    [Fact]
    public async Task FullCreate_AppliesEveryInstance_InBatchOrder()
    {
        var host = new EngineHost();
        host.SeedExisting();

        var (plan, result) = await host.CreateAsync(await Templates.FixtureAsync());

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Where(o => o.Status != OperationStatus.Succeeded).Select(o => o.Id.Id + ": " + o.Error)));
        Assert.Equal(DeploymentStatus.Succeeded, result.Status);
        Assert.Equal(EngineHost.DeploymentName, result.DeploymentName);
        Assert.Equal(Fixture.InstanceCount, result.Operations.Count);
        Assert.Equal(15, result.SucceededCount);
        Assert.Equal(1, result.IgnoredCount);
        Assert.Equal(0, result.FailedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(0, result.DetachedCount);
        Assert.Equal(0, result.DeletedCount);
        Assert.DoesNotContain(result.Diagnostics, d => d.IsError);
        Assert.Empty(plan.Changes);
        Assert.Equal(Fixture.InstanceCount, result.Changes.Count);
        Assert.All(result.Operations.Where(o => o.Status == OperationStatus.Succeeded), o =>
        {
            Assert.Equal(ChangeType.Create, o.ChangeType);
            Assert.Equal(ChangeMode.Create, o.Mode);
            Assert.False(string.IsNullOrEmpty(o.GraphId));
        });
        Assert.Equal(OperationStatus.Ignored, Operation(result, Fixture.Policy).Status);
        Assert.Equal(1, Operation(result, Fixture.User).Batch);
        Assert.Equal(5, Operation(result, Fixture.Policy).Batch);

        var directory = host.Directory;
        var app = Assert.Single(directory.Objects("/applications"));
        Assert.Equal("contoso-dev-api", app["uniqueName"]!.GetValue<string>());
        Assert.Equal(2, directory.Count("/applications/" + app["id"] + "/federatedIdentityCredentials"));
        Assert.Single(app["passwordCredentials"]!.AsArray());
        Assert.Equal(2, directory.Count("/servicePrincipals"));
        Assert.NotNull(directory.Find("/servicePrincipals", "appId", app["appId"]!.GetValue<string>()));
        Assert.Equal(2, directory.Count("/users"));
        Assert.Equal(2, directory.Count("/groups"));
        Assert.Equal(1, directory.Count("/roleManagement/directory/roleDefinitions"));
        Assert.Equal(1, directory.Count("/policies/authenticationStrengthPolicies"));
        Assert.Equal(1, directory.Count("/identity/conditionalAccess/authenticationContextClassReferences"));
        Assert.Equal(1, directory.Count("/identity/conditionalAccess/namedLocations"));
        Assert.Equal(0, directory.Count("/identity/conditionalAccess/policies"));
        Assert.Equal(1, directory.Count("/directory/administrativeUnits"));
        Assert.Equal(1, directory.Count("/oauth2PermissionGrants"));
        Assert.Equal(1, directory.Count("/roleManagement/directory/roleAssignments"));
        var sp = directory.Find("/servicePrincipals", "appId", app["appId"]!.GetValue<string>())!;
        Assert.Equal(1, directory.Count("/servicePrincipals/" + sp["id"] + "/appRoleAssignedTo"));
        Assert.Equal("c10", directory.Objects("/identity/conditionalAccess/authenticationContextClassReferences")[0]["id"]!.GetValue<string>());

        // Batch boundaries: the user lands before the application, the application before its children and the SP.
        var writes = host.Fake.Writes;
        int userWrite = writes.ToList().FindIndex(w => w.Path == "/users");
        int appWrite = writes.ToList().FindIndex(w => w.Path.StartsWith("/applications(uniqueName=", StringComparison.Ordinal));
        int spWrite = writes.ToList().FindIndex(w => w.Path == "/servicePrincipals");
        int ficWrite = writes.ToList().FindIndex(w => w.Path.Contains("/federatedIdentityCredentials(", StringComparison.Ordinal));
        Assert.True(userWrite >= 0 && appWrite > userWrite && spWrite > appWrite && ficWrite > appWrite, string.Join("\n", writes.Select(w => w.ToString())));
    }

    [Fact]
    public async Task FullCreate_PublishesOutputs_RedactingSecureOnesUnlessRevealed()
    {
        var host = new EngineHost();
        host.SeedExisting();

        var (_, redacted) = await host.CreateAsync(await Templates.FixtureAsync());
        var app = Assert.Single(host.Directory.Objects("/applications"));

        Assert.Equal(app["appId"]!.GetValue<string>(), Value(redacted.Outputs, "apiAppId"));
        Assert.Equal("string", redacted.Outputs["apiAppId"]!["type"]!.GetValue<string>());
        Assert.Equal("***", Value(redacted.Outputs, "ciSecret"));
        Assert.Equal("securestring", redacted.Outputs["ciSecret"]!["type"]!.GetValue<string>());
        Assert.StartsWith("fake-secret-", Value(redacted.SecureOutputs, "ciSecret"), StringComparison.Ordinal);
        Assert.False(redacted.SecureOutputs.ContainsKey("apiAppId"));
        Assert.Null(redacted.SecureOutputsFile);
        Assert.DoesNotContain("fake-secret-", redacted.Outputs.ToJsonString(), StringComparison.Ordinal);

        var revealedHost = new EngineHost();
        revealedHost.SeedExisting();
        var (_, revealed) = await revealedHost.CreateAsync(await Templates.FixtureAsync(), new ExecuteOptions { RevealSecureOutputs = true });

        Assert.StartsWith("fake-secret-", Value(revealed.Outputs, "ciSecret"), StringComparison.Ordinal);
        Assert.Equal(Value(revealed.SecureOutputs, "ciSecret"), Value(revealed.Outputs, "ciSecret"));
    }

    [Fact]
    public async Task SecureOutputsFile_HoldsTheRevealedValues_WithOwnerOnlyPermissions()
    {
        using var directory = new TestDirectory(nameof(SecureOutputsFile_HoldsTheRevealedValues_WithOwnerOnlyPermissions));
        string path = directory.File("secure.json");
        var host = new EngineHost();
        host.SeedExisting();

        var (_, result) = await host.CreateAsync(await Templates.FixtureAsync(), new ExecuteOptions { SecureOutputsFile = path });

        Assert.Equal(path, result.SecureOutputsFile);
        Assert.True(File.Exists(path));
        var written = JsonNode.Parse(await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken))!.AsObject();
        Assert.Equal(Value(result.SecureOutputs, "ciSecret"), Value(written, "ciSecret"));
        Assert.StartsWith("fake-secret-", Value(written, "ciSecret"), StringComparison.Ordinal);
        Assert.False(written.ContainsKey("apiAppId"));
        Assert.Equal("***", Value(result.Outputs, "ciSecret"));

        byte[] bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF, "no UTF-8 BOM");

        if (OperatingSystem.IsWindows())
        {
            AssertOwnerOnlyWindows(path);
        }
        else
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
    }

    [SupportedOSPlatform("windows")]
    private static void AssertOwnerOnlyWindows(string path)
    {
        var security = new FileInfo(path).GetAccessControl();
        var rules = security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        using var identity = WindowsIdentity.GetCurrent();

        Assert.True(security.AreAccessRulesProtected, "inheritance must be cut");
        var rule = Assert.Single(rules);
        Assert.Equal(identity.User, rule.IdentityReference);
        Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
        Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
        Assert.False(rule.IsInherited);
    }

    // ---- symbolic names ----

    [Fact]
    public async Task SymbolicServicePrincipal_IsResolvedBeforeItsBatch_AndPublishedUnderBothIds()
    {
        var host = new EngineHost();
        host.SeedExisting();

        var (plan, result) = await host.CreateAsync(await Templates.FixtureAsync());
        string appId = Assert.Single(host.Directory.Objects("/applications"))["appId"]!.GetValue<string>();
        var sp = EngineHost.Instance(plan, Fixture.SpSymbolic);

        Assert.True(result.Succeeded);
        Assert.False(sp.IsSymbolic);
        Assert.Equal(Fixture.Sp(appId), sp.Id.Id);
        Assert.Equal(Fixture.SpSymbolic, sp.PlannedId.Id);
        Assert.Equal(Fixture.Sp(appId), OperationOfType(result, Fixture.SpType).Id.Id);
        Assert.True(plan.Resolver.IsPublished(sp.Id));
        Assert.True(plan.Resolver.IsPublished(sp.PlannedId));
        Assert.True(plan.Resolver.TryGetGraphId(sp.PlannedId, out string bySymbol));
        Assert.True(plan.Resolver.TryGetGraphId(sp.Id, out string byId));
        Assert.Equal(byId, bySymbol);
        Assert.Same(sp, plan.FindInstance(sp.Id));
        Assert.True(plan.Index.Contains(sp.Id));

        // The GUID-named role assignment also has a symbolic name (it hashes the appId) and resolves the same way.
        var roleAssignment = plan.Instances.Single(i => i.Type == Fixture.RoleAssignmentType);
        Assert.False(roleAssignment.IsSymbolic);
        Assert.True(Guid.TryParse(roleAssignment.Id.NameLeaf, out _));
        Assert.Equal(OperationStatus.Succeeded, OperationOfType(result, Fixture.RoleAssignmentType).Status);

        // Dependents that referred to the symbol evaluated against the live object.
        var grant = Assert.Single(host.Directory.Objects("/oauth2PermissionGrants"));
        Assert.Equal(host.Directory.Find("/servicePrincipals", "appId", appId)!["id"]!.GetValue<string>(), grant["clientId"]!.GetValue<string>());
    }

    [Fact]
    public async Task SymbolicDependent_OfAFailedInput_IsSkippedUnderItsSymbolicId()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Fake.Failures.Forbid("/applications(uniqueName=", "PATCH");

        var (_, result) = await host.CreateAsync(await Templates.FixtureAsync(), new ExecuteOptions { ContinueOnError = true });

        Assert.False(result.Succeeded);
        Assert.Equal(OperationStatus.Failed, Operation(result, Fixture.App).Status);
        var sp = OperationOfType(result, Fixture.SpType);
        Assert.Equal(OperationStatus.Skipped, sp.Status);
        Assert.Equal(Fixture.SpSymbolic, sp.Id.Id);
    }

    // ---- replication lag ----

    [Fact]
    public async Task ExpectReplicationLag_IsUsedOnlyWhenADependencyWasCreatedInThisRun()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Directory.AddUser("bg-orders@contoso.com", "Break Glass (Orders)", u =>
        {
            u["mailNickname"] = "bg-orders";
            u["usageLocation"] = "GB";
        });

        var (plan, result) = await host.CreateAsync(await Templates.FixtureAsync());

        Assert.True(result.Succeeded);
        Assert.Equal(ChangeType.NoChange, Operation(result, Fixture.User).ChangeType);
        Assert.Equal(RetryHint.Default, EngineHost.Instance(plan, Fixture.User).RetryHint);
        Assert.Equal(RetryHint.Default, EngineHost.Instance(plan, Fixture.App).RetryHint);
        Assert.Equal(RetryHint.ExpectReplicationLag, EngineHost.Instance(plan, Fixture.SpSymbolic).RetryHint);
        Assert.Equal(RetryHint.ExpectReplicationLag, EngineHost.Instance(plan, Fixture.Fic0).RetryHint);
        Assert.Equal(RetryHint.Default, EngineHost.Instance(plan, Fixture.RoleDefinition).RetryHint);

        var writes = host.Fake.Writes;
        var appWrite = Assert.Single(writes, w => w.Path.StartsWith("/applications(uniqueName=", StringComparison.Ordinal));
        Assert.Equal(RetryHint.Default, appWrite.Hint);
        var spWrite = Assert.Single(writes, w => w.Method == "POST" && w.Path == "/servicePrincipals");
        Assert.Equal(RetryHint.ExpectReplicationLag, spWrite.Hint);
        var ficWrites = writes.Where(w => w.Path.Contains("/federatedIdentityCredentials(", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, ficWrites.Count);
        Assert.All(ficWrites, w => Assert.Equal(RetryHint.ExpectReplicationLag, w.Hint));

        // Reads always use the default hint: a 404 means "create", never "not replicated yet".
        Assert.All(host.Fake.Requests.Where(r => r.Method == "GET"), r => Assert.Equal(RetryHint.Default, r.Hint));
    }

    [Fact]
    public async Task ReplicationFlavouredFailures_AreRetriedWithoutWaiting()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Fake.Failures.ReplicationBadRequest("/servicePrincipals", times: 2, method: "POST");
        host.Fake.Failures.ReplicationLag("/federatedIdentityCredentials", times: 1, method: "PATCH");

        var (_, result) = await host.CreateAsync(await Templates.FixtureAsync());

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Where(o => o.Status == OperationStatus.Failed).Select(o => o.Error)));
        Assert.Equal(3, host.Delays.Delays.Count);
        Assert.All(host.Delays.Delays, d => Assert.Equal(TimeSpan.FromSeconds(5), d));
        Assert.Equal(3, host.Fake.Writes.Count(w => w.Method == "POST" && w.Path == "/servicePrincipals"));
        Assert.Equal(0, host.Fake.Failures.PendingRules);
    }

    [Fact]
    public async Task ReplicationFailure_WithoutTheHint_IsTerminal()
    {
        var host = new EngineHost();
        host.SeedExisting();

        // The user is created in batch 1 with no dependencies: a 404 on its POST is not replication lag.
        host.Fake.Failures.ReplicationLag("/users", times: 1, method: "POST");

        var (_, result) = await host.CreateAsync(await Templates.FixtureAsync());

        Assert.False(result.Succeeded);
        var user = Operation(result, Fixture.User);
        Assert.Equal(OperationStatus.Failed, user.Status);
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, user.ErrorCode);
        Assert.Contains("Request_ResourceNotFound", user.Error);
        Assert.Empty(host.Delays.Delays);
    }

    // ---- idempotency ----

    [Fact]
    public async Task AlreadyExists_AdoptsTheObjectCreatedByARace()
    {
        InterceptingGraphClient? intercept = null;
        var host = new EngineHost(inner => intercept = new InterceptingGraphClient(inner));
        host.SeedExisting();
        bool raced = false;
        intercept!.BeforePost = (request, _) =>
        {
            if (request.Path == "/users" && !raced)
            {
                raced = true;
                host.Directory.AddUser("bg-orders@contoso.com", "Break Glass (Orders)");
                throw new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "Another object with the same value for property userPrincipalName already exists.", request.Hint);
            }

            return Task.CompletedTask;
        };

        var (_, result) = await host.CreateAsync(await Templates.FixtureAsync());

        Assert.True(result.Succeeded);
        var user = Operation(result, Fixture.User);
        Assert.Equal(OperationStatus.Succeeded, user.Status);
        Assert.Equal(ChangeType.Create, user.ChangeType);
        var adopted = Assert.Single(host.Directory.Objects("/users"), u => u["userPrincipalName"]!.GetValue<string>() == "bg-orders@contoso.com");
        Assert.Equal(adopted["id"]!.GetValue<string>(), user.GraphId);
        // The raced POST never reached the fake; the adoption read replaced it.
        Assert.Equal(0, host.Fake.Writes.Count(w => w.Path == "/users"));
        Assert.Empty(host.Delays.Delays);
    }

    [Fact]
    public async Task Rerun_WritesNothing_AndOnlyTheSecretTextOutputCannotBeRecomputed()
    {
        var host = new EngineHost();
        host.SeedExisting();
        var (_, first) = await host.CreateAsync(await Templates.FixtureAsync());
        Assert.True(first.Succeeded);
        host.Fake.ClearRequests();

        var (_, second) = await host.CreateAsync(await Templates.FixtureAsync());

        Assert.Empty(host.Fake.Writes);
        Assert.Equal(15, second.SucceededCount);
        Assert.All(second.Operations.Where(o => o.Status == OperationStatus.Succeeded), o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Assert.Equal(Value(first.Outputs, "apiAppId"), Value(second.Outputs, "apiAppId"));

        // secretText only exists in the creating run (plan section 1.5); the output is reported, not invented.
        Assert.False(second.Outputs.ContainsKey("ciSecret"));
        var diagnostic = Assert.Single(second.Diagnostics, d => d.IsError);
        Assert.Equal(EngineDiagnosticCodes.OutputEvaluationFailed, diagnostic.Code);
        Assert.Contains("ciSecret", diagnostic.Message);
        Assert.Equal(DeploymentStatus.Failed, second.Status);
    }

    // ---- failure handling ----

    [Fact]
    public async Task FailFast_SkipsEveryLaterBatch()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Fake.Failures.Forbid("/roleManagement/directory/roleDefinitions", "POST");

        var (plan, result) = await host.CreateAsync(await Templates.FixtureAsync());

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(4, result.SucceededCount);
        Assert.Equal(Fixture.InstanceCount - plan.Batches[0].Instances.Count, result.SkippedCount);
        Assert.Equal(0, result.IgnoredCount);

        var failed = Operation(result, Fixture.RoleDefinition);
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, failed.ErrorCode);
        Assert.Contains("Authorization_RequestDenied", failed.Error);
        Assert.Equal(ChangeType.Create, failed.ChangeType);

        var skipped = result.Operations.Where(o => o.Status == OperationStatus.Skipped).ToList();
        Assert.All(skipped, o =>
        {
            Assert.Equal(EngineDiagnosticCodes.ResourceSkipped, o.ErrorCode);
            Assert.Equal("an earlier batch failed", o.Error);
            Assert.True(o.Batch > 1);
            Assert.Null(o.ChangeType);
        });
        Assert.Contains(skipped, o => o.Id.Id == Fixture.Policy);
        // The seeded cio@contoso.com plus the created break-glass user.
        Assert.Equal(2, host.Directory.Count("/users"));
        Assert.Equal(0, host.Directory.Count("/applications"));
    }

    [Fact]
    public async Task ContinueOnError_SkipsOnlyTransitiveDependents()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Fake.Failures.Forbid("/users", "POST");

        var (plan, result) = await host.CreateAsync(await Templates.FixtureAsync(), new ExecuteOptions { ContinueOnError = true });

        var user = EngineHost.Instance(plan, Fixture.User);
        var dependents = plan.Graph.TransitiveDependents(user).Select(i => i.PlannedId.Id).ToHashSet(StringComparer.Ordinal);
        Assert.False(result.Succeeded);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(dependents.Count, result.SkippedCount);
        Assert.Equal(4, result.SucceededCount);
        Assert.Equal(0, result.IgnoredCount);
        Assert.All(result.Operations.Where(o => o.Status == OperationStatus.Skipped), o =>
        {
            Assert.Contains("depends on Microsoft.Entra/users 'bg-orders@contoso.com', which failed", o.Error);
            Assert.Equal(EngineDiagnosticCodes.ResourceSkipped, o.ErrorCode);
        });
        Assert.Equal(OperationStatus.Succeeded, Operation(result, Fixture.RoleDefinition).Status);
        Assert.Equal(OperationStatus.Succeeded, Operation(result, Fixture.NamedLocation).Status);
        Assert.Equal(OperationStatus.Skipped, Operation(result, Fixture.App).Status);
        Assert.Equal(OperationStatus.Skipped, Operation(result, Fixture.Policy).Status);
    }

    [Fact]
    public async Task ContinueOnError_WithAnIndependentFailure_AppliesEverythingElse()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Fake.Failures.Forbid("/roleManagement/directory/roleDefinitions", "POST");

        var (_, result) = await host.CreateAsync(await Templates.FixtureAsync(), new ExecuteOptions { ContinueOnError = true });

        Assert.False(result.Succeeded);
        Assert.Equal(1, result.FailedCount);
        Assert.Equal(0, result.SkippedCount);
        Assert.Equal(14, result.SucceededCount);
        Assert.Equal(1, result.IgnoredCount);
    }

    [Fact]
    public async Task UnsupportedChange_FailsTheResource_WithImmutablePropertyChanged()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Directory.AddGroup("contoso-dev-orders-admins", "Orders Admins", g =>
        {
            g["mailNickname"] = "legacy-nickname";
            g["isAssignableToRole"] = true;
        });

        var (_, result) = await host.CreateAsync(await Templates.FixtureAsync(), new ExecuteOptions { ContinueOnError = true });

        var group = Operation(result, Fixture.Group);
        Assert.Equal(OperationStatus.Failed, group.Status);
        Assert.Equal(EngineDiagnosticCodes.ImmutablePropertyChanged, group.ErrorCode);
        Assert.Contains("properties.mailNickname", group.Error);
        Assert.Equal(ChangeType.Modify, group.ChangeType);
        Assert.Equal(OperationStatus.Skipped, OperationOfType(result, Fixture.AppRoleAssignmentType).Status);
        Assert.Equal("legacy-nickname", host.Directory.Find("/groups", "uniqueName", "contoso-dev-orders-admins")!["mailNickname"]!.GetValue<string>());
    }

    // ---- recreate ----

    [Fact]
    public async Task RecreatePath_DeletesThenCreates_ForAnImmutableAllType()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Directory.AddApplication("contoso-dev-api", "Orders API", configure: a =>
        {
            a["identifierUris"] = new JsonArray("api://contoso-dev-api");
            a["passwordCredentials"] = new JsonArray(new JsonObject
            {
                ["keyId"] = "K1",
                ["displayName"] = "ci-secret",
                ["endDateTime"] = "2027-10-01T00:00:00Z",
                ["rotationTag"] = "2026Q3",
                ["hint"] = "old",
                ["customKeyIdentifier"] = null,
            });
        });

        var (_, result) = await host.CreateAsync(await Templates.FixtureAsync());

        var password = Operation(result, Fixture.Password);
        Assert.Equal(OperationStatus.Succeeded, password.Status);
        Assert.Equal(ChangeType.Modify, password.ChangeType);
        Assert.Equal(ChangeMode.Recreate, password.Mode);
        Assert.NotEqual("K1", password.GraphId);

        var writes = host.Fake.Writes.Where(w => w.Path.EndsWith("Password", StringComparison.Ordinal)).ToList();
        Assert.Equal(2, writes.Count);
        Assert.EndsWith("/removePassword", writes[0].Path, StringComparison.Ordinal);
        Assert.Equal("\"K1\"", writes[0].Body!["keyId"]!.ToJsonString());
        Assert.EndsWith("/addPassword", writes[1].Path, StringComparison.Ordinal);
        Assert.Equal("\"2026Q4\"", writes[1].Body!["passwordCredential"]!["rotationTag"]!.ToJsonString());

        var credentials = Assert.Single(host.Directory.Objects("/applications"))["passwordCredentials"]!.AsArray();
        var credential = Assert.Single(credentials);
        Assert.Equal("2026Q4", credential!["rotationTag"]!.GetValue<string>());
        Assert.StartsWith("fake-secret-", Value(result.SecureOutputs, "ciSecret"), StringComparison.Ordinal);
    }

    // ---- state sink ----

    [Fact]
    public async Task StateSink_ReceivesStartingAppliedIgnoredAndCompleted_InBatchOrder()
    {
        var host = new EngineHost();
        host.SeedExisting();
        var sink = new RecordingStateSink();

        var (plan, result) = await host.CreateAsync(await Templates.FixtureAsync(), new ExecuteOptions { StateSink = sink });
        string spId = EngineHost.Instance(plan, Fixture.SpSymbolic).Id.Id;

        Assert.True(result.Succeeded);
        var calls = sink.Calls;
        Assert.Equal("Completed:Succeeded", calls[^1]);
        Assert.Equal(Fixture.InstanceCount, calls.Count(c => c.StartsWith("Starting:", StringComparison.Ordinal)));
        Assert.Equal(15, calls.Count(c => c.StartsWith("Applied:", StringComparison.Ordinal)));
        Assert.Equal(["Ignored:" + Fixture.Policy], calls.Where(c => c.StartsWith("Ignored:", StringComparison.Ordinal)).ToList());
        Assert.DoesNotContain(calls, c => c.StartsWith("Failed:", StringComparison.Ordinal) || c.StartsWith("Skipped:", StringComparison.Ordinal) || c.StartsWith("Detached:", StringComparison.Ordinal));
        Assert.True(sink.IndexOf("Starting:" + Fixture.User) < sink.IndexOf("Applied:" + Fixture.User));
        Assert.True(sink.IndexOf("Applied:" + Fixture.User) < sink.IndexOf("Starting:" + Fixture.App));
        Assert.True(sink.IndexOf("Applied:" + Fixture.App) < sink.IndexOf("Starting:" + spId));
        Assert.True(sink.IndexOf("Applied:" + spId) < sink.IndexOf("Starting:" + Fixture.Group));
        Assert.Contains("Applied:" + spId, calls);
        Assert.DoesNotContain("Applied:" + Fixture.SpSymbolic, calls);
    }

    [Fact]
    public async Task StateSink_ReceivesFailedAndSkipped_WhenARunStopsEarly()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Fake.Failures.Forbid("/users", "POST");
        var sink = new RecordingStateSink();

        var (_, result) = await host.CreateAsync(await Templates.FixtureAsync(), new ExecuteOptions { StateSink = sink });

        Assert.False(result.Succeeded);
        Assert.Equal("Completed:Failed", sink.Calls[^1]);
        Assert.Contains("Failed:" + Fixture.User, sink.Calls);
        Assert.Equal(11, sink.Calls.Count(c => c.StartsWith("Skipped:", StringComparison.Ordinal)));
        Assert.Contains("Skipped:" + Fixture.SpSymbolic, sink.Calls);
        Assert.Equal(1, sink.Calls.Count(c => c.StartsWith("Failed:", StringComparison.Ordinal)));
        Assert.DoesNotContain(sink.Calls, c => c.StartsWith("Starting:" + Fixture.App, StringComparison.Ordinal));
    }

    // ---- outputs ----

    [Fact]
    public async Task OutputReferencingAnIgnoredResource_IsOutputEvaluationFailed()
    {
        var host = new EngineHost();
        var document = Templates.Parse(
            "\"parameters\": { \"env\": { \"type\": \"string\" } }, " +
            "\"resources\": [ { \"condition\": \"[equals(parameters('env'), 'prod')]\", \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true } }, " +
            "{ \"type\": \"Microsoft.Entra/users\", \"apiVersion\": \"2026-10-01\", \"name\": \"u@contoso.com\", \"properties\": { \"displayName\": \"U\" } } ], " +
            "\"outputs\": { \"groupId\": { \"type\": \"string\", \"value\": \"[reference('g').id]\" }, \"userId\": { \"type\": \"string\", \"value\": \"[reference('u@contoso.com').id]\" }, \"skipped\": { \"condition\": \"[equals(parameters('env'), 'prod')]\", \"type\": \"string\", \"value\": \"[reference('g').id]\" } }",
            ("env", "dev"));

        var (_, result) = await host.CreateAsync(document);

        Assert.Equal(DeploymentStatus.Failed, result.Status);
        Assert.Equal(1, result.SucceededCount);
        Assert.Equal(1, result.IgnoredCount);
        Assert.Equal(0, result.FailedCount);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(EngineDiagnosticCodes.OutputEvaluationFailed, diagnostic.Code);
        Assert.True(diagnostic.IsError);
        Assert.Contains("Output 'groupId' could not be evaluated", diagnostic.Message);
        Assert.Contains("ref(/providers/Microsoft.Entra/groups/g).id", diagnostic.Message);
        Assert.Equal("outputs.groupId.value", diagnostic.Path?.ToString());
        Assert.False(result.Outputs.ContainsKey("groupId"));
        Assert.False(result.Outputs.ContainsKey("skipped"));
        Assert.Equal(Assert.Single(host.Directory.Objects("/users"))["id"]!.GetValue<string>(), Value(result.Outputs, "userId"));
    }

    [Fact]
    public async Task PlainOutputCarryingATaintedValue_IsTreatedAsSecure()
    {
        var host = new EngineHost();
        var document = Templates.Parse(
            "\"parameters\": { \"pw\": { \"type\": \"securestring\" } }, " +
            "\"resources\": [], " +
            "\"outputs\": { \"echo\": { \"type\": \"securestring\", \"value\": \"[parameters('pw')]\" } }",
            ("pw", "hunter2"));

        var (_, result) = await host.CreateAsync(document);

        Assert.True(result.Succeeded);
        Assert.Equal("***", Value(result.Outputs, "echo"));
        Assert.Equal("hunter2", Value(result.SecureOutputs, "echo"));
        Assert.False(SecretJson.ContainsSecret(result.Outputs));
    }
}
