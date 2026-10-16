using System;
using System.Collections.Generic;
using System.CommandLine;
using System.CommandLine.Help;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Hosting;
using EArm.Cli.Output;
using EArm.Core.Diagnostics;
using EArm.Engine.Permissions;
using EArm.Graph;
using EArm.Graph.Auth;
using Microsoft.Extensions.Logging;

namespace EArm.Cli.Commands;

/// <summary>What the auth verbs read from the command line.</summary>
internal sealed record AuthInvocation
{
    public required ConnectionSettings Connection { get; init; }

    public required OutputFormat Output { get; init; }

    public LogLevel LogLevel { get; init; } = LogLevel.Information;

    /// <summary><c>auth login --device-code</c>: the device-code flow instead of a browser.</summary>
    public bool DeviceCode { get; init; }

    /// <summary><c>auth login --scopes</c>: extra delegated scopes to consent (short Graph names, qualified Graph scopes, or the Key Vault scope).</summary>
    public IReadOnlyList<string> Scopes { get; init; } = [];

    /// <summary><c>-f</c>: the template whose run scopes <c>login</c> consents and <c>status</c> checks; null without one.</summary>
    public string? TemplateFile { get; init; }

    /// <summary><c>-p</c>: parameter values of <see cref="TemplateFile"/> (same syntax as the deployment verbs).</summary>
    public IReadOnlyList<string> Parameters { get; init; } = [];

    /// <summary><c>--verb</c>: the run whose scopes <see cref="TemplateFile"/> stands for (default <see cref="RunVerb.Create"/>).</summary>
    public RunVerb Verb { get; init; } = RunVerb.Create;
}

/// <summary>
/// The <c>earm auth</c> command group: <c>login [--device-code]</c> signs in interactively (browser or device code) and stores a non-secret
/// sign-in record next to an encrypted token cache, <c>logout</c> forgets that sign-in, <c>status</c> shows who the current token belongs to.
/// Service principals, workload identity and managed identity need no verb: they are configured through the <c>AZURE_*</c> environment.
/// </summary>
internal static class AuthCommand
{
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, MaxDepth = JsonOutput.MaxDepth, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Command Build()
    {
        var command = new Command("auth", "Sign in interactively, sign out, and show who EARM is signed in as.");
        command.Subcommands.Add(BuildLogin());
        command.Subcommands.Add(BuildLogout());
        command.Subcommands.Add(BuildStatus());
        command.SetAction(parseResult => new HelpAction().Invoke(parseResult));
        return command;
    }

    private static Command BuildLogin()
    {
        var deviceCode = new Option<bool>("--device-code")
        {
            Description = "Use the device-code flow (print a code to enter at https://microsoft.com/devicelogin) instead of opening a browser; for machines without a browser.",
        };
        var scopes = new Option<string[]>("--scopes")
        {
            Description = "Extra delegated scopes to consent now, besides User.Read and Organization.Read.All: Microsoft Graph names (Group.ReadWrite.All), qualified Graph scopes, or the Key Vault scope (https://vault.azure.net/user_impersonation). Space- or comma-separated.",
            AllowMultipleArgumentsPerToken = true,
            DefaultValueFactory = _ => [],
        };
        var template = TemplateScopeOptions.Create("consent now");
        var command = new Command("login", "Signs in interactively (browser by default) as yourself and caches the sign-in encrypted for later runs. The sign-in app is Microsoft Graph Command Line Tools unless --client-id names another; --scopes and -f consent the delegated scopes runs will request.");
        command.Options.Add(deviceCode);
        command.Options.Add(scopes);
        template.AddTo(command);
        command.SetAction((parseResult, ct) => RunLoginAsync(
            template.Apply(Read(parseResult, parseResult.GetValue(deviceCode)), parseResult) with { Scopes = parseResult.GetValue(scopes) ?? [] },
            parseResult.InvocationConfiguration.Output,
            parseResult.InvocationConfiguration.Error,
            ct));
        return command;
    }

    private static Command BuildLogout()
    {
        var command = new Command("logout", "Forgets the interactive sign-in: deletes the sign-in record and the encrypted token cache entry.");
        command.SetAction((parseResult, ct) => RunLogoutAsync(Read(parseResult, false), parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error, ct));
        return command;
    }

