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
            string name = SecretNames[random.Next(SecretNames.Length)];

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
    /// Any name that contains "authorization" is a secret name, not only the whole word: the name is also tested part by part (split on
    /// <c>-</c>, <c>_</c>, <c>.</c>, <c>$</c>), so prefixes and leading dashes do not hide it. The "Basic" scheme word after the name is
    /// swallowed like "Bearer", so the credential itself (not just the scheme) is masked.
    /// </summary>
    [Theory]
    [InlineData("Proxy-Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA==")]
    [InlineData("--authorization=opaque-credential-value", "opaque-credential-value")]
    [InlineData("x-ms-authorization: opaque-credential-value", "opaque-credential-value")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "dXNlcjpwYXNzd29yZA==")]
    [InlineData("proxy_authorization=opaque-credential-value", "opaque-credential-value")]
    [InlineData("X.Authorization.Header: Negotiate YIIabc123opaque", "YIIabc123opaque")]
    [InlineData("WWW-Authorization-Info: Bearer opaque-credential-value", "opaque-credential-value")]
    [InlineData("{\"Proxy-Authorization\": \"Basic dXNlcjpwYXNzd29yZA==\"}", "dXNlcjpwYXNzd29yZA==")]
    public void PrefixedAuthorizationNames_AreMasked(string text, string secret)
    {
        Assert.DoesNotContain(secret, SecretRedactor.RedactText(text), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Proxy-Authorization")]
    [InlineData("--authorization")]
    [InlineData("x-ms-authorization")]
    [InlineData("X_MS_AUTHORIZATION")]
    [InlineData("--Proxy-Authorization")]
    [InlineData("x.authorization.y")]
    [InlineData("--client-secret")]
    [InlineData("--access-token")]
    [InlineData("x-ms-token")]
    public void IsSecretName_TestsTheWholeNameAndItsParts(string name)
    {
        Assert.True(SecretRedactor.IsSecretName(name), name);
        Assert.Equal(name + "=" + SecretRedactor.Mask, SecretRedactor.RedactArgument(name + "=value"));
        Assert.Equal(SecretRedactor.Mask, SecretRedactor.RedactValue(name, "value"));
        Assert.Equal(SecretRedactor.Mask, SecretRedactor.RedactJson(new JsonObject { [name] = "value" })![name]!.GetValue<string>());
    }

    [Theory]
    [InlineData("$skiptoken")]
    [InlineData("skiptoken")]
    [InlineData("$deltatoken")]
    [InlineData("deltaToken")]
    [InlineData("SkipToken")]
    [InlineData("displayName")]
    [InlineData("auth")]
    [InlineData("author")]
    [InlineData("x-ms-client-request-id")]
    [InlineData("$filter")]
    [InlineData("-")]
    [InlineData("--")]
    [InlineData("$")]
    public void IsSecretName_KeepsThePagingTokenAndOrdinaryNamesPlain(string name)
    {
        Assert.False(SecretRedactor.IsSecretName(name), name);
        Assert.Equal("value", SecretRedactor.RedactValue(name, "value"));
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
    /// RedactText is linear: every pass (key/value scan, JSON, bearer and JWT patterns) stays well under a second on 100,000 adversarial
    /// characters. The key/value pattern used to be retried from every start position of a long unbroken word run (16,000 chars took
    /// seconds), and the JWT pattern from every <c>-eyJ</c> boundary of a long base64-looking run. The inputs are the shapes that make a
    /// backtracking engine super-linear: unbroken runs, runs of quotes, repeated scheme words and separators, unterminated JSON.
    /// </summary>
    [Theory]
    [InlineData("unbroken word run")]
    [InlineData("quotes")]
    [InlineData("bearer words")]
    [InlineData("base64")]
    [InlineData("jwt segments")]
    [InlineData("name=value chain")]
    [InlineData("name: value chain")]
    [InlineData("unterminated json name")]
    [InlineData("unterminated json value")]
    [InlineData("backslash run in json value")]
    [InlineData("spaces after separator")]
    [InlineData("many names and spaces")]
    [InlineData("bearer and spaces")]
    [InlineData("dotted dash name run")]
    [InlineData("password words")]
    [InlineData("alternating pairs")]
    public void RedactText_IsLinear_OnAdversarialInput(string shape)
    {
        const int Length = 100_000;
        string input = shape switch
        {
            "unbroken word run" => new string('x', Length),
            "quotes" => new string('"', Length),
            "bearer words" => string.Concat(Enumerable.Repeat("Bearer ", Length / 7)),
            "base64" => new string('A', Length),
            "jwt segments" => string.Concat(Enumerable.Repeat("eyJAAAAAAAA-", Length / 12)),
            "name=value chain" => string.Concat(Enumerable.Repeat("a=", Length / 2)),
            "name: value chain" => string.Concat(Enumerable.Repeat("a: ", Length / 3)),
            "unterminated json name" => "\"" + string.Concat(Enumerable.Repeat("password", Length / 8)),
            "unterminated json value" => "{\"password\":\"" + new string('a', Length),
            "backslash run in json value" => "{\"secret\":\"" + new string((char)92, Length),
            "spaces after separator" => "a=" + new string(' ', Length),
            "many names and spaces" => string.Concat(Enumerable.Repeat("a      ", Length / 7)),
            "bearer and spaces" => "Bearer" + new string(' ', Length),
            "dotted dash name run" => string.Concat(Enumerable.Repeat("x.-", Length / 3)),
            "password words" => string.Concat(Enumerable.Repeat("password=", Length / 9)),
            "alternating pairs" => string.Concat(Enumerable.Repeat("a=b;password=c,", Length / 15)),
            _ => throw new ArgumentOutOfRangeException(nameof(shape)),
        };

        // Warm up (JIT, lazy DFA construction) so the measurement is the steady-state cost of the input.
        SecretRedactor.RedactText("warm=up Bearer abc {\"password\":\"x\"} eyJabcdefgh.abcdefgh.abcdefgh");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        string redacted = SecretRedactor.RedactText(input);
        watch.Stop();

        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), $"[{shape}] took {watch.Elapsed}");
        Assert.NotNull(redacted);
    }

    [Fact]
    public void RedactText_OfALongUnbrokenToken_IsLinear_AndUnchanged()
    {
        string input = new string('x', 100_000);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        string redacted = SecretRedactor.RedactText(input);
        watch.Stop();

        Assert.Equal(input, redacted);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(1), "took " + watch.Elapsed);
    }

    /// <summary>
    /// Every pass must keep finding secrets in a huge text. (A regex-based version of the bearer and JWT passes silently missed matches
    /// in texts of about 100,000 characters or more: .NET's NonBacktracking engine drops <c>\b</c> matches there. The passes are scanners now.)
    /// </summary>
    [Theory]
    [InlineData(1_000)]
    [InlineData(60_000)]
    [InlineData(100_000)]
    [InlineData(300_000)]
    public void RedactText_StillMasksEverySecretKind_InAHugeText(int noiseLength)
    {
        string noise = new string('x', noiseLength);
        string jwt = "eyJabcdefghij.klmnopqrstuv.wxyz0123456789";

        string redacted = SecretRedactor.RedactText(
            noise + " password=hunter2-canary " + noise + " Bearer opaque-canary-token " + noise + " " + jwt + " " + noise + " {\"client_secret\": \"json-canary\"}");

        Assert.DoesNotContain("hunter2-canary", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("opaque-canary-token", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("klmnopqrstuv", redacted, StringComparison.Ordinal);
        Assert.DoesNotContain("json-canary", redacted, StringComparison.Ordinal);
        Assert.Contains("password=***", redacted, StringComparison.Ordinal);
        Assert.Equal(4 * noiseLength, redacted.Count(c => c == 'x'));
    }

    [Theory]
    [InlineData("{\"password\": \"abc", "{\"password\": \"***")]
    [InlineData("stderr: {\"x\": 1, \"client_secret\": \"abc\\\"def", "stderr: {\"x\": 1, \"client_secret\": \"***")]
    [InlineData("{\"a\": \"b\", \"token\" : \"abc\"} tail", "{\"a\": \"b\", \"token\": \"***\"} tail")]
    [InlineData("{\"displayName\": \"password\"}", "{\"displayName\": \"password\"}")]
    [InlineData("{\"password\": 5}", "{\"password\": 5}")]
    [InlineData("{\"password\" \n : \n \"a\\\"b\", \"n\": 1}", "{\"password\": \"***\", \"n\": 1}")]
    public void JsonPass_MasksStringValues_AndAValueCutOffByTruncationToTheEnd(string input, string expected)
    {
        Assert.Equal(expected, SecretRedactor.RedactText(input));
        Assert.Equal(expected, SecretRedactor.RedactText(expected));
    }

    [Theory]
    [InlineData("Bearer abc.def-ghi_jkl~+/==", "Bearer ***")]
    [InlineData("header BEARER   tok1", "header Bearer ***")]
    [InlineData("xBearer tok1", "xBearer tok1")]
    [InlineData("Bearer ", "Bearer ")]
    [InlineData("Bearer", "Bearer")]
    [InlineData("Bearertok1", "Bearertok1")]
    [InlineData("Bearer Bearer tok1", "Bearer *** tok1")]
    [InlineData("a eyJabcdefgh.abcdefgh.abcdefgh b", "a *** b")]
    [InlineData("a eyJabcdefg.abcdefgh.abcdefgh b", "a eyJabcdefg.abcdefgh.abcdefgh b")]
    [InlineData("xeyJabcdefgh.abcdefgh.abcdefgh", "xeyJabcdefgh.abcdefgh.abcdefgh")]
    [InlineData("-eyJabcdefgh.abcdefgh.abcdefgh-", "-***")]
    [InlineData("EYJabcdefgh.abcdefgh.abcdefgh", "EYJabcdefgh.abcdefgh.abcdefgh")]
    [InlineData("eyJabcdefgh.abcdefgh.abcdefgh.abcdefgh", "***.abcdefgh")]
    [InlineData("eyJ-eyJabcdefgh.abcdefgh.abcdefgh", "***")]
    public void BearerAndJwtPasses_KeepTheirShapes(string input, string expected)
    {
        Assert.Equal(expected, SecretRedactor.RedactText(input));
    }

    /// <summary>
    /// The old key/value pass rescanned the value of every non-secret pair by recursion, one stack frame per pair, so a long
    /// <c>a=a=a=...</c> chain overflowed the stack and killed the process. The pass is now one forward scan.
    /// </summary>
    [Theory]
    [InlineData("a=")]
    [InlineData("a: ")]
    [InlineData("x.y=")]
    public void RedactText_OfAVeryLongChainOfNonSecretPairs_DoesNotOverflowTheStack(string pair)
    {
        string chain = string.Concat(Enumerable.Repeat(pair, 100_000 / pair.Length));

        Assert.Equal(chain, SecretRedactor.RedactText(chain));
        Assert.Equal(chain + "password=***", SecretRedactor.RedactText(chain + "password=hunter2"));
    }

    [Theory]
    [InlineData("a=b password=hunter2 c=d", "a=b password=*** c=d")]
    [InlineData("WARNING: secretText=hunter2 (retry)", "WARNING: secretText=*** (retry)")]
    [InlineData("a=b&password=hunter2&c=d", "a=b&password=***")]
    [InlineData("a=password=hunter2", "a=password=***")]
    [InlineData("Authorization: Bearer opaque-canary", "Authorization: ***")]
    [InlineData("Authorization: Basic dXNlcjpwYXNzd29yZA==", "Authorization: ***")]
    [InlineData("authorization: bearer opaque-canary", "authorization: ***")]
    [InlineData("password = hunter2", "password = ***")]
    [InlineData("password:hunter2;next=1", "password:***;next=1")]
    [InlineData("pre.password=hunter2", "pre.password=***")]
    [InlineData("note: basic setup done", "note: basic setup done")]
    [InlineData("password=", "password=")]
    [InlineData("password= ", "password= ")]
    [InlineData("password=,x", "password=,x")]
    public void KeyValuePass_KeepsItsExistingShapes(string input, string expected)
    {
        Assert.Equal(expected, SecretRedactor.RedactText(input));
    }

    [Fact]
    public void RedactText_IsAFixedPoint_OnTheKeyValueShapes()
    {
        foreach (var input in new[] { "a=b password=hunter2 c=d", "Authorization: Basic dXNlcjpwYXNzd29yZA==", "pre.password=hunter2" })
        {
            string once = SecretRedactor.RedactText(input);
            Assert.Equal(once, SecretRedactor.RedactText(once));
        }
    }
}
