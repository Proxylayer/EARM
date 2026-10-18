using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Providers;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// Pins the decision of TODO-0063: a directory role assignment is never replaced. Every property it allows is part of its key, so the object its key
/// finds has nothing that differs (here: the same ids in another letter case) and a changed value is another assignment, a <c>Create</c>
/// (<see cref="Review1A_RunSafetyTests.AChangedCompositeKey_IsANewObject_TheOldOneIsNeverReplaced"/>). Only a named location's kind is replaced.
/// </summary>
public sealed class RecreateReachabilityTests
{
    private const string GlobalReader = "f2ef992c-3afb-46b9-b7cf-a126ee74c451";

    [Fact]
    public async Task ARoleAssignmentFoundByItsKey_IsNoChange_EvenWhenItsIdsAreSpelledInAnotherCase()
    {
        var host = new EngineHost(registry: ProviderDiscovery.CreateRegistry(ResourceTypeCatalog.Default));
        string user = IdOf(host.Directory.AddUser("u@contoso.com", "U"));
        string unit = IdOf(host.Directory.Add("/directory/administrativeUnits", new JsonObject { ["displayName"] = "AU" }));
        host.Directory.Add("/roleManagement/directory/roleAssignments", new JsonObject { ["roleDefinitionId"] = GlobalReader, ["principalId"] = user, ["directoryScopeId"] = "/administrativeUnits/" + unit });
        var document = Templates.Resources("{ \"type\": \"Microsoft.Entra/roleAssignments\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"" + Gid(5) + "\", \"properties\": { "
            + "\"roleDefinitionId\": \"" + GlobalReader.ToUpperInvariant() + "\", \"principalId\": \"" + user.ToUpperInvariant() + "\", \"directoryScopeId\": \"/administrativeUnits/" + unit.ToUpperInvariant() + "\" } }");

        var plan = await host.WhatIfAsync(document);

        var change = Assert.Single(plan.Changes);
        Assert.Equal(ChangeType.NoChange, change.Type);
        Assert.NotEqual(ChangeMode.Recreate, change.Mode);
    }
}
