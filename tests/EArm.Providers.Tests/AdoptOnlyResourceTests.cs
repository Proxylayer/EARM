using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Adopt-only resources (<c>"x-earm": { "adoptOnly": true, "objectId": "…" }</c>, docs/format.md): an existing group or user is adopted and managed; a
/// missing one is AdoptOnlyNotFound in the what-if and fails the deployment without any create; a pinned object id the key does not lead to is
/// AdoptedObjectMismatch; <c>deployment delete</c> leaves the object alone.
/// </summary>
public sealed class AdoptOnlyResourceTests
{
    private const string GroupType = "Microsoft.Entra/groups";
    private const string UserType = "Microsoft.Entra/users";

    private static string Group(string name, string properties, string extension)
    {
        return Templates.Resource(GroupType, name, properties, extra: ", \"x-earm\": " + extension);
    }

    private static string User(string upn, string properties, string extension)
    {
        return Templates.Resource(UserType, upn, properties, extra: ", \"x-earm\": " + extension);
    }

    private const string OpsProperties = """{ "displayName": "Ops", "description": "managed", "mailEnabled": false, "mailNickname": "ops", "securityEnabled": true }""";

    // ---- groups ----

    [Fact]
    public async Task AMissingAdoptOnlyGroup_IsAdoptOnlyNotFound_InTheWhatIf_AndIsNeverCreated()
    {
        var host = new ProviderHost();
        var document = host.Resources(Group("ops", OpsProperties, """{ "adoptOnly": true }"""));

        var whatIf = await host.WhatIfAsync(document);
        var row = whatIf.ChangeOfType(GroupType);
        Assert.Equal(EngineDiagnosticCodes.AdoptOnlyNotFound, row.UnsupportedCode);
        Assert.Contains("never creates it", row.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(document);
        Assert.Equal(EngineDiagnosticCodes.AdoptOnlyNotFound, run.OpOfType(GroupType).ErrorCode);
        Check.NoWrites(run);
        Assert.Null(host.Directory.Find("/groups", "uniqueName", "ops"));
    }

    [Fact]
    public async Task AnExistingAdoptOnlyGroup_IsManaged()
    {
        var host = new ProviderHost();
        string id = host.Directory.AddGroup("ops", "Ops", g => g["description"] = "old")["id"]!.GetValue<string>();

        var run = await host.CreateAsync(host.Resources(Group("ops", OpsProperties, """{ "adoptOnly": true }""")));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.Modify, run.OpOfType(GroupType).ChangeType);
        Assert.Equal("managed", host.Directory.Find("/groups", "id", id)!["description"]!.GetValue<string>());
    }

