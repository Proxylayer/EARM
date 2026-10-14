using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EntraArm.Az;

namespace EntraArm.TestSupport;

/// <summary>
/// An in-process <see cref="IAzRunner"/> for the non-rest az commands the engine issues itself
/// (<c>az keyvault secret show</c>, <c>az account show</c>, <c>az version</c>): scripted responses keyed by the
/// command words, plus a Key Vault secret table. Unknown commands fail like az does (exit 1, an <c>ERROR:</c> line
/// on stderr). Every request is recorded.
/// </summary>
public sealed class FakeAzRunner : IAzRunner
{
    private readonly object _gate = new();
    private readonly List<AzRequest> _requests = [];
    private readonly Dictionary<string, AzResult> _responses = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Secrets by <c>vault/secret</c> (optionally <c>vault/secret/version</c>).</summary>
    public Dictionary<string, string> Secrets { get; } = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<AzRequest> Requests
    {
        get
        {
            lock (_gate)
            {
                return _requests.ToList();
            }
        }
    }

    /// <summary>Adds a secret that <c>az keyvault secret show</c> returns.</summary>
    public FakeAzRunner WithSecret(string vaultName, string secretName, string value, string? version = null)
    {
        Secrets[Key(vaultName, secretName, version)] = value;
        return this;
    }

    /// <summary>Scripts the outcome of a command (words joined by spaces, e.g. <c>account show</c>).</summary>
    public FakeAzRunner WithResponse(string commandText, AzResult result)
    {
        _responses[commandText] = result;
        return this;
    }

    public Task<AzResult> RunAsync(AzRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        lock (_gate)
        {
            _requests.Add(request);
        }

        if (_responses.TryGetValue(request.CommandText, out var scripted))
        {
            return Task.FromResult(scripted);
        }

        if (request.CommandText.Equals("keyvault secret show", StringComparison.OrdinalIgnoreCase))
        {
            return Task.FromResult(ShowSecret(request));
        }

        return Task.FromResult(new AzResult(1, string.Empty, $"ERROR: '{request.CommandText}' is not scripted in FakeAzRunner.", TimeSpan.Zero, false));
    }

    private AzResult ShowSecret(AzRequest request)
    {
        string? vault = Argument(request, "--vault-name");
        string? name = Argument(request, "--name");
        string? version = Argument(request, "--version");
        if (vault is null || name is null)
        {
            return new AzResult(2, string.Empty, "ERROR: the following arguments are required: --vault-name, --name", TimeSpan.Zero, false);
        }

        if (!Secrets.TryGetValue(Key(vault, name, version), out var value) && !(version is not null && Secrets.TryGetValue(Key(vault, name, null), out value)))
        {
            return new AzResult(3, string.Empty, $"ERROR: (SecretNotFound) A secret with (name/id) {name} was not found in this key vault. If you recently deleted this secret you may be able to recover it using the correct recovery command.", TimeSpan.Zero, false);
        }

        bool queryValue = Argument(request, "--query") == "value";
        JsonNode output = queryValue
            ? JsonValue.Create(value)!
            : new JsonObject { ["value"] = value, ["name"] = name, ["id"] = $"https://{vault}.vault.azure.net/secrets/{name}/{version ?? "0000"}" };
        return new AzResult(0, output.ToJsonString() + Environment.NewLine, string.Empty, TimeSpan.Zero, false);
    }

    private static string? Argument(AzRequest request, string name)
    {
        var arguments = request.Arguments;
        if (arguments is null)
        {
            return null;
        }

        for (int i = 0; i < arguments.Count - 1; i++)
        {
            if (arguments[i].Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return arguments[i + 1];
            }
        }

        return null;
    }

    private static string Key(string vault, string secret, string? version)
    {
        return version is null ? vault + "/" + secret : vault + "/" + secret + "/" + version;
    }
}
