using System;

namespace EArm.Core.Types;

/// <summary>
/// The live <c>displayName</c> of a <c>Microsoft.Entra/applications/passwordCredentials</c> credential: the template's name
/// (<see cref="Base"/>) plus, when the template declares a <c>rotationTag</c>, <c>#&lt;tag&gt;</c>. Graph stores nothing else EARM could
/// remember the tag in, so carrying it in the name is what lets a rerun compare the declared tag with the live one without any
/// local state. The tag is the text after the LAST <see cref="TagSeparator"/>; the base name and the tag may therefore not contain one.
/// </summary>
public readonly record struct PasswordCredentialName(string Base, string? Tag)
{
    /// <summary>Separates the base name from the rotation tag in the live <c>displayName</c>.</summary>
    public const char TagSeparator = '#';

    /// <summary>
    /// The longest live <c>displayName</c> (base, separator and tag) EARM writes. Graph documents no limit for a password credential's
    /// <c>displayName</c>; 256 is a conservative cap in line with the other Entra display names (TODO-0031 tracks probing the real tenant
    /// limit), so a template is rejected offline instead of failing at <c>addPassword</c> time.
    /// </summary>
    public const int MaxLength = 256;

    /// <summary>The live <c>displayName</c>: <c>base#tag</c>, or <c>base</c> without a tag.</summary>
    public string DisplayName => Tag is null ? Base : Base + TagSeparator + Tag;

    /// <summary>A declared tag: an empty one counts as no tag (the live name would end in a bare <c>#</c>, which parses back as no tag).</summary>
    public static PasswordCredentialName Declared(string baseName, string? rotationTag)
    {
        ArgumentNullException.ThrowIfNull(baseName);
        return new PasswordCredentialName(baseName, string.IsNullOrEmpty(rotationTag) ? null : rotationTag);
    }

    /// <summary>Splits a live <c>displayName</c> at its last <see cref="TagSeparator"/>; no separator (or nothing after it) means no tag.</summary>
    public static PasswordCredentialName Parse(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        int separator = displayName.LastIndexOf(TagSeparator);
        if (separator < 0)
        {
            return new PasswordCredentialName(displayName, null);
        }

        string tag = displayName[(separator + 1)..];
        return new PasswordCredentialName(displayName[..separator], tag.Length == 0 ? null : tag);
    }

    /// <summary>
    /// Why a declared name / tag pair cannot be written, or <c>null</c>: a <see cref="TagSeparator"/> in the base name or the tag (it
    /// would make the live name ambiguous), or a live name longer than <see cref="MaxLength"/>.
    /// </summary>
    public static string? Problem(string baseName, string? rotationTag)
    {
        ArgumentNullException.ThrowIfNull(baseName);
        if (baseName.Contains(TagSeparator, StringComparison.Ordinal))
        {
            return $"the credential name '{baseName}' contains '{TagSeparator}', which EARM reserves to separate the name from the rotationTag in the live displayName ('<name>{TagSeparator}<rotationTag>'); choose a name without '{TagSeparator}'.";
        }

        if (rotationTag is not null && rotationTag.Contains(TagSeparator, StringComparison.Ordinal))
        {
            return $"properties.rotationTag ('{rotationTag}') contains '{TagSeparator}', which EARM reserves to separate the name from the tag in the live displayName; choose a tag without '{TagSeparator}'.";
        }

        var name = Declared(baseName, rotationTag);
        if (name.DisplayName.Length > MaxLength)
        {
            return $"the live displayName '<name>{TagSeparator}<rotationTag>' would be {name.DisplayName.Length} characters long; EARM caps it at {MaxLength}. Shorten the credential name or the rotationTag.";
        }

        return null;
    }
}
