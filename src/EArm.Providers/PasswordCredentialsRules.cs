using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using EArm.Graph;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Expressions.Values;

namespace EArm.Providers;

/// <summary>A live password credential as Graph lists it under <c>application.passwordCredentials</c> (never the secret).</summary>
public sealed record PasswordCredentialSummary(string KeyId, string? DisplayName, DateTimeOffset? StartDateTime, DateTimeOffset? EndDateTime, string? Hint);

/// <summary>What the template asks of a credential, parsed from the evaluated properties.</summary>
public sealed record DesiredPasswordCredential(DateTimeOffset? EndDateTime, DateTimeOffset? StartDateTime, string? RotationTag, int RotateBeforeDays);

/// <summary>The outcome of <see cref="PasswordCredentialsRules.AssessRotation"/>: why the credential is replaced, and what the user should know (warning code + text).</summary>
public sealed record RotationAssessment(IReadOnlyList<RotationTrigger> Triggers, IReadOnlyList<ProviderWarning> Warnings);

/// <summary>One reason a credential is rotated: the property that differs, both sides (rendering values) and the text for the what-if row.</summary>
public sealed record RotationTrigger(string Property, JsonNode? Before, JsonNode? After, string Reason);

/// <summary>
/// The pure rules of the password credentials provider, free of
/// Graph I/O so they can be tested on their own: how a credential is matched among the application's
/// <c>passwordCredentials</c>, what the template may declare, when the secret is rotated (<c>rotationTag</c> or
/// <c>endDateTime</c>/<c>startDateTime</c> changed, expiry within <c>rotateBeforeDays</c>, expired), the
/// <c>addPassword</c> / <c>removePassword</c> bodies, and the shapes of the live view and the diff view.
/// <c>rotationTag</c> and <c>rotateBeforeDays</c> are EARM-only properties: Graph has no field for them, so the tag
/// travels in the credential's <c>displayName</c> (<c>&lt;name&gt;#&lt;rotationTag&gt;</c>, see <see cref="PasswordCredentialName"/>)
/// and a rerun compares the declared tag with the live one: no local state is involved.
/// </summary>
public static class PasswordCredentialsRules
{
    /// <summary>A credential created without <c>endDateTime</c> expires this many days after its creation.</summary>
    public const int DefaultLifetimeDays = 180;

    /// <summary>Without <c>rotateBeforeDays</c>, a credential is rotated when fewer than this many days remain.</summary>
    public const int DefaultRotateBeforeDays = 30;

    public const string DisplayNameProperty = "displayName";

    public const string EndDateTimeProperty = "endDateTime";

    public const string StartDateTimeProperty = "startDateTime";

    public const string RotationTagProperty = "rotationTag";

    public const string RotateBeforeDaysProperty = "rotateBeforeDays";

    public const string KeyIdProperty = "keyId";

    public const string HintProperty = "hint";

    /// <summary>The navigation property of the application that lists the credentials.</summary>
    public const string PasswordCredentialsProperty = "passwordCredentials";

    /// <summary>What the parent read selects.</summary>
    public static readonly string[] ParentSelect = ["id", PasswordCredentialsProperty];

    private const string DateTimeFormat = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    private static readonly TimeSpan SameInstantTolerance = TimeSpan.FromSeconds(1);

    // ---- dates ----

    /// <summary>The canonical form written to Graph and rendered in what-if: UTC, second precision, <c>Z</c>.</summary>
    public static string Format(DateTimeOffset value)
    {
        return value.ToUniversalTime().ToString(DateTimeFormat, CultureInfo.InvariantCulture);
    }

