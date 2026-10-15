using System.CommandLine;

namespace EArm.Cli.Commands;

/// <summary>
/// <c>earm deployment create -f &lt;file&gt; [-p ...] [-n name] [--mode Incremental|Complete] [--what-if] [-c] [-y]
/// [--parallelism N] [--continue-on-error] [--reveal-secure-outputs] [--secure-outputs-file path] [--allow-ca-enforce]
/// [--on-soft-deleted-conflict error|restore] [--force-unlock] [--lock-timeout s] [--result-format ...]
/// [--exit-code-on-changes] [--no-pretty-print]</c>: applies a template through Microsoft Graph with the signed-in identity (mirrors
/// <c>az deployment group create</c>), recording every resource in the deployment's state. <c>--what-if</c> is an
/// alias of <c>deployment what-if</c>; <c>-c</c> shows the what-if, asks for confirmation and executes the same plan.
/// </summary>
internal static class CreateCommand
{
    public static Command Build()
    {
        var options = new DeploymentOptions();
        var command = new Command("create", "Deploys a template: creates and updates the declared resources in dependency order through Microsoft Graph with the signed-in identity.");
        options.AddCommon(command);
        options.AddCreate(command);
        command.SetAction((parseResult, ct) => DeploymentRunner.CreateAsync(
            options.Read(parseResult),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            DeploymentRunner.DefaultReadLine,
            ct));
        return command;
    }
}