    private static Command BuildStatus()
    {
        var template = TemplateScopeOptions.Create("check against the token (lists the missing ones)");
        var command = new Command("status", "Shows how EARM signs in and who the access token belongs to (tenant, principal, client, scopes or roles); with -f, which delegated scopes a template run needs that the token lacks. Exits 3 when no credential works.");
        template.AddTo(command);
        command.SetAction((parseResult, ct) => RunStatusAsync(template.Apply(Read(parseResult, false), parseResult), parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error, ct));
        return command;
    }

    private static AuthInvocation Read(ParseResult parseResult, bool deviceCode)
    {
        return new AuthInvocation
        {
            Connection = GlobalOptions.BuildConnection(parseResult, 1),
            Output = GlobalOptions.GetOutputFormat(parseResult),
            LogLevel = GlobalOptions.GetLogLevel(parseResult),
            DeviceCode = deviceCode,
        };
    }

    internal static async Task<int> RunLoginAsync(AuthInvocation invocation, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        using var resolveMessages = new StringWriter();
        if (!TryResolve(invocation, loggerFactory, resolveMessages, out var cloudAuth, out var factory, out int exitCode))
        {
            return FailureDocument.Write(invocation.Output, stdout, stderr, ValidateCommand.InvalidArgumentCode, resolveMessages.ToString().Trim(), exitCode);
        }

        var (_, auth) = cloudAuth;
        var overrides = invocation.Connection.Overrides ?? ServiceOverrides.Ambient;
        if (LoginRefusal(invocation, auth, overrides?.AuthFactory is null) is { } refusal)
        {
            return FailureDocument.Write(invocation.Output, stdout, stderr, ValidateCommand.InvalidArgumentCode, refusal, ExitCodes.Failure);
        }

        RunScopeSet? templateScopes = null;
        if (invocation.TemplateFile is { } templateFile)
        {
            var computed = await TemplateRunScopes.ComputeAsync(templateFile, invocation.Parameters, invocation.Verb, cancellationToken).ConfigureAwait(false);
            if (computed.Scopes is null)
            {
                return TemplateRunScopes.WriteFailure(templateFile, computed, invocation.Output, stdout, stderr);
            }

            templateScopes = computed.Scopes;
        }

        LoginConsent consent;
        try
        {
            consent = LoginConsent.Build(auth.Cloud, invocation.Scopes, templateScopes);
        }
        catch (ArgumentException ex)
        {
            return FailureDocument.Write(invocation.Output, stdout, stderr, ValidateCommand.InvalidArgumentCode, ex.Message, ExitCodes.Failure);
        }

        try
        {
            var record = await factory!.LoginAsync(
                auth,
                consent.Requests,
                invocation.DeviceCode,
                (info, _) =>
                {
                    stderr.WriteLine(info.Message);
                    return Task.CompletedTask;
                },
                cancellationToken).ConfigureAwait(false);

            if (invocation.Output == OutputFormat.Json)
            {
                stdout.WriteLine(new JsonObject
                {
                    ["account"] = record.Username,
                    ["tenantId"] = record.TenantId,
                    ["clientId"] = record.ClientId,
                    ["clientName"] = PublicClients.NameOf(record.ClientId),
                    ["authority"] = record.Authority,
                    ["record"] = factory.RecordStore.FilePath,
                    ["scopes"] = ToArray(consent.Graph.Names),
                    ["keyVaultScopes"] = ToArray(consent.KeyVault?.Scopes ?? []),
                }.ToJsonString(JsonOptions));
            }
            else if (invocation.Output == OutputFormat.Table)
            {
                stdout.WriteLine($"Signed in as {record.Username} (tenant {record.TenantId}) to {DescribeClient(record.ClientId)}.");
                stdout.WriteLine($"Delegated scopes consented: {string.Join(' ', consent.Graph.Names)}");
                if (consent.KeyVault is not null)
                {
                    stdout.WriteLine($"Key Vault scopes consented: {string.Join(' ', consent.KeyVault.Scopes)}");
                }

                stdout.WriteLine($"The sign-in record (no secrets) is stored at {factory.RecordStore.FilePath}; the tokens are in the encrypted '{GraphAuthFactory.CacheName}' cache of this machine.");
                stdout.WriteLine(GraphAuthGuidance.SharedConsentNote);
            }

            return ExitCodes.Ok;
        }
        catch (GraphConfigurationException ex)
        {
            return FailureDocument.Write(invocation.Output, stdout, stderr, ValidateCommand.InvalidArgumentCode, ex.Message, ExitCodes.Failure);
        }
        catch (GraphTransportException ex)
        {
            return FailureDocument.Write(invocation.Output, stdout, stderr, AuthenticationFailedCode, ex.Message, ExitCodes.AuthOrPreflight);
        }
    }

