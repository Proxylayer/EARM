using System;
using System.Collections.Generic;
using System.CommandLine;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using EArm.Cli.Hosting;
using EArm.Core;
using EArm.Core.Types;
using EArm.Engine.Providers;
using EArm.Engine.State;
using EArm.Providers;

namespace EArm.Cli.Commands;

/// <summary>What <c>state adopt</c> was given.</summary>
internal sealed record AdoptRequest
{
    // pin form
    public string? ResourceId { get; init; }

    public string? ObjectId { get; init; }

    public string? ParentObjectId { get; init; }

    public IReadOnlyList<string> Keys { get; init; } = [];

    // uniqueName form
    public string? Type { get; init; }

    public string? Id { get; init; }

    public string? UniqueName { get; init; }

    /// <summary>The uniqueName came from the deprecated <c>--name</c> (a hint is printed).</summary>
    public bool UsedDeprecatedName { get; init; }
}

/// <summary>
/// <c>earm state adopt</c> ("adopt"), two forms:
/// <list type="bullet">
/// <item><b>Pin</b> (offline): <c>-n &lt;deployment&gt; --resource-id &lt;id&gt; --object-id &lt;guid&gt;</c> records a Graph object id under a resource id in
/// the deployment's state, so a displayName-named or GUID-named resource that matches several objects (or none by
/// key) binds to the intended one. Needs no sign-in.</item>
/// <item><b>uniqueName</b> (online): <c>--type Microsoft.Entra/applications|groups --id &lt;objectId|appId&gt; --unique-name &lt;uniqueName&gt;</c> (<c>--name</c> still works, hidden and deprecated)
/// PATCHes <c>uniqueName</c> onto a brownfield application or group so a template can address it by name from then on.
/// uniqueName is immutable: an object that already has a different one is refused.</item>
/// </list>
/// Exit codes: 0 done (or already in place), 1 refused or failed, 3 no usable sign-in (uniqueName form), 4 the state is locked.
/// </summary>
internal static class AdoptCommand
{
    /// <summary>The types that have a uniqueName alternate key.</summary>
    private static readonly string[] UniqueNameTypes = ["Microsoft.Entra/applications", "Microsoft.Entra/groups"];

