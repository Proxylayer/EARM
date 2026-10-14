using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using EntraArm.Az;
using Xunit;

namespace EntraArm.Az.Tests;

/// <summary>
/// Property checks for <see cref="SecretRedactor"/> over generated input (fixed seed, plain theories): a secret value
/// never survives redaction in any of the shapes az and Graph print it, redaction is idempotent, text without secret
/// names is left alone, and <see cref="SecretRedactor.RedactJson"/> masks at any depth without touching its input.
/// </summary>
public sealed class SecretRedactorPropertyTests
{
    private const int Seed = 20261014;
    private const int Cases = 60;

    private static readonly string[] SecretNames =
    [
        "password", "Password", "PASSWORD", "secretText", "client_secret", "clientSecret", "accessToken", "access_token",
        "refresh_token", "idToken", "x-ms-token", "myPasswordHint", "keyCredentials", "app_secret", "credential", "Authorization",
    ];

    private static readonly string[] PlainNames = ["displayName", "description", "userPrincipalName", "$filter", "$select", "mail", "id", "appId"];

    private const string ValueAlphabet = "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789-_.~+/!@#%^&*()[]{}<>?|";

    public static TheoryData<string> Formats()
    {
        return new TheoryData<string>
        {
            "name=value",
            "name: value",
            "name:value",
            "query",
            "json",
            "json-spaced",
            "prefixed",
            "dashed-argument",
        };
    }

    private static string Value(Random random, int index)
    {
        // Distinct, never-substring-of-anything canary: a marker plus random characters.
        var builder = new StringBuilder("canary").Append(index.ToString("D3", System.Globalization.CultureInfo.InvariantCulture)).Append('-');
        int length = random.Next(6, 28);
        for (int i = 0; i < length; i++)
        {
            builder.Append(ValueAlphabet[random.Next(ValueAlphabet.Length)]);
        }

        return builder.ToString();
    }

