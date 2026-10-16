using System;
using System.Collections.Generic;
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

    public IReadOnlyList<string> Scopes { get; init; } = [];

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
            Scopes = decoded ? claims.Scopes : [],
            Roles = decoded ? claims.Roles : [],
            ExpiresOn = decoded ? claims.ExpiresOn ?? token.ExpiresOn : token.ExpiresOn,
            SignInRecord = record is null ? null : store.FilePath,
            SignInAccount = record?.Username,
        };
    }
}
