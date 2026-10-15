using System.CommandLine;

namespace EArm.Cli.Commands;

/// <summary>
/// <c>earm deployment what-if -f &lt;file&gt; [-p ...] [-n name] [--mode ...] [--result-format ...] [--exit-code-on-changes]</c>:
/// reads every declared resource through the Azure CLI session and reports the changes a <c>create</c> would make,
/// without writing anything (mirrors <c>az deployment group what-if</c>).
/// </summary>
internal static class WhatIfCommand
{
    public static Command Build()
    {
        var options = new DeploymentOptions();
        var command = new Command("what-if", "Shows the changes a deployment would make (Create / Modify / Delete / NoChange / Ignore) without applying them.");
        options.AddCommon(command);
        options.AddWhatIf(command);
        command.SetAction((parseResult, ct) => DeploymentRunner.WhatIfAsync(
            options.Read(parseResult),
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ct));
        return command;
    }
}
