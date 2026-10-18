using System;
using System.Linq;
using EArm.Engine.Planning;
using System.Threading.Tasks;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>The directory extension provider end to end: the prefixed Graph name, rerun, immutability (recreate), what-if, delete warnings and refusals.</summary>
public sealed class ExtensionPropertiesProviderTests
{
    private const string ExtType = ExtensionPropertiesProvider.ResourceType;
    private const string AppName = "hr-sync";
    private const string ExtName = "costCenter";

    private static string App()
    {
        return "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + AppName + "\", \"properties\": { \"displayName\": \"HR sync\" } }";
    }

    private static string Ext(string properties = "\"dataType\": \"String\", \"targetObjects\": [\"User\"]", string name = ExtName)
    {
        return "{ \"type\": \"" + ExtType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + AppName + "/" + name + "\", \"properties\": { " + properties + " } }";
    }

    private static string ExtensionsPath(ProviderHost host)
    {
        return "/applications/" + host.IdOf("/applications", "uniqueName", AppName) + "/extensionProperties";
    }

    private static string GraphName(ProviderHost host)
    {
        string appId = host.Directory.Find("/applications", "uniqueName", AppName)!["appId"]!.GetValue<string>();
        return "extension_" + appId.Replace("-", string.Empty, StringComparison.Ordinal) + "_" + ExtName;
    }

    [Fact]
    public void Discovery_RegistersTheProvider_WithDeleteWarningsAndRefusals()
    {
        var provider = ProviderDiscovery.CreateRegistry(EArm.Core.Types.ResourceTypeCatalog.Default).Get(ExtType);
        Assert.IsType<ExtensionPropertiesProvider>(provider);
        Assert.IsAssignableFrom<IDeleteWarningSource>(provider);
        Assert.IsAssignableFrom<IDeleteRefusalSource>(provider);
    }

    [Fact]
    public async Task Create_PostsTheShortName_AndReferenceReturnsThePrefixedGraphName()
    {
        var host = new ProviderHost();
        const string Outputs = "\"attribute\": { \"type\": \"string\", \"value\": \"[reference(resourceId('Microsoft.Entra/applications/extensionProperties', 'hr-sync', 'costCenter')).name]\" }";

        var run = await host.CreateAsync(host.Resources(App() + ", " + Ext(), Outputs));

        Check.Succeeded(run);
        var post = Assert.Single(run.Writes, w => w.Method == "POST");
        Assert.Equal(ExtensionsPath(host), post.Path);
        var body = Check.Body(post);
        Assert.Equal(ExtName, body["name"]!.GetValue<string>());
        Assert.Equal("String", body["dataType"]!.GetValue<string>());
        Assert.False(body["isMultiValued"]!.GetValue<bool>());
        var live = Assert.Single(host.Directory.Objects(ExtensionsPath(host)));
        Assert.Equal(GraphName(host), live["name"]!.GetValue<string>());
        Assert.Equal(GraphName(host), run.Output("attribute")!.GetValue<string>());
        Assert.Equal(live["id"]!.GetValue<string>(), run.Op(ExtName).GraphId);
    }