    private static readonly Regex KeyToken = new("^([A-Za-z][A-Za-z0-9._]*)=(.+)$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    public static Command Build()
    {
        // --name is the uniqueName here, so the deployment is -n/--deployment.
        var options = new StateOptions("--deployment");

        var resourceId = new Option<string?>("--resource-id")
        {
            Description = "Pin form: the ARM-style resource id to pin (/providers/Microsoft.Entra/<type>/<name>).",
        };

        var objectId = new Option<string?>("--object-id")
        {
            Description = "Pin form: the Graph object id (GUID) of the existing object.",
        };

        var parentObjectId = new Option<string?>("--parent-object-id")
        {
            Description = "Pin form, child types: the parent's Graph object id when the parent is not in the state yet.",
        };

        var key = new Option<string[]>("--key")
        {
            Description = "Pin form, GUID-named types: a key property as name=value (repeatable), for example principalId=<guid>.",
            AllowMultipleArgumentsPerToken = false,
            DefaultValueFactory = _ => [],
        };

        var type = new Option<string?>("--type")
        {
            Description = "uniqueName form: Microsoft.Entra/applications or Microsoft.Entra/groups.",
        };

        var id = new Option<string?>("--id")
        {
            Description = "uniqueName form: the object id (applications also accept the application (client) id).",
        };

        var uniqueName = new Option<string?>("--unique-name")
        {
            Description = "uniqueName form: the uniqueName to set (immutable afterwards); it becomes the resource name in templates.",
        };

        // --name used to be the uniqueName here while every other state verb uses -n/--name for the DEPLOYMENT: a footgun. It still works but is hidden and prints a hint.
        var name = new Option<string?>("--name")
        {
            Description = "Deprecated: use --unique-name. (The deployment is -n/--deployment.)",
            Hidden = true,
        };

        var command = new Command("adopt", "Brings an existing object under a template: pins its Graph id into a deployment's state (--resource-id/--object-id), or sets uniqueName on an application or group (--type/--id/--unique-name).");
        options.Add(command, nameRequired: false, withYes: true);
        command.Options.Add(resourceId);
        command.Options.Add(objectId);
        command.Options.Add(parentObjectId);
        command.Options.Add(key);
        command.Options.Add(type);
        command.Options.Add(id);
        command.Options.Add(uniqueName);
        command.Options.Add(name);
        command.SetAction((parseResult, ct) => RunAsync(
            options.Read(parseResult),
            new AdoptRequest
            {
                ResourceId = parseResult.GetValue(resourceId),
                ObjectId = parseResult.GetValue(objectId),
                ParentObjectId = parseResult.GetValue(parentObjectId),
                Keys = parseResult.GetValue(key) ?? [],
                Type = parseResult.GetValue(type),
                Id = parseResult.GetValue(id),
                UniqueName = parseResult.GetValue(uniqueName) ?? parseResult.GetValue(name),
                UsedDeprecatedName = parseResult.GetValue(uniqueName) is null && parseResult.GetValue(name) is not null,
            },
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ConsolePrompt.DefaultReadLine,
            ct));
        return command;
    }

    internal static Task<int> RunAsync(StateInvocation invocation, AdoptRequest request, TextWriter stdout, TextWriter stderr, Func<string?> readLine, CancellationToken cancellationToken)
    {
        if (request.UsedDeprecatedName)
        {
            stderr.WriteLine("Note: --name on 'state adopt' is deprecated because it names the uniqueName while -n/--deployment names the deployment; use --unique-name.");
        }

        bool pin = request.ResourceId is not null || request.ObjectId is not null || request.ParentObjectId is not null || request.Keys.Count > 0;
        bool unique = request.Type is not null || request.Id is not null || request.UniqueName is not null;
        if (pin == unique)
        {
            return Task.FromResult(VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "InvalidArgument", pin
                ? "Choose one form: --resource-id/--object-id (pin an id in the state) or --type/--id/--unique-name (set uniqueName), not both."
                : "Nothing to adopt. Pin an id: state adopt -n <deployment> --resource-id <id> --object-id <guid>. Set a uniqueName: state adopt --type Microsoft.Entra/applications|groups --id <objectId|appId> --unique-name <uniqueName>."));
        }

        return pin
            ? PinAsync(invocation, request, stdout, stderr, readLine, cancellationToken)
            : SetUniqueNameAsync(invocation, request, stdout, stderr, readLine, cancellationToken);
    }

    // ---- pin form ----

