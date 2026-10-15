using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Graph.Auth;

namespace EArm.TestSupport;

/// <summary>
/// An <see cref="IGraphTokenProvider"/> that issues unsigned JWTs with chosen claims (tenant, object id, user name or app id, scopes or roles), or fails
/// with the sign-in guidance. Counts the calls and the invalidations.
/// </summary>
public sealed class FakeJwtTokenProvider : IGraphTokenProvider
{
    private readonly object _gate = new();
    private int _generation = 1;

    public string TenantId { get; set; } = "11111111-1111-1111-1111-111111111111";

    public string ObjectId { get; set; } = "99999999-0000-4000-8000-000000000001";

    public string? UserPrincipalName { get; set; } = "alice@contoso.example";

    public string? AppId { get; set; }

    public string[] Scopes { get; set; } = ["Application.ReadWrite.All"];

    public string[] Roles { get; set; } = [];

    /// <summary>When set, <see cref="GetTokenAsync"/> throws it (typically a <see cref="GraphTransportException"/> of kind NotAuthenticated).</summary>
    public Exception? Failure { get; set; }

    public List<string> RequestedScopes { get; } = [];

    public int Invalidations { get; private set; }

    /// <summary>A provider that is not signed in: the failure carries the same guidance as the real one.</summary>
    public static FakeJwtTokenProvider NotSignedIn()
    {
        return new FakeJwtTokenProvider
        {
            Failure = new GraphTransportException(GraphTransportFailure.NotAuthenticated, GraphAuthGuidance.NotSignedIn(GraphAuthMode.Auto, "https://graph.microsoft.com/.default", null)),
        };
    }

    public ValueTask<GraphToken> GetTokenAsync(string scope, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            RequestedScopes.Add(scope);
            if (Failure is not null)
            {
                throw Failure;
            }

            return ValueTask.FromResult(new GraphToken(BuildJwt(), DateTimeOffset.UtcNow.AddHours(1)));
        }
    }

    public void Invalidate(string scope)
    {
        lock (_gate)
        {
            Invalidations++;
            _generation++;
        }
    }

    private string BuildJwt()
    {
        var claims = new System.Text.Json.Nodes.JsonObject
        {
            ["tid"] = TenantId,
            ["oid"] = ObjectId,
            ["jti"] = "fake-" + _generation,
            ["exp"] = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds(),
        };
        if (UserPrincipalName is not null)
        {
            claims["upn"] = UserPrincipalName;
        }

        if (AppId is not null)
        {
            claims["appid"] = AppId;
            claims["idtyp"] = "app";
        }

        if (Scopes.Length > 0)
        {
            claims["scp"] = string.Join(' ', Scopes);
        }

        if (Roles.Length > 0)
        {
            var roles = new System.Text.Json.Nodes.JsonArray();
            foreach (var role in Roles)
            {
                roles.Add(System.Text.Json.Nodes.JsonValue.Create(role));
            }

            claims["roles"] = roles;
        }

        return Encode("{\"alg\":\"none\",\"typ\":\"JWT\"}") + "." + Encode(claims.ToJsonString()) + ".fake-signature";
    }

    private static string Encode(string text)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(text)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }
}
