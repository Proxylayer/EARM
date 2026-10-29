using System;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace EArm.TestSupport;

/// <summary>
/// Runs a delegate on a new thread with a fixed 1 MiB stack, the size of a Windows thread. The stack guards (<c>RuntimeHelpers.TryEnsureSufficientExecutionStack</c>)
/// trip on the remaining stack, not on a depth count, so a test that expects "too deep" from a tree of N levels only holds on a known stack size: a Linux
/// or macOS thread has 8 MiB and walks much deeper before the guard trips. An exception of the delegate is rethrown on the caller with its original stack trace.
/// </summary>
public static class FixedStackThread
{
    /// <summary>The stack size of the thread: 1 MiB.</summary>
    public const int StackBytes = 1024 * 1024;

    /// <summary>Runs <paramref name="work"/> and returns its result; fails when it does not finish within <paramref name="timeout"/> (30 s by default).</summary>
    public static T Run<T>(Func<T> work, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        T result = default!;
        ExceptionDispatchInfo? failure = null;
        var thread = new Thread(
            () =>
            {
                try
                {
                    result = work();
                }
                catch (Exception ex)
                {
                    failure = ExceptionDispatchInfo.Capture(ex);
                }
            },
            StackBytes)
        {
            IsBackground = true,
            Name = nameof(FixedStackThread),
        };
        thread.Start();
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        if (!thread.Join(limit))
        {
            throw new TimeoutException($"The work did not finish within {limit.TotalSeconds:F0} seconds.");
        }

        failure?.Throw();
        return result;
    }

    /// <summary>Runs <paramref name="work"/>; fails when it does not finish within <paramref name="timeout"/> (30 s by default).</summary>
    public static void Run(Action work, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(work);
        Run(
            () =>
            {
                work();
                return true;
            },
            timeout);
    }
}
