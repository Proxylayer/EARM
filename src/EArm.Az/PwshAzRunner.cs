using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace EArm.Az;

/// <summary>
/// Runs the Azure CLI through a PowerShell 7 host process using the request-file protocol:
/// <list type="number">
/// <item>the request (and body, if any) is written to the owner-only temp directory,</item>
/// <item><c>pwsh -NoProfile -NonInteractive -NoLogo -ExecutionPolicy Bypass -File Invoke-AzRest.ps1 -RequestFile &lt;path&gt; [-AzCommand &lt;path&gt;]</c>
/// is started via <see cref="ProcessStartInfo.ArgumentList"/> (no shell, no cmd.exe, UTF-8 pipes),</item>
/// <item>the script hands az an argv array, writes az's stdout to the output file and exits with az's exit code,</item>
/// <item>all files are zeroed and deleted in <c>finally</c>.</item>
/// </list>
/// A per-call timeout kills the whole process tree. Nothing that may contain a secret is ever logged.
/// </summary>
public sealed class PwshAzRunner : IAzRunner, IDisposable
{
    private readonly AzOptions _options;
    private readonly PwshLocator _locator;
    private readonly ILogger<PwshAzRunner> _logger;
    private readonly SecureTempDirectory _tempDirectory;
    private readonly SemaphoreSlim _parallelism;

    public PwshAzRunner(AzOptions options, PwshLocator locator, ILogger<PwshAzRunner> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _locator = locator ?? throw new ArgumentNullException(nameof(locator));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _tempDirectory = new SecureTempDirectory(options.ResolveTempDirectory());
        _parallelism = new SemaphoreSlim(Math.Max(1, options.MaxParallel), Math.Max(1, options.MaxParallel));
    }

    /// <summary>The temp directory in use (request, body, output and script files live here).</summary>
    public string TempDirectory => _tempDirectory.Path;

    public async Task<AzResult> RunAsync(AzRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        var pwsh = await _locator.LocateAsync(cancellationToken).ConfigureAwait(false);
        _tempDirectory.EnsureCreated();
        var scriptPath = EmbeddedScript.Extract(_tempDirectory, _logger);
        var azCommand = ResolveAzCommand();
        var timeout = request.Timeout ?? _options.Timeout;

        await _parallelism.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var files = AzRequestFiles.Create(_tempDirectory, request);
            LogStarting(request, files.BodyFile);

            var startInfo = BuildStartInfo(pwsh.Path, scriptPath, files.RequestFile, azCommand);
            var outcome = await PwshProcess.RunAsync(startInfo, timeout, cancellationToken).ConfigureAwait(false);

            // The script writes az's stdout to the output file; the pwsh stdout pipe is only a fallback for output
            // that bypassed the in-process capture (e.g. a fake writing straight to the console).
            var fileOutput = files.TryReadOutput();
            var standardOutput = string.IsNullOrEmpty(fileOutput) ? outcome.StandardOutput : fileOutput;

            var result = new AzResult(outcome.ExitCode, standardOutput, outcome.StandardError, outcome.Elapsed, outcome.TimedOut);
            LogCompleted(request, result, timeout);
            return result;
        }
        finally
        {
            _parallelism.Release();
        }
    }

    public void Dispose()
    {
        _parallelism.Dispose();
    }

    /// <summary>Builds the pwsh start info for a request file (exposed for tests asserting that only pwsh is launched).</summary>
    internal ProcessStartInfo BuildStartInfo(string pwshPath, string scriptPath, string requestFile, string? azCommand)
    {
        var startInfo = PwshProcess.CreateStartInfo(pwshPath);
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-NoLogo");
        startInfo.ArgumentList.Add("-ExecutionPolicy");
        startInfo.ArgumentList.Add("Bypass");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(scriptPath);
        startInfo.ArgumentList.Add("-RequestFile");
        startInfo.ArgumentList.Add(requestFile);
        if (!string.IsNullOrEmpty(azCommand))
        {
            startInfo.ArgumentList.Add("-AzCommand");
            startInfo.ArgumentList.Add(azCommand);
        }

        startInfo.Environment["AZURE_CORE_OUTPUT"] = "json";
        startInfo.Environment["AZURE_CORE_NO_COLOR"] = "1";
        foreach (var pair in _options.AdditionalEnvironment)
        {
            startInfo.Environment[pair.Key] = pair.Value;
        }

        return startInfo;
    }

    private string? ResolveAzCommand()
    {
        var configured = _options.ResolveAzCommandSetting();
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var looksLikePath = configured.Contains(Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || configured.Contains(Path.AltDirectorySeparatorChar, StringComparison.Ordinal)
            || File.Exists(configured);
        return looksLikePath ? Path.GetFullPath(configured) : configured;
    }

    private void LogStarting(AzRequest request, string? bodyFile)
    {
        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        AzLog.AzRequestStarting(
            _logger,
            request.CommandText,
            request.Method,
            request.Url,
            FormatPairs(request.UrlParameters),
            FormatPairs(request.Headers),
            request.Body is not null);

        var argv = request.BuildAzArguments(bodyFile is null ? null : "<body>")
            .Select(static a => SecretRedactor.RedactArgument(a));
        AzLog.AzArgv(_logger, string.Join(' ', argv));
    }

    private void LogCompleted(AzRequest request, AzResult result, TimeSpan timeout)
    {
        if (result.TimedOut)
        {
            AzLog.AzTimedOut(_logger, request.CommandText, timeout.TotalSeconds);
        }

        if (!_logger.IsEnabled(LogLevel.Debug))
        {
            return;
        }

        AzLog.AzRequestCompleted(
            _logger,
            request.CommandText,
            result.ExitCode,
            (long)result.Elapsed.TotalMilliseconds,
            result.StandardOutput.Length,
            result.StandardError.Length,
            result.TimedOut);

        if (result.StandardError.Length > 0 && _logger.IsEnabled(LogLevel.Trace))
        {
            AzLog.AzStandardError(_logger, SecretRedactor.RedactText(result.StandardError));
        }
    }

    private static string FormatPairs(IReadOnlyDictionary<string, string>? pairs)
    {
        if (pairs is null || pairs.Count == 0)
        {
            return "{}";
        }

        return "{" + string.Join(", ", pairs.Select(static p => p.Key + "=" + SecretRedactor.RedactValue(p.Key, p.Value))) + "}";
    }
}
