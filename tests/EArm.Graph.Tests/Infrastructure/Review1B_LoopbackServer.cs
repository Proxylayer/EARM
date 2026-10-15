using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Graph.Tests.Infrastructure;

/// <summary>One request as the loopback server read it off the socket.</summary>
internal sealed record LoopbackRequest(string Method, string Target, IReadOnlyDictionary<string, string> Headers, byte[] Body)
{
    public string? Header(string name)
    {
        return Headers.TryGetValue(name, out var value) ? value : null;
    }
}

/// <summary>What the loopback server answers: a status line, headers and a body, or a deliberate misbehaviour.</summary>
internal sealed class LoopbackReply
{
    public int Status { get; init; } = 200;

    public string Reason { get; init; } = "OK";

    public List<KeyValuePair<string, string>> Headers { get; init; } = [];

    public byte[] Body { get; init; } = [];

    /// <summary>Never answer (until the server is disposed): exercises the client timeout.</summary>
    public bool Hang { get; init; }

    /// <summary>Close the connection without writing a byte: a reset after the request was fully read.</summary>
    public bool CloseWithoutReply { get; init; }

    public static LoopbackReply Json(string body, int status = 200, string reason = "OK")
    {
        return new LoopbackReply
        {
            Status = status,
            Reason = reason,
            Headers = [new("Content-Type", "application/json")],
            Body = Encoding.UTF8.GetBytes(body),
        };
    }

    public static LoopbackReply Gzip(byte[] plain, string contentType = "application/json")
    {
        using var buffer = new MemoryStream();
        using (var gzip = new GZipStream(buffer, CompressionLevel.SmallestSize, leaveOpen: true))
        {
            gzip.Write(plain, 0, plain.Length);
        }

        return new LoopbackReply
        {
            Headers = [new("Content-Type", contentType), new("Content-Encoding", "gzip")],
            Body = buffer.ToArray(),
        };
    }

    public LoopbackReply With(string name, string value)
    {
        Headers.Add(new(name, value));
        return this;
    }
}

/// <summary>
/// A tiny HTTP/1.1 server on 127.0.0.1 (a <see cref="TcpListener"/>, one request per connection, <c>Connection: close</c>) so the PRODUCTION
/// <see cref="System.Net.Http.HttpClient"/> settings (redirects, cookies, decompression, timeouts) can be exercised over a real socket.
/// </summary>
internal sealed class LoopbackServer : IDisposable
{
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<LoopbackRequest> _requests = [];
    private readonly object _gate = new();
    private readonly Func<LoopbackRequest, LoopbackReply> _handler;
    private readonly Task _acceptLoop;

    public LoopbackServer(Func<LoopbackRequest, LoopbackReply> handler)
    {
        _handler = handler;
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    public int Port { get; }

    public IReadOnlyList<LoopbackRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        _listener.Stop();
        try
        {
            _acceptLoop.Wait(TimeSpan.FromSeconds(5));
        }
        catch (AggregateException)
        {
            // Shutting down: the accept loop ends with an exception once the listener stops.
        }

        _stop.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            _ = Task.Run(() => ServeAsync(client));
        }
    }

    private async Task ServeAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                var stream = client.GetStream();
                var request = await ReadRequestAsync(stream).ConfigureAwait(false);
                if (request is null)
                {
                    return;
                }

                lock (_gate)
                {
                    _requests.Add(request);
                }

                var reply = _handler(request);
                if (reply.Hang)
                {
                    await Task.Delay(Timeout.Infinite, _stop.Token).ConfigureAwait(false);
                    return;
                }

                if (reply.CloseWithoutReply)
                {
                    client.Client.LingerState = new LingerOption(true, 0);
                    return;
                }

                var head = new StringBuilder();
                head.Append("HTTP/1.1 ").Append(reply.Status).Append(' ').Append(reply.Reason).Append("\r\n");
                foreach (var header in reply.Headers)
                {
                    head.Append(header.Key).Append(": ").Append(header.Value).Append("\r\n");
                }

                head.Append("Content-Length: ").Append(reply.Body.Length).Append("\r\nConnection: close\r\n\r\n");
                var headBytes = Encoding.ASCII.GetBytes(head.ToString());
                await stream.WriteAsync(headBytes, _stop.Token).ConfigureAwait(false);
                await stream.WriteAsync(reply.Body, _stop.Token).ConfigureAwait(false);
                await stream.FlushAsync(_stop.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                // The client went away (a timeout test) or the server is shutting down.
            }
        }
    }

    private static async Task<LoopbackRequest?> ReadRequestAsync(NetworkStream stream)
    {
        var buffer = new List<byte>();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            buffer.Add(one[0]);
            var count = buffer.Count;
            if (count >= 4 && buffer[count - 4] == '\r' && buffer[count - 3] == '\n' && buffer[count - 2] == '\r' && buffer[count - 1] == '\n')
            {
                break;
            }
        }

        var lines = Encoding.ASCII.GetString([.. buffer]).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        var requestLine = lines[0].Split(' ');
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var i = 1; i < lines.Length; i++)
        {
            var colon = lines[i].IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[lines[i][..colon].Trim()] = lines[i][(colon + 1)..].Trim();
            }
        }

        var body = Array.Empty<byte>();
        if (headers.TryGetValue("Content-Length", out var lengthText) && int.TryParse(lengthText, out var length) && length > 0)
        {
            body = new byte[length];
            var total = 0;
            while (total < length)
            {
                var read = await stream.ReadAsync(body.AsMemory(total, length - total)).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
            }
        }

        return new LoopbackRequest(requestLine[0], requestLine[1], headers, body);
    }
}
