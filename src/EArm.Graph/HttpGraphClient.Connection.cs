using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Graph;

public sealed partial class HttpGraphClient
{
    /// <summary>
    /// A <see cref="HttpClient"/> configured for Graph: no redirects, no cookies, gzip/deflate, platform TLS defaults, a connection lifetime
    /// that follows DNS changes, and an infinite client timeout (the per-call timeout is applied by <see cref="HttpGraphClient"/>).
    /// </summary>
    public static HttpClient CreateHttpClient()
    {
        return new HttpClient(CreateHandler(), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The production handler settings of <see cref="CreateHttpClient"/>, separate so tests can inspect every property.</summary>
    internal static SocketsHttpHandler CreateHandler()
    {
        return new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            ConnectTimeout = TimeSpan.FromSeconds(30),
            ConnectCallback = ConnectAsync,
        };
    }

    /// <summary>
    /// The plain TCP connect (what <see cref="SocketsHttpHandler"/> does by default; TLS is layered on top by the handler) with a probe on the request that
    /// asked for the connection, so a per-call timeout that fires while the connection is still being made can be told apart from one that fires after
    /// bytes were sent (see <see cref="ConnectProbe"/>).
    /// </summary>
    private static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        ConnectProbe? probe = null;
        context.InitialRequestMessage?.Options.TryGetValue(ConnectProbeKey, out probe);
        probe?.MarkStarted();
        var socket = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(context.DnsEndPoint, cancellationToken).ConfigureAwait(false);
            probe?.MarkConnected();
            return new System.Net.Sockets.NetworkStream(socket, ownsSocket: true);
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Tracks, per request, whether the connection it asked for was still being established. A per-call timeout that fires in exactly that state, with
    /// no other call of the client having completed meanwhile (so the request cannot have been handed an idle connection), proves that nothing was
    /// sent: the failure is not ambiguous and a write may be repeated. In every other case (including a handler without the callback) the timeout is
    /// reported as ambiguous.
    /// </summary>
    internal sealed class ConnectProbe
    {
        private volatile bool _started;
        private volatile bool _connected;

        public bool StillConnecting => _started && !_connected;

        internal void MarkStarted()
        {
            _started = true;
        }

        internal void MarkConnected()
        {
            _connected = true;
        }
    }

    /// <summary>The probe travels on the request's options to <see cref="ConnectAsync"/>.</summary>
    internal static readonly HttpRequestOptionsKey<ConnectProbe> ConnectProbeKey = new("EArm.ConnectProbe");
}
