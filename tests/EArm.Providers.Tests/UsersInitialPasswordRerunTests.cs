using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine;
using EArm.Engine.Execution;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// A user's initial password follows the <see cref="LiveSecrets"/> contract: published in the run that creates the user, and on every
/// other run (no change, update) <c>passwordProfile.password</c> is <c>null</c> with the availability marker, so an output that reads it
/// is <c>null</c> with <see cref="EngineDiagnosticCodes.SecretNotAvailableOnRerun"/> instead of failing the run with <c>OutputEvaluationFailed</c>.
/// </summary>
public sealed class UsersInitialPasswordRerunTests
{
    private const string Upn = "u1@contoso.com";
    private const string Output = "\"u1InitialPassword\": { \"type\": \"securestring\", \"value\": \"[reference('u1@contoso.com').passwordProfile.password]\" }";

    private static string User(string displayName = "User One")
    {
        return Templates.Resource(UsersProvider.ResourceType, Upn, "{ \"displayName\": \"" + displayName + "\" }");
    }

    [Fact]
    public async Task Rerun_IsNoChange_TheInitialPasswordOutputIsNull_WithOneSecretNotAvailableOnRerunWarning()
    {
        using var directory = new TestDirectory();
        var host = new ProviderHost();
        var first = await host.CreateAsync(host.Resources(User(), Output));
        Check.Succeeded(first);
        Assert.NotNull(first.SecureOutput("u1InitialPassword"));
        Check.NoDiagnostic(first, EngineDiagnosticCodes.SecretNotAvailableOnRerun);
        string secureFile = directory.File("secure-outputs-rerun.json");

        var rerun = await host.CreateAsync(host.Resources(User(), Output), secureOutputsFile: secureFile);

        Check.Succeeded(rerun);
        Assert.Equal(DeploymentStatus.Succeeded, rerun.Executed.Status);
        Assert.Equal(ChangeType.NoChange, rerun.Executed.Operations.Single().ChangeType);
        Check.NoWrites(rerun);
        Check.NoDiagnostic(rerun, EngineDiagnosticCodes.OutputEvaluationFailed);
        Assert.True(rerun.Executed.Outputs.ContainsKey("u1InitialPassword"));
        Assert.Null(rerun.Output("u1InitialPassword"));
        Assert.False(rerun.Executed.SecureOutputs.ContainsKey("u1InitialPassword"));
        Assert.False(File.Exists(secureFile));
        var warning = Assert.Single(rerun.Executed.Diagnostics);
        Assert.Equal(EngineDiagnosticCodes.SecretNotAvailableOnRerun, warning.Code);
        Assert.Equal(DiagnosticSeverity.Warning, warning.Severity);
        Assert.Contains("/providers/Microsoft.Entra/users/" + Upn, warning.Message, StringComparison.Ordinal);
        Assert.Contains("never returns a user's password", warning.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("rotationTag", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnUpdateRun_AlsoPublishesTheInitialPasswordAsUnavailable()
    {
        var host = new ProviderHost();
        Check.Succeeded(await host.CreateAsync(host.Resources(User(), Output)));

        var update = await host.CreateAsync(host.Resources(User("User One Renamed"), Output));

        Check.Succeeded(update);
        Assert.Equal(ChangeType.Modify, update.Executed.Operations.Single().ChangeType);
        Assert.Null(update.Output("u1InitialPassword"));
        Check.Diagnostic(update, EngineDiagnosticCodes.SecretNotAvailableOnRerun);
        Check.NoDiagnostic(update, EngineDiagnosticCodes.OutputEvaluationFailed);
    }

    [Fact]
    public async Task TheLiveObjectsMarkers_AreTrueInTheCreatingRun_AndFalseOnARead()
    {
        var host = new ProviderHost();
        var first = await host.CreateAsync(host.Resources(User(), Output));
        Check.Succeeded(first);

        var provider = host.Provider(UsersProvider.ResourceType);
        var key = provider.ResolveKey(first.Plan.Instances.Single());
        var existing = await provider.ReadAsync(key, host.Fake, ProviderHost.Ct);

        Assert.NotNull(existing);
        Assert.True(LiveSecrets.IsInitialPasswordUnavailable(existing.Raw));
        Assert.True(LiveSecrets.IsOneTimeSecretUnavailable(existing.Raw));
        Assert.False(LiveSecrets.IsSecretTextUnavailable(existing.Raw));
        Assert.Null(existing.Raw["passwordProfile"]!["password"]);

        // The diff view is not touched: passwordProfile is create-only and never compared.
        Assert.False(existing.Properties.ContainsKey("passwordProfile"));
        Assert.False(existing.Properties.ContainsKey(LiveSecrets.InitialPasswordAvailabilityMarker));
    }
}
