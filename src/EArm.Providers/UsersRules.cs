using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>
/// The pure rules of <c>Microsoft.Entra/users</c>: the userPrincipalName shape, the
/// <c>mailNickname</c> derivation, the create-time defaults, the <c>passwordProfile</c> handling (taint, presence,
/// generation) and the body clean-ups. No Graph access, so every rule can be unit-tested directly.
/// </summary>
internal static class UsersRules
{
    public const string UserPrincipalName = "userPrincipalName";
    public const string MailNickname = "mailNickname";
    public const string PasswordProfile = "passwordProfile";
    public const string Password = "password";
    public const string ForceChangePasswordNextSignIn = "forceChangePasswordNextSignIn";
    public const string UserType = "userType";

    /// <summary>The length of a generated initial password.</summary>
    public const int GeneratedPasswordLength = 24;

    // Visually unambiguous alphabets (no 0/O, 1/l/I) so a password read from a secure-outputs file is typed correctly.
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Symbols = "!@#$%^&*-_+=?";

    /// <summary>Why <paramref name="name"/> is not a userPrincipalName (<c>local@domain</c>, exactly one <c>@</c>, no whitespace); <c>null</c> when fine.</summary>
    public static string? ValidateUserPrincipalName(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        int at = name.IndexOf('@');
        if (at <= 0 || at != name.LastIndexOf('@') || at == name.Length - 1)
        {
            return $"'{name}' is not a userPrincipalName of the form local@domain (exactly one '@' with a non-empty local part and domain).";
        }

        foreach (char c in name)
        {
            if (char.IsWhiteSpace(c))
            {
                return $"'{name}' is not a valid userPrincipalName: it contains whitespace.";
            }
        }

        return null;
    }

    /// <summary>The part of a UPN before the <c>@</c> (for a guest, <c>alice_gmail.com#EXT#</c>).</summary>
    public static string LocalPart(string userPrincipalName)
    {
        ArgumentNullException.ThrowIfNull(userPrincipalName);
        int at = userPrincipalName.IndexOf('@');
        return at < 0 ? userPrincipalName : userPrincipalName[..at];
    }

    /// <summary>
    /// <c>mailNickname</c> from the UPN local part, sanitised like a group's (<see cref="GroupsShape.DeriveMailNickname"/>:
    /// lower-case, <c>[^a-z0-9-_]</c> → <c>-</c>, runs collapsed, at most 64 characters) with leading/trailing dashes
    /// trimmed (a guest's <c>#EXT#</c> suffix would otherwise leave one); <c>user</c> when nothing remains.
    /// </summary>
    public static string DeriveMailNickname(string userPrincipalName)
    {
        string nickname = GroupsShape.DeriveMailNickname(LocalPart(userPrincipalName)).Trim('-');
        return nickname.Length == 0 ? "user" : nickname;
    }

