using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading.Tasks;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Review 3I, finding 5: nested scopes keep what the inner one registered, short derived values are matched on word boundaries only, every encoded form
/// is masked on its own, and the scan is a single pass whatever the number of registered values.
/// </summary>
public sealed class Review3I_SecretScrubberTests
{
    private const string Secret = "p@ss w&rd+1";

    // ---- nested scopes ----

    [Fact]
    public async Task ValuesRegisteredInAnInnerScopeSurviveTheEndOfThatScope()
    {
        using var outer = SecretScrubber.Begin();

        await InnerRunAsync("inner-run-secret");

        Assert.Equal("***", SecretScrubber.Scrub("inner-run-secret"));
    }

    private static async Task InnerRunAsync(string secret)
    {
        // What DeploymentRunner / DeleteCommand do: open a scope, learn secrets inside it, finish.
        using var scope = SecretScrubber.Begin();
        await Task.Yield();
        SecretScrubber.Register(secret);
        Assert.Equal("***", SecretScrubber.Scrub(secret));
    }

    [Fact]
    public void AScopeOpenedWithoutAnOuterOneStillRestoresNothingOnDispose()
    {
        var scope = SecretScrubber.Begin();
        SecretScrubber.Register("lonely-secret");
        scope.Dispose();

        Assert.Equal("lonely-secret", SecretScrubber.Scrub("lonely-secret"));
        Assert.False(SecretScrubber.HasValues);
    }

    // ---- over-matching ----

    [Fact]
    public void ADerivedShortValueIsMatchedOnWordBoundariesOnly()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register("pass", secureDeclared: false);

        Assert.Equal("{\"passwordCredentials\":[]}", SecretScrubber.Scrub("{\"passwordCredentials\":[]}"));
        Assert.Equal("a *** b", SecretScrubber.Scrub("a pass b"));
        Assert.Equal("***,***.***", SecretScrubber.Scrub("pass,pass.pass"));
        Assert.Equal("bypass pass2 _***", SecretScrubber.Scrub("bypass pass2 _pass"));
    }

    [Fact]
    public void ASecureDeclaredShortValueKeepsPlainSubstringMatching()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register("pass");

        Assert.Equal("***wordCredentials", SecretScrubber.Scrub("passwordCredentials"));
    }

    [Fact]
    public void ADerivedValueOfEightCharactersOrMoreIsMatchedAnywhere()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register("12345678", secureDeclared: false);

        Assert.Equal("a***b", SecretScrubber.Scrub("a12345678b"));
    }

    [Fact]
    public void ARegistrationAsSecureWinsOverAnEarlierDerivedOne()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register("pass", secureDeclared: false);
        SecretScrubber.Register("pass", secureDeclared: true);

        Assert.Equal("***word", SecretScrubber.Scrub("password"));
    }

    [Fact]
    public void ADerivedRegistrationAfterASecureOneDoesNotWeakenIt()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register("pass");
        SecretScrubber.Register("pass", secureDeclared: false);

        Assert.Equal("***word", SecretScrubber.Scrub("password"));
    }

    [Fact]
    public void OverlappingMatchesAreMaskedAsOneRun()
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register("abc");
        SecretScrubber.Register("bcdefgh");

        // The shorter value starts first; the longer one must not leave its tail behind.
        Assert.Equal("***", SecretScrubber.Scrub("abcdefgh"));
        Assert.Equal("x*** y", SecretScrubber.Scrub("xabcdefgh y"));
    }

    // ---- every encoded form is masked on its own ----

    public static TheoryData<string, string> EncodedForms()
    {
        var data = new TheoryData<string, string>
        {
            { "raw", Secret },
            { "EscapeDataString", Uri.EscapeDataString(Secret) },
            { "UrlEncode", WebUtility.UrlEncode(Secret) },
            { "json", JsonEncodedText.Encode(Secret).ToString() },
            { "json relaxed", JsonEncodedText.Encode(Secret, JavaScriptEncoder.UnsafeRelaxedJsonEscaping).ToString() },
            { "html", WebUtility.HtmlEncode(Secret) },
            { "base64", Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)) },
            { "base64 unpadded", Convert.ToBase64String(Encoding.UTF8.GetBytes(Secret)).TrimEnd('=') },
            { "lower-case percent hex", System.Text.RegularExpressions.Regex.Replace(Uri.EscapeDataString(Secret), "%[0-9A-F]{2}", static m => m.Value.ToLowerInvariant()) },
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(EncodedForms))]
    public void EachEncodedFormIsMaskedOnItsOwn(string name, string form)
    {
        using var scope = SecretScrubber.Begin();
        SecretScrubber.Register(Secret);

        Assert.True(SecretScrubber.Scrub("value=" + form + "&x=1") == "value=***&x=1", name);
    }

    [Fact]
    public void LowerCaseUnicodeEscapesOfJsonAreMasked()
    {
        using var scope = SecretScrubber.Begin();
        const string secret = "p<ss>w&rd";
        SecretScrubber.Register(secret);
        var upper = JsonEncodedText.Encode(secret).ToString();
        var lower = upper.Replace("\\u003C", "\\u003c", StringComparison.Ordinal).Replace("\\u003E", "\\u003e", StringComparison.Ordinal);

        Assert.Contains("\\u003C", upper, StringComparison.Ordinal);
        Assert.Equal("[***]", SecretScrubber.Scrub("[" + upper + "]"));
        Assert.Equal("[***]", SecretScrubber.Scrub("[" + lower + "]"));
    }

    // ---- one pass whatever the number of secrets ----

    [Fact]
    [Trait("Category", "Timing")]
    public void ManySecretsAreAllMaskedInOnePass()
    {
        using var scope = SecretScrubber.Begin();
        var secrets = Enumerable.Range(0, 2000).Select(static i => "s3cr3t-" + Guid.NewGuid().ToString("N") + "-" + i).ToArray();
        foreach (var secret in secrets)
        {
            SecretScrubber.Register(secret);
        }

        var text = new StringBuilder();
        foreach (var secret in secrets.Take(40))
        {
            text.Append("field=").Append(secret).Append(" ; ");
        }

        while (text.Length < 2000)
        {
            text.Append("ordinary text without any secret in it; ");
        }

        var message = text.ToString();
        var scrubbed = SecretScrubber.Scrub(message);
        Assert.DoesNotContain("s3cr3t-", scrubbed, StringComparison.Ordinal);
        Assert.Equal(40, scrubbed.Split("***").Length - 1);

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 200; i++)
        {
            _ = SecretScrubber.Scrub(message);
        }

        stopwatch.Stop();

        // Category=Timing, generous: a scan of 2,000 characters against 2,000 registered values takes well under a millisecond, so even 100 ms each (20 s in total) is
        // still far below what the old per-value loop's order would cost, and survives a heavily loaded machine.
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(20), $"200 scrubs took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    [Trait("Category", "Timing")]
    public void RegisteringManyValuesOneByOneStaysCheap()
    {
        using var scope = SecretScrubber.Begin();
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < 2000; i++)
        {
            SecretScrubber.Register("registered-value-" + Guid.NewGuid().ToString("N"));
        }

        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(60), $"2,000 registrations took {stopwatch.ElapsedMilliseconds} ms");
    }
}
