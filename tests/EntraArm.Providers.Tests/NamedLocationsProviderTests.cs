using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EntraArm.Az;
using EntraArm.Core;
using EntraArm.Engine.Execution;
using EntraArm.Engine.Planning;
using EntraArm.Engine.Providers;
using EntraArm.Engine.State;
using EntraArm.Providers.Tests.Infrastructure;
using Xunit;
using GraphApi = EntraArm.Az.GraphApi;

namespace EntraArm.Providers.Tests;

/// <summary>The named locations provider (WI-14) end to end.</summary>
public sealed class NamedLocationsProviderTests
{
    private const string LocType = NamedLocationsProvider.ResourceType;
    private const string LocPath = "/identity/conditionalAccess/namedLocations";
    private const string LocName = "Contoso offices";
    private const string LocResourceId = "/providers/Microsoft.Entra/conditionalAccess/namedLocations/Contoso offices";

    private static string IpLocation(string ranges = "{ \"cidrAddress\": \"10.0.0.0/8\" }, { \"cidrAddress\": \"2001:db8::/32\" }", string extra = "", string name = LocName)
    {
        return "{ \"type\": \"" + LocType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"@odata.type\": \"#microsoft.graph.ipNamedLocation\", \"ipRanges\": [ " + ranges + " ]" + extra + " } }";
    }

    private static string CountryLocation(string codes = "\"gb\", \"IE\"", string extra = "", string name = LocName)
    {
        return "{ \"type\": \"" + LocType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { \"@odata.type\": \"#microsoft.graph.countryNamedLocation\", \"countriesAndRegions\": [ " + codes + " ]" + extra + " } }";
    }

    [Fact]
    public void Discovery_ImmutablesAndPermissions()
    {
        var registry = ProviderDiscovery.CreateRegistry(EntraArm.Core.Types.ResourceTypeCatalog.Default);
        var provider = Assert.IsType<NamedLocationsProvider>(registry.Get(LocType));
        var immutable = Assert.Single(provider.ImmutableProperties);
        Assert.Contains("@odata.type", immutable.ToString(), StringComparison.Ordinal);
        Assert.Equal(["Policy.ReadWrite.ConditionalAccess", "Policy.Read.All"], provider.RequiredPermissions(null));
    }

    [Fact]
    public async Task IpLocation_IsPostedWithInferredRangeTypes_AndRerunsAreNoChange()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(IpLocation()));

        Check.Succeeded(run);
        var post = Assert.Single(run.Writes);
        Assert.Equal("POST", post.Method);
        Assert.Equal(LocPath, post.Path);
        string body = post.Body!.ToJsonString();
        Assert.Contains("\"@odata.type\":\"#microsoft.graph.ipNamedLocation\"", body, StringComparison.Ordinal);
        Assert.Contains("{\"cidrAddress\":\"10.0.0.0/8\",\"@odata.type\":\"#microsoft.graph.iPv4CidrRange\"}", body, StringComparison.Ordinal);
        Assert.Contains("{\"cidrAddress\":\"2001:db8::/32\",\"@odata.type\":\"#microsoft.graph.iPv6CidrRange\"}", body, StringComparison.Ordinal);
        Assert.Contains("\"isTrusted\":false", body, StringComparison.Ordinal);
        Assert.Contains("\"displayName\":\"Contoso offices\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("\"id\"", body, StringComparison.Ordinal);
        Assert.Equal("GET " + LocPath + "?$filter=displayName eq 'Contoso offices'", run.Requests[0].ToString());

