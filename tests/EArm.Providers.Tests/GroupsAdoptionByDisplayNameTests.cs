using System;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// A group created outside EARM has no uniqueName: when no group holds the declared key, the one group without a uniqueName whose displayName equals
/// the declared one is adopted (its uniqueName is set), several are AmbiguousMatch, and a same-name group that holds another uniqueName is never adopted.
/// </summary>
public sealed class GroupsAdoptionByDisplayNameTests
{
    private const string Type = "Microsoft.Entra/groups";

    private static string Group(string name, string properties)
    {
        return Templates.Resource(Type, name, properties);
    }

    /// <summary>A security group as a script would create it: no uniqueName.</summary>
    private static string AddUntagged(ProviderHost host, string displayName, string? description = null)
    {
        var group = new JsonObject
        {
            ["displayName"] = displayName,
            ["mailEnabled"] = false,
            ["mailNickname"] = "script-" + Guid.NewGuid().ToString("N")[..8],
            ["securityEnabled"] = true,
            ["groupTypes"] = new JsonArray(),
        };
        if (description is not null)
        {
            group["description"] = description;
        }

        return host.Directory.Add("/groups", group)["id"]!.GetValue<string>();
    }

    [Fact]
    public async Task ASingleUntaggedGroup_IsAdopted_ItsUniqueNameIsSet_AndTheRerunIsNoChange()
    {
        var host = new ProviderHost();
        string id = AddUntagged(host, "Finance Readers", description: "old");
        string template = Group("finance-readers", """{ "displayName": "Finance Readers", "description": "new" }""");

        var run = await host.CreateAsync(host.Resources(template));

        Check.Succeeded(run);
        var op = run.OpOfType(Type);
        Assert.Equal(ChangeType.Modify, op.ChangeType);
        var change = run.ChangeOfType(Type);
        Assert.Contains(change.Deltas, d => d.HasEffect && d.Path.ToString().Contains("uniqueName", StringComparison.Ordinal));
        Assert.Contains(change.Deltas, d => d.HasEffect && d.Path.ToString().Contains("description", StringComparison.Ordinal));
        Assert.DoesNotContain(run.Writes, w => w.Path.StartsWith("/groups(uniqueName=", StringComparison.Ordinal));
        var adopt = run.Writes.First(w => w.Method == "PATCH" && w.Path == "/groups/" + id);
        Assert.Equal("finance-readers", Check.Body(adopt)["uniqueName"]!.GetValue<string>());
        Assert.Contains(run.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.GroupAdoptedByDisplayName && d.Severity == DiagnosticSeverity.Warning);

        var live = host.Directory.Find("/groups", "uniqueName", "finance-readers");
        Assert.NotNull(live);
        Assert.Equal(id, live["id"]!.GetValue<string>());
        Assert.Equal("new", live["description"]!.GetValue<string>());
        Assert.Single(host.Directory.Objects("/groups"));

        var rerun = await host.CreateAsync(host.Resources(template));
        Check.Succeeded(rerun);
        Check.NoWrites(rerun);
        Assert.Equal(ChangeType.NoChange, rerun.OpOfType(Type).ChangeType);
        Assert.DoesNotContain(rerun.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.GroupAdoptedByDisplayName);
    }

