using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Providers;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// Users, groups and the membership / ownership edges declared from both sides, plus a guest read by reference.
/// Skipped without <c>EARM_IT_TENANT</c>; the scenarios that create users also need <c>EARM_IT_DOMAIN</c>.
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class DirectoryScenarioTests : IntegrationTestBase
{
    public DirectoryScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_Groups_MemberOfMembersOwnersEdgesOnBothSides()
    {
        Fixture.Require(Needs.Domain);
        var run = Fixture.NewRun("groups", "groups-edges.entra.json")
            .WithParameter("domain", Fixture.Domain)
            .WithParameter("u2Password", NewPassword());

        var created = await Fixture.Cli.CreateAsync(run, Ct);
        AssertCreated(created, "create");

        string u1 = (await UserAsync(Fixture.Upn("u1"), "id"))!.RequireId();
        string u2 = (await UserAsync(Fixture.Upn("u2"), "id"))!.RequireId();
        string ga = (await GroupByNameAsync(Fixture.Name("ga"), "id"))!.RequireId();
        string gb = (await GroupByNameAsync(Fixture.Name("gb"), "id"))!.RequireId();
        string gc = (await GroupByNameAsync(Fixture.Name("gc"), "id"))!.RequireId();

        // Target side: ga declares its members and owners.
        Assert.Equal([u1], (await IdsOfAsync($"/groups/{ga}/members")).ToList());
        Assert.Equal([u1], (await IdsOfAsync($"/groups/{ga}/owners")).ToList());

        // Principal side: gb and u2 declare memberOf gc; u2 declares ownerOf gb.
        var gcMembers = await IdsOfAsync($"/groups/{gc}/members");
        Assert.Contains(gb, gcMembers);
        Assert.Contains(u2, gcMembers);
        Assert.Contains(u2, await IdsOfAsync($"/groups/{gb}/owners"));
        Assert.Contains(gc, await IdsOfAsync($"/users/{u2}/memberOf"));

        AssertNoChanges(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        // Drift on both sides: an extra member of ga (target side authoritative: removed) and u2 taken out of gc (principal side: added back).
        await Graph.PostAsync(new GraphRequest($"/groups/{ga}/members/$ref"), new JsonObject { ["@odata.id"] = $"{GraphApiExtensions.Host}/v1.0/directoryObjects/{u2}" }, Ct);
        await Graph.DeleteAsync(new GraphRequest($"/groups/{gc}/members/{u2}/$ref"), Ct);

        var drift = await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes");
        AssertExit(5, drift, "what-if after edge drift");
        Assert.True(EdgeCount(drift, "remove") >= 1, "Expected the extra member of ga to be reported as an edge to remove: " + drift.DescribeChanges());
        Assert.True(EdgeCount(drift, "add") >= 1, "Expected u2 to be reported as an edge to add back to gc: " + drift.DescribeChanges());

        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create after edge drift");
        Assert.Equal([u1], (await IdsOfAsync($"/groups/{ga}/members")).ToList());
        Assert.Contains(u2, await IdsOfAsync($"/groups/{gc}/members"));
        AssertNoChanges(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after edge repair");

        // deployment delete removes the declared users and groups (edges go with the objects).
        AssertExit(0, await Fixture.Cli.DeleteAllAsync(run, Ct), "deployment delete");
        Assert.Null(await GroupByNameAsync(Fixture.Name("ga"), "id"));
        Assert.Null(await UserAsync(Fixture.Upn("u1"), "id"));
    }

    [Fact]
    public async Task It_Users_GeneratedPasswordUsageLocationAndSecureOutput()
    {
        Fixture.Require(Needs.Domain);
        string suppliedPassword = NewPassword();
        var run = Fixture.NewRun("users", "users.entra.json")
            .WithParameter("domain", Fixture.Domain)
            .WithParameter("u2Password", suppliedPassword);
        string secretFile = Path.Combine(Fixture.ScratchDirectory, "users.secrets.json");

        var created = await Fixture.Cli.CreateAsync(run, Ct, "--secure-outputs-file", secretFile);
        AssertCreated(created, "create");
        Assert.DoesNotContain(suppliedPassword, created.Combined, StringComparison.Ordinal);

        // The generated password is a secure output: masked on screen, clear in the owner-only file.
        Assert.Equal("***", (string?)created.Json["outputs"]?["u1InitialPassword"]?["value"]);
        string generated = (string?)SecureOutputs(secretFile)["u1InitialPassword"]?["value"] ?? string.Empty;
        Assert.True(generated.Length >= UsersRules.GeneratedPasswordLength, $"The generated initial password has {generated.Length} characters; expected at least {UsersRules.GeneratedPasswordLength}.");
        Assert.DoesNotContain(generated, created.Combined, StringComparison.Ordinal);

        var u1 = await UserAsync(Fixture.Upn("u1"), "id", "userPrincipalName", "usageLocation", "accountEnabled", "jobTitle");
        var u2 = await UserAsync(Fixture.Upn("u2"), "id", "usageLocation", "accountEnabled");
        Assert.NotNull(u1);
        Assert.NotNull(u2);
        Assert.Equal("GB", u1.Text("usageLocation"));
        Assert.Equal("IE", u2.Text("usageLocation"));
        Assert.Equal("Integration test user", u1.Text("jobTitle"));

        // The state records whether this run generated the password, never the password.
        var state = await Fixture.Store.LoadAsync(run.Name, Ct);
        Assert.NotNull(state);
        var u1Entry = state.Find("/providers/Microsoft.Entra/users/" + Fixture.Upn("u1"));
        var u2Entry = state.Find("/providers/Microsoft.Entra/users/" + Fixture.Upn("u2"));
        Assert.True((bool?)u1Entry?.Extra?[UsersProvider.GeneratedPasswordExtra] == true, "The state entry of u1 does not record generatedPassword: true.");
        Assert.True((bool?)u2Entry?.Extra?[UsersProvider.GeneratedPasswordExtra] != true, "The state entry of u2 claims a generated password although one was supplied.");
        Assert.DoesNotContain(generated, await File.ReadAllTextAsync(Fixture.Store.PathFor(run.Name), Ct), StringComparison.Ordinal);

        AssertNoChanges(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        // Drift of a managed property.
        await Graph.PatchAsync(new GraphRequest("/users/" + u1.RequireId()), new JsonObject { ["jobTitle"] = "Drifted" }, Ct);
        AssertChange(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "/users/" + Fixture.Upn("u1"), "Modify", "what-if after drift");
        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create after drift");
        Assert.Equal("Integration test user", (await UserAsync(Fixture.Upn("u1"), "jobTitle"))!.Text("jobTitle"));

        AssertExit(0, await Fixture.Cli.DeleteAllAsync(run, Ct), "deployment delete");
        Assert.Null(await UserAsync(Fixture.Upn("u1"), "id"));
    }

    [Fact]
    public async Task It_Users_GuestWithExtSuffixIsReadByReference()
    {
        Fixture.Require();
        var guests = await Graph.ListAsync(new GraphRequest("/users").WithFilter("userType eq 'Guest'").WithSelect("id", "userPrincipalName").WithTop(5), Ct);
        var guest = guests.FirstOrDefault(g => g.Text("userPrincipalName")?.Contains("#EXT#", StringComparison.OrdinalIgnoreCase) == true);
        Assert.SkipWhen(guest is null, "The tenant has no guest user (a user whose userPrincipalName contains #EXT#); invite one to run this scenario.");

        var run = Fixture.NewRun("guests", "guest-member.entra.json").WithParameter("guestUpn", guest.Text("userPrincipalName")!);

        var preview = await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes");
        AssertExit(5, preview, "what-if");
        Assert.Equal(1, EdgeCount(preview, "add"));
        Assert.False(preview.HasDiagnostic("UnresolvedReference"), "The guest reference was not resolved: " + Tail(preview.StandardOutput));

        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create");
        var group = await GroupByNameAsync(Fixture.Name("guests"), "id");
        Assert.Contains(guest.RequireId(), await IdsOfAsync($"/groups/{group!.RequireId()}/members"));
        AssertNoChanges(await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");
    }
}
