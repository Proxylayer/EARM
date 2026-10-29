using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace EArm.Graph.Auth;

/// <summary>
/// What <c>earm auth status</c> shows: the configured mode and who the current token says it belongs to. The claims are decoded without
/// validation and are for display only; the token is never kept or printed.
/// </summary>
public sealed record GraphAuthStatus
{
    public required string Mode { get; init; }

    /// <summary>What the mode resolves to on this machine: <c>interactive sign-in (record)</c> or <c>default credential chain</c>.</summary>
    public required string Credential { get; init; }

    public required string Cloud { get; init; }

    public string? TenantId { get; init; }

    public string? PrincipalType { get; init; }

    public string? Principal { get; init; }

    public string? ObjectId { get; init; }

    public string? AppId { get; init; }

    /// <summary>The client (application) the token was issued to: the token's <c>appid</c>/<c>azp</c>, else the client of the stored sign-in.</summary>
    public string? ClientId { get; init; }

    /// <summary>The display name of <see cref="ClientId"/> when it is one of the known Microsoft apps (<see cref="PublicClients.NameOf"/>).</summary>
    public string? ClientName => PublicClients.NameOf(ClientId);

    /// <summary>The delegated scopes of the token (<c>scp</c>).</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary>The delegated Graph scopes a template run requires (<c>auth status -f</c>); null when no template was given.</summary>
    public IReadOnlyList<string>? RequiredScopes { get; init; }

    /// <summary>The <see cref="RequiredScopes"/> the token's <c>scp</c> does not carry (case-insensitive); empty when none are missing or no template was given.</summary>
    public IReadOnlyList<string> MissingScopes { get; init; } = [];

    /// <summary>True when the template run also needs the Key Vault consent (<see cref="GraphCloud.KeyVaultConsentScopes"/>).</summary>
    public bool KeyVaultNeeded { get; init; }

    /// <summary>True when the template run also needs the Azure Resource Manager consent (<see cref="GraphCloud.ResourceManagerConsentScopes"/>).</summary>
    public bool ResourceManagerNeeded { get; init; }

    public IReadOnlyList<string> Roles { get; init; } = [];

    public DateTimeOffset? ExpiresOn { get; init; }

    /// <summary>Path of the interactive sign-in record, when one exists.</summary>
    public string? SignInRecord { get; init; }

    /// <summary>The account of the interactive sign-in record (user name), when one exists.</summary>
    public string? SignInAccount { get; init; }

    /// <summary>Acquires a Graph token through <paramref name="tokens"/> and describes it.</summary>
    /// <exception cref="GraphTransportException">No credential produced a token.</exception>
    public static async Task<GraphAuthStatus> ReadAsync(IGraphTokenProvider tokens, GraphAuthOptions options, AuthRecordStore store, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(store);

        var record = store.TryLoad();
        var credential = options.Mode switch
        {
            GraphAuthMode.Default => "default credential chain (environment, workload identity, managed identity)",
            GraphAuthMode.Interactive => "interactive sign-in",
            _ => record is null ? "default credential chain (environment, workload identity, managed identity)" : "interactive sign-in, then the default chain",
        };

        var token = await tokens.GetTokenAsync(options.Cloud.GraphScopes, cancellationToken).ConfigureAwait(false);
        var decoded = TokenClaims.TryDecode(token.Value, out var claims);
        return new GraphAuthStatus
        {
            Mode = GraphAuthOptions.ModeName(options.Mode),
            Credential = credential,
            Cloud = options.Cloud.Key,
            TenantId = decoded ? claims.TenantId : null,
            PrincipalType = decoded ? claims.PrincipalType : null,
            Principal = decoded ? claims.PrincipalName : null,
            ObjectId = decoded ? claims.ObjectId : null,
            AppId = decoded ? claims.AppId : null,
            ClientId = (decoded ? claims.AppId : null) ?? (record is null || options.Mode == GraphAuthMode.Default ? null : GraphAuthFactory.InteractiveClientId(options, record)),
            Scopes = decoded ? claims.Scopes : [],
            Roles = decoded ? claims.Roles : [],
            ExpiresOn = decoded ? claims.ExpiresOn ?? token.ExpiresOn : token.ExpiresOn,
            SignInRecord = record is null ? null : store.FilePath,
            SignInAccount = record?.Username,
        };
    }

    /// <summary>This status compared with the delegated scopes a run requires: <see cref="MissingScopes"/> are the required names absent from <c>scp</c>.</summary>
    public GraphAuthStatus WithRequired(IReadOnlyList<string> requiredScopes, bool keyVaultNeeded, bool resourceManagerNeeded)
    {
        ArgumentNullException.ThrowIfNull(requiredScopes);
        var granted = new HashSet<string>(Scopes, StringComparer.OrdinalIgnoreCase);
        return this with
        {
            RequiredScopes = requiredScopes,
            MissingScopes = requiredScopes.Where(scope => !granted.Contains(scope)).ToList(),
            KeyVaultNeeded = keyVaultNeeded,
            ResourceManagerNeeded = resourceManagerNeeded,
        };
    }
}