    /// <summary>Parses an ISO 8601 date-time leaf (a date without offset is taken as UTC); secrets and deploy-time values never parse.</summary>
    public static bool TryParseDateTime(JsonNode? node, out DateTimeOffset value)
    {
        value = default;
        if (SecretJson.IsSecret(node) || UnknownJson.IsUnknown(node) || !JsonValues.TryGetString(node, out var text))
        {
            return false;
        }

        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out value);
    }

    /// <summary>The expiry of a credential created at <paramref name="now"/> without a declared <c>endDateTime</c>.</summary>
    public static DateTimeOffset DefaultEndDateTime(DateTimeOffset now)
    {
        return now.AddDays(DefaultLifetimeDays);
    }

    /// <summary>Instants within one second are the same (Graph keeps seconds; the template may carry an offset).</summary>
    public static bool SameInstant(DateTimeOffset? left, DateTimeOffset? right)
    {
        if (left is null || right is null)
        {
            return left is null && right is null;
        }

        return (left.Value - right.Value).Duration() <= SameInstantTolerance;
    }

    // ---- live credentials ----

    /// <summary>Reduces the application's <c>passwordCredentials</c> array to summaries; entries without <c>keyId</c> are skipped.</summary>
    public static IReadOnlyList<PasswordCredentialSummary> Summarize(JsonNode? passwordCredentials)
    {
        var summaries = new List<PasswordCredentialSummary>();
        if (passwordCredentials is not JsonArray array)
        {
            return summaries;
        }

        foreach (var item in array)
        {
            if (item is JsonObject credential && SummaryOf(credential) is { } summary)
            {
                summaries.Add(summary);
            }
        }

        return summaries;
    }

    /// <summary>One credential object (a list entry, an <c>addPassword</c> response or a published live view) as a summary; <c>null</c> without <c>keyId</c>.</summary>
    public static PasswordCredentialSummary? SummaryOf(JsonObject credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        string? keyId = JsonObjects.GetString(credential, KeyIdProperty) ?? JsonObjects.GetId(credential);
        if (keyId is null)
        {
            return null;
        }

        JsonObjects.TryGetIgnoreCase(credential, StartDateTimeProperty, out var start);
        JsonObjects.TryGetIgnoreCase(credential, EndDateTimeProperty, out var end);
        return new PasswordCredentialSummary(
            keyId,
            JsonObjects.GetString(credential, DisplayNameProperty),
            TryParseDateTime(start, out var startValue) ? startValue : null,
            TryParseDateTime(end, out var endValue) ? endValue : null,
            JsonObjects.GetString(credential, HintProperty));
    }

    /// <summary>
    /// The credentials whose live <c>displayName</c> has the base name <paramref name="baseName"/>: exactly <c>baseName</c>, or
    /// <c>baseName#&lt;tag&gt;</c> (the tag being the text after the last <c>#</c>). The ARM name is the key; it is compared case-sensitively.
    /// </summary>
    public static IReadOnlyList<PasswordCredentialSummary> SameName(IEnumerable<PasswordCredentialSummary> credentials, string baseName)
    {
        ArgumentNullException.ThrowIfNull(credentials);
        ArgumentNullException.ThrowIfNull(baseName);
        var matches = new List<PasswordCredentialSummary>();
        foreach (var credential in credentials)
        {
            if (credential.DisplayName is { } displayName && string.Equals(PasswordCredentialName.Parse(displayName).Base, baseName, StringComparison.Ordinal))
            {
                matches.Add(credential);
            }
        }

        return matches;
    }

    /// <summary>The rotation tag carried by a live credential's <c>displayName</c> (after its last <c>#</c>); <c>null</c> without one.</summary>
    public static string? LiveTag(PasswordCredentialSummary credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return credential.DisplayName is { } displayName ? PasswordCredentialName.Parse(displayName).Tag : null;
    }

    /// <summary>
    /// The credential the resource stands for among the same-name ones: the newest (latest expiry) whose live tag equals
    /// <paramref name="preferredTag"/>, else the newest of all.
    /// </summary>
    public static PasswordCredentialSummary? Select(IEnumerable<PasswordCredentialSummary> sameName, string? preferredTag = null)
    {
        ArgumentNullException.ThrowIfNull(sameName);
        PasswordCredentialSummary? newest = null;
        PasswordCredentialSummary? newestTagged = null;
        foreach (var credential in sameName)
        {
            if (newest is null || Later(credential.EndDateTime, newest.EndDateTime))
            {
                newest = credential;
            }

            if (preferredTag is not null && string.Equals(LiveTag(credential), preferredTag, StringComparison.Ordinal)
                && (newestTagged is null || Later(credential.EndDateTime, newestTagged.EndDateTime)))
            {
                newestTagged = credential;
            }
        }

        return newestTagged ?? newest;
    }

    /// <summary>Key ids are GUIDs: compared case-insensitively.</summary>
    public static bool KeyIdEquals(string? left, string? right)
    {
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
    }

    // ---- desired ----

    /// <summary>
    /// Parses the declared properties. Returns why they are not acceptable (a date that does not parse, a tag that is
    /// not a scalar, a negative <c>rotateBeforeDays</c>), or <c>null</c> with <paramref name="desired"/> set.
    /// Deploy-time-unknown and secret values are rejected here; the caller handles unknowns before.
    /// </summary>
    public static string? DescribeDesired(JsonObject properties, out DesiredPasswordCredential? desired)
    {
        ArgumentNullException.ThrowIfNull(properties);
        desired = null;

        DateTimeOffset? endDateTime = null;
        if (JsonObjects.TryGetIgnoreCase(properties, EndDateTimeProperty, out var end) && !JsonObjects.IsAbsent(end))
        {
            if (!TryParseDateTime(end, out var parsed))
            {
                return $"properties.{EndDateTimeProperty} ({JsonValues.Render(end)}) is not an ISO 8601 date-time (for example 2027-10-01T00:00:00Z).";
            }

            endDateTime = parsed;
        }

        DateTimeOffset? startDateTime = null;
        if (JsonObjects.TryGetIgnoreCase(properties, StartDateTimeProperty, out var start) && !JsonObjects.IsAbsent(start))
        {
            if (!TryParseDateTime(start, out var parsed))
            {
                return $"properties.{StartDateTimeProperty} ({JsonValues.Render(start)}) is not an ISO 8601 date-time.";
            }

            startDateTime = parsed;
        }

        if (endDateTime is { } e && startDateTime is { } s && e <= s)
        {
            return $"properties.{EndDateTimeProperty} ({Format(e)}) must be later than properties.{StartDateTimeProperty} ({Format(s)}).";
        }

        string? rotationTag = null;
        if (JsonObjects.TryGetIgnoreCase(properties, RotationTagProperty, out var tag) && !JsonObjects.IsAbsent(tag))
        {
            rotationTag = ScalarText(tag);
            if (rotationTag is null)
            {
                return $"properties.{RotationTagProperty} must be a string (or number / boolean), got {JsonValues.Describe(tag)}.";
            }

            // An empty tag is no tag: the live name would end in a bare '#', which parses back as untagged.
            rotationTag = rotationTag.Length == 0 ? null : rotationTag;
        }

        int rotateBeforeDays = DefaultRotateBeforeDays;
        if (JsonObjects.TryGetIgnoreCase(properties, RotateBeforeDaysProperty, out var before) && !JsonObjects.IsAbsent(before))
        {
            if (SecretJson.IsSecret(before) || UnknownJson.IsUnknown(before) || !JsonValues.TryGetInt64(before, out long days) || days < 0 || days > int.MaxValue)
            {
                return $"properties.{RotateBeforeDaysProperty} must be a non-negative integer number of days, got {JsonValues.Describe(before)}.";
            }

            rotateBeforeDays = (int)days;
        }

        desired = new DesiredPasswordCredential(endDateTime, startDateTime, rotationTag, rotateBeforeDays);
        return null;
    }

    /// <summary>
    /// The live <c>displayName</c> a credential is written with: <c>&lt;name&gt;#&lt;rotationTag&gt;</c>, or the plain name without a tag.
    /// Throws when the pair cannot be written (see <see cref="NameProblem"/>, checked while planning).
    /// </summary>
    public static string LiveDisplayName(string baseName, string? rotationTag)
    {
        ArgumentNullException.ThrowIfNull(baseName);
        if (PasswordCredentialName.Problem(baseName, rotationTag) is { } problem)
        {
            throw new ArgumentException(problem, nameof(baseName));
        }

        return PasswordCredentialName.Declared(baseName, rotationTag).DisplayName;
    }

    /// <summary>Why the name / tag pair cannot be written as a live <c>displayName</c> (<c>#</c> in either, or too long), or <c>null</c>.</summary>
    public static string? NameProblem(string baseName, string? rotationTag)
    {
        return PasswordCredentialName.Problem(baseName, rotationTag);
    }

    // ---- rotation ----

    /// <summary>
    /// Why the live credential is replaced, or empty for <c>NoChange</c>: the <c>rotationTag</c> differs from the
    /// tag in the live <c>displayName</c> (absent ≡ absent; a live credential without a tag matches any declared tag, see
    /// <see cref="AssessRotation"/>), a declared <c>endDateTime</c> / <c>startDateTime</c> differs from the live
    /// value, the credential expired, or it expires within <c>rotateBeforeDays</c> and a replacement would live longer.
    /// A credential created without <c>endDateTime</c> keeps the expiry it was given (reruns do not drift towards "now + 180 days").
    /// See <see cref="AssessRotation"/> for the warnings.
    /// </summary>
    public static IReadOnlyList<RotationTrigger> RotationTriggers(DesiredPasswordCredential desired, PasswordCredentialSummary live, DateTimeOffset now)
    {
        return AssessRotation(desired, live, now).Triggers;
    }

    /// <summary>
    /// <see cref="RotationTriggers"/> plus the <see cref="ProviderWarnings.SecretExpiresSoon"/> text for a credential that is expired or in its
    /// rotation window but would not get a later expiry from a replacement (Review 1D / E-07): a declared fixed <c>endDateTime</c> inside the
    /// window, or a <c>rotateBeforeDays</c> longer than the default lifetime, used to rotate the secret on every run because the
    /// replacement ends no later than the credential it replaces. The credential is rotated only when
    /// <c>liveEnd &lt; now + rotateBeforeDays</c> AND the replacement's end (the declared one, else now + 180 days) is later than the live end.
    /// <para>
    /// Upgrade path: a live credential named with the plain base name (written by an EARM version that kept the tag in its local state)
    /// carries no tag. When the template declares one, the credential is treated as matching (no surprise rotation on upgrade) and
    /// <see cref="ProviderWarnings.RotationTagNotStamped"/> says the tag is written into the name by the next rotation; until then a
    /// changed <c>rotationTag</c> cannot be detected, so declaring a different <c>endDateTime</c> is the way to force that rotation.
    /// </para>
    /// </summary>
    public static RotationAssessment AssessRotation(DesiredPasswordCredential desired, PasswordCredentialSummary live, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(live);
        var triggers = new List<RotationTrigger>();
        var warnings = new List<ProviderWarning>();

        string? liveTag = LiveTag(live);
        if (liveTag is null && desired.RotationTag is not null)
        {
            string stamped = PasswordCredentialName.Declared(live.DisplayName ?? string.Empty, desired.RotationTag).DisplayName;
            warnings.Add(new ProviderWarning(
                ProviderWarnings.RotationTagNotStamped,
                $"the live credential '{live.DisplayName}' (keyId {live.KeyId}) carries no {RotationTagProperty} in its displayName (it was created before EARM kept the tag there); it is treated as matching {RotationTagProperty} {Describe(desired.RotationTag)} and NOT rotated. The next rotation names it '{stamped}'; until then a changed {RotationTagProperty} is not detected, so declare a different {EndDateTimeProperty} to rotate it now."));
        }
        else if (!string.Equals(desired.RotationTag, liveTag, StringComparison.Ordinal))
        {
            triggers.Add(new RotationTrigger(
                RotationTagProperty,
                Leaf(liveTag),
                Leaf(desired.RotationTag),
                $"{RotationTagProperty} changed ({Describe(liveTag)} → {Describe(desired.RotationTag)})"));
        }

        if (desired.EndDateTime is { } wantedEnd && !SameInstant(wantedEnd, live.EndDateTime))
        {
            triggers.Add(new RotationTrigger(
                EndDateTimeProperty,
                Leaf(live.EndDateTime),
                Leaf(wantedEnd),
                $"{EndDateTimeProperty} changed ({Describe(live.EndDateTime)} → {Format(wantedEnd)})"));
        }

        if (desired.StartDateTime is { } wantedStart && !SameInstant(wantedStart, live.StartDateTime))
        {
            triggers.Add(new RotationTrigger(
                StartDateTimeProperty,
                Leaf(live.StartDateTime),
                Leaf(wantedStart),
                $"{StartDateTimeProperty} changed ({Describe(live.StartDateTime)} → {Format(wantedStart)})"));
        }

        if (live.EndDateTime is { } liveEnd)
        {
            var replacement = desired.EndDateTime ?? DefaultEndDateTime(now);
            bool expired = liveEnd <= now;
            bool inWindow = !expired && liveEnd - now < TimeSpan.FromDays(desired.RotateBeforeDays);
            if (expired || inWindow)
            {
                bool alreadyReplacedByEndChange = triggers.Exists(t => t.Property == EndDateTimeProperty);
                string when = expired
                    ? $"the credential expired on {Format(liveEnd)}"
                    : string.Create(CultureInfo.InvariantCulture, $"the credential expires on {Format(liveEnd)}, in {(liveEnd - now).TotalDays:0.#} days, fewer than {RotateBeforeDaysProperty} ({desired.RotateBeforeDays})");
                if (replacement > liveEnd)
                {
                    if (!alreadyReplacedByEndChange)
                    {
                        triggers.Add(new RotationTrigger(EndDateTimeProperty, Leaf(liveEnd), Leaf(replacement), when));
                    }
                }
                else if (!alreadyReplacedByEndChange)
                {
                    warnings.Add(new ProviderWarning(ProviderWarnings.SecretExpiresSoon, $"{when}, but a replacement would not live longer (its end date would be {Format(replacement)}); the secret is NOT rotated. Declare a later {EndDateTimeProperty}, lower {RotateBeforeDaysProperty}, or change {RotationTagProperty} to rotate it anyway."));
                }
            }
        }

        return new RotationAssessment(triggers, warnings);
    }
    // ---- Graph bodies ----

    /// <summary><c>{ "passwordCredential": { "displayName", "endDateTime"[, "startDateTime"] } }</c>: the <c>addPassword</c> body.</summary>
    public static JsonObject AddPasswordBody(string displayName, DateTimeOffset endDateTime, DateTimeOffset? startDateTime)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var credential = new JsonObject
        {
            [DisplayNameProperty] = displayName,
            [EndDateTimeProperty] = Format(endDateTime),
        };

        if (startDateTime is { } start)
        {
            credential[StartDateTimeProperty] = Format(start);
        }

        return new JsonObject { ["passwordCredential"] = credential };
    }

    /// <summary><c>{ "keyId": "…" }</c>: the <c>removePassword</c> body.</summary>
    public static JsonObject RemovePasswordBody(string keyId)
    {
        ArgumentNullException.ThrowIfNull(keyId);
        return new JsonObject { [KeyIdProperty] = keyId };
    }

    /// <summary>
    /// A <c>removePassword</c> failure that means the credential is already gone: 404, or Graph's 400 for a key id
    /// the application does not hold ("No password credential found with keyId …").
    /// </summary>
    public static bool IsUnknownKeyId(GraphException exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        if (exception.IsNotFound)
        {
            return true;
        }

        if (exception.Status != HttpStatusCode.BadRequest)
        {
            return false;
        }

        string message = exception.GraphMessage ?? string.Empty;
        return message.Contains("keyId", StringComparison.OrdinalIgnoreCase)
            || message.Contains("No password credential", StringComparison.OrdinalIgnoreCase)
            || message.Contains("not found", StringComparison.OrdinalIgnoreCase);
    }

    // ---- views ----

    /// <summary>
    /// What <c>reference()</c> sees: the credential metadata plus <c>secretText</c> (a secret leaf in the creating run,
    /// <c>null</c> on reruns) and the <see cref="LiveSecrets.AvailabilityMarker"/>. Never contains the secret in
    /// plain text.
    /// </summary>
    public static JsonObject LiveView(PasswordCredentialSummary credential, SecretValue? secretText)
    {
        ArgumentNullException.ThrowIfNull(credential);
        return new JsonObject
        {
            ["id"] = credential.KeyId,
            [KeyIdProperty] = credential.KeyId,
            [DisplayNameProperty] = credential.DisplayName,
            [StartDateTimeProperty] = Leaf(credential.StartDateTime),
            [EndDateTimeProperty] = Leaf(credential.EndDateTime),
            [HintProperty] = credential.Hint,
            [LiveSecrets.SecretTextProperty] = secretText is null ? null : SecretJson.Create(secretText),
            [LiveSecrets.AvailabilityMarker] = secretText is not null,
        };
    }

    /// <summary>
    /// The diff / what-if view of a live credential: the live <c>displayName</c> (<c>name#tag</c>), the dates, and the <c>rotationTag</c>
    /// parsed from that name when it carries one.
    /// </summary>
    public static JsonObject DiffView(PasswordCredentialSummary credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var view = new JsonObject
        {
            [DisplayNameProperty] = credential.DisplayName,
            [StartDateTimeProperty] = Leaf(credential.StartDateTime),
            [EndDateTimeProperty] = Leaf(credential.EndDateTime),
        };

        if (LiveTag(credential) is { } tag)
        {
            view[RotationTagProperty] = tag;
        }

        return view;
    }

    // ---- helpers ----

    private static bool Later(DateTimeOffset? candidate, DateTimeOffset? current)
    {
        if (candidate is null)
        {
            return false;
        }

        return current is null || candidate.Value > current.Value;
    }

    private static string? ScalarText(JsonNode? node)
    {
        if (SecretJson.IsSecret(node) || UnknownJson.IsUnknown(node) || node is not JsonValue value)
        {
            return null;
        }

        if (JsonValues.TryGetString(value, out var text))
        {
            return text;
        }

        return value.GetValueKind() is JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False ? value.ToJsonString() : null;
    }

    private static JsonNode? Leaf(DateTimeOffset? value)
    {
        return value is { } v ? JsonValue.Create(Format(v)) : null;
    }

    private static JsonNode? Leaf(string? value)
    {
        return value is null ? null : JsonValue.Create(value);
    }

    private static string Describe(DateTimeOffset? value)
    {
        return value is { } v ? Format(v) : "none";
    }

    private static string Describe(string? value)
    {
        return value is null ? "none" : "'" + value + "'";
    }
}
