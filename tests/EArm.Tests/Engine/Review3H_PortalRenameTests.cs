using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Graph;
using EArm.TestSupport;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// Review round 2, finding S2-04 (a rename in the portal): identity is the live key (ARM rule), so an object renamed outside the template is
/// not the declared one; the declared name is created and the renamed object is never touched.
/// </summary>
public sealed class Review3H_PortalRenameTests
{
    private const string UserType = "Microsoft.Entra/users";

    private static string UserId(string upn)
    {
        return "/providers/Microsoft.Entra/users/" + upn;
    }

    // ---- a rename outside the template: identity is the live key (ARM rule), so the declared name is a new object ----

    [Fact]
    public async Task AUserWhoseUpnWasRenamedInThePortal_IsNotTheDeclaredUser_TheDeclaredUpnIsCreated()
    {
        var host = new EngineHost();
        var renamed = host.Directory.AddUser("renamed@contoso.com", "Renamed");
        var document = Templates.Resources("{ \"type\": \"" + UserType + "\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"bg@contoso.com\", \"properties\": { \"displayName\": \"Break glass\", \"accountEnabled\": true, \"passwordProfile\": { \"password\": \"Initial-P@ssw0rd-1\" } } }");

        var (_, result) = await ExecuteAsync(host, document);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(ChangeType.Create, Assert.Single(result.Operations).ChangeType);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Path.Contains(IdOf(renamed), StringComparison.Ordinal));
        Assert.Equal(2, host.Directory.Count("/users"));
        Assert.Equal("Renamed", renamed["displayName"]!.GetValue<string>());
        Assert.NotEqual(IdOf(renamed), Assert.Single(result.Operations).GraphId);
    }

    [Fact]
    public async Task ANamedLocationWhoseDisplayNameWasRenamed_IsNotTheDeclaredOne_TheDeclaredNameIsCreated()
    {
        var host = new EngineHost();
        var renamed = host.Directory.Add("/identity/conditionalAccess/namedLocations", new JsonObject { ["@odata.type"] = "#microsoft.graph.ipNamedLocation", ["displayName"] = "HQ (renamed in the portal)", ["isTrusted"] = true, ["ipRanges"] = new JsonArray(new JsonObject { ["@odata.type"] = "#microsoft.graph.iPv4CidrRange", ["cidrAddress"] = "10.0.0.0/8" }) });
        var document = Templates.Resources("{ \"type\": \"" + Fixture.NamedLocationType + "\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"HQ\", \"properties\": { \"@odata.type\": \"#microsoft.graph.ipNamedLocation\", \"isTrusted\": true, \"ipRanges\": [ { \"@odata.type\": \"#microsoft.graph.iPv4CidrRange\", \"cidrAddress\": \"10.0.0.0/8\" } ] } }");

        var plan = await host.WhatIfAsync(document);
        var row = Assert.Single(plan.Changes);
        Assert.False(row.HasError);
        Assert.Equal(ChangeType.Create, row.Type);

        var (_, result) = await ExecuteAsync(host, document);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        Assert.Equal(ChangeType.Create, Assert.Single(result.Operations).ChangeType);
        Assert.DoesNotContain(host.Fake.Writes, w => w.Path.Contains(IdOf(renamed), StringComparison.Ordinal));
        Assert.Equal(2, host.Directory.Count("/identity/conditionalAccess/namedLocations"));
    }
}
