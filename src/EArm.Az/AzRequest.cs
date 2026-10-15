using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;

namespace EArm.Az;

/// <summary>
/// One Azure CLI invocation, expressed in the request-file protocol consumed by <c>Invoke-AzRest.ps1</c>.
/// For <c>rest</c> calls the query string always travels in <see cref="UrlParameters"/> (az URL-encodes them);
/// <see cref="Url"/> never carries a <c>?</c>.
/// </summary>
public sealed record AzRequest
{
    public static readonly IReadOnlyList<string> RestCommand = ["rest"];

    /// <summary>az sub-command, e.g. <c>["rest"]</c>, <c>["account", "show"]</c>, <c>["version"]</c>.</summary>
    public IReadOnlyList<string> Command { get; init; } = RestCommand;

    /// <summary>HTTP method (rest only).</summary>
    public string? Method { get; init; }

    /// <summary>Absolute URL without a query string (rest only).</summary>
    public string? Url { get; init; }

    /// <summary>Query parameters, passed as <c>--url-parameters k=v ...</c> (rest only).</summary>
    public IReadOnlyDictionary<string, string>? UrlParameters { get; init; }

    /// <summary>HTTP headers, passed as <c>--headers k=v ...</c> (rest only).</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Request body; written to a secure temp file and passed as <c>--body @file</c>. Never inlined in argv.</summary>
    public JsonNode? Body { get; init; }

    /// <summary>Extra arguments appended after the command-specific ones (e.g. <c>--resource-type ms-graph</c>).</summary>
    public IReadOnlyList<string>? Arguments { get; init; }

    /// <summary>Per-call timeout override; null uses <see cref="AzOptions.Timeout"/>.</summary>
    public TimeSpan? Timeout { get; init; }

    public bool IsRest => Command.Count >= 1 && string.Equals(Command[0], "rest", StringComparison.Ordinal);

    /// <summary>The command words joined with spaces, for logs and error messages.</summary>
    public string CommandText => string.Join(' ', Command);

    /// <summary>Creates an <c>az rest</c> request.</summary>
    public static AzRequest Rest(
        string method,
        string url,
        IReadOnlyDictionary<string, string>? urlParameters = null,
        IReadOnlyDictionary<string, string>? headers = null,
        JsonNode? body = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(method);
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        if (url.Contains('?', StringComparison.Ordinal))
        {
            throw new ArgumentException("Query strings must be passed as url parameters, never embedded in the url.", nameof(url));
        }

        return new AzRequest
        {
            Command = RestCommand,
            Method = method.ToUpperInvariant(),
            Url = url,
            UrlParameters = urlParameters,
            Headers = headers,
            Body = body,
        };
    }

    /// <summary>Creates a non-rest request such as <c>az account show</c> or <c>az version</c>.</summary>
    public static AzRequest ForCommand(params string[] command)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (command.Length == 0 || command.Any(string.IsNullOrWhiteSpace))
        {
            throw new ArgumentException("A command must have at least one non-empty word.", nameof(command));
        }

        return new AzRequest { Command = [.. command] };
    }

    public AzRequest WithArguments(params string[] arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return this with { Arguments = [.. Arguments ?? [], .. arguments] };
    }

    /// <summary>The JSON document written to the request file.</summary>
    internal JsonObject ToRequestFileJson(string? bodyFile, string? outputFile)
    {
        var json = new JsonObject
        {
            ["command"] = ToJsonArray(Command),
        };

        if (IsRest)
        {
            json["method"] = Method;
            json["url"] = Url;
            json["urlParameters"] = ToJsonObject(UrlParameters);
            json["headers"] = ToJsonObject(Headers);
            json["bodyFile"] = bodyFile;
        }

        json["outputFile"] = outputFile;
        json["args"] = ToJsonArray(Arguments ?? []);
        return json;
    }

    /// <summary>
    /// The exact argv the host script hands to az for this request (mirrors <c>Invoke-AzRest.ps1</c>). Used for
    /// redacted debug logging and by tests that compare against what the fake az recorded.
    /// </summary>
    public IReadOnlyList<string> BuildAzArguments(string? bodyFile)
    {
        var args = new List<string>(Command);
        if (IsRest)
        {
            if (!string.IsNullOrEmpty(Method))
            {
                args.Add("--method");
                args.Add(Method);
            }

            if (!string.IsNullOrEmpty(Url))
            {
                args.Add("--url");
                args.Add(Url);
            }

            if (UrlParameters is { Count: > 0 })
            {
                args.Add("--url-parameters");
                args.AddRange(UrlParameters.Select(static p => p.Key + "=" + p.Value));
            }

            if (Headers is { Count: > 0 })
            {
                args.Add("--headers");
                args.AddRange(Headers.Select(static p => p.Key + "=" + p.Value));
            }

            if (bodyFile is not null)
            {
                args.Add("--body");
                args.Add("@" + bodyFile);
            }
        }

        if (Arguments is not null)
        {
            args.AddRange(Arguments);
        }

        args.Add("--only-show-errors");
        args.Add("-o");
        args.Add("json");
        return args;
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(JsonValue.Create(value));
        }

        return array;
    }

    private static JsonObject ToJsonObject(IReadOnlyDictionary<string, string>? values)
    {
        var json = new JsonObject();
        if (values is null)
        {
            return json;
        }

        foreach (var pair in values)
        {
            json[pair.Key] = pair.Value;
        }

        return json;
    }
}
