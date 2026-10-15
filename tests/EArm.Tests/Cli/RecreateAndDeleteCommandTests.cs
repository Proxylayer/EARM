using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli;
using EArm.Cli.Commands;
using EArm.Engine;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The confirmation of replacements (an immutable property changed: delete, then create again) and the template-based <c>deployment delete</c>,
/// driven in-process against an in-memory directory (docs/cli.md).
/// </summary>
[Collection(CliCollection.Name)]
public sealed class RecreateAndDeleteCommandTests
{
    private const string LocType = "Microsoft.Entra/conditionalAccess/namedLocations";
    private const string LocPath = "/identity/conditionalAccess/namedLocations";

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static (CliSandbox Sandbox, FakeDirectory Directory) Setup(string test)
    {
        var sandbox = new CliSandbox(test);
        var directory = new FakeDirectory();
        sandbox.Handler.Directory = directory;
        sandbox.Tokens.TenantId = CliSandbox.Tenant;
        return (sandbox, directory);
    }

    /// <summary>An IP location in the tenant and a template that declares the same name as a country location: the kind is immutable, so the object is replaced.</summary>
    private static string SeedReplacement(CliSandbox sandbox, FakeDirectory directory)
    {
        directory.Add(LocPath, new JsonObject
        {
            ["@odata.type"] = "#microsoft.graph.ipNamedLocation",
            ["displayName"] = "Contoso offices",
            ["isTrusted"] = false,
            ["ipRanges"] = new JsonArray(new JsonObject { ["@odata.type"] = "#microsoft.graph.iPv4CidrRange", ["cidrAddress"] = "10.0.0.0/8" }),
        });
        return sandbox.WriteTemplate("loc.entra.json", "\"resources\": [ { \"type\": \"" + LocType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"Contoso offices\", \"properties\": { \"@odata.type\": \"#microsoft.graph.countryNamedLocation\", \"countriesAndRegions\": [ \"GB\" ] } } ]");
    }

    private static DeploymentInvocation Invocation(CliSandbox sandbox, string template, bool yes = false, OutputFormat output = OutputFormat.Table)
    {
        return new DeploymentInvocation
        {
            TemplateFile = template,
            Yes = yes,
            Output = output,
            Connection = sandbox.Connection,
        };
    }

    private static int Writes(CliSandbox sandbox)
    {
        return sandbox.RecordedCommands().Count(c => c.StartsWith("DELETE ", StringComparison.Ordinal) || c.StartsWith("POST ", StringComparison.Ordinal) || c.StartsWith("PATCH ", StringComparison.Ordinal));
    }

    private static string IpOrCountry(FakeDirectory directory)
    {
        return directory.Find(LocPath, "displayName", "Contoso offices")!["@odata.type"]!.GetValue<string>();
    }

    // ---- replacements ----

    [Fact]
    public async Task AReplacement_DeclinedAtThePrompt_WritesNothing_AndFailsWithRecreateNotConfirmed()
    {
        var (sandbox, directory) = Setup(nameof(AReplacement_DeclinedAtThePrompt_WritesNothing_AndFailsWithRecreateNotConfirmed));
        using var _ = sandbox;
        string template = SeedReplacement(sandbox, directory);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await DeploymentRunner.CreateAsync(Invocation(sandbox, template), stdout, stderr, () => "n", Ct);

        Assert.Equal(ExitCodes.Failure, exit);
        string err = stderr.ToString();
        Assert.Contains("The deployment replaces 1 object(s)", err, StringComparison.Ordinal);
        Assert.Contains("/providers/" + LocType + "/Contoso offices (" + LocType + ")", err, StringComparison.Ordinal);
        Assert.Contains("Replace them? (y/n):", err, StringComparison.Ordinal);
        Assert.Contains("that was not confirmed; pass -y to confirm", err, StringComparison.Ordinal);
        Assert.Contains("Deployment not started.", err, StringComparison.Ordinal);
        Assert.Equal(0, Writes(sandbox));
        Assert.Equal("#microsoft.graph.ipNamedLocation", IpOrCountry(directory));
    }

