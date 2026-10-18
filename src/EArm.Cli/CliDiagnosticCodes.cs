namespace EArm.Cli;

/// <summary>
/// The diagnostic codes the command line itself reports (not the template, the engine or a provider): each constant is named after its value, and
/// every one is documented in <c>docs/diagnostics.md</c>.
/// </summary>
internal static class CliDiagnosticCodes
{
    /// <summary>A command-line argument (or an option value) is invalid.</summary>
    public const string InvalidArgument = "InvalidArgument";

    /// <summary>An https parameter file could not be downloaded.</summary>
    public const string ParameterDownloadFailed = "ParameterDownloadFailed";

    /// <summary><c>earm auth</c>: the sign-in or the token request failed.</summary>
    public const string AuthenticationFailed = "AuthenticationFailed";

    /// <summary>The preflight (sign-in, tenant, <c>GET /organization</c>) failed.</summary>
    public const string PreflightFailed = "PreflightFailed";

    /// <summary>The preflight found requested delegated scopes missing from the token and consent was not completed.</summary>
    public const string ConsentRequired = "ConsentRequired";

    /// <summary>A resource type of the template has no registered provider.</summary>
    public const string UnknownProvider = "UnknownProvider";

    /// <summary>The confirmation prompt of <c>deployment create</c> or <c>deployment delete</c> was declined.</summary>
    public const string NotConfirmed = "NotConfirmed";

    /// <summary><c>--secure-outputs-file</c> cannot be written safely.</summary>
    public const string SecureOutputsFileInvalid = "SecureOutputsFileInvalid";
}
