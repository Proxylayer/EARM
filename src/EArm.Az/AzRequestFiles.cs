using System;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace EArm.Az;

/// <summary>
/// The three files of one az invocation: the request file read by the host script, the optional body file it
/// passes as <c>--body @file</c>, and the output file it writes az's stdout to. Disposal zeroes and deletes all
/// three (the output may carry secrets such as <c>secretText</c> or an access token).
/// </summary>
internal sealed class AzRequestFiles : IDisposable
{
    private static readonly JsonSerializerOptions FileJsonOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private AzRequestFiles(string requestFile, string? bodyFile, string outputFile)
    {
        RequestFile = requestFile;
        BodyFile = bodyFile;
        OutputFile = outputFile;
    }

    public string RequestFile { get; }

    public string? BodyFile { get; }

    public string OutputFile { get; }

    public static AzRequestFiles Create(SecureTempDirectory directory, AzRequest request)
    {
        ArgumentNullException.ThrowIfNull(directory);
        ArgumentNullException.ThrowIfNull(request);

        var id = Guid.NewGuid().ToString("N");
        string? bodyFile = null;
        var outputFile = directory.FilePath($"out-{id}.json");
        var requestFile = directory.FilePath($"req-{id}.json");

        try
        {
            if (request.Body is not null)
            {
                bodyFile = directory.FilePath($"body-{id}.json");
                SecureFile.WriteAllText(bodyFile, request.Body.ToJsonString(FileJsonOptions));
            }

            var requestJson = request.ToRequestFileJson(bodyFile, outputFile).ToJsonString(FileJsonOptions);
            SecureFile.WriteAllText(requestFile, requestJson);
        }
        catch
        {
            SecureFile.ZeroAndDelete(bodyFile);
            SecureFile.ZeroAndDelete(requestFile);
            throw;
        }

        return new AzRequestFiles(requestFile, bodyFile, outputFile);
    }

    /// <summary>az's stdout as written by the host script, or null when the script never got that far.</summary>
    public string? TryReadOutput()
    {
        return SecureFile.TryReadAllText(OutputFile);
    }

    public void Dispose()
    {
        SecureFile.ZeroAndDelete(BodyFile);
        SecureFile.ZeroAndDelete(RequestFile);
        SecureFile.ZeroAndDelete(OutputFile);
    }
}