    /// <summary>
    /// Whether standard input and output are both redirected (a pipeline, a script, a CI step): nobody is there to complete a browser sign-in.
    /// A seam for the tests, which cannot redirect the console of the test host.
    /// </summary>
    internal static Func<bool> StandardStreamsRedirected { get; set; } = () => Console.IsInputRedirected && Console.IsOutputRedirected;

    /// <summary>
    /// The reason <c>auth login</c> cannot run, or <c>null</c>: <c>--auth default</c> (the credential chain, which signs nobody in) and a browser
    /// sign-in with redirected standard streams would otherwise wait forever. <paramref name="realSignIn"/> is false when a test replaced the sign-in.
    /// </summary>
    internal static string? LoginRefusal(AuthInvocation invocation, GraphAuthOptions auth, bool realSignIn)
    {
        if (auth.Mode == GraphAuthMode.Default && (invocation.Scopes.Count > 0 || invocation.TemplateFile is not null))
        {
            return "--scopes and -f consent delegated scopes for an interactive sign-in; they do not apply to --auth default (a service principal, workload identity or managed identity), which uses the application permissions an administrator granted to it. List those with 'earm permissions -f <template> --mode app'.";
        }

        if (auth.Mode == GraphAuthMode.Default)
        {
            return "'auth login' is only for interactive sign-in. --auth default uses the credential chain (environment variables, workload identity, managed identity), which needs no login: set AZURE_TENANT_ID, AZURE_CLIENT_ID and a secret, certificate or federated token, or use --auth interactive (or auto) with 'auth login'.";
        }

        if (realSignIn && !invocation.DeviceCode && StandardStreamsRedirected())
        {
            return "'auth login' opens a browser, but standard input and output are redirected (a script or a pipeline), so nobody could complete it. Add --device-code to sign in with a code instead, or run the command in an interactive terminal.";
        }

        return null;
    }

    internal static Task<int> RunLogoutAsync(AuthInvocation invocation, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        using var resolveMessages = new StringWriter();
        if (!TryResolve(invocation, loggerFactory, resolveMessages, out _, out var factory, out int exitCode))
        {
            return Task.FromResult(FailureDocument.Write(invocation.Output, stdout, stderr, ValidateCommand.InvalidArgumentCode, resolveMessages.ToString().Trim(), exitCode));
        }

        var result = factory!.Logout();
        if (invocation.Output == OutputFormat.Json)
        {
            stdout.WriteLine(new JsonObject
            {
                ["recordDeleted"] = result.RecordDeleted,
                ["cacheFilesDeleted"] = result.CacheFilesDeleted,
            }.ToJsonString(JsonOptions));
        }
        else if (invocation.Output == OutputFormat.Table)
        {
            stdout.WriteLine(result.RecordDeleted || result.CacheFilesDeleted > 0
                ? $"Signed out: removed the sign-in record and {result.CacheFilesDeleted} token cache file(s)."
                : "Nothing to sign out of: there was no interactive sign-in.");
            stdout.WriteLine("Service principal, workload identity and managed identity sign-ins come from the environment and are not affected.");
        }

        return Task.FromResult(ExitCodes.Ok);
    }

