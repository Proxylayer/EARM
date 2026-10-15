using System;
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
        var command = new Command("login", "Signs in interactively (browser by default) and caches the sign-in encrypted for later runs. Use --tenant and --client-id to choose the tenant and the app registration.");
        command.Options.Add(deviceCode);
        command.SetAction((parseResult, ct) => RunLoginAsync(Read(parseResult, parseResult.GetValue(deviceCode)), parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error, ct));
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
        var command = new Command("status", "Shows how EARM signs in and who the access token belongs to (tenant, principal, scopes or roles). Exits 3 when no credential works.");
        command.SetAction((parseResult, ct) => RunStatusAsync(Read(parseResult, false), parseResult.InvocationConfiguration.Output, parseResult.InvocationConfiguration.Error, ct));
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

        try
        {
            var record = await factory!.LoginAsync(
                auth,
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
                    ["authority"] = record.Authority,
                    ["record"] = factory.RecordStore.FilePath,
                }.ToJsonString(JsonOptions));
            }
            else if (invocation.Output == OutputFormat.Table)
            {
                stdout.WriteLine($"Signed in as {record.Username} (tenant {record.TenantId}).");
                stdout.WriteLine($"The sign-in record (no secrets) is stored at {factory.RecordStore.FilePath}; the tokens are in the encrypted '{GraphAuthFactory.CacheName}' cache of this machine.");
            }

            return ExitCodes.Ok;
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
        factory = overrides?.AuthFactory ?? new GraphAuthFactory(overrides?.RecordStore, loggerFactory.CreateLogger<GraphAuthFactory>());
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
                ["scopes"] = ToArray(status.Scopes),
                ["roles"] = ToArray(status.Roles),
                ["expiresOn"] = status.ExpiresOn?.ToUniversalTime().ToString("u", System.Globalization.CultureInfo.InvariantCulture),
                ["signInRecord"] = status.SignInRecord,
                ["signInAccount"] = status.SignInAccount,
            };
            stdout.WriteLine(json.ToJsonString(JsonOptions));
            return;
        }

        stdout.WriteLine($"Mode:          {status.Mode} ({status.Credential})");
        stdout.WriteLine($"Cloud:         {status.Cloud}");
        stdout.WriteLine($"Tenant:        {status.TenantId ?? "(unknown)"}");
        stdout.WriteLine($"Principal:     {status.Principal ?? "(unknown)"} ({status.PrincipalType ?? "unknown"})");
        stdout.WriteLine($"Object id:     {status.ObjectId ?? "(unknown)"}");
        if (status.AppId is not null)
        {
            stdout.WriteLine($"App id:        {status.AppId}");
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