        var rerun = await host.CreateAsync(host.Resources(IpLocation()));
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);

        var reordered = await host.CreateAsync(host.Resources(IpLocation("{ \"cidrAddress\": \"2001:DB8::/32\", \"@odata.type\": \"#microsoft.graph.iPv6CidrRange\" }, { \"cidrAddress\": \"10.0.0.0/8\" }")));
        Assert.All(reordered.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(reordered);

        var whatIf = await new ProviderHost().WhatIfAsync(host.Resources(IpLocation()));
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.Create, row.Type);
        Assert.False(row.HasError);
        Check.NoWrites(whatIf);
    }

    [Fact]
    public async Task IsTrustedDrift_IsOnePatchCarryingTheODataType()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(IpLocation()));
        string id = host.IdOf(LocPath, "displayName", LocName);

        var drift = await host.CreateAsync(host.Resources(IpLocation(extra: ", \"isTrusted\": true")));

        Check.Succeeded(drift);
        var patch = Assert.Single(drift.Writes);
        Assert.Equal("PATCH", patch.Method);
        Assert.Equal(LocPath + "/" + id, patch.Path);
        Assert.Equal("{\"@odata.type\":\"#microsoft.graph.ipNamedLocation\",\"isTrusted\":true}", patch.Body!.ToJsonString());
        Assert.Equal("GET", drift.Requests[^1].Method);
        Assert.Equal(LocPath + "/" + id, drift.Requests[^1].Path);
        Assert.True(host.Directory.FindById(id)!["isTrusted"]!.GetValue<bool>());
    }

    [Fact]
    public async Task CountryLocation_IsPostedWithDefaults_AndComparedCaseInsensitively()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(CountryLocation()));

        Check.Succeeded(run);
        string body = Assert.Single(run.Writes).Body!.ToJsonString();
        Assert.Contains("\"countriesAndRegions\":[\"GB\",\"IE\"]", body, StringComparison.Ordinal);
        Assert.Contains("\"includeUnknownCountriesAndRegions\":false", body, StringComparison.Ordinal);
        Assert.Contains("\"countryLookupMethod\":\"clientIpAddress\"", body, StringComparison.Ordinal);
        Assert.DoesNotContain("isTrusted", body, StringComparison.Ordinal);

        var rerun = await host.CreateAsync(host.Resources(CountryLocation("\"IE\", \"gb\"")));
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);

        var changed = await host.CreateAsync(host.Resources(CountryLocation("\"GB\", \"IE\", \"FR\"", ", \"countryLookupMethod\": \"authenticatorAppGps\"")));
        Check.Succeeded(changed);
        Assert.Equal("{\"@odata.type\":\"#microsoft.graph.countryNamedLocation\",\"countriesAndRegions\":[\"GB\",\"IE\",\"FR\"],\"countryLookupMethod\":\"authenticatorAppGps\"}", Assert.Single(changed.Writes).Body!.ToJsonString());
    }

    [Fact]
    public async Task KindChange_IsARecreate_DeleteThenPost_WithTheStateRePinned()
    {
        var host = new ProviderHost();
        var first = await host.CreateAsync(host.Resources(IpLocation()));
        Check.Succeeded(first);
        string oldId = host.IdOf(LocPath, "displayName", LocName);
        Assert.Equal(oldId, first.State!.Resources.Values.Single().GraphId);

        var whatIf = await host.WhatIfAsync(host.Resources(CountryLocation()));
        var row = Assert.Single(whatIf.Plan.Changes);
        Assert.Equal(ChangeType.Modify, row.Type);
        Assert.Equal(ChangeMode.Recreate, row.Mode);
        Assert.False(row.HasError);

        var second = await host.CreateAsync(host.Resources(CountryLocation()));

        Check.Succeeded(second);
        var writes = second.Writes;
        Assert.Equal(2, writes.Count);
        Assert.Equal("DELETE", writes[0].Method);
        Assert.Equal(LocPath + "/" + oldId, writes[0].Path);
        Assert.Equal("POST", writes[1].Method);
        Assert.Equal(LocPath, writes[1].Path);
        var live = Assert.Single(host.Directory.Objects(LocPath));
        string newId = live["id"]!.GetValue<string>();
        Assert.Equal("#microsoft.graph.countryNamedLocation", live["@odata.type"]!.GetValue<string>());
        Assert.NotEqual(oldId, newId);
        Assert.Equal(newId, second.State!.Resources.Values.Single().GraphId);
        Assert.Equal(newId, host.StateEntryJson(LocResourceId)!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task InvalidRangesAndKinds_AreUnsupportedRows_AndTheCreateFailsBeforeWriting()
    {
        var host = new ProviderHost();

        var hostBits = await host.WhatIfAsync(host.Resources(IpLocation("{ \"cidrAddress\": \"10.0.0.1/8\" }")));
        var row = Assert.Single(hostBits.Plan.Changes);
        Assert.True(row.HasError);
        Assert.Contains("host bits", row.UnsupportedReason, StringComparison.Ordinal);

        var wrongType = await host.WhatIfAsync(host.Resources(IpLocation("{ \"cidrAddress\": \"10.0.0.0/8\", \"@odata.type\": \"#microsoft.graph.iPv6CidrRange\" }")));
        Assert.True(Assert.Single(wrongType.Plan.Changes).HasError);

        var foreign = await host.WhatIfAsync(host.Resources(CountryLocation(extra: ", \"isTrusted\": true")));
        Assert.True(Assert.Single(foreign.Plan.Changes).HasError);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(host.Resources("{ \"type\": \"" + LocType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"x\", \"properties\": { \"@odata.type\": \"#microsoft.graph.somethingElse\" } }")));
        Assert.Contains("@odata.type", ex.Message, StringComparison.Ordinal);

        var badKind = await host.WhatIfAsync(host.Resources("{ \"type\": \"" + LocType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"x\", \"properties\": { \"@odata.type\": \"[concat('#microsoft.graph.', 'somethingElse')]\" } }"));
        var badRow = Assert.Single(badKind.Plan.Changes);
        Assert.True(badRow.HasError);
        Assert.Contains("must be", badRow.UnsupportedReason, StringComparison.Ordinal);

        var noKind = await host.WhatIfAsync(host.Resources("{ \"type\": \"" + LocType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"x\", \"properties\": { \"isTrusted\": true } }"));
        var noKindRow = Assert.Single(noKind.Plan.Changes);
        Assert.True(noKindRow.HasError);
        Assert.Contains("@odata.type", noKindRow.UnsupportedReason, StringComparison.Ordinal);

        var create = await host.CreateAsync(host.Resources(IpLocation("{ \"cidrAddress\": \"10.0.0.1/8\" }")));
        Assert.NotEqual(DeploymentStatus.Succeeded, create.Executed.Status);
        Check.NoWrites(create);
    }

    [Fact]
    public async Task TwoLiveLocationsWithTheSameName_AreAmbiguous_WithAnAdoptHint()
    {
        var host = new ProviderHost();
        host.Directory.Add(LocPath, new JsonObject { ["@odata.type"] = "#microsoft.graph.ipNamedLocation", ["displayName"] = LocName, ["isTrusted"] = false, ["ipRanges"] = new JsonArray() });
        host.Directory.Add(LocPath, new JsonObject { ["@odata.type"] = "#microsoft.graph.ipNamedLocation", ["displayName"] = LocName, ["isTrusted"] = true, ["ipRanges"] = new JsonArray() });

        var run = await host.CreateAsync(host.Resources(IpLocation()));

        var op = run.Op("namedLocations");
        Assert.Equal(OperationStatus.Failed, op.Status);
        Assert.Contains("2 named locations have displayName", op.Error, StringComparison.Ordinal);
        Assert.Contains("entraarm state adopt --resource-id " + LocResourceId + " --object-id", op.Error, StringComparison.Ordinal);
        Check.NoWrites(run);

        var provider = host.Provider(LocType);
        var key = new ResourceKey { Type = LocType, Values = new Dictionary<string, string> { ["displayName"] = LocName } };
        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.ReadAsync(key, null, host.Fake, ProviderHost.Ct));
        Assert.Equal(ProviderException.AmbiguousMatch, ex.Code);
        Assert.All(host.Directory.Objects(LocPath).Select(o => o["id"]!.GetValue<string>()), id => Assert.Contains(id, ex.Message, StringComparison.Ordinal));
    }

    [Fact]
    public async Task ASingleQuoteInTheName_IsDoubledInTheFilter()
    {
        var host = new ProviderHost();

        var run = await host.CreateAsync(host.Resources(IpLocation(name: "O'Brien HQ")));

        Check.Succeeded(run);
        Assert.Equal("GET " + LocPath + "?$filter=displayName eq 'O''Brien HQ'", run.Requests[0].ToString());
        Assert.NotNull(host.Directory.Find(LocPath, "displayName", "O'Brien HQ"));
    }

    [Fact]
    public async Task PinnedId_IsReadFirst_AndA404FallsBackToTheNameFilter_RePinning()
    {
        var host = new ProviderHost();
        await host.CreateAsync(host.Resources(IpLocation()));
        string id = host.IdOf(LocPath, "displayName", LocName);

        var rerun = await host.CreateAsync(host.Resources(IpLocation()));
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Assert.Equal("GET " + LocPath + "/" + id, rerun.Requests[0].ToString());
        Assert.DoesNotContain(rerun.Requests, r => r.UrlParameters is not null && r.UrlParameters.ContainsKey("$filter"));

        await host.Fake.DeleteAsync(new GraphRequest(LocPath + "/" + id, GraphApi.V1), ProviderHost.Ct);
        var replacement = host.Directory.Add(LocPath, new JsonObject { ["@odata.type"] = "#microsoft.graph.ipNamedLocation", ["displayName"] = LocName, ["isTrusted"] = false, ["ipRanges"] = new JsonArray(new JsonObject { ["@odata.type"] = "#microsoft.graph.iPv4CidrRange", ["cidrAddress"] = "10.0.0.0/8" }, new JsonObject { ["@odata.type"] = "#microsoft.graph.iPv6CidrRange", ["cidrAddress"] = "2001:db8::/32" }) });
        string newId = replacement["id"]!.GetValue<string>();

        var drift = await host.CreateAsync(host.Resources(IpLocation()));

        Check.Succeeded(drift);
        Assert.Equal(ChangeType.NoChange, drift.Executed.Operations.Single().ChangeType);
        Assert.Equal("GET " + LocPath + "/" + id, drift.Requests[0].ToString());
        Assert.Equal("GET " + LocPath + "?$filter=displayName eq 'Contoso offices'", drift.Requests[1].ToString());
        Assert.Equal(newId, drift.State!.Resources.Values.Single().GraphId);
    }

    [Fact]
    public async Task Delete_ReferencedByAPolicy_IsNamedLocationReferenced_AndNotFoundIsTolerated()
    {
        var host = new ProviderHost();
        var location = host.Directory.Add(LocPath, new JsonObject { ["@odata.type"] = "#microsoft.graph.ipNamedLocation", ["displayName"] = LocName });
        string id = location["id"]!.GetValue<string>();
        host.Fake.Failures.Add("DELETE", LocPath + "/" + id, r => new GraphException(HttpStatusCode.BadRequest, "Request_BadRequest", "Cannot delete the named location because it is referenced by one or more Conditional Access policies.", r.Hint));
        var provider = host.Provider(LocType);
        var entry = new StateEntry { Id = ResourceIdentifier.Create(LocType, LocName), Type = LocType, ApiVersion = ProviderHost.ApiVersion, Key = new Dictionary<string, string> { ["displayName"] = LocName }, GraphId = id };

        var ex = await Assert.ThrowsAsync<ProviderException>(() => provider.DeleteAsync(entry, host.Fake, ProviderHost.Ct));

        Assert.Equal(NamedLocationsProvider.ReferencedByPolicy, ex.Code);
        Assert.Contains("referenced by one or more", ex.Message, StringComparison.Ordinal);
        Assert.Contains("conditions.locations", ex.Message, StringComparison.Ordinal);

        host.ClearRequests();
        await provider.DeleteAsync(entry with { GraphId = "missing" }, host.Fake, ProviderHost.Ct);
        var write = Assert.Single(host.Fake.Writes);
        Assert.Equal("DELETE", write.Method);
    }
}
