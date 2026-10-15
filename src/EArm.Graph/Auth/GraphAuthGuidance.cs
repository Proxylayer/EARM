using System;
using System.Collections.Generic;
using System.Text;
using System.Text.RegularExpressions;

namespace EArm.Graph.Auth;

/// <summary>The actionable text shown when no credential produced a token. Credential messages are redacted before they are printed.</summary>
public static partial class GraphAuthGuidance
{
    /// <summary>The most credential lines kept from one failure.</summary>
    private const int MaxLines = 12;

    /// <summary>The "not signed in" message: which credentials were tried and how to sign in.</summary>
    public static string NotSignedIn(GraphAuthMode mode, string scope, Exception? failure)
    {
        if (failure is InteractiveSignInExpiredException expired)
        {
            return InteractiveExpired(expired, scope);
        }

        var builder = new StringBuilder();
        builder.Append("Not signed in: no credential produced an access token for ").Append(scope).Append('.');

        var tried = failure is null ? [] : CredentialLines(failure);
        if (tried.Count > 0)
        {
            builder.AppendLine().AppendLine("Credentials tried:");
            foreach (var line in tried)
            {
                builder.Append("  - ").AppendLine(line);
            }
        }

        builder.AppendLine();
        if (mode == GraphAuthMode.Interactive)
        {
            builder.Append("Run 'earm auth login' to sign in (add --device-code on a machine without a browser).");
            return builder.ToString();
        }

        builder.AppendLine("Sign in with one of:");
        builder.AppendLine("  - a service principal: set AZURE_TENANT_ID, AZURE_CLIENT_ID and AZURE_CLIENT_SECRET (or AZURE_CLIENT_CERTIFICATE_PATH for a certificate);");
        builder.AppendLine("  - workload identity: set AZURE_TENANT_ID, AZURE_CLIENT_ID and AZURE_FEDERATED_TOKEN_FILE;");
        builder.AppendLine("  - a managed identity: run EARM on an Azure resource that has one (set AZURE_CLIENT_ID for a user-assigned identity);");
        builder.Append("  - your own account: run 'earm auth login' (add --device-code on a machine without a browser).");
        return builder.ToString();
    }

    /// <summary>
    /// The message for a stored interactive sign-in that can no longer produce a token. EARM does NOT continue with another identity (the default
    /// chain could be a different principal with different permissions), so the text tells the user how to choose deliberately.
    /// </summary>
    private static string InteractiveExpired(InteractiveSignInExpiredException expired, string scope)
    {
        var builder = new StringBuilder();
        builder.Append("Not signed in: your interactive sign-in");
        if (!string.IsNullOrWhiteSpace(expired.Account))
        {
            builder.Append(" (").Append(SecretRedactor.RedactText(expired.Account)).Append(')');
        }

        builder.Append(" expired or can no longer be refreshed, so no access token for ").Append(scope).AppendLine(" could be obtained.");
        var detail = expired.InnerException is null ? [] : CredentialLines(expired.InnerException);
        if (detail.Count > 0)
        {
            builder.Append("Detail: ").AppendLine(detail[0]);
        }

        builder.AppendLine("EARM does not fall back to another identity on its own. Either:");
        builder.AppendLine("  - run 'earm auth login' to sign in again (add --device-code on a machine without a browser); or");
        builder.Append("  - pass '--auth default' (or set EARM_AUTH=default) to use the service principal, workload identity or managed identity of this environment instead.");
        return builder.ToString();
    }

    /// <summary>The per-credential lines of an Azure.Identity failure ("EnvironmentCredential authentication unavailable. ..."), redacted and shortened.</summary>
    public static IReadOnlyList<string> CredentialLines(Exception failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var lines = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (Exception? current = failure; current is not null && lines.Count < MaxLines; current = current.InnerException)
        {
            var messageLines = current.Message.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            for (var i = 0; i < messageLines.Length && lines.Count < MaxLines; i++)
            {
                var raw = messageLines[i];
                if (!raw.StartsWith("- ", StringComparison.Ordinal) && !CredentialNamePattern().IsMatch(raw))
                {
                    continue;
                }

                // "ClientSecretCredential authentication failed:" is followed by the identity provider's reason ("AADSTS7000215: ..."): keep it.
                if (raw.EndsWith(':') && i + 1 < messageLines.Length)
                {
                    raw += " " + messageLines[i + 1];
                }

                var line = Clean(raw.StartsWith("- ", StringComparison.Ordinal) ? raw[2..] : raw);
                if (line.Length > 0 && seen.Add(line))
                {
                    lines.Add(line);
                }
            }
        }

        // "ClientSecretCredential authentication failed:" has its reason in the inner exception ("AADSTS90002: Tenant '...' not found."): attach it.
        if (lines.Count > 0 && lines[^1].EndsWith(':') && failure.InnerException is { } reason)
        {
            var text = reason.Message.Split('\n', 2, StringSplitOptions.TrimEntries)[0];
            var cut = text.IndexOf(" Trace ID", StringComparison.Ordinal);
            var detail = Clean(cut < 0 ? text : text[..cut]);
            if (detail.Length > 0)
            {
                lines[^1] += " " + detail;
            }
        }

        if (lines.Count == 0 && failure.Message.Length > 0)
        {
            var first = failure.Message.Split('\n', 2, StringSplitOptions.TrimEntries)[0];
            var cleaned = Clean(first);
            if (cleaned.Length > 0)
            {
                lines.Add(cleaned);
            }
        }

        return lines;
    }

    private static string Clean(string line)
    {
        var text = SecretRedactor.RedactText(line.Trim());
        text = UrlPattern().Replace(text, string.Empty);
        text = text.Replace("See the troubleshooting guide for more information.", string.Empty, StringComparison.Ordinal);
        text = WhitespacePattern().Replace(text, " ").Trim();
        return text.Length <= 240 ? text : text[..240] + "...";
    }

    [GeneratedRegex(@"^(?!DefaultAzureCredential\b)[A-Za-z]+Credential\b")]
    private static partial Regex CredentialNamePattern();

    [GeneratedRegex(@"https?://\S+")]
    private static partial Regex UrlPattern();

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespacePattern();
}