    [Fact]
    public async Task APinnedObjectId_ThatTheKeyLeadsTo_IsAdopted_AParameterMayHoldIt()
    {
        var host = new ProviderHost();
        string id = host.Directory.AddGroup("ops", "Ops")["id"]!.GetValue<string>();
        string template = Group("ops", OpsProperties, """{ "objectId": "[parameters('opsId')]" }""");

        var run = await host.CreateAsync(host.Resources(template, parameters: "\"opsId\": { \"type\": \"string\" }", values: ("opsId", id.ToUpperInvariant())));

        Check.Succeeded(run);
        Assert.Contains(run.ChangeOfType(GroupType).Reasons, r => r.Contains("adopt-only: the object is the pinned", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APinnedObjectId_ThatTheKeyDoesNotLeadTo_IsAdoptedObjectMismatch_AndNothingIsWritten()
    {
        var host = new ProviderHost();
        string id = host.Directory.AddGroup("ops", "Ops")["id"]!.GetValue<string>();
        const string other = "99999999-8888-7777-6666-555555555555";
        var document = host.Resources(Group("ops", OpsProperties, "{ \"objectId\": \"" + other + "\" }"));

        var whatIf = await host.WhatIfAsync(document);
        Assert.Equal(EngineDiagnosticCodes.AdoptedObjectMismatch, whatIf.ChangeOfType(GroupType).UnsupportedCode);
        Assert.Contains(id, whatIf.ChangeOfType(GroupType).UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(document);
        Assert.Equal(EngineDiagnosticCodes.AdoptedObjectMismatch, run.OpOfType(GroupType).ErrorCode);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task AGroupAdoptedByDisplayName_IsCheckedAgainstThePin()
    {
        var host = new ProviderHost();
        string id = host.Directory.Add("/groups", new JsonObject
        {
            ["displayName"] = "Ops",
            ["mailEnabled"] = false,
            ["mailNickname"] = "script-ops",
            ["securityEnabled"] = true,
            ["groupTypes"] = new JsonArray(),
        })["id"]!.GetValue<string>();

        var pinnedToIt = await host.WhatIfAsync(host.Resources(Group("ops", OpsProperties, "{ \"objectId\": \"" + id + "\" }")));
        Assert.False(pinnedToIt.ChangeOfType(GroupType).HasError);
        Assert.Contains(pinnedToIt.Plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.GroupAdoptedByDisplayName);

        var pinnedElsewhere = await host.WhatIfAsync(host.Resources(Group("ops", OpsProperties, "{ \"objectId\": \"99999999-8888-7777-6666-555555555555\" }")));
        Assert.Equal(EngineDiagnosticCodes.AdoptedObjectMismatch, pinnedElsewhere.ChangeOfType(GroupType).UnsupportedCode);
    }

    [Fact]
    public async Task APinThatIsOnlyKnownAtDeployment_IsAdoptedObjectMismatch()
    {
        var host = new ProviderHost();
        host.Directory.AddGroup("ops", "Ops");
        host.Directory.AddGroup("other", "Other");
        string template = Templates.Resource(GroupType, "other", """{ "displayName": "Other", "mailEnabled": false, "mailNickname": "other", "securityEnabled": true }""")
            + ", " + Group("ops", OpsProperties, """{ "objectId": "[reference('other').id]" }""");

        var whatIf = await host.WhatIfAsync(host.Resources(template));

        var row = whatIf.Plan.Changes.Single(c => c.Id.Id.EndsWith("/ops", StringComparison.Ordinal));
        Assert.Equal(EngineDiagnosticCodes.AdoptedObjectMismatch, row.UnsupportedCode);
    }

    [Fact]
    public async Task DeploymentDelete_LeavesAnAdoptOnlyGroupAlone()
    {
        var host = new ProviderHost();
        host.Directory.AddGroup("ops", "Ops");
        var document = host.Resources(Group("ops", OpsProperties, """{ "adoptOnly": true }"""));
        Check.Succeeded(await host.CreateAsync(document));

        var deletion = await host.DeleteDeclaredAsync(document);

        Assert.Empty(deletion.Outcomes);
        Assert.NotNull(host.Directory.Find("/groups", "uniqueName", "ops"));
    }

    // ---- users ----

    [Fact]
    public async Task AMissingAdoptOnlyUser_IsAdoptOnlyNotFound_NotAMissingDisplayName()
    {
        var host = new ProviderHost();
        var document = host.Resources(User("alice@contoso.com", """{ "jobTitle": "Engineer" }""", """{ "adoptOnly": true }"""));

        var whatIf = await host.WhatIfAsync(document);
        var row = whatIf.ChangeOfType(UserType);
        Assert.Equal(EngineDiagnosticCodes.AdoptOnlyNotFound, row.UnsupportedCode);
        Assert.DoesNotContain("displayName", row.UnsupportedReason, StringComparison.Ordinal);

        var run = await host.CreateAsync(document);
        Assert.Equal(EngineDiagnosticCodes.AdoptOnlyNotFound, run.OpOfType(UserType).ErrorCode);
        Check.NoWrites(run);
    }

    [Fact]
    public async Task AnExistingAdoptOnlyUser_IsManaged_WithoutTheCreateOnlyProperties()
    {
        var host = new ProviderHost();
        string id = host.Directory.AddUser("alice@contoso.com", "Alice")["id"]!.GetValue<string>();

        var run = await host.CreateAsync(host.Resources(User("alice@contoso.com", """{ "jobTitle": "Engineer" }""", "{ \"adoptOnly\": true, \"objectId\": \"" + id + "\" }")));

        Check.Succeeded(run);
        Assert.Equal("Engineer", host.Directory.Find("/users", "id", id)!["jobTitle"]!.GetValue<string>());
    }
}
