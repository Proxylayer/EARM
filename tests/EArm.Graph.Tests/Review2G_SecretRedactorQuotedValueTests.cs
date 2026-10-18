using System;
using System.Diagnostics;
using System.Text;
using Xunit;

namespace EArm.Graph.Tests;

/// <summary>
/// Review 2G: <see cref="SecretRedactor.RedactText"/> masks a QUOTED value assigned to a secret name (<c>password="x"</c>, <c>secretText='x'</c>,
/// <c>client_secret: "x"</c>) through the matching closing quote, honouring backslash escapes, while staying a single linear forward scan.
/// </summary>
public sealed class Review2G_SecretRedactorQuotedValueTests
{
    private static string Redact(string text) => SecretRedactor.RedactText(text);

    [Theory]
    [InlineData("password=\"x1\"", "password=\"***\"")]
    [InlineData("password='x1'", "password='***'")]
    [InlineData("secretText='x1'", "secretText='***'")]
    [InlineData("client_secret: \"x1\"", "client_secret: \"***\"")]
    [InlineData("client_secret :   \"x1\"", "client_secret :   \"***\"")]
    [InlineData("Password=\"x1\"", "Password=\"***\"")]
    [InlineData("PASSWORD='x1'", "PASSWORD='***'")]
    [InlineData("clientSecret=\"x1\"", "clientSecret=\"***\"")]
    [InlineData("access_token=\"x1\"", "access_token=\"***\"")]
    [InlineData("refresh_token: 'x1'", "refresh_token: '***'")]
    [InlineData("accessToken=\"x1\"", "accessToken=\"***\"")]
    public void AQuotedValueOfASecretName_IsMasked_AndTheQuotesStay(string input, string expected)
    {
        Assert.Equal(expected, Redact(input));
    }