    [Fact]
    public async Task AReplacement_ConfirmedAtThePrompt_IsApplied()
    {
        var (sandbox, directory) = Setup(nameof(AReplacement_ConfirmedAtThePrompt_IsApplied));
        using var _ = sandbox;
        string template = SeedReplacement(sandbox, directory);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await DeploymentRunner.CreateAsync(Invocation(sandbox, template), stdout, stderr, () => "y", Ct);

        Assert.True(exit == ExitCodes.Ok, stdout + stderr.ToString());
        Assert.Contains(sandbox.RecordedCommands(), c => c.StartsWith("DELETE ", StringComparison.Ordinal) && c.Contains("namedLocations/", StringComparison.Ordinal));
        Assert.Equal("#microsoft.graph.countryNamedLocation", IpOrCountry(directory));
    }

    [Fact]
    public async Task AReplacement_WithYes_IsAppliedWithoutAPrompt()
    {
        var (sandbox, directory) = Setup(nameof(AReplacement_WithYes_IsAppliedWithoutAPrompt));
        using var _ = sandbox;
        string template = SeedReplacement(sandbox, directory);

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-y", "-o", "json");

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.DoesNotContain("Replace them?", result.StandardError, StringComparison.Ordinal);
        Assert.Equal("#microsoft.graph.countryNamedLocation", IpOrCountry(directory));
    }

