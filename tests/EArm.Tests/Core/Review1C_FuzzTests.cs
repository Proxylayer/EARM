using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Expressions.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 1C, finding I-05: every crash found by hand (repeated keys, huge numbers, deep values, syntax errors behind variables) was a different
/// unhandled exception type in a different corner of load and validate. This is the deterministic structural fuzzer that stands behind the individual
/// regression tests: each iteration takes one of the repository samples, applies a few seeded mutations (type swaps, huge numbers, deep nesting,
/// control characters, repeated keys, lone surrogates, hostile expressions, truncation) and runs load, schema and expression validation in-process.
/// The only exception allowed out is <see cref="TemplateLoadException"/> (a document that cannot be loaded at all).
/// The 5,000 mutations (seeds <see cref="BaseSeed"/> to <see cref="BaseSeed"/> + 4,999) run as ten ranges of 500 (<see cref="MutateAndValidate"/>),
/// two per nested test class (<see cref="Mutations0To999"/> to <see cref="Mutations4000To4999"/>): xUnit runs the tests of one class one after
/// another, so separate classes let the ranges run in parallel with each other and with the rest of the suite.
/// </summary>
public sealed class Review1C_FuzzTests
{
    private const int Mutations = 5_000;

    /// <summary>The mutations each test row runs: ten rows cover <see cref="Mutations"/>.</summary>
    private const int RangeSize = 500;

    private const int BaseSeed = 20_261_014;
    private const string RangeTestName = "SeededMutations_OfTheSamples_NeverEscapeLoadAndValidateWithAnUnhandledException";

    private static readonly string[] Expressions =
    [
        "[variables('v')]", "[concat(", "[concat(c)]", "[a.b(c)]", "[format('{0,1000000000}', 'x')]", "[reference('x')]", "[reference(variables('v')).id]",
        "[createArray(createArray(createArray()))]", "[union(createArray(1), 1)]", "[map(range(0,100), lambda('i', lambdaVariables('i')))]",
        "[parameters('nope')]", "[json('{\"a\":1,\"a\":2}')]", "[if(true(), 1)]", "[copyIndex()]", "[base64ToString('###')]", "[replace('a','','b')]",
        "[string(createObject('a',1))]", "[padLeft('a', 99999, 'b')]", "[range(0, 99999999)]", "[substring('abc', 5)]", "[div(1, 0)]", "[int('x')]",
        "[utcNow()]", "[add(9223372036854775807, 1)]", "[tenant()]", "[resourceId('Microsoft.Entra/groups', 'x')]", "[]", "[[escaped]]", "[lambda('x', 1)]",
        "[concat('a', concat('b', concat('c', concat('d', concat('e', 'f')))))]", "[toUpper(base64ToString('Cg=='))]",
    ];

    private static readonly string[] HostileStrings =
    [
        "line\nbreak", "esc\u001b[2Jx", "bell\u0007", "##vso[task.setvariable variable=X]1", "::set-output name=a::b", " leading", "trailing ", ".", "..", "tab\there",
        "nul-free but long " + new string('x', 5_000), "r\rcarriage", ((char)0x2028).ToString() + "sep", "[concat('a'", "]]", "[", "'", "\"", "\\",
    ];

    private sealed record Failure(int Seed, string Description, string ExceptionType, string Message, string Text);

    private static IReadOnlyList<string> Samples()
    {
        return Directory.GetFiles(TestPaths.SamplesDirectory, "*.entra.json").OrderBy(f => f, StringComparer.Ordinal).Select(f => File.ReadAllText(f, Encoding.UTF8)).ToList();
    }

