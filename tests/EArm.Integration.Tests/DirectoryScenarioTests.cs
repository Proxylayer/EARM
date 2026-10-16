using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
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
        string ga = (await FindOneEventuallyAsync("/groups", "displayName", Fixture.Name("ga"), found => found is not null, "id"))!.RequireId();
        string gb = (await FindOneEventuallyAsync("/groups", "displayName", Fixture.Name("gb"), found => found is not null, "id"))!.RequireId();
        string gc = (await FindOneEventuallyAsync("/groups", "displayName", Fixture.Name("gc"), found => found is not null, "id"))!.RequireId();

        // Target side: ga declares its members and owners.
        Assert.Equal([u1], (await IdsOfAsync($"/groups/{ga}/members")).ToList());
        Assert.Equal([u1], (await IdsOfAsync($"/groups/{ga}/owners")).ToList());

        // Principal side: gb and u2 declare memberOf gc; u2 declares ownerOf gb.
        var gcMembers = await IdsOfAsync($"/groups/{gc}/members");
        Assert.Contains(gb, gcMembers);
        Assert.Contains(u2, gcMembers);
        Assert.Contains(u2, await IdsOfAsync($"/groups/{gb}/owners"));
        Assert.Contains(gc, await IdsOfAsync($"/users/{u2}/memberOf"));

        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        // Drift on both sides: an extra member of ga (target side authoritative: removed) and u2 taken out of gc (principal side: added back).
        await Graph.PostAsync(new GraphRequest($"/groups/{ga}/members/$ref").WithHint(RetryHint.ExpectReplicationLag), new JsonObject { ["@odata.id"] = $"{GraphApiExtensions.Host}/v1.0/directoryObjects/{u2}" }, Ct);
        await Graph.DeleteAsync(new GraphRequest($"/groups/{gc}/members/{u2}/$ref").WithHint(RetryHint.ExpectReplicationLag), Ct);

        var drift = await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes");
        AssertExit(5, drift, "what-if after edge drift");
        Assert.True(EdgeCount(drift, "remove") >= 1, "Expected the extra member of ga to be reported as an edge to remove: " + drift.DescribeChanges());
        Assert.True(EdgeCount(drift, "add") >= 1, "Expected u2 to be reported as an edge to add back to gc: " + drift.DescribeChanges());

        AssertCreated(await Fixture.Cli.CreateAsync(run, Ct), "create after edge drift");
        Assert.Equal([u1], (await IdsOfAsync($"/groups/{ga}/members")).ToList());
        Assert.Contains(u2, await IdsOfAsync($"/groups/{gc}/members"));
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after edge repair");

        // deployment delete removes the declared users and groups (edges go with the objects).
        AssertExit(0, await Fixture.Cli.DeleteAllAsync(run, Ct), "deployment delete");
        Assert.Null(await FindOneEventuallyAsync("/groups", "displayName", Fixture.Name("ga"), found => found is null, "id"));
        Assert.Null(await UserAsync(Fixture.Upn("u1"), "id"));
    }

    [Fact]
    public async Task It_Groups_AdoptsExistingGroupByDisplayName()
    {
        Fixture.Require();
        string displayName = Fixture.Name("adopt");
        string uniqueName = Fixture.Name("adopt-u");
        string rowSuffix = "/groups/" + uniqueName;

        // A group created outside EARM: no uniqueName, the declared displayName and mailNickname. The adoption reads it by displayName, so wait for the list.
        string groupId = (await TenantObjects.CreateGroupAsync(Fixture, "adopt", Ct)).RequireId();
        await FindOneEventuallyAsync("/groups", "displayName", displayName, found => found is not null, "id");

        var run = Fixture.NewRun("adopt", "group-adopt.entra.json");
        var preview = await Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes");
        AssertChange(preview, rowSuffix, "Modify", "what-if of the adoption");
        Assert.DoesNotContain(preview.Changes, c => (string?)c["changeType"] == "Create");
        Assert.True(preview.HasDiagnostic(EngineDiagnosticCodes.GroupAdoptedByDisplayName), $"what-if did not warn {EngineDiagnosticCodes.GroupAdoptedByDisplayName}: {Tail(preview.Combined)}");

        var created = await Fixture.Cli.CreateAsync(run, Ct);
        AssertCreated(created, "create (adoption)");
        Assert.True(created.HasDiagnostic(EngineDiagnosticCodes.GroupAdoptedByDisplayName), $"create did not warn {EngineDiagnosticCodes.GroupAdoptedByDisplayName}: {Tail(created.Combined)}");

        var adopted = await EventuallyAsync(
            () => Graph.GetObjectAsync(new GraphRequest("/groups/" + groupId).WithSelect("id", "uniqueName", "description"), Ct),
            found => string.Equals(found.Text("uniqueName"), uniqueName, StringComparison.OrdinalIgnoreCase) && found.Text("description") is not null);
        Assert.Equal(uniqueName, adopted.Text("uniqueName"), ignoreCase: true);
        Assert.Equal("Adopted by the EARM integration tests", adopted.Text("description"));
        Assert.Equal(groupId, (await FindOneAsync("/groups", "displayName", displayName, "id"))!.RequireId());
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after the adoption");

        // Two groups without a uniqueName share the declared displayName: EARM cannot tell which to adopt and creates nothing.
        string duplicateName = Fixture.Name("adopt-dup");
        string duplicateUniqueName = Fixture.Name("adopt-dup-u");
        string first = (await TenantObjects.CreateGroupAsync(Fixture, "adopt-dup1", Ct, duplicateName)).RequireId();
        string second = (await TenantObjects.CreateGroupAsync(Fixture, "adopt-dup2", Ct, duplicateName)).RequireId();
        await EventuallyAsync(() => GroupIdsByDisplayNameAsync(duplicateName), ids => ids.Contains(first) && ids.Contains(second));
        var ambiguousRun = Fixture.NewRun("adopt-dup", "group-adopt.entra.json")
            .WithParameter("uniqueNameSuffix", "adopt-dup-u")
            .WithParameter("displayNameSuffix", "adopt-dup");

        var ambiguousPreview = await Fixture.Cli.WhatIfAsync(ambiguousRun, Ct, "--exit-code-on-changes");
        Assert.NotEqual(0, ambiguousPreview.ExitCode);
        var row = ambiguousPreview.Changes.FirstOrDefault(c => ((string?)c["resourceId"])?.EndsWith("/groups/" + duplicateUniqueName, StringComparison.OrdinalIgnoreCase) == true);
        string reason = (string?)row?["unsupportedReason"] ?? string.Empty;
        Assert.True(
            (string?)row?["changeType"] == "Unsupported" && reason.Contains(first, StringComparison.OrdinalIgnoreCase) && reason.Contains(second, StringComparison.OrdinalIgnoreCase),
            $"what-if of the ambiguous adoption: expected an Unsupported row naming both groups ({first}, {second}) but got: {ambiguousPreview.DescribeChanges()}{Environment.NewLine}{Tail(ambiguousPreview.Combined)}");

        var ambiguousCreate = await Fixture.Cli.CreateAsync(ambiguousRun, Ct);
        Assert.NotEqual(0, ambiguousCreate.ExitCode);
        var operation = ambiguousCreate.Operations.FirstOrDefault(o => ((string?)o["resourceId"])?.EndsWith("/groups/" + duplicateUniqueName, StringComparison.OrdinalIgnoreCase) == true);
        Assert.True(
            (string?)operation?["errorCode"] == EngineDiagnosticCodes.AmbiguousMatch,
            $"create of the ambiguous adoption: expected {EngineDiagnosticCodes.AmbiguousMatch} but the operations are: {ambiguousCreate.DescribeOperations()}{Environment.NewLine}{Tail(ambiguousCreate.Combined)}");
        Assert.Null(await Graph.GetObjectAsync(new GraphRequest(GraphPath.AlternateKey("/groups", "uniqueName", duplicateUniqueName)).WithSelect("id"), Ct));
        var duplicates = await GroupIdsByDisplayNameAsync(duplicateName);
        Assert.Equal(2, duplicates.Count);
        Assert.Contains(first, duplicates);
        Assert.Contains(second, duplicates);

        // The adopted group now holds the uniqueName, so deployment delete removes it.
        AssertExit(0, await Fixture.Cli.DeleteAllAsync(run, Ct), "deployment delete");
        Assert.Null(await EventuallyAsync(() => Graph.GetObjectAsync(new GraphRequest("/groups/" + groupId).WithSelect("id"), Ct), found => found is null));
    }

    private async Task<HashSet<string>> GroupIdsByDisplayNameAsync(string displayName)
    {
        var groups = await Graph.ListAsync(new GraphRequest("/groups").WithFilter($"displayName eq '{displayName}'").WithSelect("id"), Ct);
        return new HashSet<string>(groups.Select(g => g.RequireId()), StringComparer.OrdinalIgnoreCase);
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

        // Only u1's password was generated: only it is a one-time secret in the file.
        var oneTimeSecrets = SecureOutputs(secretFile)[SecureOutputsFile.OneTimeSecretsKey];
        Assert.Equal(generated, (string?)oneTimeSecrets?["/providers/Microsoft.Entra/users/" + Fixture.Upn("u1")]?["generatedPassword"]);
        Assert.Null(oneTimeSecrets?["/providers/Microsoft.Entra/users/" + Fixture.Upn("u2")]);

        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");

        // Drift of a managed property.
        await Graph.PatchAsync(new GraphRequest("/users/" + u1.RequireId()).WithHint(RetryHint.ExpectReplicationLag), new JsonObject { ["jobTitle"] = "Drifted" }, Ct);
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
        var group = await FindOneEventuallyAsync("/groups", "displayName", Fixture.Name("guests"), found => found is not null, "id");
        Assert.Contains(guest.RequireId(), await IdsOfAsync($"/groups/{group!.RequireId()}/members"));
        await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(run, Ct, "--exit-code-on-changes"), "what-if after create");
    }
}
