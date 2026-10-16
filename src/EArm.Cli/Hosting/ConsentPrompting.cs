using System;
using System.IO;
using System.Threading.Tasks;
using EArm.Graph.Auth;

namespace EArm.Cli.Hosting;

/// <summary>
/// Decides whether a run may prompt for missing delegated consent (incremental consent) and builds the prompter: only when a person can answer it
/// (standard input and standard error are an interactive terminal) and the run signs in interactively with a stored sign-in
/// (<see cref="GraphAuthFactory.CreateConsentPrompter"/> returns null otherwise). Otherwise the run gets no prompter, and a consent failure stops it
/// with <see cref="ConsentRequiredException"/> guidance before any write. The verbs that change the tenant opt in by passing the result to
/// <see cref="GraphAuthFactory.Create(GraphAuthOptions, IConsentPrompter?)"/>.
/// </summary>
internal static class ConsentPrompting
{
    /// <summary>Whether a person is at the terminal: standard input and standard error not redirected. A seam for the tests.</summary>
    internal static Func<bool> TerminalIsInteractive { get; set; } = () => !Console.IsInputRedirected && !Console.IsErrorRedirected;

    /// <summary>The consent prompter of the run, or null when it must not prompt (non-interactive terminal, <c>--auth default</c>, no stored sign-in).</summary>
    /// <param name="factory">The run's authentication factory.</param>
    /// <param name="auth">The run's authentication options.</param>
    /// <param name="stderr">Where the "consent needed" notice and the device-code instructions go.</param>
    /// <param name="deviceCode">Prompt with the device-code flow instead of a browser.</param>
    public static IConsentPrompter? Create(GraphAuthFactory factory, GraphAuthOptions auth, TextWriter stderr, bool deviceCode = false)
    {
        ArgumentNullException.ThrowIfNull(factory);
        ArgumentNullException.ThrowIfNull(auth);
        ArgumentNullException.ThrowIfNull(stderr);
        if (auth.Mode == GraphAuthMode.Default || !TerminalIsInteractive())
        {
            return null;
        }

        return factory.CreateConsentPrompter(
            auth,
            deviceCode,
            (info, _) =>
            {
                stderr.WriteLine(info.Message);
                return Task.CompletedTask;
            },
            notice => stderr.WriteLine(notice));
    }
}