    internal static async Task<int> RunStatusAsync(AuthInvocation invocation, TextWriter stdout, TextWriter stderr, CancellationToken cancellationToken)
    {
        using var loggerFactory = GlobalOptions.CreateLoggerFactory(invocation.LogLevel);
        using var resolveMessages = new StringWriter();
        if (!TryResolve(invocation, loggerFactory, resolveMessages, out var cloudAuth, out var factory, out int exitCode))
        {
            return StatusFailure(invocation.Output, stdout, stderr, ValidateCommand.InvalidArgumentCode, resolveMessages.ToString().Trim(), exitCode);
        }

        var (_, auth) = cloudAuth;
        var overrides = invocation.Connection.Overrides ?? ServiceOverrides.Ambient;
        if (invocation.TemplateFile is not null && auth.Mode == GraphAuthMode.Default)
        {
            return StatusFailure(invocation.Output, stdout, stderr, ValidateCommand.InvalidArgumentCode, AppOnlyTemplateCheck, ExitCodes.Failure);
        }

        RunScopeSet? required = null;
        if (invocation.TemplateFile is { } templateFile)
        {
            var computed = await TemplateRunScopes.ComputeAsync(templateFile, invocation.Parameters, invocation.Verb, cancellationToken).ConfigureAwait(false);
            if (computed.Scopes is null)
            {
                return TemplateRunScopes.WriteFailure(templateFile, computed, invocation.Output, stdout, stderr);
            }

            required = computed.Scopes;
        }

        IGraphTokenProvider tokens;
        try
        {
            tokens = overrides?.Tokens ?? factory!.Create(auth);
        }
        catch (InvalidOperationException ex)
        {
            return StatusFailure(invocation.Output, stdout, stderr, AuthenticationFailedCode, ex.Message, ExitCodes.AuthOrPreflight);
        }

        try
        {
            var status = await GraphAuthStatus.ReadAsync(tokens, auth, factory!.RecordStore, cancellationToken).ConfigureAwait(false);
            if (required is not null)
            {
                if (status.Scopes.Count == 0 && (status.Roles.Count > 0 || string.Equals(status.PrincipalType, "application", StringComparison.OrdinalIgnoreCase)))
                {
                    return StatusFailure(invocation.Output, stdout, stderr, ValidateCommand.InvalidArgumentCode, AppOnlyTemplateCheck, ExitCodes.Failure);
                }

                status = status.WithRequired(required.GraphScopes, required.NeedsKeyVault);
            }

            Write(status, invocation.Output, stdout);
            return ExitCodes.Ok;
        }
        catch (GraphTransportException ex)
        {
            return StatusFailure(invocation.Output, stdout, stderr, AuthenticationFailedCode, ex.Message, ExitCodes.AuthOrPreflight);
        }
        finally
        {
            (tokens as IDisposable)?.Dispose();
        }
    }

    /// <summary>Why <c>auth status -f</c> refuses an app-only identity: delegated scopes do not apply to it.</summary>
    private const string AppOnlyTemplateCheck = "-f checks the delegated scopes of a user sign-in; this identity signs in with application permissions (a service principal, workload identity or managed identity), so delegated scopes do not apply. List the application permissions a template needs with 'earm permissions -f <template> --mode app'.";

    /// <summary><c>Microsoft Graph Command Line Tools (14d82eec-...)</c>, or the bare id of another client.</summary>
    private static string DescribeClient(string? clientId)
    {
        if (string.IsNullOrEmpty(clientId))
        {
            return "the default client";
        }

        return PublicClients.NameOf(clientId) is { } name ? $"{name} ({clientId})" : clientId;
    }

    /// <summary>Diagnostic code of a sign-in that could not be read (CLI-level, like <see cref="ValidateCommand.InvalidArgumentCode"/>).</summary>
    public const string AuthenticationFailedCode = "AuthenticationFailed";

    /// <summary>
    /// Reports a failed <c>auth status</c>: the message on standard error as ever, and with <c>-o json</c> also a JSON document on standard
    /// output (<c>status</c> and one error diagnostic, the shape of the other verbs), so a script that parses the output never meets plain text.
    /// </summary>
    private static int StatusFailure(OutputFormat output, TextWriter stdout, TextWriter stderr, string code, string message, int exitCode)
    {
        return FailureDocument.Write(output, stdout, stderr, code, message, exitCode);
    }

    private static bool TryResolve(AuthInvocation invocation, ILoggerFactory loggerFactory, TextWriter stderr, out (GraphCloud Cloud, GraphAuthOptions Auth) cloudAuth, out GraphAuthFactory? factory, out int exitCode)
    {
        cloudAuth = default;
        factory = null;
        exitCode = ExitCodes.Ok;
        try
        {
            cloudAuth = invocation.Connection.Resolve();
        }
        catch (ArgumentException ex)
        {
            stderr.WriteLine(ex.Message);
            exitCode = ExitCodes.Failure;
            return false;
        }

        var overrides = invocation.Connection.Overrides ?? ServiceOverrides.Ambient;
        factory = overrides?.AuthFactory ?? new GraphAuthFactory(overrides?.RecordStore, loggerFactory.CreateLogger<GraphAuthFactory>(), overrides?.TokenCacheDirectories);
        return true;
    }

