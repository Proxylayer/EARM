using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Azure.Identity;

namespace EArm.Integration.Tests;

/// <summary>How one token request of the delegated-auth spike ended.</summary>
internal enum AuthSpikeOutcome
{
    /// <summary>A token was issued.</summary>
    Granted,

    /// <summary>A silent request could not be satisfied without user interaction (<see cref="AuthenticationRequiredException"/>); nothing was prompted.</summary>
    AuthenticationRequired,

    /// <summary>Microsoft Entra refused the request with an AADSTS error.</summary>
    Denied,

    /// <summary>The interactive prompt was not completed within the spike's time limit.</summary>
    TimedOut,

    /// <summary>Any other failure (no AADSTS code: credential unavailable, browser could not start, ...).</summary>
    Failed,
}

/// <summary>
/// The result of one token request made by <see cref="DelegatedAuthSpikeProbes"/>: what was asked for, how it ended, the
/// non-secret claims of the token (aud, scp, tid, appid) and the AADSTS code and message on failure. The token itself is only
/// kept in memory for the Key Vault usability check and is never part of <see cref="Describe"/>.
/// </summary>
internal sealed record AuthSpikeAttempt
{
    private static readonly Regex _aadstsCode = new(@"AADSTS(\d+)", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public required string Label { get; init; }

    public required string ClientId { get; init; }

    public required IReadOnlyList<string> Scopes { get; init; }

    /// <summary>True when the request was made with <c>DisableAutomaticAuthentication</c> (no prompt possible).</summary>
    public required bool Silent { get; init; }

    public required AuthSpikeOutcome Outcome { get; init; }

    /// <summary>The token claims that matter for the design (aud, scp, roles, tid, appid/azp, ver); empty when no token was issued.</summary>
    public IReadOnlyDictionary<string, string> Claims { get; init; } = new Dictionary<string, string>();

    /// <summary>The exception type name on failure.</summary>
    public string? ExceptionType { get; init; }

    /// <summary><c>AADSTS65002</c> etc., when the failure carried one.</summary>
    public string? AadstsCode { get; init; }

    /// <summary>The AADSTS sentence (trace and correlation ids cut off), or the exception's first line.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>The access token, for the Key Vault check only. Never printed.</summary>
    internal string? Token { get; init; }

    public bool IsGranted => Outcome == AuthSpikeOutcome.Granted;

    /// <summary>The <c>scp</c> claim split into scope names (empty without a token or without delegated scopes).</summary>
    public IReadOnlyList<string> GrantedScopes =>
        Claims.TryGetValue("scp", out string? scp) ? scp.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];

    /// <summary><c>GRANTED</c>, <c>DENIED (AADSTS65002)</c>, <c>AUTH-REQUIRED (AADSTS65001)</c>, ...</summary>
    public string Verdict
    {
        get
        {
            string word = Outcome switch
            {
                AuthSpikeOutcome.Granted => "GRANTED",
                AuthSpikeOutcome.AuthenticationRequired => "AUTH-REQUIRED",
                AuthSpikeOutcome.Denied => "DENIED",
                AuthSpikeOutcome.TimedOut => "TIMED-OUT",
                _ => "FAILED",
            };
            return AadstsCode is null ? word : $"{word} ({AadstsCode})";
        }
    }

    public static AuthSpikeAttempt FromToken(string label, string clientId, IReadOnlyList<string> scopes, bool silent, string token)
    {
        return new AuthSpikeAttempt
        {
            Label = label,
            ClientId = clientId,
            Scopes = scopes,
            Silent = silent,
            Outcome = AuthSpikeOutcome.Granted,
            Claims = ReadClaims(token),
            Token = token,
        };
    }

    public static AuthSpikeAttempt FromException(string label, string clientId, IReadOnlyList<string> scopes, bool silent, Exception exception, bool timedOut = false)
    {
        ArgumentNullException.ThrowIfNull(exception);
        string full = exception.ToString();
        Match code = _aadstsCode.Match(full);
        AuthSpikeOutcome outcome = timedOut ? AuthSpikeOutcome.TimedOut
            : exception is AuthenticationRequiredException ? AuthSpikeOutcome.AuthenticationRequired
            : code.Success ? AuthSpikeOutcome.Denied
            : AuthSpikeOutcome.Failed;
        return new AuthSpikeAttempt
        {
            Label = label,
            ClientId = clientId,
            Scopes = scopes,
            Silent = silent,
            Outcome = outcome,
            ExceptionType = exception.GetType().Name,
            AadstsCode = code.Success ? code.Value : null,
            ErrorMessage = ErrorSentence(full, exception),
        };
    }

    /// <summary>The diagnostic lines of this attempt (never the token).</summary>
    public IEnumerable<string> Describe()
    {
        yield return $"{Label}: client={ClientId} mode={(Silent ? "silent (DisableAutomaticAuthentication)" : "interactive allowed")} scopes=[{string.Join(", ", Scopes)}]";
        yield return $"{Label}: outcome={Verdict}{(ExceptionType is null ? string.Empty : " exception=" + ExceptionType)}";
        if (Claims.Count > 0)
        {
            yield return $"{Label}: claims " + string.Join(" ", Claims.Select(c => $"{c.Key}=[{c.Value}]"));
        }

        if (ErrorMessage is not null)
        {
            yield return $"{Label}: error {ErrorMessage}";
        }
    }

    /// <summary>The non-secret claims of a JWT access token; empty when the token is not a readable JWT.</summary>
    internal static IReadOnlyDictionary<string, string> ReadClaims(string token)
    {
        var claims = new SortedDictionary<string, string>(StringComparer.Ordinal);
        string[] parts = token.Split('.');
        if (parts.Length < 2)
        {
            claims["note"] = "token is not a JWT";
            return claims;
        }

        try
        {
            using var document = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            foreach (string name in new[] { "aud", "scp", "roles", "tid", "appid", "azp", "ver" })
            {
                if (document.RootElement.TryGetProperty(name, out JsonElement value))
                {
                    claims[name] = value.ValueKind == JsonValueKind.Array
                        ? string.Join(" ", value.EnumerateArray().Select(v => v.ToString()))
                        : value.ToString();
                }
            }
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
            claims["note"] = "token payload is not readable JSON (" + ex.GetType().Name + ")";
        }

        return claims;
    }

    private static byte[] Base64UrlDecode(string segment)
    {
        string padded = segment.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        return Convert.FromBase64String(padded);
    }

    private static string ErrorSentence(string full, Exception exception)
    {
        string? line = full.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Contains("AADSTS", StringComparison.Ordinal));
        if (line is null)
        {
            line = exception.Message.Split('\n')[0].Trim();
        }

        int trace = line.IndexOf(" Trace ID", StringComparison.Ordinal);
        if (trace > 0)
        {
            line = line[..trace];
        }

        var builder = new StringBuilder(line.Length);
        foreach (char c in line)
        {
            builder.Append(char.IsControl(c) ? ' ' : c);
        }

        string text = builder.ToString();
        return text.Length > 400 ? text[..400] + "..." : text;
    }
}
