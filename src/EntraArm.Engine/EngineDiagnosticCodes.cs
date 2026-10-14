namespace EntraArm.Engine;

/// <summary>
/// Diagnostic codes raised by the engine (planning and execution), in addition to the offline codes of
/// <see cref="EntraArm.Core.Diagnostics.DiagnosticCodes"/>.
/// </summary>
public static class EngineDiagnosticCodes
{
    /// <summary>The dependency graph has a cycle; the message carries the path.</summary>
    public const string DependencyCycle = "DependencyCycle";

    /// <summary>A parameter whose value is a Key Vault reference cannot be resolved by this version of the executor.</summary>
    public const string KeyVaultReferenceUnresolved = "KeyVaultReferenceUnresolved";

    /// <summary>A <c>reference(resourceId(...), apiVersion)</c> lookup could not be performed (no provider, unresolvable key).</summary>
    public const string UnresolvedReference = "UnresolvedReference";

    /// <summary>A resource <c>condition</c> depends on a deploy-time value and could not be decided.</summary>
    public const string ConditionUnresolved = "ConditionUnresolved";

    /// <summary>A resource's evaluated properties still contain deploy-time-unknown values when it is applied.</summary>
    public const string UnresolvedDeployTimeValue = "UnresolvedDeployTimeValue";

    /// <summary>A resource name that depends on another resource could not be computed before its apply.</summary>
    public const string SymbolicNameUnresolved = "SymbolicNameUnresolved";

    /// <summary>The provider could not derive the Graph key of a resource (unknown composite properties).</summary>
    public const string ResourceKeyUnresolved = "ResourceKeyUnresolved";

    /// <summary>An immutable property differs between the template and the live object and the type cannot be recreated.</summary>
    public const string ImmutablePropertyChanged = "ImmutablePropertyChanged";

    /// <summary>A Graph read for an existing object failed during planning.</summary>
    public const string ReadFailed = "ReadFailed";

    /// <summary>Applying a resource failed (the message carries the Graph error).</summary>
    public const string ResourceApplyFailed = "ResourceApplyFailed";

    /// <summary>A resource was skipped because one of its dependencies failed.</summary>
    public const string ResourceSkipped = "ResourceSkipped";

    /// <summary>An output could not be evaluated after the resources were applied.</summary>
    public const string OutputEvaluationFailed = "OutputEvaluationFailed";

    /// <summary>A <c>copy</c> block could not be expanded (count/mode/batchSize not resolvable).</summary>
    public const string CopyExpansionFailed = "CopyExpansionFailed";

    /// <summary>
    /// An output reads the one-time secret of a resource that was not created in this run
    /// (<c>reference(...).secretText</c> of a password credential on a rerun): the output is reported as <c>null</c>
    /// and left out of the secure outputs file (WI-11, <see cref="Providers.LiveSecrets"/>).
    /// </summary>
    public const string SecretNotAvailableOnRerun = "SecretNotAvailableOnRerun";

    // ---- State (WI-06) ----

    /// <summary>Another <c>entraarm</c> process holds the deployment's state lock (CLI exit 4).</summary>
    public const string StateLocked = "StateLocked";

    /// <summary>The state file was written for a different tenant than the one the Azure CLI session is signed in to (CLI exit 3).</summary>
    public const string TenantMismatch = "TenantMismatch";

    /// <summary>The state file's <c>schemaVersion</c> is newer than this executor understands, or the file is not a state file.</summary>
    public const string StateSchemaUnsupported = "StateSchemaUnsupported";

    /// <summary>A state-managed resource is no longer declared in the template and was detached from the state (Incremental mode).</summary>
    public const string ResourceDetached = "ResourceDetached";

    /// <summary>A state-managed resource is no longer declared in the template and was deleted from the tenant (Complete mode), or its deletion failed.</summary>
    public const string ResourceDeleted = "ResourceDeleted";

    // ---- Edge pass (WI-09) ----

    /// <summary>A relationship declaration is invalid: dynamic target, non-existent target, wrong value shape, conflicting declarations, or a placeholder at apply time.</summary>
    public const string EdgeValidationFailed = "EdgeValidationFailed";

    /// <summary>Adding or removing a relationship edge failed (the message carries the Graph error).</summary>
    public const string EdgeApplyFailed = "EdgeApplyFailed";

    /// <summary>A declared relationship kind has no handler in this version (app role assignments, directory roles until their work items land).</summary>
    public const string EdgeKindNotSupported = "EdgeKindNotSupported";

    /// <summary>The edge pass did not run because an earlier batch failed (fail-fast).</summary>
    public const string EdgePassSkipped = "EdgePassSkipped";

    /// <summary>Applying the template removes the signed-in principal from an object's owners.</summary>
    public const string SignedInPrincipalRemoved = "SignedInPrincipalRemoved";

    /// <summary>Complete mode is about to delete an object that cannot be restored (a security group).</summary>
    public const string DestructiveDelete = "DestructiveDelete";
}
