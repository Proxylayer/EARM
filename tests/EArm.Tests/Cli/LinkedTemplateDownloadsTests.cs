using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using EArm.Cli.Commands;
using EArm.Engine.Planning.Nested;
using Xunit;

namespace EArm.Tests.Cli;

/// <summary>
/// <see cref="LinkedTemplateDownloads.ForRun()"/>: one deployment run reads its https linked templates in the offline expansion before sign-in, the
/// validation and the plan; each URL is downloaded once per run.
/// </summary>
public sealed class LinkedTemplateDownloadsTests
{
    private const string Url = "https://templates.contoso.example/linked.json?sv=2024&sig=secret";

    private static LinkedLocation Remote(string url)
    {
        return new LinkedLocation(url, IsRemote: true);
    }

    [Fact]
    public void ARunReader_DownloadsEachUrlOnce_AndServesLaterReadsFromMemory()
    {
        var calls = new List<string>();
        var reader = LinkedTemplateDownloads.ForRun((uri, _) =>
        {
            calls.Add(uri.AbsoluteUri);
            return Task.FromResult(Encoding.UTF8.GetBytes("{}"));
        });

        byte[] first = reader.Read(Remote(Url), "Linked template", TestContext.Current.CancellationToken);
        byte[] second = reader.Read(Remote(Url), "Linked template", TestContext.Current.CancellationToken);
        reader.Read(Remote(Url), "Linked template", TestContext.Current.CancellationToken);

        Assert.Equal([Url], calls);
        Assert.Equal(first, second);
    }

    [Fact]
    public void ARunReader_KeysOnTheWholeUrl_IncludingTheQueryString()
    {
        int calls = 0;
        var reader = LinkedTemplateDownloads.ForRun((_, _) =>
        {
            calls++;
            return Task.FromResult(Encoding.UTF8.GetBytes("{}"));
        });

        reader.Read(Remote(Url), "Linked template", TestContext.Current.CancellationToken);
        reader.Read(Remote("https://templates.contoso.example/linked.json?sv=2024&sig=other"), "Linked template", TestContext.Current.CancellationToken);
        reader.Read(Remote("https://templates.contoso.example/other.json"), "Linked template", TestContext.Current.CancellationToken);

        Assert.Equal(3, calls);
    }

    [Fact]
    public void ARunReader_DoesNotKeepAFailedDownload()
    {
        int calls = 0;
        var reader = LinkedTemplateDownloads.ForRun((_, _) =>
        {
            calls++;
            return calls == 1 ? Task.FromException<byte[]>(new HttpRequestException("unreachable")) : Task.FromResult(Encoding.UTF8.GetBytes("{}"));
        });

        Assert.Throws<HttpRequestException>(() => reader.Read(Remote(Url), "Linked template", TestContext.Current.CancellationToken));
        reader.Read(Remote(Url), "Linked template", TestContext.Current.CancellationToken);
        reader.Read(Remote(Url), "Linked template", TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
    }

    [Fact]
    public void TwoRuns_DoNotShareDownloads()
    {
        int calls = 0;
        Func<Uri, System.Threading.CancellationToken, Task<byte[]>> download = (_, _) =>
        {
            calls++;
            return Task.FromResult(Encoding.UTF8.GetBytes("{}"));
        };

        LinkedTemplateDownloads.ForRun(download).Read(Remote(Url), "Linked template", TestContext.Current.CancellationToken);
        LinkedTemplateDownloads.ForRun(download).Read(Remote(Url), "Linked template", TestContext.Current.CancellationToken);

        Assert.Equal(2, calls);
    }
}
