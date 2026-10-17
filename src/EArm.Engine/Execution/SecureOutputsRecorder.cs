using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Engine.Providers;
using Microsoft.Extensions.Logging;

namespace EArm.Engine.Execution;

/// <summary>
/// The secure outputs file of a deployment run: the one-time secrets saved the moment the object that returned them is applied, and the secure
/// outputs merged in at the end of the run.
/// </summary>
internal sealed class SecureOutputsRecorder
{
    private readonly string? _path;
    private readonly RunDiagnostics _diagnostics;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _secretsFileGate = new(1, 1);
    private bool _oneTimeSecretsWritten;

    /// <param name="path">The secure outputs file; nothing is written when it is not set.</param>
    public SecureOutputsRecorder(string? path, RunDiagnostics diagnostics, ILogger logger)
    {
        _path = path;
        _diagnostics = diagnostics;
        _logger = logger;
    }

    /// <summary>
    /// When a secure outputs file is configured, writes the one-time secrets of the object that was just applied (a password
    /// credential's <c>secretText</c>, a generated user password) to it at once, under <c>oneTimeSecrets</c> keyed by the
    /// resource id. Graph returns them exactly once: a crash, a Ctrl-C or a failure later in the run must not lose them (E-03d).
    /// </summary>
    public async Task PersistOneTimeSecretsAsync(ResourceInstance instance, AppliedResource applied)
    {
        string? path = _path;
        if (string.IsNullOrWhiteSpace(path) || ExtractOneTimeSecrets(applied) is not { } secrets)
        {
            return;
        }

        await _secretsFileGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            SecureOutputsFile.MergeOneTimeSecret(path, instance.Id.Id, secrets);
            _oneTimeSecretsWritten = true;
            EngineLog.OneTimeSecretSaved(_logger, instance.Id.Id);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            string message = SecretRegistration.Scrub(ex.Message);
            EngineLog.SecureOutputsSaveFailed(_logger, message);
            string detail = ex is SecureOutputsPendingException ? string.Empty : ". It cannot be read from Graph again: rotate the credential (change rotationTag) to obtain a new one.";
            _diagnostics.AddForInstance(instance, Diagnostic.Error(EngineDiagnosticCodes.SecureOutputsWriteFailed, $"The one-time secret of '{instance.Id.Id}' could not be saved to the secure outputs file '{path}': {message}{detail}"));
        }
        finally
        {
            _secretsFileGate.Release();
        }
    }

    private static JsonObject? ExtractOneTimeSecrets(AppliedResource applied)
    {
        var secrets = new JsonObject();
        if (JsonObjects.TryGetIgnoreCase(applied.Live, LiveSecrets.SecretTextProperty, out var secretText) && SecretJson.IsSecret(secretText, out var secret) && secret.Kind == JsonValueKind.String)
        {
            secrets["secretText"] = secret.Value;
            if (applied.Extra is { } extra && JsonObjects.GetString(extra, "keyId") is { } keyId)
            {
                secrets["keyId"] = keyId;
            }
        }

        bool generated = applied.Extra is { } generatedExtra
            && JsonObjects.TryGetIgnoreCase(generatedExtra, "generatedPassword", out var flag)
            && flag is JsonValue flagValue
            && flagValue.TryGetValue<bool>(out bool isGenerated)
            && isGenerated;
        if (generated
            && JsonObjects.TryGetIgnoreCase(applied.Live, "passwordProfile", out var profile)
            && profile is JsonObject profileObject
            && JsonObjects.TryGetIgnoreCase(profileObject, "password", out var password)
            && SecretJson.IsSecret(password, out var passwordSecret)
            && passwordSecret.Kind == JsonValueKind.String)
        {
            secrets["generatedPassword"] = passwordSecret.Value;
        }

        return secrets.Count == 0 ? null : secrets;
    }

    /// <summary>
    /// Merges the secure outputs into the secure outputs file (it may already hold one-time secrets of this run or earlier runs, which
    /// stay). Writes nothing when there are no secure values (E-06). A failure to write is an error diagnostic, never a crash (E-03).
    /// </summary>
    public string? WriteSecureOutputs(JsonObject secureOutputs)
    {
        string? path = _path;
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            if (SecureOutputsFile.MergeOutputs(path, secureOutputs))
            {
                EngineLog.SecureOutputsWritten(_logger, path);
                return path;
            }

            return _oneTimeSecretsWritten ? path : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            string message = SecretRegistration.Scrub(ex.Message);
            EngineLog.SecureOutputsSaveFailed(_logger, message);
            string detail = ex is SecureOutputsPendingException ? string.Empty : ". The values were not saved anywhere; run the deployment again with a writable --secure-outputs-file (secure outputs of resources that already exist are available again; one-time secrets are not).";
            _diagnostics.Add(Diagnostic.Error(EngineDiagnosticCodes.SecureOutputsWriteFailed, $"The secure outputs could not be written to '{path}': {message}{detail}"));
            return null;
        }
    }
}