    [Fact]
    public async Task Rerun_IsNoChange_FoundByTheFilteredName()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(App() + ", " + Ext())));

        var rerun = await host.CreateAsync(host.Resources(App() + ", " + Ext()));

        Check.Succeeded(rerun);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
        var read = Assert.Single(rerun.Requests, r => r.Method == "GET" && r.Path == ExtensionsPath(host));
        Assert.Equal("name eq '" + GraphName(host) + "'", read.UrlParameters!["$filter"]);
    }

    [Fact]
    public async Task ChangingTheDataType_IsARecreate_WithADataLossWarning()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(App() + ", " + Ext())));
        string oldId = host.Directory.Objects(ExtensionsPath(host)).Single()["id"]!.GetValue<string>();
        var changed = host.Resources(App() + ", " + Ext("\"dataType\": \"Integer\", \"targetObjects\": [\"User\"]"));

        var whatIf = await host.WhatIfAsync(changed);
        var row = whatIf.ChangeOfType(ExtType);
        Assert.Equal(ChangeType.Modify, row.Type);
        Assert.Equal(ChangeMode.Recreate, row.Mode);
        Check.NoWrites(whatIf);

        var run = await host.CreateAsync(changed);

        Check.Succeeded(run);
        Assert.Equal(["DELETE", "POST"], run.Writes.Select(w => w.Method).ToList());
        Assert.Equal(ExtensionsPath(host) + "/" + oldId, run.Writes[0].Path);
        var warning = Check.Diagnostic(run, EngineDiagnosticCodes.DestructiveDelete);
        Assert.Contains("unreachable", warning.Message, StringComparison.Ordinal);
        var live = Assert.Single(host.Directory.Objects(ExtensionsPath(host)));
        Assert.Equal("Integer", live["dataType"]!.GetValue<string>());
    }

    [Fact]
    public async Task ARecreate_IsNotDone_WithoutConfirmation()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(App() + ", " + Ext())));
        var plan = await host.Planner().PlanAsync(host.Resources(App() + ", " + Ext("\"dataType\": \"String\", \"targetObjects\": [\"User\", \"Group\"]")), DeploymentMode.Incremental, PlanMode.Create, ProviderHost.Ct);
        host.ClearRequests();

        var result = await host.Executor().ExecuteAsync(plan, new ExecuteOptions { Parallelism = 1, ApprovedRecreates = new System.Collections.Generic.HashSet<string>() }, ProviderHost.Ct);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Operations, o => o.ErrorCode == EngineDiagnosticCodes.RecreateNotConfirmed);
        Assert.Empty(host.Fake.Writes);
    }

    [Fact]
    public async Task ADeclaredFullGraphName_IsANameConflict()
    {
        var host = new ProviderHost();

        var error = await Assert.ThrowsAsync<ValidationFailedException>(() => host.WhatIfAsync(host.Resources(App() + ", " + Ext("\"name\": \"extension_0000_costCenter\", \"dataType\": \"String\", \"targetObjects\": [\"User\"]"))));

        Assert.Contains(EArm.Core.Diagnostics.DiagnosticCodes.NameMappedPropertyConflict, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DeploymentDelete_DeletesTheExtension_WithTheWarningListed()
    {
        var host = new ProviderHost();
        var document = host.Resources(App() + ", " + Ext());
        Check.Succeeded(await host.CreateAsync(document));
        string id = host.Directory.Objects(ExtensionsPath(host)).Single()["id"]!.GetValue<string>();
        var plan = await host.Planner().PlanAsync(host.Resources(Ext()), DeploymentMode.Incremental, PlanMode.WhatIf, ProviderHost.Ct);
        var targets = await host.Deleter().FindDeclaredObjectsAsync(plan, ProviderHost.Ct);
        var target = Assert.Single(targets);
        Assert.Contains("unreachable", target.Risk, StringComparison.Ordinal);
        host.ClearRequests();

        var deletion = await host.DeleteDeclaredAsync(host.Resources(Ext()));

        Assert.True(deletion.Succeeded, string.Join("; ", deletion.Outcomes.Select(o => o.Error)));
        var write = Assert.Single(host.Fake.Writes);
        Assert.Equal("DELETE", write.Method);
        Assert.Equal(ExtensionsPath(host) + "/" + id, write.Path);
        Assert.Empty(host.Directory.Objects(ExtensionsPath(host)));
    }

    [Fact]
    public async Task AnExtensionSyncedFromOnPremises_IsKept_ByDeploymentDelete()
    {
        var host = new ProviderHost();
        var app = host.Directory.AddApplication(AppName, "HR sync");
        host.Directory.AddExtensionProperty(app["id"]!.GetValue<string>(), ExtName, syncedFromOnPremises: true, targetObjects: "User");

        var deletion = await host.DeleteDeclaredAsync(host.Resources(Ext()));

        Assert.Equal(1, deletion.RefusedCount);
        Assert.Empty(host.Fake.Writes);
        Assert.Single(host.Directory.Objects(ExtensionsPath(host)));
    }

    [Fact]
    public async Task AnExistingExtension_IsAdopted_WithoutWrites()
    {
        var host = new ProviderHost();
        var app = host.Directory.AddApplication(AppName, "HR sync");
        host.Directory.AddExtensionProperty(app["id"]!.GetValue<string>(), ExtName, targetObjects: "User");

        var run = await host.CreateAsync(host.Resources(App() + ", " + Ext()));

        Check.Succeeded(run);
        Assert.Equal(ChangeType.NoChange, run.Op(ExtName).ChangeType);
        Assert.DoesNotContain(run.Writes, w => w.Path.Contains("extensionProperties", StringComparison.Ordinal));
    }
}