    [Fact]
    public async Task TheDisplayNameMatch_IsCaseInsensitive_AndAnAdoptionWithoutOtherChanges_StillSetsTheUniqueName()
    {
        var host = new ProviderHost();
        string id = AddUntagged(host, "Ops Team");

        var run = await host.CreateAsync(host.Resources(Group("ops-team", """{ "displayName": "Ops Team" }""")));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Modify, run.OpOfType(Type).ChangeType);
        Assert.Equal(id, host.IdOf("/groups", "uniqueName", "ops-team"));
    }

    [Fact]
    public async Task WhatIf_ShowsTheAdoption_AsAModifyRow_WithAWarning_AndWritesNothing()
    {
        var host = new ProviderHost();
        string id = AddUntagged(host, "Finance Readers");

        var whatIf = await host.WhatIfAsync(host.Resources(Group("finance-readers", """{ "displayName": "Finance Readers" }""")));

        Check.NoWrites(whatIf);
        var change = whatIf.ChangeOfType(Type);
        Assert.Equal(ChangeType.Modify, change.Type);
        Assert.Equal(id, change.GraphId);
        Assert.Contains($"adopting existing group 'Finance Readers' (Graph id {id}) by display name; uniqueName will be set", whatIf.WhatIfTextOutput, StringComparison.Ordinal);
        Assert.Contains(whatIf.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.GroupAdoptedByDisplayName && d.Severity == DiagnosticSeverity.Warning);
        var filtered = Assert.Single(whatIf.Requests, r => r.UrlParameters is not null && r.UrlParameters.TryGetValue("$filter", out var f) && f.StartsWith("displayName eq", StringComparison.Ordinal));
        Assert.Equal("displayName eq 'Finance Readers'", filtered.UrlParameters!["$filter"]);
    }

    [Fact]
    public async Task TwoUntaggedGroupsWithTheDisplayName_AreAmbiguousMatch_AndNothingIsCreated()
    {
        var host = new ProviderHost();
        string first = AddUntagged(host, "Dup Name");
        string second = AddUntagged(host, "dup name");

        var run = await host.CreateAsync(host.Resources(Group("dup-name", """{ "displayName": "Dup Name" }""")));

        Assert.False(run.Succeeded);
        var op = run.OpOfType(Type);
        Assert.Equal(EngineDiagnosticCodes.AmbiguousMatch, op.ErrorCode);
        Assert.Contains(first, op.Error, StringComparison.Ordinal);
        Assert.Contains(second, op.Error, StringComparison.Ordinal);
        Check.NoWrites(run);
        Assert.Equal(2, host.Directory.Objects("/groups").Count);
    }

    [Fact]
    public async Task ASameNameGroupHoldingAnotherUniqueName_IsNotAdopted_AndTheGroupIsCreated()
    {
        var host = new ProviderHost();
        string other = host.Directory.AddGroup("someone-else", "Shared Name")["id"]!.GetValue<string>();

        var run = await host.CreateAsync(host.Resources(Group("mine", """{ "displayName": "Shared Name" }""")));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Create, run.OpOfType(Type).ChangeType);
        Assert.NotEqual(other, host.IdOf("/groups", "uniqueName", "mine"));
        Assert.Equal("someone-else", host.Directory.Find("/groups", "id", other)!["uniqueName"]!.GetValue<string>());
        Assert.DoesNotContain(run.Executed.Diagnostics, d => d.Code == EngineDiagnosticCodes.GroupAdoptedByDisplayName);
    }

    [Fact]
    public async Task ASameNameGroupHoldingAnotherUniqueName_DoesNotMakeASingleUntaggedOneAmbiguous()
    {
        var host = new ProviderHost();
        host.Directory.AddGroup("someone-else", "Shared Name");
        string untagged = AddUntagged(host, "Shared Name");

        var run = await host.CreateAsync(host.Resources(Group("mine", """{ "displayName": "Shared Name" }""")));

        Check.Succeeded(run);
        Assert.Equal(untagged, host.IdOf("/groups", "uniqueName", "mine"));
    }

    [Fact]
    public async Task NoCandidate_IsCreatedAsBefore()
    {
        var host = new ProviderHost();
        AddUntagged(host, "Something Else");

        var run = await host.CreateAsync(host.Resources(Group("brand-new", """{ "displayName": "Brand New" }""")));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Create, run.OpOfType(Type).ChangeType);
        Assert.Contains(run.Writes, w => w.Method == "PATCH" && w.Path == "/groups(uniqueName='brand-new')");
        Assert.Equal(2, host.Directory.Objects("/groups").Count);
    }

    [Fact]
    public async Task AGraphRefusalToSetTheUniqueName_IsUniqueNameNotSettable_AndNothingElseIsWritten()
    {
        var host = new ProviderHost();
        string id = AddUntagged(host, "Locked", description: "old");
        host.Fake.Failures.Add("PATCH", "/groups/" + id, request => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "Property uniqueName is read-only.", request.Hint), times: -1);

        var run = await host.CreateAsync(host.Resources(Group("locked", """{ "displayName": "Locked", "description": "new" }""")));

        Assert.False(run.Succeeded);
        var op = run.OpOfType(Type);
        Assert.Equal(EngineDiagnosticCodes.UniqueNameNotSettable, op.ErrorCode);
        Assert.Contains("found by its display name", op.Error, StringComparison.Ordinal);
        Assert.Contains("locked", op.Error, StringComparison.Ordinal);
        Assert.Equal("old", host.Directory.Find("/groups", "id", id)!["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task DeploymentDelete_KeepsAnUntaggedGroupThatMatchesOnlyByDisplayName()
    {
        var host = new ProviderHost();
        string id = AddUntagged(host, "Keep Me");

        var result = await host.DeleteDeclaredAsync(host.Resources(Group("keep-me", """{ "displayName": "Keep Me" }""")));

        var outcome = Assert.Single(result.Outcomes);
        Assert.Equal(DeletionStatus.Refused, outcome.Status);
        Assert.Equal(EngineDiagnosticCodes.NotDeletable, outcome.ErrorCode);
        Assert.NotNull(host.Directory.Find("/groups", "id", id));
    }
}
