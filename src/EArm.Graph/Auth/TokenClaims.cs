using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace EArm.Graph.Auth;

/// <summary>
/// The display-only claims of an access token: tenant, object id, user principal name, app id, delegated scopes, app roles.
/// The payload is decoded WITHOUT validating the signature, issuer or audience, so nothing here may drive a security decision;
/// it only tells the user (and the planner) who the token says it is. The token itself is never kept or printed.
/// </summary>
public sealed record TokenClaims
{
    /// <summary>Tokens longer than this are not decoded (<see cref="TryDecode"/> returns false).</summary>
    public const int MaxTokenLength = 65_536;

    public string? TenantId { get; init; }

    public string? ObjectId { get; init; }

    public string? UserPrincipalName { get; init; }

    public string? Name { get; init; }

    public string? AppId { get; init; }

    public string? Audience { get; init; }

    /// <summary>Delegated permissions (<c>scp</c>), space-separated in the token.</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>Application permissions / directory roles (<c>roles</c>).</summary>
    public IReadOnlyList<string> Roles { get; init; } = [];

    /// <summary>True for an app-only token (<c>idtyp=app</c>, or roles without scopes and without a user name).</summary>
    public bool IsApplication { get; init; }

    public DateTimeOffset? ExpiresOn { get; init; }

    /// <summary>The user principal name, or the app id for an app-only token, or the object id.</summary>
    public string? PrincipalName => UserPrincipalName ?? (IsApplication ? AppId : null) ?? ObjectId;

    public string PrincipalType => IsApplication ? "application" : "user";

    /// <summary>Decodes a JWT payload; returns false for anything that is not a three-part token with a JSON object payload.</summary>
    public static bool TryDecode(string? jwt, out TokenClaims claims)
    {
        claims = new TokenClaims();
        if (string.IsNullOrWhiteSpace(jwt))
        {
            return false;
        }

        // Real access tokens are a few KB; refuse anything absurd before splitting and decoding it.
        if (jwt.Length > MaxTokenLength)
        {
            return false;
        }

        var parts = jwt.Split('.');
        if (parts.Length != 3 || parts[1].Length == 0)
        {
            return false;
        }

        byte[] payload;
        try
        {
            payload = DecodeBase64Url(parts[1]);
        }
        catch (FormatException)
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(payload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var roles = ReadStringArray(root, "roles");
            var scopes = ReadString(root, "scp") is { } scp ? scp.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
            var upn = ReadString(root, "upn") ?? ReadString(root, "preferred_username") ?? ReadString(root, "unique_name");
            var idtyp = ReadString(root, "idtyp");
            var isApplication = string.Equals(idtyp, "app", StringComparison.OrdinalIgnoreCase)
                || (idtyp is null && upn is null && scopes.Length == 0 && roles.Count > 0);

            DateTimeOffset? expires = null;
            if (root.TryGetProperty("exp", out var exp) && exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var seconds))
            {
                expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
            }

            claims = new TokenClaims
            {
                TenantId = ReadString(root, "tid"),
                ObjectId = ReadString(root, "oid"),
                UserPrincipalName = upn,
                Name = ReadString(root, "name"),
                AppId = ReadString(root, "appid") ?? ReadString(root, "azp"),
                Audience = ReadString(root, "aud"),
                Scopes = scopes,
                Roles = roles,
                IsApplication = isApplication,
                ExpiresOn = expires,
            };
            return true;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static string? ReadString(JsonElement root, string name)
    {
        return root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    }

    private static List<string> ReadStringArray(JsonElement root, string name)
    {
        var list = new List<string>();
        if (root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String && item.GetString() is { } text)
                {
                    list.Add(text);
                }
            }
        }

        return list;
    }

    private static byte[] DecodeBase64Url(string text)
    {
        var builder = new StringBuilder(text.Length + 3);
        foreach (var c in text)
        {
            builder.Append(c switch
            {
                '-' => '+',
                '_' => '/',
                _ => c,
            });
        }

        while (builder.Length % 4 != 0)
        {
            builder.Append('=');
        }

        return Convert.FromBase64String(builder.ToString());
    }

    internal static string FormatExpiry(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString("u", CultureInfo.InvariantCulture);
    }
}
