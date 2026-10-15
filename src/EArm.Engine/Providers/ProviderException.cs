using System;
using EArm.Core;

namespace EArm.Engine.Providers;

/// <summary>
/// A provider-level failure that is not a Graph error: an unresolvable key, a name/property conflict, an
/// ambiguous lookup, an immutable change. <see cref="Code"/> is an <see cref="EngineDiagnosticCodes"/> or provider
/// constant; the executor reports it against <see cref="ResourceId"/>.
/// </summary>
public class ProviderException : Exception
{
    public ProviderException(string code, string message, ResourceIdentifier? resourceId = null, Exception? inner = null)
        : base(message, inner)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);
        Code = code;
        ResourceId = resourceId;
    }

    public string Code { get; }

    public ResourceIdentifier? ResourceId { get; }

    /// <summary>
    /// Set by a provider whose create or update writes in several steps and failed AFTER the first one (the object exists, a later step such as
    /// <c>assignLicense</c>, owners or a restore failed): the state of the object as far as it got. The executor records it (the Graph id, a one-time secret such
    /// as a generated password) and reports the resource failed, so a rerun converges instead of conflicting with an unrecorded object (review round 2, S2-08).
    /// </summary>
    public AppliedResource? Partial { get; init; }

    /// <summary>A key lookup matched more than one object.</summary>
    public const string AmbiguousMatch = EngineDiagnosticCodes.AmbiguousMatch;

    /// <summary>The key of a resource to create is held by a soft-deleted object.</summary>
    public const string SoftDeletedConflict = EngineDiagnosticCodes.SoftDeletedConflict;
}
