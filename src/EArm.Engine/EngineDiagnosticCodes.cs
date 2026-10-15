namespace EArm.Engine;

/// <summary>
/// Diagnostic codes raised by the engine (planning and execution), in addition to the offline codes of
/// <see cref="EArm.Core.Diagnostics.DiagnosticCodes"/>.
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
    /// and left out of the secure outputs file (<see cref="Providers.LiveSecrets"/>).
    /// </summary>
    public const string SecretNotAvailableOnRerun = "SecretNotAvailableOnRerun";

    // ---- State ----

    /// <summary>Another <c>earm</c> process holds the deployment's state lock (CLI exit 4).</summary>
    public const string StateLocked = "StateLocked";

    /// <summary>The state file was written for a different tenant than the one EARM is signed in to (CLI exit 3).</summary>
    public const string TenantMismatch = "TenantMismatch";

    /// <summary>The state file's <c>schemaVersion</c> is newer than this executor understands, or the file is not a state file.</summary>
    public const string StateSchemaUnsupported = "StateSchemaUnsupported";

    /// <summary>A state entry is not what it must be (its key is not the key its resource id implies, a recorded id is malformed): the state is refused at load, nothing is acted on (CLI exit 1).</summary>
    public const string StateEntryInvalid = "StateEntryInvalid";

    /// <summary><c>deployment delete</c> failed to delete a declared object (the message carries the Graph error).</summary>
    public const string ResourceDeleted = "ResourceDeleted";

    // ---- Edge pass ----

    /// <summary>A relationship declaration is invalid: dynamic target, non-existent target, wrong value shape, conflicting declarations, or a placeholder at apply time.</summary>
    public const string EdgeValidationFailed = "EdgeValidationFailed";

    /// <summary>Adding or removing a relationship edge failed (the message carries the Graph error).</summary>
    public const string EdgeApplyFailed = "EdgeApplyFailed";

    /// <summary>A declared relationship kind has no handler in this version (for example app role assignments or directory roles when only the default edge kinds are registered).</summary>
    public const string EdgeKindNotSupported = "EdgeKindNotSupported";

    /// <summary>The edge pass did not run because an earlier batch failed (fail-fast).</summary>
    public const string EdgePassSkipped = "EdgePassSkipped";

    /// <summary>Applying the template removes the signed-in principal from an object's owners.</summary>
    public const string SignedInPrincipalRemoved = "SignedInPrincipalRemoved";

    /// <summary>A delete (<c>deployment delete</c>, or the old object of a recreate) removes an object that cannot be restored (a security group).</summary>
    public const string DestructiveDelete = "DestructiveDelete";

    // ---- Safety review (state trust, adoption, blast radius) ----

    /// <summary>The state entry does not describe the live object it points at (pinned id / key mismatch), so it was not used to modify or delete anything.</summary>
    public const string StateEntryMismatch = "StateEntryMismatch";

    /// <summary>A delete (a recreate, or <c>deployment delete</c>) was refused because the object is the signed-in principal or a service principal owned by another organization.</summary>
    public const string ProtectedObject = "ProtectedObject";

    /// <summary>Two declared resources resolve to the same Graph object.</summary>
    public const string DuplicateGraphObject = "DuplicateGraphObject";

    /// <summary>A create failed with an ambiguous outcome (timeout, transport error, 5xx); the object was looked up by key and found, so the create counts as done.</summary>
    public const string CreateOutcomeUncertain = "CreateOutcomeUncertain";

    /// <summary>A replacement (an immutable property changed: delete and create again) was not confirmed with <c>-y</c> or at the prompt; nothing was deleted.</summary>
    public const string RecreateNotConfirmed = "RecreateNotConfirmed";

    // ---- Orphan report (read-only; nothing is ever deleted) ----

    /// <summary>The orphan report did not list a declared type: one of its declared resources is not resolved in this run, the listing failed, or the type is tenant-wide (applications, groups, users).</summary>
    public const string OrphanReportIncomplete = "OrphanReportIncomplete";

    /// <summary>A live assignment or grant of a principal the template declares is not declared (for example the old tuple of a changed assignment): it is retained and still grants access.</summary>
    public const string AssignmentRetained = "AssignmentRetained";

    /// <summary>The state directory or a state file is a symbolic link / junction, or the directory is not a directory.</summary>
    public const string StateStoreUnsafe = "StateStoreUnsafe";

    /// <summary>The state could not be written (I/O or permission error) after retrying.</summary>
    public const string StateSaveFailed = "StateSaveFailed";

    /// <summary>The secure outputs file could not be written; one-time secrets that did not reach it are lost.</summary>
    public const string SecureOutputsWriteFailed = "SecureOutputsWriteFailed";

    // ---- Provider findings (reported against one resource; the providers alias these in ProviderWarnings and as provider constants) ----

    /// <summary>A key lookup matched more than one object.</summary>
    public const string AmbiguousMatch = "AmbiguousMatch";

    /// <summary>The key of a resource to create is held by a soft-deleted object (pass <c>--on-soft-deleted-conflict restore</c> or purge it).</summary>
    public const string SoftDeletedConflict = "SoftDeletedConflict";

    /// <summary>The <c>/directory/deletedItems</c> probe before a create was refused or rejected; the create proceeds without it.</summary>
    public const string SoftDeleteProbeSkipped = "SoftDeleteProbeSkipped";

    /// <summary>A Microsoft 365 group is created without owners declared (created with an application token it is ownerless).</summary>
    public const string GroupHasNoOwnerAtCreate = "GroupHasNoOwnerAtCreate";

    /// <summary>A password credential has expired or is about to, and rotating it would not extend its life.</summary>
    public const string SecretExpiresSoon = "SecretExpiresSoon";

    /// <summary>A live password credential carries no rotation tag in its displayName (created by an older EARM) while the template declares one: treated as matching, stamped by the next rotation.</summary>
    public const string RotationTagNotStamped = "RotationTagNotStamped";

    /// <summary>A failed <c>addPassword</c> left a credential of the same name that EARM did not record; it is reported, never removed.</summary>
    public const string PasswordCredentialOrphaned = "PasswordCredentialOrphaned";

    /// <summary>Graph refused to set the <c>uniqueName</c> of an existing application (the property is immutable).</summary>
    public const string UniqueNameNotSettable = "UniqueNameNotSettable";

    /// <summary>An <c>appRoleAssignments</c> declaration is invalid (unresolvable app role, principal or resource).</summary>
    public const string AppRoleAssignmentInvalid = "AppRoleAssignmentInvalid";

    /// <summary>An <c>oauth2PermissionGrants</c> declaration is invalid (unknown scope, missing principal for a <c>Principal</c> grant...).</summary>
    public const string PermissionGrantInvalid = "PermissionGrantInvalid";

    /// <summary>A <c>roleAssignments</c> declaration is invalid (unknown role, principal or scope).</summary>
    public const string RoleAssignmentInvalid = "RoleAssignmentInvalid";

    /// <summary>A <c>roleDefinitions</c> declaration names a built-in role, which cannot be created, changed or deleted.</summary>
    public const string RoleDefinitionBuiltIn = "RoleDefinitionBuiltIn";

    /// <summary>A custom role definition cannot be deleted because role assignments still use it.</summary>
    public const string RoleDefinitionInUse = "RoleDefinitionInUse";

    /// <summary>An authentication context class reference cannot be deleted while a conditional access policy references it.</summary>
    public const string AuthenticationContextReferenced = "AuthenticationContextReferenced";

    /// <summary>An authentication strength cannot be deleted while a conditional access policy references it.</summary>
    public const string AuthenticationStrengthReferenced = "AuthenticationStrengthReferenced";

    /// <summary>A built-in authentication strength cannot be created, changed or deleted.</summary>
    public const string AuthenticationStrengthBuiltIn = "AuthenticationStrengthBuiltIn";

    /// <summary>A conditional access policy declaration was refused before the write (invalid state or shape).</summary>
    public const string ConditionalAccessPolicyRejected = "ConditionalAccessPolicyRejected";

    /// <summary>A named location cannot be deleted while a conditional access policy references it.</summary>
    public const string NamedLocationReferenced = "NamedLocationReferenced";

    // ---- Edge pass: app-only tokens and forbidden reads ----

    /// <summary>A declared relationship cannot be read or changed with an application (app-only) token: <c>ownerOf</c> on a user (Learn: user-list-ownedobjects). The row is reported and skipped.</summary>
    public const string EdgeUnsupportedInAppMode = "EdgeUnsupportedInAppMode";

    /// <summary>Reading <c>/users/{id}/manager</c> was refused (403); the manager relationship is skipped instead of failing.</summary>
    public const string ManagerReadForbidden = "ManagerReadForbidden";
}
