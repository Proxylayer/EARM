namespace EArm.Engine.Providers;

/// <summary>
/// Optional provider capability: says why an object of the provider's type cannot be deleted at all (a tenant singleton Graph neither deletes nor
/// resets, such as the sms authentication method configuration). <c>deployment delete</c> keeps such an object and reports the reason
/// (<see cref="EngineDiagnosticCodes.NotDeletable"/>), exactly like a protected object: it is listed as kept, and it is not a failure.
/// </summary>
public interface IDeleteRefusalSource
{
    /// <summary>Why the live object <paramref name="live"/> found under <paramref name="key"/> is kept instead of deleted, or <c>null</c> when it can be deleted.</summary>
    string? DescribeDeleteRefusal(ResourceKey key, ExistingResource live);
}