    internal static async Task<int> PinAsync(StateInvocation invocation, AdoptRequest request, TextWriter stdout, TextWriter stderr, Func<string?> readLine, CancellationToken cancellationToken)
    {
        try
        {
            return await PinCoreAsync(invocation, request, stdout, stderr, readLine, cancellationToken).ConfigureAwait(false);
        }
        catch (StateException ex)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, ex.Code, ex.Message);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "StateIoFailed", $"The state could not be read or written: {ex.Message}");
        }
    }

    private static async Task<int> PinCoreAsync(StateInvocation invocation, AdoptRequest request, TextWriter stdout, TextWriter stderr, Func<string?> readLine, CancellationToken cancellationToken)
    {
        string name;
        try
        {
            name = invocation.RequireName();
        }
        catch (ArgumentException ex)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", ex.Message + " (-n/--deployment names the deployment whose state receives the id.)");
        }

        if (string.IsNullOrWhiteSpace(request.ResourceId) || string.IsNullOrWhiteSpace(request.ObjectId))
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", "Pinning needs both --resource-id and --object-id.");
        }

        var catalog = ResourceTypeCatalog.Default;
        if (!catalog.TryParseResourceId(request.ResourceId, out var identifier) || identifier is null || !catalog.TryGet(identifier.Type, out var definition))
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", $"'{request.ResourceId}' is not a resource id of a known type. Expected /providers/Microsoft.Entra/<type>/<name> (child types: .../<parentType>/<parent>/<childType>/<child>).");
        }

        if (definition.NameKind == ResourceNameKind.Id)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", $"{definition.Type} is addressed by its name ('{identifier.NameLeaf}' is the Graph id); there is nothing to pin.");
        }

        string? nameProblem = NameProblem(definition, identifier.NameLeaf);
        if (nameProblem is not null)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", nameProblem);
        }

        // The id ends up in Graph paths (and in deletes): only well-formed ids are accepted (S-01c). Directory objects have GUID ids;
        // role assignments, app role assignments and permission grants have opaque ids, authentication contexts c1..c99.
        string objectId = request.ObjectId.Trim();
        if (Guid.TryParse(objectId, out var objectGuid))
        {
            objectId = objectGuid.ToString("D");
        }

        if (!StateIdShapes.IsValidGraphId(definition.Type, objectId))
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", $"--object-id '{request.ObjectId}' is not {StateIdShapes.Describe(definition.Type)}.");
        }

        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        var store = StateHost.OpenStore(invocation.StateDir, invocation.TemplateFile, loggerFactory);
        IAsyncDisposable lease;
        try
        {
            lease = await store.LockAsync(name, TimeSpan.Zero, cancellationToken).ConfigureAwait(false);
        }
        catch (StateLockedException ex)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.StateLocked, ex.Code, ex.Message);
        }

        await using (lease.ConfigureAwait(false))
        {
            StateFile? state;
            try
            {
                state = await store.LoadAsync(name, cancellationToken).ConfigureAwait(false);
            }
            catch (StateException ex)
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, ex.Code, ex.Message);
            }

            bool created = state is null;
            state ??= StateFile.CreateNew(name, null, DateTimeOffset.UtcNow);

            var keyResult = BuildKey(definition, identifier, request.Keys, state, request.ParentObjectId, out string? parentGraphId);
            if (keyResult.Error is not null)
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", keyResult.Error);
            }

            string graphId = objectId;
            var existing = state.Find(identifier.Id);
            if (existing is not null && string.Equals(existing.GraphId, graphId, StringComparison.OrdinalIgnoreCase))
            {
                return VerbOutput.Succeed(invocation.Output, stdout, $"{identifier.Id} is already pinned to {graphId} in deployment '{name}'.", json =>
                {
                    json["deployment"] = name;
                    json["resourceId"] = identifier.Id;
                    json["objectId"] = graphId;
                    json["changed"] = false;
                });
            }

            if (existing is not null && !invocation.Yes
                && !ConsolePrompt.Confirm($"{identifier.Id} is pinned to {existing.GraphId} in deployment '{name}'. Re-pin it to {graphId}?", stdout, stderr, readLine))
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", "Nothing changed.");
            }

            string apiVersion = definition.ApiVersions.Where(pair => pair.Value == EArm.Core.Types.GraphApi.V1).Select(pair => pair.Key).FirstOrDefault() ?? definition.ApiVersions.Keys.First();
            state.Upsert(new StateEntry
            {
                Id = identifier,
                Type = definition.Type,
                ApiVersion = existing?.ApiVersion ?? apiVersion,
                Key = keyResult.Key!,
                GraphId = graphId,
                ParentGraphId = parentGraphId,
                Provenance = StateProvenance.Adopted,
                Extra = existing?.Extra,
                Relationships = existing?.Relationships,
                DependsOn = existing?.DependsOn ?? [],
                LastWriteOn = DateTimeOffset.UtcNow,
            });

            await store.SaveAsync(state, cancellationToken).ConfigureAwait(false);

            if (invocation.Output == OutputFormat.Json)
            {
                stdout.WriteLine(new JsonObject
                {
                    ["status"] = "Succeeded",
                    ["deployment"] = name,
                    ["stateFile"] = store.PathFor(name),
                    ["resourceId"] = identifier.Id,
                    ["type"] = definition.Type,
                    ["objectId"] = graphId,
                    ["previousObjectId"] = existing?.GraphId,
                    ["stateCreated"] = created,
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            else if (invocation.Output == OutputFormat.Table)
            {
                stdout.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Pinned {identifier.Id} to {graphId} in deployment '{name}' ({store.PathFor(name)}){(created ? "; the state file was created" : string.Empty)}."));
                stdout.WriteLine("The next deployment reads this object by its id and falls back to the key only if the id is gone.");
            }

            return ExitCodes.Ok;
        }
    }

    private sealed record KeyResult(IReadOnlyDictionary<string, string>? Key, string? Error);

    /// <summary>The state key of the entry: the name mapped to the type's key property, or the supplied composite properties.</summary>
    private static KeyResult BuildKey(ResourceTypeDefinition definition, ResourceIdentifier identifier, IReadOnlyList<string> tokens, StateFile state, string? parentObjectId, out string? parentGraphId)
    {
        parentGraphId = null;
        var supplied = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string token in tokens)
        {
            var match = KeyToken.Match(token);
            if (!match.Success)
            {
                return new KeyResult(null, $"--key '{token}' is not name=value.");
            }

            string property = definition.KeyProperties.FirstOrDefault(k => k.Equals(match.Groups[1].Value, StringComparison.OrdinalIgnoreCase)) ?? string.Empty;
            if (property.Length == 0)
            {
                return new KeyResult(null, $"--key '{match.Groups[1].Value}' is not a key property of {definition.Type}; expected {(definition.KeyProperties.Count == 0 ? "none" : string.Join(", ", definition.KeyProperties))}.");
            }

            supplied[property] = match.Groups[2].Value;
        }

        var key = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        switch (definition.NameKind)
        {
            case ResourceNameKind.Guid:
                var missing = definition.KeyProperties
                    .Where(k => !supplied.ContainsKey(k))
                    .Where(k => !(k.Equals("principalId", StringComparison.OrdinalIgnoreCase)
                        && definition.Type.EndsWith("oauth2PermissionGrants", StringComparison.OrdinalIgnoreCase)
                        && !(supplied.TryGetValue("consentType", out string? consent) && consent.Equals("Principal", StringComparison.OrdinalIgnoreCase))))
                    .ToList();
                if (missing.Count > 0)
                {
                    return new KeyResult(null, $"{definition.Type} is matched by its properties, not its name: pass --key {string.Join(" --key ", missing.Select(m => m + "=<value>"))} so deletes and lookups can find the object.");
                }

                foreach (var pair in supplied)
                {
                    key[pair.Key] = pair.Value;
                }

                break;

            default:
                if (supplied.Count > 0)
                {
                    return new KeyResult(null, $"--key applies to GUID-named types only; the key of {definition.Type} is its name.");
                }

                string property = definition.KeyProperties.Count > 0 ? definition.KeyProperties[0] : (definition.NameMapsTo ?? "displayName");
                key[property] = identifier.NameLeaf;
                break;
        }

        if (definition.IsChildType)
        {
            string parentResourceId = identifier.Parent!.Id;
            parentGraphId = parentObjectId ?? state.GraphIdOf(parentResourceId);
            if (parentGraphId is null)
            {
                return new KeyResult(null, $"{definition.Type} is a child of {identifier.Parent.Id}, which the state does not know yet: adopt the parent first, or pass --parent-object-id <guid>.");
            }
        }
        else if (parentObjectId is not null)
        {
            return new KeyResult(null, $"--parent-object-id applies to child types; {definition.Type} has no parent.");
        }

        return new KeyResult(key, null);
    }

    /// <summary>Why <paramref name="name"/> cannot be the name of a <paramref name="definition"/> resource, or <c>null</c>.</summary>
    private static string? NameProblem(ResourceTypeDefinition definition, string name)
    {
        switch (definition.NameKind)
        {
            case ResourceNameKind.Guid:
            case ResourceNameKind.AppId:
                if (!Guid.TryParse(name, out _))
                {
                    return $"The name '{name}' must be a GUID for {definition.Type}.";
                }

                break;
            case ResourceNameKind.UserPrincipalName:
                if (!name.Contains('@', StringComparison.Ordinal))
                {
                    return $"The name '{name}' must be a user principal name (user@domain).";
                }

                break;
        }

        if (definition.NamePattern is { } pattern && !Regex.IsMatch(name, pattern, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
        {
            return $"The name '{name}' is not valid for {definition.Type}; it must match '{pattern}'.";
        }

        return null;
    }

    // ---- uniqueName form ----

    private static string SingleLine(string text)
    {
        return Regex.Replace(text, @"\s+", " ",RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)).Trim();
    }

    internal static async Task<int> SetUniqueNameAsync(StateInvocation invocation, AdoptRequest request, TextWriter stdout, TextWriter stderr, Func<string?> readLine, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Type) || string.IsNullOrWhiteSpace(request.Id) || string.IsNullOrWhiteSpace(request.UniqueName))
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", "Setting a uniqueName needs --type, --id and --unique-name.");
        }

        var catalog = ResourceTypeCatalog.Default;
        string typeText = request.Type.Trim();
        string fullType = typeText.StartsWith(ResourceIdentifier.ProviderNamespace + "/", StringComparison.OrdinalIgnoreCase) ? typeText : ResourceIdentifier.ProviderNamespace + "/" + typeText;
        string? type = UniqueNameTypes.FirstOrDefault(t => t.Equals(fullType, StringComparison.OrdinalIgnoreCase));
        if (type is null || catalog.Find(type) is not { } definition)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", $"--type '{request.Type}' has no uniqueName; use {string.Join(" or ", UniqueNameTypes)}.");
        }

        string id = request.Id.Trim();
        if (!Guid.TryParse(id, out _))
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", $"--id '{request.Id}' is not a GUID (an object id{(type.EndsWith("applications", StringComparison.Ordinal) ? " or an application (client) id" : string.Empty)}).");
        }

        string uniqueName = request.UniqueName.Trim();
        if (NameProblem(definition, uniqueName) is { } problem)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", problem);
        }

        bool isApplication = type.EndsWith("applications", StringComparison.Ordinal);
        string collection = isApplication ? "/applications" : "/groups";
        string[] select = isApplication ? ["id", "appId", "displayName", "uniqueName"] : ["id", "displayName", "uniqueName"];

        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        if (!EngineServices.TryCreate(invocation.Connection, loggerFactory, stdout, stderr, invocation.Output, out var created, out int createExit))
        {
            return createExit;
        }

        using var services = created!;

        // Preflight first: nothing is read or written without a working sign-in.
        GraphPreflightResult preflight;
        try
        {
            preflight = await services.Preflight.RunAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is GraphTransportException or GraphException)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.AuthOrPreflight, "PreflightFailed", "Preflight failed: " + ex.Message);
        }

        try
        {
            var graph = services.Graph;
            var live = await graph.GetAsync(new GraphRequest(collection + "/" + GraphPath.Segment(id)).WithSelect(select), cancellationToken).ConfigureAwait(false) as JsonObject;
            if (live is null && isApplication)
            {
                live = await graph.GetAsync(new GraphRequest(GraphPath.AlternateKey(collection, "appId", id)).WithSelect(select), cancellationToken).ConfigureAwait(false) as JsonObject;
            }

            if (live is null || live["id"]?.GetValue<string>() is not { } objectId)
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", $"No {(isApplication ? "application" : "group")} with {(isApplication ? "object id or application id" : "object id")} {id} in tenant {preflight.TenantId}.");
            }

            string display = live["displayName"]?.GetValue<string>() ?? objectId;
            string? current = live["uniqueName"] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;
            if (!string.IsNullOrEmpty(current))
            {
                if (string.Equals(current, uniqueName, StringComparison.Ordinal))
                {
                    return VerbOutput.Succeed(invocation.Output, stdout, $"{display} ({objectId}) already has uniqueName '{current}'; nothing to do.", json =>
                    {
                        json["type"] = type;
                        json["objectId"] = objectId;
                        json["uniqueName"] = current;
                        json["changed"] = false;
                    });
                }

                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", $"{display} ({objectId}) already has uniqueName '{current}', which is immutable; '{uniqueName}' cannot be set. Use '{current}' as the resource name in the template.");
            }

            if (!invocation.Yes && !ConsolePrompt.Confirm($"Set uniqueName '{uniqueName}' on {(isApplication ? "application" : "group")} '{display}' ({objectId}) in tenant {preflight.TenantId}? uniqueName is immutable once set.", stdout, stderr, readLine))
            {
                return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", "Nothing changed.");
            }

            if (isApplication)
            {
                // The provider owns the explanation for Graph refusing an immutable uniqueName (UniqueNameNotSettable).
                string resourceName = new ResourceIdentifier(type, [uniqueName]).Id;
                try
                {
                    await new ApplicationsProvider(catalog).AdoptUniqueNameAsync(graph, objectId, uniqueName, EArm.Graph.GraphApi.V1, RetryHint.Default, $"application {display}", new ResourceIdentifier(type, [uniqueName]), cancellationToken).ConfigureAwait(false);
                }
                catch (ProviderException ex) when (ex.Code == ApplicationsProvider.UniqueNameNotSettable)
                {
                    return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", $"{ApplicationsProvider.UniqueNameNotSettable}: {SingleLine(ex.Message)} ({resourceName})");
                }
            }
            else
            {
                await graph.PatchAsync(new GraphRequest(collection + "/" + GraphPath.Segment(objectId)), new JsonObject { ["uniqueName"] = uniqueName }, cancellationToken).ConfigureAwait(false);
            }

            string resourceId = new ResourceIdentifier(type, [uniqueName]).Id;
            if (invocation.Output == OutputFormat.Json)
            {
                stdout.WriteLine(new JsonObject
                {
                    ["status"] = "Succeeded",
                    ["type"] = type,
                    ["objectId"] = objectId,
                    ["displayName"] = display,
                    ["uniqueName"] = uniqueName,
                    ["resourceId"] = resourceId,
                    ["tenantId"] = preflight.TenantId,
                }.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
            }
            else if (invocation.Output == OutputFormat.Table)
            {
                stdout.WriteLine($"Set uniqueName '{uniqueName}' on {(isApplication ? "application" : "group")} '{display}' ({objectId}).");
                stdout.WriteLine($"Declare it in a template as type {type}, name '{uniqueName}' ({resourceId}); the next deployment adopts the object by that name.");
            }

            return ExitCodes.Ok;
        }
        catch (GraphException ex)
        {
            string hint = ex.IsForbidden
                ? $" The signed-in identity needs {(isApplication ? "Application.ReadWrite.All (Application.ReadWrite.OwnedBy for applications it owns)" : "Group.ReadWrite.All")}, or an Entra role that manages {(isApplication ? "applications" : "groups")}."
                : string.Empty;
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.Failure, "AdoptFailed", $"Microsoft Graph refused the request: {ex.Message}{hint}");
        }
        catch (GraphTransportException ex)
        {
            return VerbOutput.Fail(invocation.Output, stdout, stderr, ExitCodes.AuthOrPreflight, "PreflightFailed", ex.Message);
        }
    }
}
