using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;

namespace EntraArm.Az.Tests.Infrastructure;

/// <summary>
/// An in-memory <see cref="IAzRunner"/> for tests of the layers above the process: a script maps each request to an
/// <see cref="AzResult"/> (no pwsh, no files), every request is recorded.
/// </summary>
internal sealed class ScriptedAzRunner : IAzRunner
{
    private readonly object _gate = new();
    private readonly List<AzRequest> _requests = [];
    private readonly Func<AzRequest, int, AzResult> _script;

    public ScriptedAzRunner(Func<AzRequest, int, AzResult> script)
    {
        _script = script ?? throw new ArgumentNullException(nameof(script));
    }

    public IReadOnlyList<AzRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>A runner that answers the n-th call with the n-th result (the last result repeats).</summary>
    public static ScriptedAzRunner Sequence(params AzResult[] results)
    {
        return new ScriptedAzRunner((_, index) => results[Math.Min(index, results.Length - 1)]);
    }

    public static AzResult Ok(string stdout = "")
    {
        return new AzResult(0, stdout, string.Empty, TimeSpan.Zero, false);
    }

    public static AzResult Fail(string stderr, int exitCode = 1, string stdout = "")
    {
        return new AzResult(exitCode, stdout, stderr, TimeSpan.Zero, false);
    }

    public Task<AzResult> RunAsync(AzRequest request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int index;
        lock (_gate)
        {
            index = _requests.Count;
            _requests.Add(request);
        }

        return Task.FromResult(_script(request, index));
    }
}
