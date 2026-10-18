using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine;
using EArm.Engine.Providers;

namespace EArm.Providers;

/// <summary>
/// <c>Microsoft.Entra/applications/keyCredentials</c>: a certificate credential (public X.509 certificate) of an application. The resource name is
/// <c>&lt;application uniqueName&gt;/&lt;credential displayName&gt;</c>; the credential is found among the application's <c>keyCredentials</c> by its exact
/// <c>displayName</c> (the stateless key, like <c>passwordCredentials</c>), and the certificate's SHA-1 thumbprint decides whether the live one is the declared
/// one. Private keys are never handled: <c>key</c> is the public certificate (base64 DER or PEM), and a PEM private key or a PFX is refused.
/// <para>
/// Graph has no single-credential add for certificates without proof of possession (<c>addKey</c> needs a JWT signed with an existing key), so EARM writes the
/// whole collection with <c>PATCH /applications/{id} { "keyCredentials": [...] }</c> (Learn: applications-how-to-add-certificate: "Because this is a PATCH call
/// ... including only the new certificate replaces the existing certificates"). Every write is read-modify-write of the full collection, ensure-only:
/// <list type="bullet">
/// <item><b>Read</b>: <c>GET /applications/{parentId}?$select=id,keyCredentials</c> (or the <c>uniqueName</c> alternate key); the single-object read is the only
/// one that returns the certificate bytes (<c>key</c>), from which the thumbprint is computed. A 404 is "does not exist" (parent and credential alike).</item>
/// <item><b>Create</b> / <b>Update</b> (replacement): under a per-application gate, re-read the collection, keep every entry whose <c>displayName</c> differs
/// (sent back as read, with its <c>key</c>), add the declared entry in place of the same-name one, <c>PATCH</c>, then re-read to publish the new <c>keyId</c>.
/// Nothing is written when an unmanaged entry came back without its <c>key</c> (re-sending it would lose that certificate).</item>
/// <item><b>Delete</b>: the same read-modify-write without the same-name entries; a missing application or credential is "already gone".</item>
/// </list>
/// </para>
/// <para>
/// <see cref="Plan"/>: <c>Create</c> when no same-name credential exists; <c>NoChange</c> when the thumbprint, <c>type</c>, <c>usage</c> and the declared dates
/// match; otherwise <c>Modify</c> (the entry is replaced in one PATCH). Two or more live credentials with the same <c>displayName</c>, a parent application
/// that also declares its own <c>keyCredentials</c>, an invalid certificate, a <c>type</c> other than <c>AsymmetricX509Cert</c> and a <c>usage</c> other than
/// <c>Verify</c> (<c>Sign</c> needs the private key) are refused (unsupported rows). Service principals are out of scope: their certificates sign SAML tokens and need the private key.
/// </para>
/// </summary>
[EntraProvider(ResourceType)]
public sealed partial class KeyCredentialsProvider : ProviderBase, IOrphanCandidateSource
{
    public const string ResourceType = "Microsoft.Entra/applications/keyCredentials";

    private const string ParentKeyProperty = "uniqueName";

    /// <summary>The live same-name credentials seen by the last read, per (parent, name): more than one is an ambiguous key.</summary>
    private readonly ConcurrentDictionary<string, IReadOnlyList<KeyCredentialSummary>> _sameName = new(StringComparer.Ordinal);

    /// <summary>One gate per parent application id: two credentials of one application never PATCH the collection at the same time.</summary>
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _parentWriteGates = new(StringComparer.OrdinalIgnoreCase);

    public KeyCredentialsProvider(ResourceTypeDefinition definition, ResourceTypeCatalog? catalog = null)
        : base(definition, catalog)
    {
        if (!ResourceIdentifier.TypeEquals(definition.Type, ResourceType))
        {
            throw new ArgumentException($"{nameof(KeyCredentialsProvider)} implements '{ResourceType}', not '{definition.Type}'.", nameof(definition));
        }
    }

    public KeyCredentialsProvider(ResourceTypeCatalog catalog)
        : this(RequireDefinition(catalog), catalog)
    {
    }

    /// <summary>The collection PATCH is idempotent (the same-name entry is replaced, not appended), so a create with an unknown outcome can be repeated.</summary>
    public override bool CreateIsRepeatable => true;

    /// <summary>Same rule as the applications provider: <c>Application.ReadWrite.OwnedBy</c> when the parent is declared in the template, else <c>Application.ReadWrite.All</c>.</summary>
    public override IReadOnlyList<string> RequiredPermissions(ResourceInstance? desired)
    {
        return desired?.Parent is not null ? [ApplicationsProvider.OwnedByPermission] : [ApplicationsProvider.ReadWriteAllPermission];
    }

    // ---- read ----