    /// <summary>
    /// Runs mutations <paramref name="firstMutation"/> to <paramref name="firstMutation"/> + <see cref="RangeSize"/> - 1 (seed <see cref="BaseSeed"/> + i,
    /// sample i modulo the sample count). The proportions asserted at the end are a tenth of the whole run's (a range is a tenth of it), so every range
    /// still breaks loading, loads and produces validation errors.
    /// </summary>
    private static void MutateAndValidate(int firstMutation)
    {
        Assert.InRange(firstMutation, 0, Mutations - RangeSize);
        Assert.Equal(0, firstMutation % RangeSize);
        var samples = Samples();
        Assert.NotEmpty(samples);
        var failures = new ConcurrentBag<Failure>();
        int loadFailures = 0;
        int validated = 0;
        int withErrors = 0;
        var catalog = ResourceTypeCatalog.Default;

        Parallel.For(firstMutation, firstMutation + RangeSize, new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount, 2, 8) }, i =>
        {
            int seed = BaseSeed + i;
            var random = new Random(seed);
            string description = string.Empty;
            string text = Mutate(samples[i % samples.Count], random, out description);
            try
            {
                var document = new TemplateLoader(catalog).Parse(text, "fuzz.entra.json", ParameterSet.Empty);
                var diagnostics = new List<Diagnostic>(new TemplateValidator(catalog).Validate(document));
                diagnostics.AddRange(new ExpressionValidator(catalog).Analyze(document).Diagnostics);
                foreach (var diagnostic in diagnostics)
                {
                    // Rendering must work too, and a message is always a single printable line.
                    string rendered = diagnostic.ToString();
                    Assert.DoesNotContain('\n', rendered);
                }

                Interlocked.Increment(ref validated);
                if (diagnostics.Any(d => d.IsError))
                {
                    Interlocked.Increment(ref withErrors);
                }
            }
            catch (TemplateLoadException ex)
            {
                Interlocked.Increment(ref loadFailures);
                Assert.False(string.IsNullOrEmpty(ex.Code));
                Assert.DoesNotContain('\n', ex.ToDiagnostic().ToString());
            }
            catch (Exception ex)
            {
                failures.Add(new Failure(seed, description, ex.GetType().FullName ?? ex.GetType().Name, ex.Message, text.Length > 400 ? text[..400] + "..." : text));
            }
        });

        Assert.True(failures.IsEmpty, "Unhandled exceptions:\n" + string.Join("\n", failures.OrderBy(f => f.Seed).Take(5).Select(f => $"  seed {f.Seed} ({f.Description}): {f.ExceptionType}: {f.Message}\n    {f.Text}")));
        Assert.True(loadFailures > 10, "the mutations should break loading sometimes, saw " + loadFailures);
        Assert.True(validated > 100, "the mutations should mostly load, saw " + validated);
        Assert.True(withErrors > 10, "the mutations should produce validation errors, saw " + withErrors);
    }

    [Fact]
    public void TheMutationRanges_CoverEveryMutationExactlyOnce()
    {
        var firstMutations = typeof(Review1C_FuzzTests).Assembly.GetTypes()
            .Select(t => t.GetMethod(RangeTestName))
            .Where(m => m is not null)
            .SelectMany(m => m!.GetCustomAttributes(typeof(InlineDataAttribute), inherit: false).Cast<InlineDataAttribute>())
            .Select(row => (int)row.Data.Single()!)
            .Order()
            .ToList();

        Assert.Equal(Enumerable.Range(0, Mutations / RangeSize).Select(i => i * RangeSize), firstMutations);
    }

    [Fact]
    public void TheMutator_IsDeterministic()
    {
        var samples = Samples();

        string first = Mutate(samples[0], new Random(BaseSeed + 7), out string firstDescription);
        string second = Mutate(samples[0], new Random(BaseSeed + 7), out string secondDescription);

        Assert.Equal(first, second);
        Assert.Equal(firstDescription, secondDescription);
        Assert.NotEqual(samples[0], first);
    }

    // ---- the mutator ----

    private static string Mutate(string sample, Random random, out string description)
    {
        var root = JsonNode.Parse(sample)!;
        var steps = new List<string>();

        // Most iterations keep one resource of the sample: validating a whole sample costs ~15 ms, a single resource a fraction of that.
        if (random.Next(5) != 0 && root["resources"] is JsonArray resources && resources.Count > 1)
        {
            var kept = resources[random.Next(resources.Count)];
            resources.Clear();
            resources.Add(kept?.DeepClone());
            steps.Add("oneResource");
        }

        int count = random.Next(1, 4);
        for (int i = 0; i < count; i++)
        {
            steps.Add(MutateTree(root, random));
        }

        string text = root.ToJsonString();
        if (random.Next(5) == 0)
        {
            steps.Add(MutateText(ref text, random));
        }

        description = string.Join(", ", steps);
        return text;
    }

    private static List<(JsonNode? Parent, object Key)> Slots(JsonNode root)
    {
        var slots = new List<(JsonNode? Parent, object Key)>();
        var stack = new Stack<JsonNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            switch (node)
            {
                case JsonObject obj:
                    foreach (var property in obj)
                    {
                        slots.Add((obj, property.Key));
                        if (property.Value is { } child)
                        {
                            stack.Push(child);
                        }
                    }

                    break;
                case JsonArray array:
                    for (int i = 0; i < array.Count; i++)
                    {
                        slots.Add((array, i));
                        if (array[i] is { } child)
                        {
                            stack.Push(child);
                        }
                    }

                    break;
            }
        }

        return slots;
    }

    private static void Set(JsonNode parent, object key, JsonNode? value)
    {
        if (parent is JsonObject obj)
        {
            obj[(string)key] = value;
        }
        else
        {
            ((JsonArray)parent)[(int)key] = value;
        }
    }

    private static string MutateTree(JsonNode root, Random random)
    {
        var slots = Slots(root);
        if (slots.Count == 0)
        {
            return "none";
        }

        var (parent, key) = slots[random.Next(slots.Count)];
        string where = key.ToString() ?? "?";
        switch (random.Next(9))
        {
            case 0:
                Set(parent!, key, TypeSwap(random));
                return "typeSwap@" + where;
            case 1:
                string[] numbers = ["1e300", "-1e400", "123456789012345678901234567890", "1.7976931348623157e308", "-0.0", "4.9e-324", "9223372036854775808", "1e-400"];
                Set(parent!, key, JsonNode.Parse(numbers[random.Next(numbers.Length)]));
                return "hugeNumber@" + where;
            case 2:
                Set(parent!, key, Deep(random.Next(1, 90), random));
                return "deep@" + where;
            case 3:
                Set(parent!, key, JsonValue.Create(HostileStrings[random.Next(HostileStrings.Length)]));
                return "hostileString@" + where;
            case 4:
                if (parent is JsonObject removeFrom)
                {
                    removeFrom.Remove((string)key);
                }
                else
                {
                    ((JsonArray)parent!).RemoveAt((int)key);
                }

                return "remove@" + where;
            case 5:
                Set(parent!, key, JsonValue.Create(Expressions[random.Next(Expressions.Length)]));
                return "expression@" + where;
            case 6:
                if (parent is JsonObject rename && rename[(string)key] is { } moved)
                {
                    rename.Remove((string)key);
                    rename[HostileStrings[random.Next(HostileStrings.Length)]] = moved;
                }

                return "renameKey@" + where;
            case 7:
                Set(parent!, key, JsonValue.Create(string.Concat(Enumerable.Range(0, random.Next(1, 40)).Select(_ => (char)random.Next(0, 0x2100)).Where(c => !char.IsSurrogate(c)))));
                return "randomChars@" + where;
            default:
                Set(parent!, key, JsonValue.Create("[concat(" + string.Concat(Enumerable.Repeat("concat(", random.Next(1, 30))) + "'x'" + new string(')', 30) + ")]"));
                return "unbalanced@" + where;
        }
    }

    private static JsonNode? TypeSwap(Random random)
    {
        return random.Next(7) switch
        {
            0 => JsonValue.Create(random.Next()),
            1 => JsonValue.Create(true),
            2 => null,
            3 => new JsonArray(1, "a", null),
            4 => new JsonObject { ["x"] = 1 },
            5 => JsonValue.Create("text"),
            _ => JsonValue.Create(1.5),
        };
    }

    private static JsonNode Deep(int depth, Random random)
    {
        JsonNode node = JsonValue.Create(random.Next(2) == 0 ? "leaf" : "[variables('v')]");
        for (int i = 0; i < depth; i++)
        {
            node = random.Next(3) == 0 ? new JsonObject { ["k"] = node } : new JsonArray(node);
        }

        return node;
    }

    private static string MutateText(ref string text, Random random)
    {
        switch (random.Next(4))
        {
            case 0:
                {
                    // Repeat a property name right after itself.
                    int at = text.IndexOf("\":", random.Next(Math.Max(1, text.Length - 2)), StringComparison.Ordinal);
                    if (at < 0)
                    {
                        return "dup-none";
                    }

                    int start = text.LastIndexOf('"', at - 1);
                    if (start < 0)
                    {
                        return "dup-none";
                    }

                    string key = text[start..(at + 1)];
                    text = text.Insert(start, key + ": null, ");
                    return "duplicateKey";
                }

            case 1:
                {
                    int at = text.IndexOf("\"", random.Next(Math.Max(1, text.Length - 2)), StringComparison.Ordinal);
                    if (at < 0)
                    {
                        return "surrogate-none";
                    }

                    text = text.Insert(at + 1, "\\ud800");
                    return "loneSurrogate";
                }

            case 2:
                text = text[..random.Next(1, text.Length)];
                return "truncate";

            default:
                text = ((char)0xFEFF).ToString() + text.Replace("\"resources\"", "\"resources\" /* c */", StringComparison.Ordinal);
                return "bomAndComment";
        }
    }

    // ---- the mutation ranges: one nested class (one xUnit collection) per thousand, so the ranges run in parallel ----

    /// <summary>Mutations 0 to 999.</summary>
    public sealed class Mutations0To999
    {
        [Theory]
        [InlineData(0)]
        [InlineData(500)]
        public void SeededMutations_OfTheSamples_NeverEscapeLoadAndValidateWithAnUnhandledException(int firstMutation)
        {
            MutateAndValidate(firstMutation);
        }
    }

    /// <summary>Mutations 1000 to 1999.</summary>
    public sealed class Mutations1000To1999
    {
        [Theory]
        [InlineData(1000)]
        [InlineData(1500)]
        public void SeededMutations_OfTheSamples_NeverEscapeLoadAndValidateWithAnUnhandledException(int firstMutation)
        {
            MutateAndValidate(firstMutation);
        }
    }

    /// <summary>Mutations 2000 to 2999.</summary>
    public sealed class Mutations2000To2999
    {
        [Theory]
        [InlineData(2000)]
        [InlineData(2500)]
        public void SeededMutations_OfTheSamples_NeverEscapeLoadAndValidateWithAnUnhandledException(int firstMutation)
        {
            MutateAndValidate(firstMutation);
        }
    }

    /// <summary>Mutations 3000 to 3999.</summary>
    public sealed class Mutations3000To3999
    {
        [Theory]
        [InlineData(3000)]
        [InlineData(3500)]
        public void SeededMutations_OfTheSamples_NeverEscapeLoadAndValidateWithAnUnhandledException(int firstMutation)
        {
            MutateAndValidate(firstMutation);
        }
    }

    /// <summary>Mutations 4000 to 4999.</summary>
    public sealed class Mutations4000To4999
    {
        [Theory]
        [InlineData(4000)]
        [InlineData(4500)]
        public void SeededMutations_OfTheSamples_NeverEscapeLoadAndValidateWithAnUnhandledException(int firstMutation)
        {
            MutateAndValidate(firstMutation);
        }
    }
}
