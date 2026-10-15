using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.TestSupport;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// Review 2E: <c>state adopt --type applications --name ...</c> goes through <c>ApplicationsProvider.AdoptUniqueNameAsync</c>: when Graph refuses to
/// set the (immutable) uniqueName the user gets the provider's explanation as one line with the code <c>UniqueNameNotSettable</c>, exit 1, not a bare Graph error.
/// </summary>
[Collection(CliCollection.Name)]
public sealed class Review2E_AdoptUniqueNameTests
{
    private const string AppObjectId = "11110000-0000-4000-8000-000000000001";
    private const string AppClientId = "22220000-0000-4000-8000-000000000002";
    private const string GroupObjectId = "33330000-0000-4000-8000-000000000003";

    private const string GetApp = "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/applications/" + AppObjectId + "\" }, \"status\": 200, \"body\": { \"id\": \"" + AppObjectId + "\", \"appId\": \"" + AppClientId + "\", \"displayName\": \"Orders API\" } }";

    private const string GetGroup = "{ \"match\": { \"method\": \"GET\", \"urlContains\": \"/groups/" + GroupObjectId + "\" }, \"status\": 200, \"body\": { \"id\": \"" + GroupObjectId + "\", \"displayName\": \"Admins\" } }";

    private const string PatchRejected = "{ \"match\": { \"method\": \"PATCH\" }, \"status\": 400, \"body\": { \"error\": { \"code\": \"Request_BadRequest\", \"message\": \"Property uniqueName is read-only.\" } } }";

    private static Task<CliResult> Adopt(CliSandbox sandbox, string type, string id, string name)
    {
        return CliRunner.RunAsync(["state", "adopt", "--type", type, "--id", id, "--unique-name", name, "-y", .. sandbox.StateArgs]);
    }

    [Fact]
    public async Task AnApplicationThatGraphRefusesToNameImmutably_ReportsUniqueNameNotSettable_InOneLine_AndExitsOne()
    {
        using var sandbox = new CliSandbox(nameof(AnApplicationThatGraphRefusesToNameImmutably_ReportsUniqueNameNotSettable_InOneLine_AndExitsOne));
        sandbox.UsePreflightOk(extraResponses: GetApp + "," + PatchRejected);

        var result = await Adopt(sandbox, "Microsoft.Entra/applications", AppObjectId, "orders-api");

        Assert.Equal(1, result.ExitCode);
        string error = result.StandardError.TrimEnd();
        Assert.StartsWith("UniqueNameNotSettable:", error, StringComparison.Ordinal);
        Assert.DoesNotContain('\n', error);
        Assert.Contains("orders-api", error, StringComparison.Ordinal);
        Assert.Contains("immutable", error, StringComparison.Ordinal);
        Assert.Contains("Property uniqueName is read-only.", error, StringComparison.Ordinal);
        Assert.DoesNotContain("Microsoft Graph refused the request", result.StandardError, StringComparison.Ordinal);
        Assert.Equal(string.Empty, result.StandardOutput.Trim());
    }

    [Fact]
    public async Task AnApplicationThatAcceptsTheName_StillSucceeds_ThroughTheProvider()
    {
        using var sandbox = new CliSandbox(nameof(AnApplicationThatAcceptsTheName_StillSucceeds_ThroughTheProvider));
        sandbox.UsePreflightOk(extraResponses: GetApp + ",{ \"match\": { \"method\": \"PATCH\" }, \"status\": 204 }");

        var result = await Adopt(sandbox, "applications", AppObjectId, "orders-api");

        Assert.Equal(0, result.ExitCode);
        var patch = Assert.Single(sandbox.RecordedRequests(), r => r.Method == "PATCH");
        Assert.Contains("/applications/" + AppObjectId, patch.Command, StringComparison.Ordinal);
        Assert.Equal("{\"uniqueName\":\"orders-api\"}", patch.Body!.Trim());
    }

    [Fact]
    public async Task AGroupThatGraphRejects_KeepsTheGenericGraphError()
    {
        using var sandbox = new CliSandbox(nameof(AGroupThatGraphRejects_KeepsTheGenericGraphError));
        sandbox.UsePreflightOk(extraResponses: GetGroup + "," + PatchRejected);

        var result = await Adopt(sandbox, "groups", GroupObjectId, "contoso-admins");

        Assert.Equal(1, result.ExitCode);
        Assert.Contains("Microsoft Graph refused the request:", result.StandardError, StringComparison.Ordinal);
        Assert.DoesNotContain("UniqueNameNotSettable", result.StandardError, StringComparison.Ordinal);
    }
}
