using System;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;

namespace EArm.Integration.Tests;

/// <summary>Creates tenant objects directly through Graph (hand-made objects for the adopt scenarios and the probes); every object is tracked for the cleanup guard.</summary>
internal static class TenantObjects
{
    /// <summary>An application created the way the provider creates one: <c>PATCH /applications(uniqueName='…')</c> with <c>Prefer: create-if-missing</c>. Its uniqueName and displayName are <c>&lt;prefix&gt;-&lt;suffix&gt;</c>.</summary>
    public static async Task<JsonObject> CreateApplicationAsync(IntegrationFixture fixture, string suffix, JsonObject? extra, CancellationToken cancellationToken)
    {
        string name = fixture.Name(suffix);
        var body = new JsonObject { ["displayName"] = name, ["signInAudience"] = "AzureADMyOrg" };
        if (extra is not null)
        {
            foreach (var pair in extra)
            {
                body[pair.Key] = pair.Value?.DeepClone();
            }
        }

        var request = new GraphRequest(GraphPath.AlternateKey("/applications", "uniqueName", name)).WithPrefer("create-if-missing");
        var response = await fixture.Graph.PatchAsync(request, body, cancellationToken);
        var application = response.Body as JsonObject;
        if (application?.Text("id") is null)
        {
            application = await fixture.Graph.GetObjectAsync(new GraphRequest(GraphPath.AlternateKey("/applications", "uniqueName", name)), cancellationToken)
                ?? throw new InvalidOperationException($"Application '{name}' does not exist right after create-if-missing (status {response.Status}).");
        }

        fixture.Track("/applications/" + application.RequireId());
        return application;
    }

    /// <summary>
    /// A disabled user <c>&lt;prefix&gt;-&lt;suffix&gt;@&lt;domain&gt;</c> created with POST /users (no license, a random password that is never kept; it never
    /// signs in). Needs <c>Require(Needs.Domain)</c>.
    /// </summary>
    public static async Task<JsonObject> CreateDisabledUserAsync(IntegrationFixture fixture, string suffix, CancellationToken cancellationToken)
    {
        string name = fixture.Name(suffix);
        var user = await fixture.Graph.CreateObjectAsync(
            "/users",
            new JsonObject
            {
                ["accountEnabled"] = false,
                ["displayName"] = name,
                ["mailNickname"] = name,
                ["userPrincipalName"] = fixture.Upn(suffix),
                ["passwordProfile"] = new JsonObject { ["password"] = "Aa1!" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)), ["forceChangePasswordNextSignIn"] = true },
            },
            cancellationToken);
        fixture.Track("/users/" + user.RequireId());
        return user;
    }

    /// <summary>A security group <c>&lt;prefix&gt;-&lt;suffix&gt;</c> created with POST /groups.</summary>
    public static async Task<JsonObject> CreateGroupAsync(IntegrationFixture fixture, string suffix, CancellationToken cancellationToken, string? displayName = null)
    {
        string name = fixture.Name(suffix);
        var group = await fixture.Graph.CreateObjectAsync(
            "/groups",
            new JsonObject
            {
                ["displayName"] = displayName ?? name,
                ["mailEnabled"] = false,
                ["mailNickname"] = name,
                ["securityEnabled"] = true,
            },
            cancellationToken);
        fixture.Track("/groups/" + group.RequireId());
        return group;
    }
}