    public override async Task<ExistingResource?> ReadAsync(ResourceKey key, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(graph);
        string name = NameOf(key);
        string? parentUniqueName = ParentUniqueName(key);
        string snapshotKey = SnapshotKey(key.ParentGraphId, parentUniqueName, name);

        var parent = await ReadParentAsync(graph, ParentPath(key.ParentGraphId, parentUniqueName), key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        if (parent is null)
        {
            _sameName.TryRemove(snapshotKey, out _);
            return null;
        }

        var sameName = KeyCredentialsRules.SameName(KeyCredentialsRules.Summarize(parent[KeyCredentialsRules.KeyCredentialsProperty]), name);
        _sameName[snapshotKey] = sameName;
        return sameName.Count == 0 ? null : Existing(sameName[0]);
    }

    // ---- create / update ----

    public override Task<AppliedResource> CreateAsync(ResourceInstance desired, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(graph);
        return WriteAsync(desired, graph, cancellationToken);
    }

    /// <summary>The replacement: one PATCH of the collection with the declared entry in place of the live one.</summary>
    public override Task<AppliedResource> UpdateAsync(ResourceInstance desired, ExistingResource existing, ResourceChange change, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(desired);
        ArgumentNullException.ThrowIfNull(existing);
        ArgumentNullException.ThrowIfNull(change);
        ArgumentNullException.ThrowIfNull(graph);
        return WriteAsync(desired, graph, cancellationToken);
    }

    // ---- delete ----

    public override async Task DeleteAsync(ResourceKey key, ExistingResource live, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(live);
        ArgumentNullException.ThrowIfNull(graph);
        string name = key.TryGet(KeyCredentialsRules.DisplayNameProperty, out var keyed) ? keyed
            : JsonObjects.GetString(live.Raw, KeyCredentialsRules.DisplayNameProperty)
            ?? throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: neither the key {key.Describe()} nor the live credential {live.GraphId} carries a displayName.");
        var parent = await ReadParentAsync(graph, ParentPath(key.ParentGraphId, ParentUniqueName(key)), key.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
        if (parent is null || JsonObjects.GetId(parent) is not { } parentId)
        {
            // The application is gone (deleted outside EARM, or by this pass): its credentials went with it.
            return;
        }

        await RemoveAsync(graph, parentId, name, key.Api, cancellationToken).ConfigureAwait(false);
    }

    // ---- paths ----

    /// <summary><c>/applications/{id}</c> when the parent's Graph id is known, otherwise <c>/applications(uniqueName='…')</c> (reads only).</summary>
    private string ParentPath(string? parentGraphId, string? parentUniqueName)
    {
        if (parentGraphId is not null)
        {
            return ParentPathById(parentGraphId);
        }

        if (parentUniqueName is not null)
        {
            return GraphPath.AlternateKey(CollectionPath, ParentKeyProperty, parentUniqueName);
        }

        throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the parent application is not known (neither its Graph id nor its uniqueName).");
    }

    private string ParentPathById(string parentGraphId)
    {
        return CollectionPath + "/" + GraphPath.Segment(parentGraphId);
    }

    private static string SnapshotKey(string? parentGraphId, string? parentUniqueName, string name)
    {
        string parent = parentGraphId is not null ? "id=" + parentGraphId : ParentKeyProperty + "=" + (parentUniqueName ?? string.Empty);
        return parent + "|" + name;
    }

    private static string? ParentUniqueName(ResourceKey key)
    {
        return key.ParentKey is { } parentKey && parentKey.TryGet(ParentKeyProperty, out var unique) ? unique : null;
    }

    private string NameOf(ResourceKey key)
    {
        return key.TryGet(KeyCredentialsRules.DisplayNameProperty, out var name) ? name
            : throw new ProviderException(EngineDiagnosticCodes.ResourceKeyUnresolved, $"{TypeName}: the key {key.Describe()} carries no credential displayName.");
    }

    // ---- helpers ----

    private static ResourceTypeDefinition RequireDefinition(ResourceTypeCatalog catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        return catalog.Find(ResourceType)
            ?? throw new InvalidOperationException($"The catalog does not define '{ResourceType}'.");
    }

    /// <summary>A live credential wrapped for the engine: Graph id = <c>keyId</c>, the diff view, and the reference view.</summary>
    private static ExistingResource Existing(KeyCredentialSummary credential)
    {
        return new ExistingResource(credential.KeyId, KeyCredentialsRules.DiffView(credential), KeyCredentialsRules.LiveView(credential));
    }

    private DesiredKeyCredential ParseDesired(ResourceInstance desired)
    {
        if (desired.Properties is not { } properties)
        {
            throw new InvalidOperationException($"{desired.Describe()}: properties must be evaluated before applying.");
        }

        string? problem = KeyCredentialsRules.DescribeDesired(properties, out var parsed);
        if (problem is not null || parsed is null)
        {
            throw new ProviderException(EngineDiagnosticCodes.ResourceApplyFailed, $"{desired.Describe()}: {problem ?? "the properties could not be interpreted."}", desired.Id);
        }

        return parsed;
    }

    // ---- orphan report ----

    /// <summary>The orphan report lists the certificate credentials of the applications the template declares (Graph id = <c>keyId</c>).</summary>
    public OrphanScope OrphanScope => OrphanScope.ChildrenOfDeclaredParents;

    public string? OrphanParentType => Definition.ParentType;

    public async Task<IReadOnlyList<OrphanCandidate>> ListOrphanCandidatesAsync(OrphanQuery query, IGraphClient graph, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(graph);
        var candidates = new List<OrphanCandidate>();
        foreach (var parent in query.Parents)
        {
            var live = await ReadParentAsync(graph, ParentPathById(parent.GraphId), query.Api, RetryHint.Default, cancellationToken).ConfigureAwait(false);
            foreach (var credential in KeyCredentialsRules.Summarize(live?[KeyCredentialsRules.KeyCredentialsProperty]))
            {
                string name = credential.DisplayName is { Length: > 0 } displayName ? displayName : credential.KeyId;
                candidates.Add(new OrphanCandidate(credential.KeyId, name, parent.Name, credential.Thumbprint is null ? null : $"thumbprint {credential.Thumbprint}"));
            }
        }

        return candidates;
    }
}
