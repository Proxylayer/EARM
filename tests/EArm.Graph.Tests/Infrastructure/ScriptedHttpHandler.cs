using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Graph.Tests.Infrastructure;

/// <summary>One request as it reached the (fake) wire: the method, the URL, every header (request and content), the body text.</summary>
internal sealed record RecordedRequest(HttpMethod Method, Uri Uri, IReadOnlyDictionary<string, string> Headers, string? Body)
{
    public string? Header(string name)
    {
        return Headers.TryGetValue(name, out var value) ? value : null;
    }

    /// <summary>The decoded <c>key=value</c> pairs of the query string (percent-decoding applied once, <c>+</c> kept literal).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Query
    {
        get
        {
            var query = Uri.Query;
            if (query.Length <= 1)
            {
                return [];
            }

            return [.. query[1..].Split('&').Select(static part =>
            {
                var separator = part.IndexOf('=', StringComparison.Ordinal);
                return separator < 0
                    ? new KeyValuePair<string, string>(System.Uri.UnescapeDataString(part), string.Empty)
                    : new KeyValuePair<string, string>(System.Uri.UnescapeDataString(part[..separator]), System.Uri.UnescapeDataString(part[(separator + 1)..]));
            })];
        }
    }

    public string? QueryValue(string name)
    {
        foreach (var pair in Query)
        {
            if (pair.Key == name)
            {
                return pair.Value;
            }
        }

        return null;
    }

    public override string ToString()
    {
        return Method + " " + Uri.PathAndQuery;
    }
}

/// <summary>A scripted answer: status, body, headers, an optional delay (honouring cancellation) or an exception to throw instead.</summary>
internal sealed class ScriptedResponse
{
    public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;

    public string? Body { get; init; }

    public Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public TimeSpan Delay { get; init; }

    public Exception? Throw { get; init; }

    public static ScriptedResponse Json(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        return new ScriptedResponse { Status = status, Body = body };
    }

    public static ScriptedResponse NoContent()
    {
        return new ScriptedResponse { Status = HttpStatusCode.NoContent };
    }

    /// <summary>A Graph error document with the real status.</summary>
    public static ScriptedResponse Error(HttpStatusCode status, string code, string message, string? requestId = null)
    {
        var inner = requestId is null ? string.Empty : ",\"innerError\":{\"request-id\":\"" + requestId + "\"}";
        return new ScriptedResponse { Status = status, Body = "{\"error\":{\"code\":\"" + code + "\",\"message\":\"" + message + "\"" + inner + "}}" };
    }

    public ScriptedResponse WithHeader(string name, string value)
    {
        Headers[name] = value;
        return this;
    }
}

/// <summary>
/// A <see cref="HttpMessageHandler"/> that records every request (method, URL, headers, body) and replays scripted responses in order; when the
/// script is exhausted the last response repeats unless <see cref="Fallback"/> is set. Thread-safe.
/// </summary>
internal sealed class ScriptedHttpHandler : HttpMessageHandler
{
    private readonly List<RecordedRequest> _requests = [];
    private readonly Queue<Func<RecordedRequest, ScriptedResponse>> _script = new();
    private readonly object _gate = new();
    private int _inFlight;
    private int _maxInFlight;

    public ScriptedHttpHandler(params ScriptedResponse[] responses)
    {
        foreach (var response in responses)
        {
            Enqueue(response);
        }
    }

    /// <summary>Used when the script is empty; null makes an empty script throw.</summary>
    public Func<RecordedRequest, ScriptedResponse>? Fallback { get; set; }

    public IReadOnlyList<RecordedRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return [.. _requests];
            }
        }
    }

    /// <summary>The highest number of requests that were being handled at the same time.</summary>
    public int MaxConcurrency
    {
        get
        {
            lock (_gate)
            {
                return _maxInFlight;
            }
        }
    }

    public ScriptedHttpHandler Enqueue(ScriptedResponse response)
    {
        return Enqueue(_ => response);
    }

    public ScriptedHttpHandler Enqueue(Func<RecordedRequest, ScriptedResponse> responder)
    {
        lock (_gate)
        {
            _script.Enqueue(responder);
        }

        return this;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in request.Headers)
        {
            headers[header.Key] = string.Join(", ", header.Value);
        }

        string? body = null;
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                headers[header.Key] = string.Join(", ", header.Value);
            }

            body = Encoding.UTF8.GetString(await request.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
        }

        var recorded = new RecordedRequest(request.Method, request.RequestUri!, headers, body);
        Func<RecordedRequest, ScriptedResponse>? responder;
        lock (_gate)
        {
            _requests.Add(recorded);
            _inFlight++;
            _maxInFlight = Math.Max(_maxInFlight, _inFlight);
            responder = _script.Count > 0 ? _script.Dequeue() : Fallback;
        }

        try
        {
            if (responder is null)
            {
                throw new InvalidOperationException($"The scripted handler has no response left for {recorded}.");
            }

            var scripted = responder(recorded);
            if (scripted.Delay > TimeSpan.Zero)
            {
                await Task.Delay(scripted.Delay, cancellationToken).ConfigureAwait(false);
            }

            if (scripted.Throw is not null)
            {
                throw scripted.Throw;
            }

            var response = new HttpResponseMessage(scripted.Status) { RequestMessage = request };
            if (scripted.Body is not null)
            {
                response.Content = new StringContent(scripted.Body, new UTF8Encoding(false), "application/json");
            }

            foreach (var header in scripted.Headers)
            {
                if (!response.Headers.TryAddWithoutValidation(header.Key, header.Value))
                {
                    response.Content ??= new StringContent(string.Empty);
                    response.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
                }
            }

            return response;
        }
        finally
        {
            lock (_gate)
            {
                _inFlight--;
            }
        }
    }
}
