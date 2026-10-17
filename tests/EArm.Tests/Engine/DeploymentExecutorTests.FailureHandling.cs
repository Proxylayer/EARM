using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using Xunit;

namespace EArm.Tests.Engine;

public sealed partial class DeploymentExecutorTests
{
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
            g["isAssignableToRole"] = false;
        });

        var (_, result) = await host.CreateAsync(await Templates.FixtureAsync(), new ExecuteOptions { ContinueOnError = true });

        var group = Operation(result, Fixture.Group);
        Assert.Equal(OperationStatus.Failed, group.Status);
        Assert.Equal(EngineDiagnosticCodes.ImmutablePropertyChanged, group.ErrorCode);
        Assert.Contains("properties.isAssignableToRole", group.Error);
        Assert.DoesNotContain("mailNickname", group.Error, StringComparison.Ordinal);
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

        // A replacement deletes the old object: the executor does it when confirmed (ApprovedRecreates null = every replacement, the CLI's -y).
        var (_, result) = await host.CreateAsync(await Templates.FixtureAsync(), new ExecuteOptions());

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

    // ---- operations ----

    [Fact]
    public async Task Operations_ReportSucceededAndIgnored_InBatchOrder()
    {
        var host = new EngineHost();
        host.SeedExisting();

        var (plan, result) = await host.CreateAsync(await Templates.FixtureAsync());
        string spId = EngineHost.Instance(plan, Fixture.SpSymbolic).Id.Id;

        Assert.True(result.Succeeded);
        var operations = result.Operations;
        Assert.Equal(Fixture.InstanceCount, operations.Count);
        Assert.Equal(15, operations.Count(o => o.Status == OperationStatus.Succeeded));
        Assert.Equal([Fixture.Policy], operations.Where(o => o.Status == OperationStatus.Ignored).Select(o => o.Id.Id).ToList());
        Assert.DoesNotContain(operations, o => o.Status is OperationStatus.Failed or OperationStatus.Skipped);
        int BatchOf(string id) => operations.Single(o => o.Id.Id == id).Batch;
        Assert.True(BatchOf(Fixture.User) < BatchOf(Fixture.App));
        Assert.True(BatchOf(Fixture.App) < BatchOf(spId));
        Assert.True(BatchOf(spId) < BatchOf(Fixture.Group));
        Assert.DoesNotContain(operations, o => o.Id.Id == Fixture.SpSymbolic);
    }

    [Fact]
    public async Task Operations_ReportFailedAndSkipped_WhenARunStopsEarly()
    {
        var host = new EngineHost();
        host.SeedExisting();
        host.Fake.Failures.Forbid("/users", "POST");

        var (_, result) = await host.CreateAsync(await Templates.FixtureAsync());

        Assert.False(result.Succeeded);
        Assert.Equal(DeploymentStatus.Failed, result.Status);
        var failed = Assert.Single(result.Operations, o => o.Status == OperationStatus.Failed);
        Assert.Equal(Fixture.User, failed.Id.Id);
        Assert.Equal(11, result.Operations.Count(o => o.Status == OperationStatus.Skipped));
        Assert.Contains(result.Operations, o => o.Status == OperationStatus.Skipped && o.Id.Id == Fixture.SpSymbolic);
        Assert.Equal(OperationStatus.Skipped, result.Operations.Single(o => o.Id.Id == Fixture.App).Status);
    }
}
