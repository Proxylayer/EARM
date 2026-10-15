using System;
using System.Net.Http;
using Microsoft.Extensions.Logging;

namespace EArm.Graph.Tests.Infrastructure;

/// <summary>An <see cref="HttpGraphClient"/> wired to a scripted handler, a fake token provider, a recording delay provider and a capturing logger.</summary>
internal sealed class ClientHarness : IDisposable
{
    private readonly HttpClient _http;

    public ClientHarness(ScriptedHttpHandler? handler = null, Action<GraphOptions>? configure = null, GraphCloud? cloud = null, TimeProvider? time = null)
    {
        Handler = handler ?? new ScriptedHttpHandler();
        Tokens = new FakeTokenProvider();
        Delays = new RecordingDelayProvider();
        Log = new LogSink();
        Time = new ManualTimeProvider(new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero));
        var options = new GraphOptions { Cloud = cloud ?? GraphCloud.Public };
        configure?.Invoke(options);
        _http = new HttpClient(Handler, disposeHandler: false) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        Retry = new RetryPolicy(new RetryOptions(), Delays, new TestLogger<RetryPolicy>(Log));
        Client = new HttpGraphClient(_http, Tokens, Retry, options, new TestLogger<HttpGraphClient>(Log), time ?? Time);
    }

    public ScriptedHttpHandler Handler { get; }

    public FakeTokenProvider Tokens { get; }

    public RecordingDelayProvider Delays { get; }

    public LogSink Log { get; }

    public ManualTimeProvider Time { get; }

    public RetryPolicy Retry { get; }

    public HttpGraphClient Client { get; }

    public void Dispose()
    {
        _http.Dispose();
    }
}
