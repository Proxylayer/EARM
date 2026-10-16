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

        if (ConsentRequiredException.FindIn(failure) is { } consent)
        {
            return ConsentNeeded(consent);
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

    /// <summary>The one-line note on shared consent: a grant to the sign-in app applies to every tool that signs in with that app.</summary>
    public const string SharedConsentNote = "Note: consent is granted to the sign-in app (Microsoft Graph Command Line Tools by default) and is shared by every tool that signs in with that app, such as Microsoft Graph PowerShell; review or revoke it under My Apps (https://myapps.microsoft.com) or Enterprise applications.";

    /// <summary>
    /// The message for a sign-in that is valid but lacks consent for some delegated scopes: the silent request was refused (AADSTS65001), or a token was
    /// issued without them in <c>scp</c> (<see cref="ConsentGap.ScopesMissingFromToken"/>, worded differently). Which scopes of which resource, and the exact
    /// command that consents them (<c>earm auth login --scopes ...</c>, or <c>earm auth login -f &lt;template&gt;</c> for everything a template needs).
    /// </summary>
    public static string ConsentNeeded(ConsentRequiredException consent)
    {
        ArgumentNullException.ThrowIfNull(consent);
        var builder = new StringBuilder();
        builder.Append("Consent needed: your sign-in");
        if (!string.IsNullOrWhiteSpace(consent.Account))
        {
            builder.Append(" (").Append(SecretRedactor.RedactText(consent.Account)).Append(')');
        }

        // Graph scopes are listed by name (as --scopes and a token's scp take them); a Key Vault scope fully qualified.
        var listed = IsKeyVault(consent.Scopes.Resource) ? consent.Scopes.Scopes : consent.Scopes.Names;
        if (consent.Gap == ConsentGap.ScopesMissingFromToken)
        {
            // A token WAS issued (no AADSTS65001): its scp lacks the scopes, so the sign-in app has no grant for them (never given, or revoked since).
            builder.Append(" got an access token for ").Append(consent.Scopes.Resource).Append(" whose scp does not include ").Append(string.Join(' ', listed))
                .AppendLine(": those delegated permissions are not (or no longer) granted to the sign-in app, so EARM stopped before reading or writing anything.");
        }
        else
        {
            builder.Append(" has not been granted ").Append(string.Join(' ', listed)).Append(" for ").Append(consent.Scopes.Resource)
                .AppendLine(" (AADSTS65001), so no access token could be obtained without a consent prompt.");
        }

        builder.AppendLine("Grant it once, interactively, with:");
        builder.Append("  earm auth login").Append(consent.Scopes.IsDefault ? string.Empty : " --scopes " + string.Join(' ', listed)).Append(ClientArgument(consent.ClientId)).AppendLine();
        builder.Append("or consent everything a template needs: earm auth login -f <template> [-p ...] [--verb create|whatif|delete]").Append(ClientArgument(consent.ClientId)).AppendLine();
        builder.AppendLine("If your organization requires admin consent, ask an administrator to grant these delegated permissions to the sign-in app.");
        builder.Append(SharedConsentNote);
        return builder.ToString();
    }

    /// <summary><c> --client-id X</c> for a sign-in to a custom app (so the consent goes to the same app); empty for the known Microsoft apps (a new login switches to the default).</summary>
    private static string ClientArgument(string? clientId)
    {
        return string.IsNullOrWhiteSpace(clientId) || PublicClients.NameOf(clientId) is not null ? string.Empty : " --client-id " + clientId;
    }

    private static bool IsKeyVault(string resource)
    {
        foreach (var cloud in GraphCloud.All)
        {
            if (string.Equals(resource, cloud.KeyVaultResource, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