    private static string Format(string format, string name, string value)
    {
        return format switch
        {
            "name=value" => $"{name}={value}",
            "name: value" => $"{name}: {value}",
            "name:value" => $"{name}:{value}",
            "query" => $"https://graph.microsoft.com/v1.0/x?$top=5&{name}={value}&$select=id",
            "json" => "{\"" + name + "\":\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"}",
            "json-spaced" => "{ \"" + name + "\"  :  \"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\" , \"other\": 1 }",
            "prefixed" => $"WARNING: request failed ({name}={value}) see log",
            "dashed-argument" => $"--{name}={value}",
            _ => throw new ArgumentOutOfRangeException(nameof(format)),
        };
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void ASecretValue_NeverSurvivesRedactText(string format)
    {
        var random = new Random(Seed);
        for (int i = 0; i < Cases; i++)
        {
            // A "--authorization=..." argument is a known gap, pinned by Defect_PrefixedAuthorizationNames_AreNotMasked.
            string name = SecretNames[random.Next(SecretNames.Length)];
            if (format == "dashed-argument" && name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            {
                name = "password";
            }

            string value = Value(random, i);
            string input = Format(format, name, value);

            string redacted = SecretRedactor.RedactText(input);

            Assert.True(!redacted.Contains(value, StringComparison.Ordinal), $"[{format}] case {i}: '{input}' leaked the value: '{redacted}'");
            Assert.Contains(SecretRedactor.Mask, redacted, StringComparison.Ordinal);
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void RedactText_IsIdempotent(string format)
    {
        var random = new Random(Seed + 1);
        for (int i = 0; i < Cases; i++)
        {
            string name = (i % 3 == 0 ? PlainNames : SecretNames)[random.Next(i % 3 == 0 ? PlainNames.Length : SecretNames.Length)];
            string input = Format(format, name, Value(random, i));

            string once = SecretRedactor.RedactText(input);
            string twice = SecretRedactor.RedactText(once);

            Assert.True(once == twice, $"[{format}] case {i}: '{input}' -> '{once}' -> '{twice}'");
        }
    }

    [Theory]
    [MemberData(nameof(Formats))]
    public void TextWithoutASecretName_IsNotChanged(string format)
    {
        var random = new Random(Seed + 2);
        for (int i = 0; i < Cases; i++)
        {
            string name = PlainNames[random.Next(PlainNames.Length)];
            // Alphanumeric values only: punctuation-heavy values could legitimately look like a JWT or bearer token.
            string value = "v" + i.ToString(System.Globalization.CultureInfo.InvariantCulture) + new string(Enumerable.Range(0, random.Next(3, 12)).Select(_ => (char)('a' + random.Next(26))).ToArray());
            string input = Format(format, name, value);

            Assert.True(SecretRedactor.RedactText(input) == input, $"[{format}] case {i}: '{input}' changed to '{SecretRedactor.RedactText(input)}'");
        }
    }

    /// <summary>
    /// Documents the paging-token asymmetry: <c>IsSecretName</c> exempts <c>$skiptoken</c> / <c>deltatoken</c>, but the JSON-text pass of
    /// <c>RedactText</c> masks any property whose name contains "token", so a paging token inside JSON (or behind a dashed argument) is
    /// masked as well. Over-redaction only; harmless, pinned so a change is noticed.
    /// </summary>
    [Fact]
    public void PagingTokens_AreExemptByName_ButMaskedInsideJsonTextAndDashedArguments()
    {
        Assert.Equal("$skiptoken=abc", SecretRedactor.RedactText("$skiptoken=abc"));
        Assert.Equal("{\"$skiptoken\": \"***\"}", SecretRedactor.RedactText("{\"$skiptoken\":\"abc\"}"));
        Assert.Equal("--$skiptoken=***", SecretRedactor.RedactText("--$skiptoken=abc"));
    }

    /// <summary>
    /// DEFECT (low severity, not fixed here: src/ is out of scope for the test pass). SecretRedactor.IsSecretName matches "authorization" only
    /// as the whole name, so credential-carrying names that merely contain it (<c>Proxy-Authorization</c>, <c>--authorization</c>,
    /// <c>x-ms-authorization</c>) are not masked in free text and the credential reaches logs / exception messages.
    /// Expected: the value after any name containing "authorization" is masked. Actual: left as is (src/EntraArm.Az/SecretRedactor.cs, IsSecretName).
    /// </summary>
    [Theory(Skip = "DEFECT: names containing authorization (Proxy-Authorization, --authorization, x-ms-authorization) are not treated as secret names (SecretRedactor.IsSecretName).")]
    [InlineData("Proxy-Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA==")]
    [InlineData("--authorization=opaque-credential-value", "opaque-credential-value")]
    [InlineData("x-ms-authorization: opaque-credential-value", "opaque-credential-value")]
    public void Defect_PrefixedAuthorizationNames_AreNotMasked(string text, string secret)
    {
        Assert.DoesNotContain(secret, SecretRedactor.RedactText(text), StringComparison.Ordinal);
    }
    [Fact]
    public void BearerTokensAndJwts_AreMasked_WhereverTheyAreEmbedded()
    {
        var random = new Random(Seed + 3);
        for (int i = 0; i < Cases; i++)
        {
            string segment() => new string(Enumerable.Range(0, random.Next(10, 40)).Select(_ => "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789_-"[random.Next(64)]).ToArray());
            string jwt = "eyJ" + segment() + "." + segment() + "." + segment();
            string opaque = segment();
            string text = $"call failed: header Bearer {opaque} and token {jwt}; retry";

            string redacted = SecretRedactor.RedactText(text);

            Assert.DoesNotContain(opaque, redacted, StringComparison.Ordinal);
            Assert.DoesNotContain(jwt, redacted, StringComparison.Ordinal);
            Assert.StartsWith("call failed:", redacted, StringComparison.Ordinal);
            Assert.EndsWith("; retry", redacted, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RedactArgument_MasksSecretNamedPairs_AndRedactsTokensInBareValues()
    {
        var random = new Random(Seed + 4);
        for (int i = 0; i < Cases; i++)
        {
            string name = SecretNames[random.Next(SecretNames.Length)];
            string value = Value(random, i);

            string pair = SecretRedactor.RedactArgument(name + "=" + value);
            string bare = SecretRedactor.RedactArgument("Bearer " + "tok" + value.Replace("/", string.Empty, StringComparison.Ordinal).Replace("+", string.Empty, StringComparison.Ordinal).Replace("!", string.Empty, StringComparison.Ordinal));

            Assert.Equal(name + "=" + SecretRedactor.Mask, pair);
            Assert.StartsWith("Bearer " + SecretRedactor.Mask, bare, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void RedactValue_MasksBySecretName_AndHandlesNull()
    {
        Assert.Equal(SecretRedactor.Mask, SecretRedactor.RedactValue("password", "whatever"));
        Assert.Equal(SecretRedactor.Mask, SecretRedactor.RedactValue("x-api-token", string.Empty));
        Assert.Equal("plain", SecretRedactor.RedactValue("displayName", "plain"));
        Assert.Equal(string.Empty, SecretRedactor.RedactValue("password", null));
        Assert.Equal(string.Empty, SecretRedactor.RedactText(null));
        Assert.Equal(string.Empty, SecretRedactor.RedactText(string.Empty));
        Assert.Throws<ArgumentNullException>(() => SecretRedactor.RedactArgument(null!));
    }

    [Fact]
    public void RedactJson_MasksSecretNamedProperties_AtAnyDepth_WithoutMutatingTheInput()
    {
        var random = new Random(Seed + 5);
        for (int i = 0; i < Cases; i++)
        {
            string value = Value(random, i);
            JsonNode node = JsonValue.Create(value)!;
            int depth = random.Next(1, 7);
            for (int d = 0; d < depth; d++)
            {
                string key = d == 0 ? SecretNames[random.Next(SecretNames.Length)] : "level" + d;
                node = d % 2 == 0 ? new JsonObject { [key] = node, ["keep"] = "kept-" + d } : new JsonArray(node, JsonValue.Create(d)!);
            }

            var root = new JsonObject { ["root"] = node };
            string before = root.ToJsonString();

            var redacted = SecretRedactor.RedactJson(root)!.ToJsonString();

            Assert.True(!redacted.Contains(value, StringComparison.Ordinal), $"case {i}: leaked in {redacted}");
            Assert.Equal(before, root.ToJsonString());
            Assert.Contains("kept-0", redacted, StringComparison.Ordinal);
        }

        Assert.Null(SecretRedactor.RedactJson(null));
        Assert.Equal("42", SecretRedactor.RedactJson(JsonValue.Create(42))!.ToJsonString());
    }

    [Fact]
    public void RedactJson_OfTheSameDocument_IsIdempotent()
    {
        var document = JsonNode.Parse("""
            { "name": "x", "secretText": "a", "nested": { "password": "b", "list": [ { "client_secret": "c" }, 3, null, "plain" ] }, "$skiptoken": "page-2" }
            """)!;

        string once = SecretRedactor.RedactJson(document)!.ToJsonString();
        string twice = SecretRedactor.RedactJson(JsonNode.Parse(once))!.ToJsonString();

        Assert.Equal(once, twice);
        Assert.Contains("page-2", once, StringComparison.Ordinal);
        Assert.DoesNotContain("\"a\"", once, StringComparison.Ordinal);
        Assert.Equal(new List<string> { "name", "secretText", "nested", "$skiptoken" }, ((JsonObject)JsonNode.Parse(once)!).Select(p => p.Key).ToList());
    }

    /// <summary>
    /// DEFECT (medium-low severity, not fixed here: src/ is out of scope for the test pass). SecretRedactor.RedactText takes quadratic time on a long
    /// unbroken run of word characters: the KeyValueSecretPattern (src/EntraArm.Az/SecretRedactor.cs, `[\w$.-]+` followed by `\s*[=:]`) is retried from
    /// every start position and scans to the end of the run each time. Measured on this machine: 4,000 chars 0.33 s, 8,000 chars 1.3 s, 16,000 chars 5.4 s,
    /// ~200,000 chars effectively never returns. GraphException / AzCliException redact stderr and messages on construction, so one long unspaced token
    /// (a base64 body, a long opaque id list) in az output can stall the CLI. Expected: linear time (under a second for 100,000 characters).
    /// </summary>
    [Fact(Skip = "DEFECT: RedactText is quadratic on long unbroken word-character runs (16,000 chars take 5+ s); this input would not return.")]
    public void Defect_RedactText_IsQuadraticOnALongUnbrokenToken()
    {
        string input = new string('x', 100_000);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        SecretRedactor.RedactText(input);

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(2), "took " + watch.Elapsed);
    }
}
