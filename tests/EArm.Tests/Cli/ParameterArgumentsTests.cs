using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using EArm.Cli.Commands;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Graph;
using EArm.Tests.Engine;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// The <c>-p</c> forms of the Azure CLI (<see cref="ParameterArguments"/>): parameter file with or without <c>@</c>, inline JSON, <c>name=value</c>,
/// <c>name=@file</c>, https URL (<see cref="ParameterFileDownloader"/>), applied in order; <c>.bicepparam</c> refused.
/// </summary>
public sealed class ParameterArgumentsTests
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static string Write(TestDirectory directory, string name, string text)
    {
        string path = directory.File(name);
        File.WriteAllText(path, text, Utf8NoBom);
        return path;
    }

    [Fact]
    public async Task AnInlineJsonObject_IsAParameterDocument_InEitherShape()
    {
        var bare = await ParameterArguments.BuildAsync(["{\"a\":{\"value\":1},\"b\":{\"value\":\"x\"}}"], Ct);
        var full = await ParameterArguments.BuildAsync(["{ \"$schema\": \"s\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"a\": { \"value\": [1, 2] } } }"], Ct);

        Assert.Equal(1, bare.Parameters.Values["a"].Value!.GetValue<int>());
        Assert.Equal("x", bare.Parameters.Values["B"].Value!.GetValue<string>());
        Assert.Null(bare.ParameterFile);
        Assert.Equal(2, full.Parameters.Values["a"].Value!.AsArray().Count);
    }

    [Fact]
    public async Task AParameterFile_LoadsWithOrWithoutTheAt()
    {
        using var directory = new TestDirectory(nameof(AParameterFile_LoadsWithOrWithoutTheAt));
        string path = Write(directory, "p.json", "{ \"parameters\": { \"env\": { \"value\": \"prod\" } } }");

        var bare = await ParameterArguments.BuildAsync([path], Ct);
        var at = await ParameterArguments.BuildAsync(["@" + path], Ct);

        Assert.Equal("prod", bare.Parameters.Values["env"].Value!.GetValue<string>());
        Assert.Equal(path, bare.ParameterFile);
        Assert.Equal("prod", at.Parameters.Values["env"].Value!.GetValue<string>());
    }

    [Fact]
    public async Task NameAtFile_TakesTheFileContentAsTheValue()
    {
        using var directory = new TestDirectory(nameof(NameAtFile_TakesTheFileContentAsTheValue));
        string text = Write(directory, "owner.txt", "\uFEFFalice@contoso.com");
        string array = Write(directory, "array.json", "[\"a\", \"b\"]");

        var result = await ParameterArguments.BuildAsync(["owner=@" + text, "list=@" + array], Ct);

        Assert.Equal("alice@contoso.com", result.Parameters.Values["owner"].RawText);
        Assert.True(result.Parameters.Values["owner"].IsInline);
        Assert.Equal("[\"a\", \"b\"]", result.Parameters.Values["list"].RawText);
    }

    [Fact]
    public async Task NameAtAMissingFile_IsAnError_NotALiteral()
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => ParameterArguments.BuildAsync(["password=@NotAFile123"], Ct));

        Assert.Contains("does not exist", error.Message, StringComparison.Ordinal);
        Assert.Contains("inline JSON object", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("main.bicepparam")]
    [InlineData("@main.bicepparam")]
    [InlineData("C:/x/MAIN.BICEPPARAM")]
    public async Task ABicepParameterFile_IsRefused_WithTheReason(string token)
    {
        var error = await Assert.ThrowsAsync<ArgumentException>(() => ParameterArguments.BuildAsync([token], Ct));

        Assert.Contains("Bicep compiler", error.Message, StringComparison.Ordinal);
        Assert.Contains("bicep build-params", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("http://example.com/p.json", "https only")]
    [InlineData("https://user:secret@example.com/p.json", "user name or password")]
    public async Task AnUnsafeUrl_IsRefused_BeforeAnythingIsSent(string url, string reason)
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{}");

        var error = await Assert.ThrowsAsync<ArgumentException>(() => ParameterArguments.BuildAsync([url], Ct, new ParameterFileDownloader(new HttpMessageInvoker(handler))));

        Assert.Contains(reason, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task AnHttpsUrl_IsDownloaded_WithoutCredentials_AndReportedWithoutItsQuery()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, "{ \"parameters\": { \"env\": { \"value\": \"from-url\" } } }");

        var result = await ParameterArguments.BuildAsync(["https://example.com/p.json?sig=SECRET", "other=1"], Ct, new ParameterFileDownloader(new HttpMessageInvoker(handler)));

        Assert.Equal("from-url", result.Parameters.Values["env"].Value!.GetValue<string>());
        Assert.Equal("https://example.com/p.json", result.ParameterFile);
        Assert.Equal(1, handler.Calls);
        Assert.Null(handler.LastRequest!.Headers.Authorization);
        Assert.Equal("https://example.com/p.json?sig=SECRET", handler.LastRequest.RequestUri!.ToString());
    }

    [Theory]
    [InlineData(HttpStatusCode.Redirect, "redirect (302)")]
    [InlineData(HttpStatusCode.NotFound, "404")]
    public async Task AFailedDownload_IsParameterDownloadFailed_AndARedirectIsNeverFollowed(HttpStatusCode status, string expected)
    {
        var handler = new RecordingHandler(status, "{}");

        var error = await Assert.ThrowsAsync<TemplateLoadException>(() => ParameterArguments.BuildAsync(["https://example.com/p.json?sig=SECRET"], Ct, new ParameterFileDownloader(new HttpMessageInvoker(handler))));

        Assert.Equal(ParameterFileDownloader.DownloadFailedCode, error.Code);
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET", error.Message, StringComparison.Ordinal);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task ADownloadOverTheLimit_IsInputTooLarge()
    {
        var handler = new RecordingHandler(HttpStatusCode.OK, new string(' ', (int)KeyVaultClient.MaxResponseBytes + 10));

        var error = await Assert.ThrowsAsync<TemplateLoadException>(() => ParameterArguments.BuildAsync(["https://example.com/big.json"], Ct, new ParameterFileDownloader(new HttpMessageInvoker(handler))));

        Assert.Equal(DiagnosticCodes.InputTooLarge, error.Code);
    }

    [Fact]
    public async Task EveryForm_AppliesInCommandLineOrder_TheLaterValueWins()
    {
        using var directory = new TestDirectory(nameof(EveryForm_AppliesInCommandLineOrder_TheLaterValueWins));
        string file = Write(directory, "p.json", "{ \"parameters\": { \"env\": { \"value\": \"file\" } } }");

        var jsonLast = await ParameterArguments.BuildAsync(["env=inline", file, "{\"env\":{\"value\":\"json\"}}"], Ct);
        var inlineLast = await ParameterArguments.BuildAsync(["{\"env\":{\"value\":\"json\"}}", file, "env=inline"], Ct);

        Assert.Equal("json", jsonLast.Parameters.Values["env"].Value!.GetValue<string>());
        Assert.Equal("inline", inlineLast.Parameters.Values["env"].RawText);
    }

    [Fact]
    public async Task AMissingJsonFile_IsReportedAsAMissingFile_AndAnUnknownToken_AsAnInvalidArgument()
    {
        var missing = await Assert.ThrowsAsync<TemplateLoadException>(() => ParameterArguments.BuildAsync(["no-such-file.json"], Ct));
        var stray = await Assert.ThrowsAsync<ArgumentException>(() => ParameterArguments.BuildAsync(["stray"], Ct));

        Assert.Equal(DiagnosticCodes.FileNotFound, missing.Code);
        Assert.Contains("'stray'", stray.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TheDisplayedUrl_DropsTheQueryAndTheFragment()
    {
        Assert.Equal("https://h/p.json", ParameterFileDownloader.Display("https://h/p.json?sv=1&sig=x#frag"));
        Assert.True(ParameterFileDownloader.IsUrl("HTTPS://h/p.json"));
        Assert.False(ParameterFileDownloader.IsUrl("@https.json"));
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public int Calls { get; private set; }

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            LastRequest = request;
            var response = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            if ((int)status is >= 300 and < 400)
            {
                response.Headers.Location = new Uri("http://elsewhere.example/p.json");
            }

            return Task.FromResult(response);
        }
    }
}
