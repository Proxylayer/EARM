namespace EArm.Core.Diagnostics;

/// <summary>
/// Every diagnostic code EARM can emit while loading and validating a template offline.
/// The human-readable catalogue (severity and meaning) lives in <c>docs/diagnostics.md</c>; keep both in sync.
/// </summary>
public static class DiagnosticCodes
{
    // ---- Load failures (fatal: raised through TemplateLoadException, CLI exit code 1) ----
    public const string FileNotFound = "FileNotFound";
    public const string InvalidJson = "InvalidJson";
    public const string TemplateRootNotObject = "TemplateRootNotObject";
    public const string InvalidParameterFile = "InvalidParameterFile";
    public const string DuplicateJsonKey = "DuplicateJsonKey";
    public const string InputTooLarge = "InputTooLarge";

    // ---- Load diagnostics (non-fatal: attached to the document and reported by the validator) ----
    public const string NestingTooDeep = "NestingTooDeep";
    public const string DuplicateDefinition = "DuplicateDefinition";
    public const string UnknownParameter = "UnknownParameter";

    // ---- JSON Schema ----
    public const string SchemaViolation = "SchemaViolation";

    // ---- Envelope ----
    public const string UnrecognizedSchema = "UnrecognizedSchema";
    public const string InvalidContentVersion = "InvalidContentVersion";
    public const string UnknownTopLevelProperty = "UnknownTopLevelProperty";
    public const string MissingRequiredProperty = "MissingRequiredProperty";
    public const string ArmLimitExceeded = "ArmLimitExceeded";
    public const string InvalidLanguageVersion = "InvalidLanguageVersion";
    public const string InvalidSymbolicName = "InvalidSymbolicName";
    public const string InvalidExistingResource = "InvalidExistingResource";
    public const string InvalidTypeDefinition = "InvalidTypeDefinition";

    // ---- Resources ----
    public const string MissingResourceType = "MissingResourceType";
    public const string MissingApiVersion = "MissingApiVersion";
    public const string MissingResourceName = "MissingResourceName";
    public const string UnknownResourceType = "UnknownResourceType";
    public const string UnknownApiVersion = "UnknownApiVersion";
    public const string InvalidResourceName = "InvalidResourceName";
    public const string ParentChildTypeMismatch = "ParentChildTypeMismatch";
    public const string DuplicateResourceId = "DuplicateResourceId";
    public const string PropertyTypeMismatch = "PropertyTypeMismatch";
    public const string NameMappedPropertyConflict = "NameMappedPropertyConflict";
    public const string DynamicMembershipConflict = "DynamicMembershipConflict";
    public const string DirectoryRolesRequireRoleAssignableGroup = "DirectoryRolesRequireRoleAssignableGroup";
    public const string AssignedLicensesRequireUsageLocation = "AssignedLicensesRequireUsageLocation";
    public const string DuplicateFederatedCredentialAudience = "DuplicateFederatedCredentialAudience";
    public const string DuplicateFederatedCredentialSubject = "DuplicateFederatedCredentialSubject";
    public const string ConflictingApplicationIncludes = "ConflictingApplicationIncludes";
    public const string InvalidAuthenticationMethodConfiguration = "InvalidAuthenticationMethodConfiguration";
    public const string InvalidRoleEligibilityScheduleRequest = "InvalidRoleEligibilityScheduleRequest";
    public const string InvalidGroupEligibilityScheduleRequest = "InvalidGroupEligibilityScheduleRequest";
    public const string InvalidRoleAssignmentScheduleRequest = "InvalidRoleAssignmentScheduleRequest";
    public const string InvalidGroupAssignmentScheduleRequest = "InvalidGroupAssignmentScheduleRequest";
    public const string InvalidRoleManagementPolicy = "InvalidRoleManagementPolicy";
    public const string InvalidCopy = "InvalidCopy";

    // ---- Parameters ----
    public const string InvalidParameterType = "InvalidParameterType";
    public const string AllowedValueTypeMismatch = "AllowedValueTypeMismatch";
    public const string InvalidParameterConstraint = "InvalidParameterConstraint";
    public const string DefaultValueTypeMismatch = "DefaultValueTypeMismatch";
    public const string DefaultValueNotAllowed = "DefaultValueNotAllowed";
    public const string SecureParameterHasDefault = "SecureParameterHasDefault";
    public const string MissingParameterValue = "MissingParameterValue";
    public const string ParameterValueTypeMismatch = "ParameterValueTypeMismatch";
    public const string ParameterValueNotAllowed = "ParameterValueNotAllowed";

    // ---- Outputs ----
    public const string InvalidOutputType = "InvalidOutputType";

    // ---- Expressions: syntax and function resolution (EArm.Expressions) ----
    public const string ExpressionSyntaxError = "ExpressionSyntaxError";
    public const string ExpressionTooLong = "ExpressionTooLong";
    public const string ExpressionTooDeep = "ExpressionTooDeep";
    public const string UnknownFunction = "UnknownFunction";
    public const string UnsupportedArmFunction = "UnsupportedArmFunction";
    public const string NotImplementedYet = "NotImplementedYet";
    public const string NonDeterministicFunctionNotAllowed = "NonDeterministicFunctionNotAllowed";
    public const string FunctionNotAllowedHere = "FunctionNotAllowedHere";
    public const string InvalidFunctionArguments = "InvalidFunctionArguments";
    public const string ExpressionEvaluationFailed = "ExpressionEvaluationFailed";
    public const string ValueTooDeep = "ValueTooDeep";
    public const string EvaluationBudgetExceeded = "EvaluationBudgetExceeded";
    public const string ExpansionLimitExceeded = "ExpansionLimitExceeded";

    // ---- Expressions: parameters, variables, copy ----
    public const string UndefinedParameter = "UndefinedParameter";
    public const string UndefinedVariable = "UndefinedVariable";
    public const string VariableCycle = "VariableCycle";
    public const string VariableChainTooDeep = "VariableChainTooDeep";
    public const string ParameterDefaultCycle = "ParameterDefaultCycle";
    public const string CopyIndexOutsideCopy = "CopyIndexOutsideCopy";

    // ---- Expressions: references and dependencies ----
    public const string ReferenceNotAllowedHere = "ReferenceNotAllowedHere";
    public const string ReferenceNotStaticallyResolvable = "ReferenceNotStaticallyResolvable";
    public const string ReferenceDependsOnMissingParameter = "ReferenceDependsOnMissingParameter";
    public const string ReferenceTargetNotFound = "ReferenceTargetNotFound";
    public const string AmbiguousReference = "AmbiguousReference";
    public const string InvalidResourceId = "InvalidResourceId";
    public const string DependsOnTargetNotFound = "DependsOnTargetNotFound";
    public const string AmbiguousDependsOn = "AmbiguousDependsOn";

    // ---- Expressions: evaluated resource keys and outputs ----
    public const string ApiVersionMustBeLiteral = "ApiVersionMustBeLiteral";
    public const string ResourceNameNotString = "ResourceNameNotString";
    public const string ConditionNotBoolean = "ConditionNotBoolean";
    public const string SecureValueInResourceKey = "SecureValueInResourceKey";
    public const string SecureValueInPlainOutput = "SecureValueInPlainOutput";

    // ---- Nested and linked deployments (Microsoft.Resources/deployments) ----
    public const string InvalidNestedDeployment = "InvalidNestedDeployment";
    public const string NestedDeploymentFeatureNotSupported = "NestedDeploymentFeatureNotSupported";
    public const string NestedDeploymentPropertyIgnored = "NestedDeploymentPropertyIgnored";
}