    private static void Write(GraphAuthStatus status, OutputFormat output, TextWriter stdout)
    {
        if (output == OutputFormat.None)
        {
            return;
        }

        if (output == OutputFormat.Json)
        {
            var json = new JsonObject
            {
                ["mode"] = status.Mode,
                ["credential"] = status.Credential,
                ["cloud"] = status.Cloud,
                ["tenantId"] = status.TenantId,
                ["principalType"] = status.PrincipalType,
                ["principal"] = status.Principal,
                ["objectId"] = status.ObjectId,
                ["appId"] = status.AppId,
                ["clientId"] = status.ClientId,
                ["clientName"] = status.ClientName,
                ["scopes"] = ToArray(status.Scopes),
                ["roles"] = ToArray(status.Roles),
                ["expiresOn"] = status.ExpiresOn?.ToUniversalTime().ToString("u", System.Globalization.CultureInfo.InvariantCulture),
                ["signInRecord"] = status.SignInRecord,
                ["signInAccount"] = status.SignInAccount,
            };
            if (status.RequiredScopes is not null)
            {
                json["requiredScopes"] = ToArray(status.RequiredScopes);
                json["missingScopes"] = ToArray(status.MissingScopes);
                json["keyVaultNeeded"] = status.KeyVaultNeeded;
            }

            stdout.WriteLine(json.ToJsonString(JsonOptions));
            return;
        }

        stdout.WriteLine($"Mode:          {status.Mode} ({status.Credential})");
        stdout.WriteLine($"Cloud:         {status.Cloud}");
        stdout.WriteLine($"Tenant:        {status.TenantId ?? "(unknown)"}");
        stdout.WriteLine($"Principal:     {status.Principal ?? "(unknown)"} ({status.PrincipalType ?? "unknown"})");
        stdout.WriteLine($"Object id:     {status.ObjectId ?? "(unknown)"}");
        if (status.ClientId is not null)
        {
            stdout.WriteLine($"Client:        {DescribeClient(status.ClientId)}");
        }

        if (status.Scopes.Count > 0)
        {
            stdout.WriteLine($"Scopes:        {string.Join(' ', status.Scopes)}");
        }

        if (status.Roles.Count > 0)
        {
            stdout.WriteLine($"Roles:         {string.Join(' ', status.Roles)}");
        }

        if (status.ExpiresOn is { } expires)
        {
            stdout.WriteLine($"Token expires: {expires.ToUniversalTime():u}");
        }

        if (status.SignInRecord is not null)
        {
            stdout.WriteLine($"Sign-in:       {status.SignInAccount ?? "(account unknown)"} ({status.SignInRecord})");
        }

        if (PublicClients.IsAzureCli(status.ClientId) && status.SignInRecord is not null)
        {
            stdout.WriteLine("Note:          this sign-in uses the Microsoft Azure CLI app, which cannot consent to the delegated scopes EARM requests; run 'earm auth login' to switch to Microsoft Graph Command Line Tools.");
        }

        if (status.RequiredScopes is not null)
        {
            stdout.WriteLine($"Required:      {string.Join(' ', status.RequiredScopes)}");
            stdout.WriteLine(status.MissingScopes.Count == 0
                ? "Missing:       (none)"
                : $"Missing:       {string.Join(' ', status.MissingScopes)} (consent with 'earm auth login --scopes {string.Join(' ', status.MissingScopes)}' or 'earm auth login -f <template>')");
            if (status.KeyVaultNeeded)
            {
                stdout.WriteLine("Key Vault:     the template resolves Key Vault references; 'earm auth login -f <template>' also consents the Key Vault scope (user_impersonation).");
            }
        }
    }

    private static JsonArray ToArray(System.Collections.Generic.IReadOnlyList<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
        {
            array.Add(JsonValue.Create(value));
        }

        return array;
    }
}