    /// <summary>
    /// Replaces every plain leaf at the overlay's <c>secure</c> paths (<c>passwordProfile.password</c>) by a tainted
    /// <see cref="SecretValue"/>, in place, so a literal password in a template renders as <c>***</c> in what-if, logs,
    /// and diagnostics exactly like one bound from a <c>securestring</c> parameter. Leaves that are
    /// already secret, deploy-time-unknown or <c>null</c> are left alone.
    /// </summary>
    public static void TaintSecrets(JsonObject properties, IReadOnlyList<JsonPath> securePaths)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(securePaths);
        foreach (var path in securePaths)
        {
            if (path.IsRoot)
            {
                continue;
            }

            JsonObject? current = properties;
            var segments = path.Segments;
            for (int i = 0; i < segments.Count - 1 && current is not null; i++)
            {
                if (segments[i].IsIndex || !JsonObjects.TryGetIgnoreCase(current, segments[i].Name!, out var child))
                {
                    current = null;
                    break;
                }

                current = child as JsonObject;
            }

            var leafSegment = segments[^1];
            if (current is null || leafSegment.IsIndex || JsonObjects.FindKey(current, leafSegment.Name!) is not { } key)
            {
                continue;
            }

            if (current[key] is not JsonValue value || SecretJson.IsSecret(value) || UnknownJson.IsUnknown(value) || value.GetValueKind() == JsonValueKind.Null)
            {
                continue;
            }

            current[key] = SecretJson.Create(SecretValue.FromLeaf(value)!);
        }
    }

    /// <summary>True when <c>passwordProfile.password</c> is declared (a secret, a deploy-time value, or a non-empty string).</summary>
    public static bool HasPassword(JsonObject? properties)
    {
        if (!JsonObjects.TryGetDotted(properties, PasswordProfile + "." + Password, out var node) || node is not JsonValue value)
        {
            return false;
        }

        if (SecretJson.IsSecret(value) || UnknownJson.IsUnknown(value))
        {
            return true;
        }

        return value.GetValueKind() == JsonValueKind.String && !string.IsNullOrEmpty(value.GetValue<string>());
    }

    /// <summary>
    /// A random initial password: <see cref="GeneratedPasswordLength"/> characters from a CSPRNG
    /// with at least one upper-case letter, one lower-case letter, one digit and one symbol, shuffled so the
    /// guaranteed characters sit at random positions.
    /// </summary>
    public static string GeneratePassword()
    {
        Span<char> chars = stackalloc char[GeneratedPasswordLength];
        chars[0] = Upper[RandomNumberGenerator.GetInt32(Upper.Length)];
        chars[1] = Lower[RandomNumberGenerator.GetInt32(Lower.Length)];
        chars[2] = Digits[RandomNumberGenerator.GetInt32(Digits.Length)];
        chars[3] = Symbols[RandomNumberGenerator.GetInt32(Symbols.Length)];
        RandomNumberGenerator.GetItems<char>(Upper + Lower + Digits + Symbols, chars[4..]);
        RandomNumberGenerator.Shuffle(chars);
        return new string(chars);
    }

    /// <summary>Mirrors <see cref="ProviderBase.InjectName"/> at plan time: a declared <c>userPrincipalName</c> must equal the resource name.</summary>
    public static string? DeclaredNameConflict(JsonObject properties, string name, string describe)
    {
        ArgumentNullException.ThrowIfNull(properties);
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(describe);
        if (!JsonObjects.TryGetIgnoreCase(properties, UserPrincipalName, out var declared) || JsonObjects.IsAbsent(declared) || SecretJson.IsSecret(declared) || UnknownJson.IsUnknown(declared))
        {
            return null;
        }

        if (JsonValues.TryGetString(declared, out var text) && string.Equals(text, name, StringComparison.Ordinal))
        {
            return null;
        }

        return $"{describe}: properties.userPrincipalName ({JsonValues.Render(declared)}) conflicts with the resource name '{name}'; the name is written to userPrincipalName, so either omit the property or make it equal ({DiagnosticCodes.NameMappedPropertyConflict}).";
    }

    /// <summary>
    /// Fills the create-time defaults the template left out: <c>mailNickname</c> derived from the UPN and
    /// <c>passwordProfile.forceChangePasswordNextSignIn: true</c> (the overlay default, repeated here so a plan that
    /// runs before <see cref="ProviderBase.BuildCreateBody"/> shows the same object). <c>accountEnabled: true</c> is
    /// applied separately (<see cref="ApplyCreatedStateDefaults"/>) because a symbolic create shows it too.
    /// </summary>
    public static void ApplyCreateDefaults(JsonObject target, string userPrincipalName)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(userPrincipalName);
        if (!JsonObjects.TryGetIgnoreCase(target, MailNickname, out var nickname) || JsonObjects.IsAbsent(nickname))
        {
            target[JsonObjects.FindKey(target, MailNickname) ?? MailNickname] = DeriveMailNickname(userPrincipalName);
        }

        JsonObjects.SetDefault(target, PasswordProfile + "." + ForceChangePasswordNextSignIn, JsonValue.Create(true));
    }

    /// <summary>
    /// The state a user EARM creates starts in (<c>accountEnabled: true</c>). Create only: an existing user found by UPN
    /// never receives it, so an undeclared <c>accountEnabled</c> is left as it is live.
    /// </summary>
    public static void ApplyCreatedStateDefaults(JsonObject target)
    {
        ArgumentNullException.ThrowIfNull(target);
        JsonObjects.SetDefault(target, "accountEnabled", JsonValue.Create(true));
    }

    /// <summary><c>id</c> is assigned by Graph and rejected in a create body.</summary>
    public static void RemoveServerAssigned(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        RemoveKey(body, "id");
    }

    /// <summary>
    /// What never goes into a <c>PATCH /users/{id}</c>: <c>id</c>, <c>userPrincipalName</c> (immutable here: a
    /// differing live UPN is rejected at plan time) and <c>passwordProfile</c> (create-only; a password reset is a
    /// separate permission and out of scope for v1).
    /// </summary>
    public static void RemoveNeverPatched(JsonObject body)
    {
        ArgumentNullException.ThrowIfNull(body);
        RemoveKey(body, "id");
        RemoveKey(body, UserPrincipalName);
        RemoveKey(body, PasswordProfile);
    }

    /// <summary>
    /// Adds the properties just written to the object Graph returned for the create (its default property set),
    /// except <c>passwordProfile</c>, so <c>reference()</c> sees every declared property without a second read.
    /// Properties the response already carries win.
    /// </summary>
    public static void MergeWritten(JsonObject live, JsonObject written)
    {
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(written);
        foreach (var property in written)
        {
            if (property.Key.Equals(PasswordProfile, StringComparison.OrdinalIgnoreCase) || JsonObjects.FindKey(live, property.Key) is not null)
            {
                continue;
            }

            live[property.Key] = JsonValues.Clone(property.Value);
        }
    }

    /// <summary>The what-if reason for a <c>userType</c> change: Graph allows it, but it is rarely intended.</summary>
    public static string UserTypeChangeReason(JsonNode? before, JsonNode? after)
    {
        return $"userType changes from {JsonValues.Render(before)} to {JsonValues.Render(after)}; Graph allows the change, but it alters the user's licensing, guest restrictions and default permissions";
    }

    private static void RemoveKey(JsonObject body, string property)
    {
        if (JsonObjects.FindKey(body, property) is { } key)
        {
            body.Remove(key);
        }
    }
}
