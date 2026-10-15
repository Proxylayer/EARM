using System;
using System.Collections.Generic;
using System.CommandLine;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Cli.Hosting;
using EArm.Cli.Output;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Engine.State;

namespace EArm.Cli.Commands;

/// <summary>
/// <c>earm deployment show -n &lt;name&gt; [--refresh] [-f template] [--state-dir dir] [-o table|json]</c>: the
/// deployment's state summary and its resources. <c>--refresh</c> re-reads every object through its provider (needs
/// a sign-in; exit 3 on a tenant mismatch) and reports drift (<c>InSync</c>, <c>Missing</c>,
/// <c>Replaced</c>) without changing the state.
/// </summary>
internal static class ShowCommand
{
    public static Command Build()
    {
        var options = new StateOptions();
        var refresh = new Option<bool>("--refresh")
        {
            Description = "Re-read every state-managed object from the tenant and report drift (needs a sign-in).",
        };

        var command = new Command("show", "Shows a deployment's state: last run, tenant and the resources it manages.");
        options.Add(command, nameRequired: true);
        command.Options.Add(refresh);
        command.SetAction((parseResult, ct) => RunAsync(
            options.Read(parseResult),
            parseResult.GetValue(refresh),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ct));
        return command;
    }

    internal static async Task<int> RunAsync(StateInvocation invocation, bool refresh, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        string name;
        try
        {
            name = invocation.RequireName();
        }
        catch (ArgumentException ex)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "InvalidArgument", ex.Message);
        }

        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        var store = StateHost.OpenStore(invocation.StateDir, invocation.TemplateFile, loggerFactory);
        StateFile? state;
        StateLockInfo? lockInfo;
        try
        {
            state = await store.LoadAsync(name, cancellationToken).ConfigureAwait(false);
            lockInfo = state is null ? null : await store.GetLockAsync(name, cancellationToken).ConfigureAwait(false);
        }
        catch (StateException ex)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "StateIoFailed", $"The state of deployment '{name}' could not be read: {ex.Message}");
        }

        if (state is null)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "DeploymentNotFound", $"No deployment named '{name}' in {store.Directory}.");
        }

        IReadOnlyDictionary<string, DriftReport>? drift = null;
        if (refresh)
        {
            if (!EngineServices.TryCreate(invocation.Connection, loggerFactory, stdout, stderr, invocation.Output, out var created, out int createExit))
            {
                return createExit;
            }

            using var services = created!;
            GraphPreflightResult preflight;
            try
            {
                preflight = await services.Preflight.RunAsync(cancellationToken).ConfigureAwait(false);
                state.EnsureTenant(preflight.TenantId);
            }
            catch (Exception ex) when (ex is GraphTransportException or GraphException or TenantMismatchException)
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.AuthOrPreflight, ex is TenantMismatchException mismatch ? mismatch.Code : "PreflightFailed", ex.Message);
            }

            drift = await RefreshAsync(state, services, cancellationToken).ConfigureAwait(false);
        }

        StateWriter.WriteShow(state, store.PathFor(name), lockInfo, drift, invocation.Output, prettyPrint: true, stdout);
        return ExitCodes.Ok;
    }

    private static async Task<IReadOnlyDictionary<string, DriftReport>> RefreshAsync(StateFile state, EngineServices services, CancellationToken cancellationToken)
    {
        var catalog = ResourceTypeCatalog.Default;
        var drift = new Dictionary<string, DriftReport>(StringComparer.Ordinal);
        foreach (var pair in state.Resources)
        {
            var entry = pair.Value;
            if (!services.Providers.TryGet(entry.Type, out var provider))
            {
                drift[pair.Key] = new DriftReport(DriftReport.Unknown, null, $"no provider for {entry.Type}");
                continue;
            }

            try
            {
                var existing = await provider.ReadAsync(entry.ToResourceKey(catalog), services.Graph, cancellationToken).ConfigureAwait(false);
                drift[pair.Key] = existing switch
                {
                    null => new DriftReport(DriftReport.Missing, null, "not found in the tenant"),
                    _ when string.Equals(existing.GraphId, entry.GraphId, StringComparison.OrdinalIgnoreCase) => new DriftReport(DriftReport.InSync, existing.GraphId, null),
                    _ => new DriftReport(DriftReport.Replaced, existing.GraphId, $"the key now belongs to {existing.GraphId}"),
                };
            }
            catch (Exception ex) when (ex is ProviderException or GraphException or GraphTransportException)
            {
                drift[pair.Key] = new DriftReport(DriftReport.Unknown, null, ex.Message);
            }
        }

        return drift;
    }
}
