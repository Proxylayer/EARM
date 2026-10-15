using System;
using System.IO;
using EArm.Engine.State;
using Microsoft.Extensions.Logging;

namespace EArm.Cli.Hosting;

/// <summary>Opens the file state store for a command: <c>--state-dir</c> → <c>EARM_STATE_DIR</c> → <c>.earm/state</c> next to the template (or the working directory).</summary>
internal static class StateHost
{
    public static FileStateStore OpenStore(string? stateDir, string? templateFile, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(loggerFactory);
        string directory = StateDirectory.Resolve(stateDir, templateFile);
        var logger = loggerFactory.CreateLogger("EArm.Cli.State");
        logger.LogDebug("State directory: {Directory}", directory);
        if (!StateDirectory.IsExpectedLocation(directory, templateFile))
        {
            // A state directory somewhere else (a share, another drive, a temp folder) is legitimate but unusual: say where it is so a mistyped --state-dir or EARM_STATE_DIR shows.
            logger.LogWarning("The state directory {Directory} is not under your user profile, the current directory or the template's folder; check --state-dir / EARM_STATE_DIR if that is not what you meant.", directory);
        }

        return new FileStateStore(directory, logger: loggerFactory.CreateLogger<FileStateStore>());
    }
}