    [Fact]
    public async Task AReplacement_WithoutYes_OnRedirectedStdin_FailsBeforeAnyWrite()
    {
        Assert.SkipUnless(Console.IsInputRedirected, "standard input is interactive in this test host; the prompt would block.");
        var (sandbox, directory) = Setup(nameof(AReplacement_WithoutYes_OnRedirectedStdin_FailsBeforeAnyWrite));
        using var _ = sandbox;
        string template = SeedReplacement(sandbox, directory);

        var result = await CliRunner.RunAsync("deployment", "create", "-f", template, "-o", "json");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("pass --yes to confirm", result.StandardError, StringComparison.Ordinal);
        var diagnostic = Assert.Single(result.Diagnostics)!;
        Assert.Equal(EngineDiagnosticCodes.RecreateNotConfirmed, diagnostic["code"]!.GetValue<string>());
        Assert.Contains("Deployment not started.", diagnostic["message"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal(0, Writes(sandbox));
        Assert.Equal("#microsoft.graph.ipNamedLocation", IpOrCountry(directory));
    }

    [Fact]
    public async Task APlainCreateWithoutReplacements_NeverPrompts()
    {
        var (sandbox, _) = Setup(nameof(APlainCreateWithoutReplacements_NeverPrompts));
        using var disposeSandbox = sandbox;
        string template = sandbox.WriteTemplate("g.entra.json", "\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true } } ]");
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await DeploymentRunner.CreateAsync(Invocation(sandbox, template), stdout, stderr, () => throw new InvalidOperationException("no prompt expected"), Ct);

        Assert.True(exit == ExitCodes.Ok, stdout + stderr.ToString());
        Assert.DoesNotContain("(y/n)", stderr.ToString(), StringComparison.Ordinal);
    }

    // ---- deployment delete ----

    private static string AppWithCredentialTemplate(CliSandbox sandbox)
    {
        return sandbox.WriteTemplate("app.entra.json", "\"resources\": [ "
            + "{ \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"orders-api\", \"properties\": { \"displayName\": \"Orders API\" } }, "
            + "{ \"type\": \"Microsoft.Entra/applications/federatedIdentityCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"orders-api/github-main\", \"properties\": { \"issuer\": \"https://token.actions.githubusercontent.com\", \"subject\": \"repo:contoso/orders:ref:refs/heads/main\" }, \"dependsOn\": [ \"[resourceId('Microsoft.Entra/applications', 'orders-api')]\" ] } ]");
    }

    private static string SeedAppWithCredential(FakeDirectory directory)
    {
        string appId = directory.AddApplication("orders-api", "Orders API")["id"]!.GetValue<string>();
        directory.Add("/applications/" + appId + "/federatedIdentityCredentials", new JsonObject { ["name"] = "github-main", ["issuer"] = "https://token.actions.githubusercontent.com", ["subject"] = "repo:contoso/orders:ref:refs/heads/main", ["audiences"] = new JsonArray("api://AzureADTokenExchange") });
        return appId;
    }

    [Fact]
    public async Task Delete_DeletesTheDeclaredObjects_ChildrenBeforeTheirParent()
    {
        var (sandbox, directory) = Setup(nameof(Delete_DeletesTheDeclaredObjects_ChildrenBeforeTheirParent));
        using var _ = sandbox;
        string appId = SeedAppWithCredential(directory);
        directory.AddApplication("not-declared", "Somebody else's app");
        string template = AppWithCredentialTemplate(sandbox);

        var result = await CliRunner.RunAsync("deployment", "delete", "-f", template, "-y", "-o", "json");

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.Equal(2, result.Json["deleted"]!.GetValue<int>());
        var deletes = sandbox.RecordedCommands().Where(c => c.StartsWith("DELETE ", StringComparison.Ordinal)).ToList();
        int credential = deletes.FindIndex(c => c.Contains("federatedIdentityCredentials", StringComparison.Ordinal));
        int application = deletes.FindIndex(c => c.EndsWith("/applications/" + appId, StringComparison.Ordinal));
        Assert.True(credential >= 0 && application > credential, "the credential goes before its application: " + string.Join(" | ", deletes));
        Assert.Null(directory.Find("/applications", "uniqueName", "orders-api"));
        Assert.NotNull(directory.Find("/applications", "uniqueName", "not-declared"));
        Assert.Equal(["orders-api/github-main", "orders-api"], result.Json["outcomes"]!.AsArray().Select(o => o!["resourceId"]!.GetValue<string>()).Select(id => id[(id.IndexOf("applications/", StringComparison.Ordinal) + "applications/".Length)..].Replace("/federatedIdentityCredentials", string.Empty, StringComparison.Ordinal)).ToArray());
    }

    [Fact]
    public async Task Delete_ListsWhatItDeletes_AndDecliningDeletesNothing()
    {
        var (sandbox, directory) = Setup(nameof(Delete_ListsWhatItDeletes_AndDecliningDeletesNothing));
        using var _ = sandbox;
        SeedAppWithCredential(directory);
        string template = AppWithCredentialTemplate(sandbox);
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();

        int exit = await DeleteCommand.RunAsync(Invocation(sandbox, template), stdout, stderr, () => "n", Ct);

        Assert.Equal(ExitCodes.Failure, exit);
        string err = stderr.ToString();
        Assert.Contains("declares 2 object(s) found in tenant " + CliSandbox.Tenant + "; 2 will be DELETED, in this order:", err, StringComparison.Ordinal);
        Assert.Contains("/providers/Microsoft.Entra/applications/orders-api (Microsoft.Entra/applications) \"Orders API\"", err, StringComparison.Ordinal);
        Assert.True(err.IndexOf("federatedIdentityCredentials/github-main", StringComparison.Ordinal) < err.IndexOf("applications/orders-api (", StringComparison.Ordinal));
        Assert.Contains("Delete these 2 object(s)? (y/n):", err, StringComparison.Ordinal);
        Assert.Contains("Nothing deleted.", err, StringComparison.Ordinal);
        Assert.DoesNotContain(sandbox.RecordedCommands(), c => c.StartsWith("DELETE ", StringComparison.Ordinal));
        Assert.NotNull(directory.Find("/applications", "uniqueName", "orders-api"));
    }

    [Fact]
    public async Task Delete_WritesNothingToDisk()
    {
        var (sandbox, directory) = Setup(nameof(Delete_WritesNothingToDisk));
        using var _ = sandbox;
        SeedAppWithCredential(directory);
        string template = AppWithCredentialTemplate(sandbox);

        var result = await CliRunner.RunAsync("deployment", "delete", "-f", template, "-y");

        Assert.True(result.ExitCode == 0, result.StandardOutput + result.StandardError);
        Assert.Empty(sandbox.UnexpectedFiles());
        Assert.Contains("deleted 2 object(s)", result.StandardOutput, StringComparison.Ordinal);
    }
}
