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

    // ---- Delete ----

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

    // ---- Safety review (adoption, blast radius) ----

    /// <summary>A delete (a recreate, or <c>deployment delete</c>) was refused because the object is the signed-in principal or a service principal owned by another organization.</summary>
    public const string ProtectedObject = "ProtectedObject";

    /// <summary>
    /// A delete (<c>deployment delete</c>) was refused because Graph cannot delete or reset the object: a tenant singleton such as an authentication method
    /// configuration without a documented reset (sms, voice, email, softwareOath, microsoftAuthenticator). The object is kept, like a <see cref="ProtectedObject"/>.
    /// </summary>
    public const string NotDeletable = "NotDeletable";

    /// <summary>Two declared resources resolve to the same Graph object.</summary>
    public const string DuplicateGraphObject = "DuplicateGraphObject";

    /// <summary>A create failed with an ambiguous outcome (timeout, transport error, 5xx); the object was looked up by key and found, so the create counts as done.</summary>
    public const string CreateOutcomeUncertain = "CreateOutcomeUncertain";

    /// <summary>A create succeeded, but a lookup by the key (a <c>displayName</c> <c>$filter</c> list) did not return the new object within the replication window; a run started right away could create it again.</summary>
    public const string CreatedObjectNotVisible = "CreatedObjectNotVisible";

    /// <summary>A replacement (an immutable property changed: delete and create again) was not confirmed with <c>-y</c> or at the prompt; nothing was deleted.</summary>
    public const string RecreateNotConfirmed = "RecreateNotConfirmed";

    // ---- Orphan report (read-only; nothing is ever deleted) ----

    /// <summary>The orphan report did not list a declared type: one of its declared resources is not resolved in this run, the listing failed, or the type is tenant-wide (applications, groups, users).</summary>
    public const string OrphanReportIncomplete = "OrphanReportIncomplete";

    /// <summary>A live assignment or grant of a principal the template declares is not declared (for example the old tuple of a changed assignment): it is retained and still grants access.</summary>
    public const string AssignmentRetained = "AssignmentRetained";

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

    /// <summary>No group holds the declared uniqueName and exactly one group without a uniqueName carries the declared displayName (created outside EARM): it is adopted, and its uniqueName is set.</summary>
    public const string GroupAdoptedByDisplayName = "GroupAdoptedByDisplayName";

    /// <summary>A password credential has expired or is about to, and rotating it would not extend its life.</summary>
    public const string SecretExpiresSoon = "SecretExpiresSoon";

    /// <summary>A live password credential carries no rotation tag in its displayName (created by an older EARM) while the template declares one: treated as matching, stamped by the next rotation.</summary>
    public const string RotationTagNotStamped = "RotationTagNotStamped";

    /// <summary>A FIDO2 authentication method configuration declares <c>isAttestationEnforced</c> or <c>keyRestrictions</c>, which Microsoft deprecated (removed October 2027) in favour of <c>passkeyProfiles</c>.</summary>
    public const string DeprecatedAuthenticationMethodProperty = "DeprecatedAuthenticationMethodProperty";

    /// <summary>A failed <c>addPassword</c> left a credential of the same name that EARM did not record; it is reported, never removed.</summary>
    public const string PasswordCredentialOrphaned = "PasswordCredentialOrphaned";

    /// <summary>Graph refused to set the <c>uniqueName</c> of an existing application or group being adopted (the property is immutable).</summary>
    public const string UniqueNameNotSettable = "UniqueNameNotSettable";

    /// <summary>An <c>appRoleAssignments</c> declaration is invalid (unresolvable app role, principal or resource).</summary>
    public const string AppRoleAssignmentInvalid = "AppRoleAssignmentInvalid";

    /// <summary>An <c>oauth2PermissionGrants</c> declaration is invalid (unknown scope, missing principal for a <c>Principal</c> grant...).</summary>
    public const string PermissionGrantInvalid = "PermissionGrantInvalid";

    /// <summary>A <c>roleAssignments</c> declaration is invalid (unknown role, principal or scope).</summary>
    public const string RoleAssignmentInvalid = "RoleAssignmentInvalid";

    /// <summary>A <c>roleEligibilityScheduleRequests</c> declaration is invalid (unknown role, unit or principal, a group that is not role-assignable, a bad schedule).</summary>
    public const string RoleEligibilityInvalid = "RoleEligibilityInvalid";

    /// <summary>A <c>privilegedAccess/groupEligibilityScheduleRequests</c> declaration is invalid (unknown group or principal, a dynamic or on-premises synced group, a principal that is not a user or group).</summary>
    public const string GroupEligibilityInvalid = "GroupEligibilityInvalid";

    /// <summary>A <c>roleAssignmentScheduleRequests</c> declaration is invalid (unknown role, unit or principal, a group that is not role-assignable, a bad or permanent schedule).</summary>
    public const string RoleAssignmentScheduleInvalid = "RoleAssignmentScheduleInvalid";

    /// <summary>A <c>privilegedAccess/groupAssignmentScheduleRequests</c> declaration is invalid (unknown group or principal, a dynamic or on-premises synced group, a principal that is not a user or group, a bad or permanent schedule).</summary>
    public const string GroupAssignmentScheduleInvalid = "GroupAssignmentScheduleInvalid";

    /// <summary>
    /// A time-bound PIM active assignment (<c>roleAssignmentScheduleRequests</c>, <c>privilegedAccess/groupAssignmentScheduleRequests</c>) would fight
    /// another declaration or a permanent live assignment: the same role assignment declared by <c>roleAssignments</c> or a principal's
    /// <c>directoryRoles</c>, a target group whose <c>members</c> / <c>owners</c> list prunes it or a principal's <c>memberOf</c> / <c>ownerOf</c> that would
    /// re-add it permanently (refused before planning), or a permanent assignment under the same tuple (refused at plan time, on adoption, kept on delete).
    /// </summary>
    public const string PimAssignmentConflict = "PimAssignmentConflict";

    /// <summary>Graph refused a PIM schedule request because a rule of the role's (or group's) management policy failed (ExpirationRule, JustificationRule, TicketingRule...); the message carries Graph's text.</summary>
    public const string PimPolicyViolation = "PimPolicyViolation";

    /// <summary>
    /// A <c>roleManagementPolicies</c> declaration names a PIM policy Graph does not list (no <c>roleManagementPolicyAssignments</c> entry for the scope,
    /// role and access): an unknown directory role, or a group PIM for Groups has not onboarded yet. An error when nothing in the deployment onboards the
    /// group; a warning (the policy is deferred to the next run) when a schedule request of the same deployment does.
    /// </summary>
    public const string PimPolicyNotAvailable = "PimPolicyNotAvailable";

    /// <summary>
    /// Graph refused a PIM for Groups read or request (<c>/identityGovernance/privilegedAccess/group/*</c>) with 403 UnauthorizedAccessException: the caller
    /// lacks an ACTIVE (not just eligible) directory-scope role that governs the group (Groups Administrator, Identity Governance Administrator, User
    /// Administrator or Directory Writers for a group that is not role-assignable; Privileged Role Administrator for a role-assignable one) and does not own it.
    /// </summary>
    public const string PimGroupAccessDenied = "PimGroupAccessDenied";

    /// <summary>
    /// Graph refused to remove a PIM schedule (adminRemove) with 400 <c>ActiveDurationTooShort</c>: an assignment cannot be removed within five minutes
    /// of being assigned (Learn: pim-how-to-add-role-to-user). The assignment and its access stay; remove it again once it is five minutes old.
    /// </summary>
    public const string ActiveAssignmentTooNew = "ActiveAssignmentTooNew";

    /// <summary>
    /// Graph refused to change or remove a PIM for Groups owner assignment (adminUpdate or adminRemove) with 400 <c>CannotDeleteLastAdminAssignment</c>:
    /// the principal is the group's only owner, and PIM replaces a schedule by removing it, which would leave the group without an owner. Add another
    /// owner to the group first.
    /// </summary>
    public const string PimLastOwnerAssignment = "PimLastOwnerAssignment";

    /// <summary>A <c>roleManagementPolicies</c> declaration is invalid at plan time (unknown role or group, a computed value that breaks a rule, a rule id the live policy does not have).</summary>
    public const string RoleManagementPolicyInvalid = "RoleManagementPolicyInvalid";

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

    /// <summary>Graph kept refusing to delete a named location because it is marked trusted (error 1177), even after EARM unmarked it.</summary>
    public const string NamedLocationTrusted = "NamedLocationTrusted";

    // ---- Entitlement management (access packages) ----

    /// <summary>Graph refused to add a group to a catalog (403): the caller needs catalog ownership plus a role that changes the group's members; Global Administrator or Privileged Role Administrator for a role-assignable group.</summary>
    public const string CatalogResourceAccessDenied = "CatalogResourceAccessDenied";

    /// <summary>A catalog resource request ended in <c>deliveryFailed</c>, or the group was still not listed in the catalog after the wait.</summary>
    public const string CatalogResourceRequestFailed = "CatalogResourceRequestFailed";

    /// <summary>Graph refused to remove a group from a catalog because an access package of the catalog still grants one of its roles.</summary>
    public const string CatalogResourceInUse = "CatalogResourceInUse";

    /// <summary>Graph refused to delete a catalog that still holds access packages.</summary>
    public const string CatalogNotEmpty = "CatalogNotEmpty";

    /// <summary>A resource role scope names a group that is not a resource of the package's catalog, or a role the catalog does not offer for it (an eligible role of a group not onboarded in PIM for Groups).</summary>
    public const string AccessPackageRoleNotAvailable = "AccessPackageRoleNotAvailable";

    /// <summary>
    /// A resource role scope grants an eligible group role (Eligible Member / Eligible Owner) of a group PIM for Groups has not onboarded: the catalog
    /// offers eligible roles only for onboarded groups. Deferred to the next run (a warning on an Ignore row) when a schedule request of the same
    /// deployment onboards the group; an error otherwise.
    /// </summary>
    public const string PimGroupNotOnboarded = "PimGroupNotOnboarded";

    /// <summary>Graph refused to delete an access package or an assignment policy that still has assignments.</summary>
    public const string AccessPackageAssignmentsExist = "AccessPackageAssignmentsExist";

    // ---- Edge pass: app-only tokens and forbidden reads ----

    /// <summary>A declared relationship cannot be read or changed with an application (app-only) token: <c>ownerOf</c> on a user (Learn: user-list-ownedobjects). The row is reported and skipped.</summary>
    public const string EdgeUnsupportedInAppMode = "EdgeUnsupportedInAppMode";

    /// <summary>Reading <c>/users/{id}/manager</c> was refused (403); the manager relationship is skipped instead of failing.</summary>
    public const string ManagerReadForbidden = "ManagerReadForbidden";
}
