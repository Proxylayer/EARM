using System;

namespace EArm.Engine.State;

/// <summary>
/// How a state entry came to be (review finding E-02). <see cref="Created"/>: this deployment created the object, so
/// Complete mode and <c>deployment delete --delete-resources</c> may delete it. <see cref="Adopted"/>: the object existed before the
/// deployment managed it (found by key with no state entry, pinned with <c>state adopt</c>, or an entry written by a
/// version that did not record provenance); it is never deleted unless the user passes <c>--delete-adopted</c>.
/// </summary>
public enum StateProvenance
{
    Created,

    Adopted,
}

/// <summary>The on-disk spelling of <see cref="StateProvenance"/>.</summary>
public static class StateProvenances
{
    public const string CreatedText = "created";

    public const string AdoptedText = "adopted";

    public static string ToText(StateProvenance provenance)
    {
        return provenance == StateProvenance.Created ? CreatedText : AdoptedText;
    }

    /// <summary>
    /// Parses the stored text. A missing or unknown value is <see cref="StateProvenance.Adopted"/>: an entry that does
    /// not say it was created by EARM must never be deleted by it (safe default for files written before provenance existed).
    /// </summary>
    public static StateProvenance Parse(string? text)
    {
        return string.Equals(text?.Trim(), CreatedText, StringComparison.OrdinalIgnoreCase) ? StateProvenance.Created : StateProvenance.Adopted;
    }
}
