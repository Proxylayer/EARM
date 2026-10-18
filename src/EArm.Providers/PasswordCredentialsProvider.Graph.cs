using System;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core.Secrets;
using EArm.Engine;
using EArm.Engine.Providers;
using GraphApi = EArm.Graph.GraphApi;

namespace EArm.Providers;

/// <summary>The Graph calls of <see cref="PasswordCredentialsProvider"/>: <c>addPassword</c> (with the ambiguous-write check), <c>removePassword</c> and the parent application lookups.</summary>
public sealed partial class PasswordCredentialsProvider
{
    // ---- Graph helpers ----

    /// <summary><c>POST {parent}/addPassword</c> named <c>&lt;name&gt;[#&lt;rotationTag&gt;]</c>; the created credential and its live view (secret leaf inside).</summary>
    private async Task<(PasswordCredentialSummary Created, JsonObject Live)> AddAsync(ResourceInstance desired, DesiredPasswordCredential parsed, string parentId, IGraphClient graph, CancellationToken cancellationToken)
    {
        string name = desired.Id.NameLeaf;
        string liveName = PasswordCredentialsRules.LiveDisplayName(name, parsed.RotationTag);
        var endDateTime = parsed.EndDateTime ?? PasswordCredentialsRules.DefaultEndDateTime(Clock.GetUtcNow());
        var body = PasswordCredentialsRules.AddPasswordBody(liveName, endDateTime, parsed.StartDateTime);
        var request = new GraphRequest(ParentPathById(parentId) + "/" + AddPasswordAction, desired.Api, hint: desired.RetryHint);

        GraphResponse response;
        try
        {
            response = await graph.PostAsync(request, body, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsAmbiguousWrite(ex))
        {
            // The credential may exist although no answer came back, and the secret was lost with the answer: the call is never repeated
            // blindly. Report any same-name credential EARM did not record so the user can remove it; nothing is deleted automatically.
            throw await DescribeAmbiguousAddAsync(desired, parentId, name, ex, graph, cancellationToken).ConfigureAwait(false);
        }

        if (response.Body is not JsonObject result)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: addPassword returned no credential object.", desired.Id);
        }

        // The response is the only copy of the secret: it is lifted into a secret leaf and the plain text is not kept.
        string secretText = JsonObjects.GetString(result, LiveSecrets.SecretTextProperty)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: addPassword returned no secretText.", desired.Id);
        result.Remove(JsonObjects.FindKey(result, LiveSecrets.SecretTextProperty)!);

        var created = PasswordCredentialsRules.SummaryOf(result)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: addPassword returned no keyId.", desired.Id);
        if (created.EndDateTime is null)
        {
            created = created with { EndDateTime = endDateTime };
        }

        if (!string.Equals(created.DisplayName, liveName, StringComparison.Ordinal))
        {
            created = created with { DisplayName = liveName };
        }

        var live = PasswordCredentialsRules.LiveView(created, new SecretValue(secretText));
        return (created, live);
    }

    private static bool IsAmbiguousWrite(Exception exception)
    {
        return exception is GraphException { IsAmbiguous: true } or GraphTransportException { Ambiguous: true };
    }

    /// <summary>
    /// After an ambiguous <c>addPassword</c> failure: lists the application's same-name credentials and reports the ones this run did not
    /// know about (not in the last read's snapshot) by <c>keyId</c>. The Graph answer that carried the secret is lost, so such a credential
    /// is an orphan (its secret is unrecoverable): it is reported, never removed.
    /// </summary>
    private async Task<ProviderException> DescribeAmbiguousAddAsync(ResourceInstance desired, string parentId, string name, Exception cause, IGraphClient graph, CancellationToken cancellationToken)
    {
        string text = $"{desired.Describe()}: addPassword failed without a usable answer ({cause.Message}); the request is not repeated because the credential may have been created and its secret is only returned once.";
        try
        {
            var parent = await ReadParentAsync(graph, ParentPathById(parentId), desired.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
            var known = _sameName.TryGetValue(SnapshotKey(desired.ParentGraphId, desired.Id.Parent?.NameLeaf, name), out var snapshot) ? snapshot : [];
            var orphans = PasswordCredentialsRules.SameName(PasswordCredentialsRules.Summarize(parent?[PasswordCredentialsRules.PasswordCredentialsProperty]), name)
                .Where(c => !known.Any(k => PasswordCredentialsRules.KeyIdEquals(k.KeyId, c.KeyId)))
                .ToList();
            if (orphans.Count > 0)
            {
                string ids = string.Join(", ", orphans.Select(o => $"keyId {o.KeyId} (hint '{o.Hint}', expires {(o.EndDateTime is { } end ? PasswordCredentialsRules.Format(end) : "?")})"));
                text += $" {ProviderWarnings.PasswordCredentialOrphaned}: the application now holds {orphans.Count} credential(s) named '{name}' that EARM did not record: {ids}. Their secrets are lost; remove them with POST /applications/{parentId}/removePassword {{ \"keyId\": \"…\" }} (EARM never deletes them automatically), then run the deployment again.";
            }
            else
            {
                text += " No unrecorded credential named '" + name + "' was found, so the deployment can simply be run again.";
            }
        }
        catch (Exception reconcile) when (reconcile is GraphException or GraphTransportException)
        {
            text += $" The credentials could not be listed to check for a duplicate ({reconcile.Message}); before running again, look for an extra credential named '{name}' on application {parentId}.";
        }

        return new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, text, desired.Id, cause);
    }

    /// <summary><c>POST {parent}/removePassword { keyId }</c>; an unknown key id (404, or Graph's 400) counts as removed.</summary>
    private static async Task RemoveAsync(IGraphClient graph, string parentPath, string keyId, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(parentPath + "/" + RemovePasswordAction, api, hint: hint);
        try
        {
            await graph.PostAsync(request, PasswordCredentialsRules.RemovePasswordBody(keyId), cancellationToken).ConfigureAwait(false);
        }
        catch (GraphException ex) when (PasswordCredentialsRules.IsUnknownKeyId(ex))
        {
            // Already gone (removed outside EARM, or never existed under this application).
        }
    }

    /// <summary><c>GET {parent}?$select=id,passwordCredentials</c>; <c>null</c> when the application does not exist.</summary>
    private static async Task<JsonObject?> ReadParentAsync(IGraphClient graph, string parentPath, GraphApi api, RetryHint hint, CancellationToken cancellationToken)
    {
        var request = new GraphRequest(parentPath, api, hint: hint).WithSelect(PasswordCredentialsRules.ParentSelect);
        return await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) as JsonObject;
    }

    /// <summary>The parent's object id: known from the engine (applied in this run, or read while planning) or looked up by <c>uniqueName</c>.</summary>
    private async Task<string> ResolveParentIdAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        if (desired.ParentGraphId is not null)
        {
            return desired.ParentGraphId;
        }

        if (desired.Id.Parent is not { } parentId)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{desired.Describe()}: the name must be '<application uniqueName>/<credential displayName>'.", desired.Id);
        }

        var request = new GraphRequest(GraphPath.AlternateKey(CollectionPath, ParentKeyProperty, parentId.NameLeaf), desired.Api, hint: desired.RetryHint).WithSelect("id");
        var parent = await graph.GetAsync(request, cancellationToken).ConfigureAwait(false) as JsonObject;
        return JsonObjects.GetId(parent)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: application '{parentId.NameLeaf}' does not exist, so no password credential can be added to it.", desired.Id);
    }

    private ResourceKey KeyWithParent(ResourceInstance desired, string parentId)
    {
        return ResolveKey(desired) with { ParentGraphId = parentId };
    }
}
