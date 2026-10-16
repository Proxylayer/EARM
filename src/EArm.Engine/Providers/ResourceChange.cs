using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Json;

namespace EArm.Engine.Providers;

/// <summary>ARM what-if change types.</summary>
public enum ChangeType
{
    Create,
    Modify,
    Delete,
    NoChange,
    Ignore,
}

/// <summary>Why a row of the orphan report is listed (see docs/whatif.md, "Orphan report").</summary>
public enum OrphanReason
{
    /// <summary>A live object of a type the template declares that no declared resource matches by key (Complete mode only). It is never deleted.</summary>
    Undeclared,

    /// <summary>
    /// A live assignment or grant of a principal (or client) the template declares that no declared assignment matches: typically the old tuple of an
    /// assignment whose principal, role or scope changed. It is retained (never removed) and still grants access. Listed in both modes.
    /// </summary>
    Retained,
}

/// <summary>How a change is applied.</summary>
public enum ChangeMode
{
    None,
    Create,
    Patch,

    /// <summary>Delete then create (immutable change on a cheap type, composite-key change of an assignment).</summary>
    Recreate,
    Delete,
}

/// <summary>ARM what-if property change types.</summary>
public enum PropertyChangeType
{
    Create,
    Delete,
    Modify,
    Array,

    /// <summary>A difference the executor will not act on (for example a live-only sub-property Graph refuses to null).</summary>
    NoEffect,
}

/// <summary>
/// One property-level difference. <see cref="Path"/> is ARM-style and rooted at the resource
/// (<c>properties.web.redirectUris</c>); array members are addressed by stable member key or index.
/// Secret values are never carried: <see cref="IsSecret"/> deltas hold <c>***</c> on both sides. Deploy-time-unknown
/// desired values are flagged <see cref="IsUnknown"/> and rendered as <c>(deploy-time)</c>. <see cref="Note"/> is a short
/// provider explanation shown next to the delta (for example why a declared value is held back); a <see cref="PropertyChangeType.NoEffect"/>
/// delta that carries one is rendered even without <c>--verbose</c>.
/// </summary>
public sealed record PropertyDelta(
    JsonPath Path,
    PropertyChangeType Kind,
    JsonNode? Before,
    JsonNode? After,
    bool IsUnknown = false,
    bool IsSecret = false,
    IReadOnlyList<PropertyDelta>? Children = null,
    string? Note = null)
{
    /// <summary>True when the delta (or any child) requires a write.</summary>
    public bool HasEffect => Kind != PropertyChangeType.NoEffect && (Children is null || Children.Count == 0 || Children.Any(c => c.HasEffect));
}

/// <summary>
/// The planned change of one resource instance (what-if row and executor instruction). <see cref="Before"/> and
/// <see cref="After"/> are rendering copies: secrets redacted, unknowns rendered.
/// </summary>
public sealed record ResourceChange
{
    public required ResourceIdentifier Id { get; init; }

    public required string ResourceType { get; init; }

    public required ChangeType Type { get; init; }

    public ChangeMode Mode { get; init; } = ChangeMode.None;

    public IReadOnlyList<PropertyDelta> Deltas { get; init; } = [];

    /// <summary>Human-readable reasons (why Ignore, why Recreate, what is unknown).</summary>
    public IReadOnlyList<string> Reasons { get; init; } = [];

    /// <summary>The live object (diff view) before the change; <c>null</c> for Create.</summary>
    public JsonObject? Before { get; init; }

    /// <summary>The desired object after the change; <c>null</c> for Delete / Ignore.</summary>
    public JsonObject? After { get; init; }

    /// <summary>Set when the change cannot be applied (immutable change on a type that cannot be recreated, read failure...).</summary>
    public string? UnsupportedReason { get; init; }

    /// <summary>The diagnostic code a deployment reports an unsupported change under; <c>null</c> for the generic <see cref="EngineDiagnosticCodes.ImmutablePropertyChanged"/>.</summary>
    public string? UnsupportedCode { get; init; }

    /// <summary>True when the key or the desired properties contain deploy-time values the planner could not resolve.</summary>
    public bool IsUnknown { get; init; }

    /// <summary>True when the resource id is still symbolic (its name depends on a resource that does not exist yet).</summary>
    public bool IsSymbolicId { get; init; }

    /// <summary>The Graph id of the existing object when one was found.</summary>
    public string? GraphId { get; init; }

    /// <summary>
    /// Set on the rows of the orphan report only (an <see cref="ChangeType.Ignore"/> row for a live object no declared resource matches); <c>null</c> for
    /// every planned change. Orphans are never written to: they do not count as changes.
    /// </summary>
    public OrphanReason? Orphan { get; init; }

    public bool IsOrphan => Orphan is not null;

    public bool HasError => UnsupportedReason is not null;

    public bool IsWrite => Type is ChangeType.Create or ChangeType.Modify or ChangeType.Delete;

    public static ResourceChange Ignore(ResourceIdentifier id, string resourceType, string reason, bool isUnknown = false)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new ResourceChange { Id = id, ResourceType = resourceType, Type = ChangeType.Ignore, Mode = ChangeMode.None, Reasons = [reason], IsUnknown = isUnknown };
    }

    /// <summary>A row of the orphan report: an <see cref="ChangeType.Ignore"/> row for the live object <paramref name="graphId"/>, never acted on.</summary>
    public static ResourceChange OrphanRow(ResourceIdentifier id, string resourceType, string graphId, OrphanReason reason, string text)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new ResourceChange { Id = id, ResourceType = resourceType, Type = ChangeType.Ignore, Mode = ChangeMode.None, GraphId = graphId, Orphan = reason, Reasons = [text] };
    }

    public static ResourceChange Unsupported(ResourceIdentifier id, string resourceType, ChangeType type, string reason)
    {
        ArgumentNullException.ThrowIfNull(id);
        return new ResourceChange { Id = id, ResourceType = resourceType, Type = type, Mode = ChangeMode.None, UnsupportedReason = reason, Reasons = [reason] };
    }

    /// <summary>The ARM what-if symbol: <c>+ ~ - = *</c>.</summary>
    public char Symbol => Type switch
    {
        ChangeType.Create => '+',
        ChangeType.Modify => '~',
        ChangeType.Delete => '-',
        ChangeType.NoChange => '=',
        _ => '*',
    };
}