    [Theory]
    [InlineData("Authorization: Bearer x1")]
    [InlineData("authorization: Bearer x1")]
    [InlineData("AUTHORIZATION: bearer x1")]
    [InlineData("authorization: Basic x1")]
    [InlineData("Authorization=Bearer x1")]
    public void AuthorizationWithAScheme_MasksTheCredential(string input)
    {
        string redacted = Redact(input);

        Assert.DoesNotContain("x1", redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("authorization: Bearer \"x1\"")]
    [InlineData("authorization: \"Bearer x1\"")]
    [InlineData("Authorization: 'Bearer x1'")]
    public void AuthorizationWithAQuotedCredential_MasksTheCredential(string input)
    {
        string redacted = Redact(input);

        Assert.DoesNotContain("x1", redacted, StringComparison.Ordinal);
        Assert.Contains(SecretRedactor.Mask, redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTextAroundAQuotedSecret_IsKept()
    {
        Assert.Equal("Graph refused password=\"***\" (Request_BadRequest)", Redact("Graph refused password=\"quoted-1\" (Request_BadRequest)"));
        Assert.Equal("before secretText='***' after", Redact("before secretText='quoted-2' after"));
    }

    [Fact]
    public void MixedQuotes_TheValueEndsAtItsOwnQuoteKind()
    {
        // A single quote inside a double-quoted value, and the other way round, does not end the value.
        Assert.Equal("password=\"***\" tail", Redact("password=\"it's-secret\" tail"));
        Assert.Equal("password='***' tail", Redact("password='say \"hi\" secret' tail"));
    }

    [Fact]
    public void NestedQuotes_OfTheOtherKind_AreMaskedWithTheValue()
    {
        string redacted = Redact("secretText=\"{'inner': 'deep-secret'}\" ok=1");

        Assert.DoesNotContain("deep-secret", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("inner", redacted, StringComparison.Ordinal);
        Assert.Equal("secretText=\"***\" ok=1", redacted);
    }

    [Fact]
    public void AnEscapedQuote_DoesNotEndTheValue()
    {
        string redacted = Redact("password=\"abc\\\"def-secret-tail\" visible=1");

        Assert.DoesNotContain("def-secret-tail", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("abc", redacted, StringComparison.Ordinal);
        Assert.Equal("password=\"***\" visible=1", redacted);
    }

    [Fact]
    public void AnEscapedBackslashBeforeTheClosingQuote_EndsTheValue()
    {
        // password="a\\" then text: the value is a\ (an escaped backslash); the quote that follows closes it.
        Assert.Equal("password=\"***\" visible=1", Redact("password=\"a\\\\\" visible=1"));
    }

    [Fact]
    public void ABackslashAtTheVeryEnd_DoesNotThrow_AndMasksTheRest()
    {
        Assert.Equal("password=\"***", Redact("password=\"abc\\"));
    }

    [Theory]
    [InlineData("password=\"never-closed rest of the text token=1", "password=\"***")]
    [InlineData("secretText='never closed", "secretText='***")]
    [InlineData("client_secret: \"", "client_secret: \"***")]
    public void AnUnterminatedQuote_MasksToTheEndOfTheText(string input, string expected)
    {
        Assert.Equal(expected, Redact(input));
    }

    [Fact]
    public void MultiplePairsOnOneLine_AreAllMasked_AndTheNonSecretOnesStay()
    {
        string redacted = Redact("user=\"alice\" password=\"p1\" mode='fast' secretText='p2', client_secret: \"p3\"; id=7 access_token=p4");

        Assert.Equal("user=\"alice\" password=\"***\" mode='fast' secretText='***', client_secret: \"***\"; id=7 access_token=***", redacted);
    }

    [Fact]
    public void MultiplePairsAcrossLines_AreAllMasked()
    {
        string redacted = Redact("password=\"a\"\nsecretText='b'\nname=\"c\"\nrefresh_token: \"d\"");

        Assert.Equal("password=\"***\"\nsecretText='***'\nname=\"c\"\nrefresh_token: \"***\"", redacted);
    }

    [Fact]
    public void AValueSpanningLines_IsMaskedThroughTheClosingQuote()
    {
        string redacted = Redact("secretText=\"-----BEGIN KEY-----\nMIIEvQ\n-----END KEY-----\" after=1");

        Assert.Equal("secretText=\"***\" after=1", redacted);
    }

    [Fact]
    public void ASecretNameInsideANonSecretQuotedValue_IsStillFound()
    {
        string redacted = Redact("message=\"the call failed with password=hunter2 and more\" code=400");

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.Contains("code=400", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AQuotedValueOfANonSecretName_IsLeftAlone()
    {
        const string text = "displayName=\"Contoso Admins\" description='plain text' $filter=\"startswith(displayName,'a')\" skiptoken=\"abc\"";

        Assert.Equal(text, Redact(text));
    }

    [Fact]
    public void Unicode_InsideAndAroundTheQuotedValue_IsHandled()
    {
        string redacted = Redact("名前=\"山田\" password=\"пароль-секрет-密码-🔑\" ünïcode='keep' secretText='ключ-🔐' end");

        Assert.DoesNotContain("пароль", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("密码", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("🔑", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("ключ", redacted, StringComparison.Ordinal);
        Assert.Contains("名前=\"山田\"", redacted, StringComparison.Ordinal);
        Assert.Contains("ünïcode='keep'", redacted, StringComparison.Ordinal);
        Assert.EndsWith(" end", redacted, StringComparison.Ordinal);
    }

    [Fact]
    public void AVeryLongQuotedValue_IsMaskedCompletely()
    {
        string secret = new('s', 500_000);

        string redacted = Redact("password=\"" + secret + "\" tail=1");

        Assert.Equal("password=\"***\" tail=1", redacted);
    }

    [Fact]
    public void ARedactedText_IsStable_WhenRedactedAgain()
    {
        string once = Redact("password=\"a b c\" secretText='d' client_secret: \"e\"");

        Assert.Equal(once, Redact(once));
    }

    // ---- linear time ----

    [Theory]
    [InlineData("closed pairs")]
    [InlineData("unterminated opens")]
    [InlineData("escape runs")]
    [InlineData("open quotes only")]
    [InlineData("alternating quote kinds")]
    [InlineData("secret names without values")]
    [Trait("Category", "Timing")]
    public void QuotedValueScan_IsLinear_OnAdversarialInput(string shape)
    {
        const int Length = 200_000;
        var builder = new StringBuilder(Length + 64);
        switch (shape)
        {
            case "closed pairs":
                while (builder.Length < Length)
                {
                    builder.Append("password=\"x\" ");
                }

                break;
            case "unterminated opens":
                // Every name opens a quote that the next name's quote closes: each scan must consume what it masks, never rescan it.
                while (builder.Length < Length)
                {
                    builder.Append("password=\"password=\"");
                }

                break;
            case "escape runs":
                builder.Append("password=\"");
                while (builder.Length < Length)
                {
                    builder.Append("\\\\\\\"");
                }

                break;
            case "open quotes only":
                while (builder.Length < Length)
                {
                    builder.Append("secretText='");
                }

                break;
            case "alternating quote kinds":
                while (builder.Length < Length)
                {
                    builder.Append("token=\"'token='\"");
                }

                break;
            default:
                while (builder.Length < Length)
                {
                    builder.Append("password= secret: token= ");
                }

                break;
        }

        string input = builder.ToString();
        Redact("warm=up password=\"x\" secretText='y'");
        var watch = Stopwatch.StartNew();
        string redacted = Redact(input);
        watch.Stop();

        // Category=Timing: a wall-clock bound, generous on purpose (200,000 characters take milliseconds; a quadratic rescan takes minutes), run serially by test.ps1.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(30), $"[{shape}] took {watch.Elapsed}");
        Assert.NotNull(redacted);
    }
}
