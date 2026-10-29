using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Integration.Tests;

/// <summary>One Azure Resource Manager response as the recorder saw it: the request line, the status and the throttling and correlation headers only.</summary>
/// <param name="Sequence">The position in the run (1, 2, 3, ...): a probe takes <see cref="ArmResponseRecorder.Count"/> before its calls and reads what follows.</param>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The URL path (no query string: the filter is the probe's own and the path is enough to tell the calls apart).</param>
/// <param name="Status">The HTTP status code.</param>
/// <param name="Headers">The recorded headers by name (case-insensitive); see <see cref="ArmResponseRecorder.IsRecorded"/>.</param>
internal sealed record ArmExchange(int Sequence, string Method, string Path, int Status, IReadOnlyDictionary<string, string> Headers);

/// <summary>
/// A <see cref="DelegatingHandler"/> under the fixture's <see cref="EArm.Graph.ArmClient"/> that records every response's status and the headers the Azure
/// PIM policy probes ask about: the <c>x-ms-ratelimit-*</c> family (remaining reads and writes per subscription, tenant and resource), <c>Retry-After</c>
/// and <c>x-ms-request-id</c>. Request and response bodies, the <c>Authorization</c> header and every other header are never read or kept, and the
/// response is passed on untouched (the client above still sees every retry, as in production).
/// </summary>
internal sealed class ArmResponseRecorder : DelegatingHandler
{
    private readonly ConcurrentQueue<ArmExchange> _exchanges = new();
    private int _sequence;

    public ArmResponseRecorder(HttpMessageHandler innerHandler)
        : base(innerHandler)
    {
    }

    /// <summary>How many responses were recorded so far.</summary>
    public int Count => Volatile.Read(ref _sequence);

    /// <summary>Every recorded response in order.</summary>
    public IReadOnlyList<ArmExchange> All => [.. _exchanges.OrderBy(e => e.Sequence)];

    /// <summary>The responses recorded after <paramref name="mark"/> (a value of <see cref="Count"/> taken earlier).</summary>
    public IReadOnlyList<ArmExchange> Since(int mark)
    {
        return [.. _exchanges.Where(e => e.Sequence > mark).OrderBy(e => e.Sequence)];
    }

    /// <summary>True for the header names the recorder keeps.</summary>
    public static bool IsRecorded(string headerName)
    {
        ArgumentNullException.ThrowIfNull(headerName);
        return headerName.StartsWith("x-ms-ratelimit-", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("Retry-After", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("x-ms-request-id", StringComparison.OrdinalIgnoreCase);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Where(h => IsRecorded(h.Key)))
        {
            headers[header.Key] = string.Join(",", header.Value);
        }

        int sequence = Interlocked.Increment(ref _sequence);
        _exchanges.Enqueue(new ArmExchange(sequence, request.Method.Method, request.RequestUri?.AbsolutePath ?? string.Empty, (int)response.StatusCode, headers));
        return response;
    }
}
