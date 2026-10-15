using System;
using System.Text.RegularExpressions;

namespace EArm.Az;

/// <summary>Classification of an Azure CLI failure that did not carry a Microsoft Graph error body.</summary>
public enum AzCliFailure
{
    /// <summary>az could not be found (exit 127 from the host script, or an "az not recognized" message).</summary>
    NotInstalled,

    /// <summary>No Azure CLI session; the user must run <c>az login --allow-no-subscriptions</c>.</summary>
    NotLoggedIn,

    /// <summary>Network / timeout style failure. Retried (3 attempts total).</summary>
    Transient,

    /// <summary>az exited non-zero for an unrecognised reason. Also retried (3 attempts) per the retry table.</summary>
    Unknown,
}

/// <summary>
/// The Azure CLI (or the pwsh host running it) failed without a Graph error body, or produced unusable output.
/// Graph-level errors are <see cref="GraphException"/> instead.
/// </summary>
public sealed partial class AzCliException : Exception
{
    /// <summary>The exact remedy printed for <see cref="AzCliFailure.NotLoggedIn"/>.</summary>
    public const string LoginHint = "Run: az login --allow-no-subscriptions";

    public AzCliException(AzCliFailure failure, string message, int exitCode = -1, string? standardError = null, bool timedOut = false, Exception? innerException = null)
        : base(message, innerException)
    {
        Failure = failure;
        ExitCode = exitCode;
        StandardError = SecretRedactor.RedactText(standardError ?? string.Empty);
        TimedOut = timedOut;
    }

    public AzCliFailure Failure { get; }

    /// <summary>Exit code of the pwsh host (which forwards az's exit code); -1 when the process was killed.</summary>
    public int ExitCode { get; }

    /// <summary>Captured stderr, with secret-looking values masked.</summary>
    public string StandardError { get; }

    public bool TimedOut { get; }

    /// <summary>True for <see cref="AzCliFailure.Transient"/> and <see cref="AzCliFailure.Unknown"/>.</summary>
    public bool IsRetryable => Failure is AzCliFailure.Transient or AzCliFailure.Unknown;

    /// <summary>Builds the exception for a failed <see cref="AzResult"/>, classifying the failure from stderr.</summary>
    public static AzCliException FromResult(AzResult result, string operation)
    {
        ArgumentNullException.ThrowIfNull(result);
        var failure = Classify(result);
        var detail = FirstLine(result.StandardError);
        var message = failure switch
        {
            AzCliFailure.NotInstalled => $"{operation}: the Azure CLI ('az') was not found. Install it (winget install Microsoft.AzureCLI, https://aka.ms/installazurecli) or pass --az-command <path>. {detail}".TrimEnd(),
            AzCliFailure.NotLoggedIn => $"{operation}: not logged in to the Azure CLI. {LoginHint}. {detail}".TrimEnd(),
            AzCliFailure.Transient when result.TimedOut => $"{operation}: az did not finish within the timeout ({result.Elapsed.TotalSeconds:F0} s) and was killed.",
            AzCliFailure.Transient => $"{operation}: transient Azure CLI failure (exit {result.ExitCode}). {detail}".TrimEnd(),
            _ => $"{operation}: Azure CLI failed (exit {result.ExitCode}). {detail}".TrimEnd(),
        };

        return new AzCliException(failure, message, result.ExitCode, result.StandardError, result.TimedOut);
    }

    /// <summary>Pure classification of a failed az result (exposed for tests).</summary>
    public static AzCliFailure Classify(AzResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        if (result.TimedOut)
        {
            return AzCliFailure.Transient;
        }

        var text = result.StandardError + "\n" + result.StandardOutput;
        if (result.ExitCode == 127 || NotInstalledPattern().IsMatch(text))
        {
            return AzCliFailure.NotInstalled;
        }

        if (NotLoggedInPattern().IsMatch(text))
        {
            return AzCliFailure.NotLoggedIn;
        }

        if (TransientPattern().IsMatch(text))
        {
            return AzCliFailure.Transient;
        }

        return AzCliFailure.Unknown;
    }

    private static string FirstLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var trimmed = text.Trim();
        var newline = trimmed.IndexOfAny(['\r', '\n']);
        var line = newline < 0 ? trimmed : trimmed[..newline];
        return SecretRedactor.RedactText(line);
    }

    [GeneratedRegex(@"EArm: Azure CLI (?:\('az'\)|command '[^']*') .* not found|'az' is not recognized|az: (?:command )?not found|No such file or directory.*\baz\b", RegexOptions.IgnoreCase)]
    private static partial Regex NotInstalledPattern();

    [GeneratedRegex(@"az login|Please run ['""]?az login|Interactive authentication is needed|No subscriptions? (?:were )?found|AADSTS(?:700082|70043|50173|50076|50078|50079|65001)|Credentials have expired|not logged in", RegexOptions.IgnoreCase)]
    private static partial Regex NotLoggedInPattern();

    [GeneratedRegex(@"Connection (?:aborted|reset|refused|broken)|Max retries exceeded|timed? ?out|Temporary failure in name resolution|getaddrinfo failed|Name or service not known|SSLError|SSL: |RemoteDisconnected|ChunkedEncodingError|ConnectionError|NewConnectionError|HTTPSConnectionPool|EOF occurred|Read timed out|ProtocolError|Service Unavailable|Bad Gateway|Gateway Time-?out|Internal Server Error", RegexOptions.IgnoreCase)]
    private static partial Regex TransientPattern();
}
