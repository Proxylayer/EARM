using System.CommandLine;
using System.CommandLine.Invocation;

namespace EArm.Cli;

/// <summary>
/// Terminating action for <c>--version</c>: writes the CLI assembly's informational version and exits 0.
/// </summary>
internal sealed class VersionAction : SynchronousCommandLineAction
{
    public override int Invoke(ParseResult parseResult)
    {
        parseResult.InvocationConfiguration.Output.WriteLine(VersionInfo.InformationalVersion);
        return 0;
    }
}
