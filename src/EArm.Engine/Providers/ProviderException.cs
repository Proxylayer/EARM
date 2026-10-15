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

    /// <summary>A key lookup matched more than one object.</summary>
    public const string AmbiguousMatch = "AmbiguousMatch";

    /// <summary>The key of a resource to create is held by a soft-deleted object.</summary>
    public const string SoftDeletedConflict = "SoftDeletedConflict";
}
