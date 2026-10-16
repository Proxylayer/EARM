using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Read-your-write after a create of a <c>displayName</c>-keyed type (<see cref="IResourceProvider.KeyReadLagsCreate"/>): Graph's
/// <c>$filter=displayName eq</c> list can omit a new object for a few seconds, so the executor reads the key again (on the
/// <see cref="ReplicationWait"/> schedule) until the created id shows up; when it never does the create still succeeds, with a
/// <see cref="EngineDiagnosticCodes.CreatedObjectNotVisible"/> warning.
/// </summary>
public sealed class ReadYourWriteAfterCreateTests
{
    private const string LocType = NamedLocationsProvider.ResourceType;
    private const string LocPath = "/identity/conditionalAccess/namedLocations";

    private static string IpLocation(string name = "Contoso offices")
    {
        return "{ \"type\": \"" + LocType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"@odata.type\": \"#microsoft.graph.ipNamedLocation\", \"ipRanges\": [ { \"cidrAddress\": \"10.0.0.0/8\" } ] } }";
    }

    private static int ListReads(Run run, string path)
    {
        return run.Requests.Count(r => r.Method == "GET" && r.Path == path);
    }

    [Fact]
    public void OnlyTheDisplayNameKeyedTypes_ReadTheirKeyBackAfterACreate()
    {
        var registry = ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default);
        var lagging = ResourceTypeCatalog.Default.Types
            .Where(t => registry.Contains(t.Type) && registry.Get(t.Type).KeyReadLagsCreate)
            .Select(t => t.Type)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(
            [
                "Microsoft.Entra/administrativeUnits",
                "Microsoft.Entra/conditionalAccess/authenticationStrengths",
                "Microsoft.Entra/conditionalAccess/namedLocations",
                "Microsoft.Entra/conditionalAccess/policies",
                "Microsoft.Entra/roleDefinitions",
            ],
            lagging);
    }

    [Fact]
    public async Task ACreateTheListOmitsForAFewReads_IsWaitedFor_AndTheNextRunIsNoChange()
    {
        var host = new ProviderHost();
        host.Fake.HideCreatedFromLists(LocPath, 3);

        var run = await host.CreateAsync(host.Resources(IpLocation()));

        Check.Succeeded(run);
        Check.NoDiagnostic(run, EngineDiagnosticCodes.CreatedObjectNotVisible);
        Assert.Single(run.Writes);
        Assert.Equal(3, host.ReplicationDelays.Count);

        // The plan read, then three reads that miss the new object and the one that finds it.
        Assert.Equal(1 + 3 + 1, ListReads(run, LocPath));

        var rerun = await host.CreateAsync(host.Resources(IpLocation()));
        Check.Succeeded(rerun);
        Assert.Equal(ChangeType.NoChange, rerun.Executed.Operations.Single().ChangeType);
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task ACreateTheListShowsAtOnce_CostsOneExtraReadAndNoWait()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(IpLocation()));

        Check.Succeeded(run);
        Check.NoDiagnostic(run, EngineDiagnosticCodes.CreatedObjectNotVisible);
        Assert.Equal(0, host.ReplicationDelays.Count);
        Assert.Equal(2, ListReads(run, LocPath));
    }

    [Fact]
    public async Task ACreateTheListNeverShows_SucceedsWithACreatedObjectNotVisibleWarning_AfterTheWholeWindow()
    {
        var host = new ProviderHost();
        host.Fake.HideCreatedFromLists(LocPath, -1);

        var run = await host.CreateAsync(host.Resources(IpLocation()));

        Check.Succeeded(run);
        var operation = run.Executed.Operations.Single();
        Assert.Equal(OperationStatus.Succeeded, operation.Status);
        Assert.Equal(ChangeType.Create, operation.ChangeType);
        var warning = Check.Diagnostic(run, EngineDiagnosticCodes.CreatedObjectNotVisible);
        Assert.Equal(EArm.Core.Diagnostics.DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains(operation.GraphId!, warning.Message, StringComparison.Ordinal);
        Assert.Contains("90 s", warning.Message, StringComparison.Ordinal);
        Assert.Single(run.Writes);

        var wait = ReplicationWait.Default;
        int expectedDelays = (int)(wait.Window / wait.Interval);
        Assert.Equal(expectedDelays, host.ReplicationDelays.Count);
        Assert.Equal(1 + expectedDelays + 1, ListReads(run, LocPath));
    }

    [Fact]
    public async Task ATypeKeyedByAnAlternateKey_IsNotReadBackAfterItsCreate()
    {
        var host = new ProviderHost();
        host.Fake.HideCreatedFromLists("/groups", -1);

        var run = await host.CreateAsync(host.Resources("{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"ryw-group\", \"properties\": { \"displayName\": \"RYW\", \"mailEnabled\": false, \"mailNickname\": \"ryw\", \"securityEnabled\": true } }"));

        Check.Succeeded(run);
        Check.NoDiagnostic(run, EngineDiagnosticCodes.CreatedObjectNotVisible);
        Assert.Equal(0, host.ReplicationDelays.Count);
    }
}
