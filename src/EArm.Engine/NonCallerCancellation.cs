using System;
using EArm.Engine.Execution;

namespace EArm.Engine;

/// <summary>
/// The text for an <see cref="OperationCanceledException"/> the caller did not ask for: an HTTP timeout or a cancelled inner operation. The caller's own
/// cancellation (Ctrl+C) ends the command; this one only fails the row, read or step it happened in, so the rest of the run goes on.
/// </summary>
internal static class NonCallerCancellation
{
    public static string Describe(OperationCanceledException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        return SecretRegistration.Scrub($"the request timed out or was cancelled before it completed ({exception.GetType().Name}: {exception.Message})");
    }
}
