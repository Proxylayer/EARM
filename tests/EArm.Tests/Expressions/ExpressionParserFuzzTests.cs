using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Syntax;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// The expression parser over mutated and random text (fixed seed): it either produces a tree or throws an
/// <see cref="ExpressionException"/> whose offset points inside the text, and every tree it accepts evaluates to a value or an
/// expression error. Includes deep nesting, very long argument lists and pathological quoting.
/// </summary>
public sealed class ExpressionParserFuzzTests
{
    private const int Seed = 8675309;

    private static readonly string[] Valid =
    [
        "[concat('a', 'b')]", "[parameters('x')]", "[variables('v').name]", "[createArray(1, 2, 3)[1]]", "[if(equals(1, 1), 'yes', 'no')]", "[format('{0}-{1}', 'a', 'b')]",
        "[createObject('k', createArray(1, 2)).k[0]]", "[resourceId('Microsoft.Entra/groups', 'g')]", "[reference('g').id]", "[add(mul(2, 3), sub(10, 4))]",
        "[map(createArray(1, 2), lambda('x', add(lambdaVariables('x'), 1)))]", "[toLower(toUpper('MiXeD'))]", "[json('{\"a\": [1, 2]}').a[1]]", "[substring('abcdef', 1, 3)]",
        "[union(createObject('a', 1), createObject('b', 2))]", "[empty('')]", "[coalesce(null(), 'x')]", "[contains('abc', 'b')]", "['quote''d']",
    ];

    private const string Alphabet = "[]()',.0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJ -+_\\\"{}\n\tü日";

    private static string Mutate(string text, Random random)
    {
        var builder = new StringBuilder(text);
        int edits = random.Next(1, 5);
        for (int i = 0; i < edits; i++)
        {
            switch (random.Next(4))
            {
                case 0 when builder.Length > 0:
                    builder.Remove(random.Next(builder.Length), 1);
                    break;
                case 1:
                    builder.Insert(random.Next(builder.Length + 1), Alphabet[random.Next(Alphabet.Length)]);
                    break;
                case 2 when builder.Length > 1:
                    int at = random.Next(builder.Length - 1);
                    (builder[at], builder[at + 1]) = (builder[at + 1], builder[at]);
                    break;
                default:
                    builder.Length = random.Next(builder.Length + 1);
                    break;
            }
        }

        return builder.ToString();
    }

    private static string Describe(string text)
    {
        return "'" + text.Replace("\n", "\\n", StringComparison.Ordinal).Replace("\t", "\\t", StringComparison.Ordinal) + "'";
    }

    private static void CheckParse(string text, List<string> problems)
    {
        try
        {
            var tree = ExpressionParser.Parse(text);
            Assert.NotNull(tree);
        }
        catch (ExpressionException ex)
        {
            if (ex.Offset is { } offset && (offset < 0 || offset > text.Length))
            {
                problems.Add(Describe(text) + " -> offset " + offset + " outside 0.." + text.Length);
            }
        }
        catch (Exception ex)
        {
            problems.Add(Describe(text) + " -> " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    [Fact]
    public void MutatedExpressions_ParseOrFailWithAnInRangeOffset()
    {
        var random = new Random(Seed);
        var problems = new List<string>();
        for (int i = 0; i < 4000; i++)
        {
            CheckParse(Mutate(Valid[random.Next(Valid.Length)], random), problems);
        }

        Assert.True(problems.Count == 0, problems.Count + " problem(s):" + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(15)));
    }

    [Fact]
    public void RandomBracketedText_ParsesOrFailsCleanly()
    {
        var random = new Random(Seed + 1);
        var problems = new List<string>();
        for (int i = 0; i < 4000; i++)
        {
            int length = random.Next(0, 40);
            var builder = new StringBuilder("[");
            for (int j = 0; j < length; j++)
            {
                builder.Append(Alphabet[random.Next(Alphabet.Length)]);
            }

            builder.Append(']');
            CheckParse(builder.ToString(), problems);
        }

        Assert.True(problems.Count == 0, problems.Count + " problem(s):" + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(15)));
    }

    [Fact]
    public void EveryTreeThatParses_EvaluatesToAValueOrAnExpressionError()
    {
        var random = new Random(Seed + 2);
        var problems = new List<string>();
        for (int i = 0; i < 3000; i++)
        {
            string text = Mutate(Valid[random.Next(Valid.Length)], random);
            try
            {
                ExpressionParser.Parse(text);
            }
            catch (ExpressionException)
            {
                continue;
            }

            try
            {
                Expr.Eval(text);
            }
            catch (ExpressionException)
            {
            }
            catch (Exception ex)
            {
                problems.Add(Describe(text) + " -> " + ex.GetType().Name + ": " + ex.Message);
            }
        }

        Assert.True(problems.Count == 0, problems.Count + " problem(s):" + Environment.NewLine + string.Join(Environment.NewLine, problems.Take(15)));
    }

    [Fact]
    public void ModerateNesting_LongArgumentLists_AndTheLengthLimit_AreHandled()
    {
        string nested = "[" + string.Concat(Enumerable.Repeat("concat(", 90)) + "'x'" + new string(')', 90) + "]";
        Assert.Equal("x", Expr.Eval(nested)!.GetValue<string>());

        string manyArguments = "[concat(" + string.Join(", ", Enumerable.Repeat("'a'", 3000)) + ")]";
        Assert.Equal(3000, Expr.Eval(manyArguments)!.GetValue<string>().Length);

        string atTheLimit = "['" + new string('x', ExpressionParser.MaxExpressionLength - 4) + "']";
        Assert.Equal(ExpressionParser.MaxExpressionLength - 4, Expr.Eval(atTheLimit)!.GetValue<string>().Length);

        string overTheLimit = "['" + new string('x', ExpressionParser.MaxExpressionLength) + "']";
        var ex = Assert.ThrowsAny<ExpressionException>(() => ExpressionParser.Parse(overTheLimit));
        Assert.Equal("ExpressionTooLong", ex.Code);
        Assert.Equal(0, ex.Offset);
    }

    /// <summary>
    /// ExpressionParser is a recursive-descent parser: nested function calls about 1,200 deep used to overflow the stack and abort the
    /// process (uncatchable). The parser now caps nesting at <see cref="ExpressionParser.MaxNestingDepth"/> and raises an
    /// <see cref="ExpressionParseException"/> (code ExpressionTooDeep) with the offset where the limit was hit.
    /// </summary>
    [Fact]
    public void DeeplyNestedCalls_ShouldBeADiagnostic_NotAStackOverflow()
    {
        string tooDeep = "[" + string.Concat(Enumerable.Repeat("or(", 1500)) + "true()" + new string(')', 1500) + "]";

        var outcome = Record.Exception(() => ExpressionParser.Parse(tooDeep));

        Assert.IsAssignableFrom<ExpressionException>(outcome);
        var parseError = Assert.IsType<ExpressionParseException>(outcome);
        Assert.Equal("ExpressionTooDeep", parseError.Code);
        Assert.Contains("maximum depth of 100", parseError.Message, StringComparison.Ordinal);
        Assert.InRange(parseError.Offset!.Value, 1, tooDeep.Length);
    }

    private static string NestedCalls(int depth)
    {
        return "[" + string.Concat(Enumerable.Repeat("or(", depth)) + "true()" + new string(')', depth) + "]";
    }

    [Fact]
    public void Nesting_AtTheLimitParses_AndOneLevelMoreIsADiagnostic()
    {
        // The whole expression is level 1 and every nested argument, index or lambda body one more: n nested calls around a leaf are n + 1 levels.
        Assert.NotNull(ExpressionParser.Parse(NestedCalls(ExpressionParser.MaxNestingDepth - 2)));
        Assert.NotNull(ExpressionParser.Parse(NestedCalls(ExpressionParser.MaxNestingDepth - 1)));

        var ex = Assert.Throws<ExpressionParseException>(() => ExpressionParser.Parse(NestedCalls(ExpressionParser.MaxNestingDepth)));

        Assert.Equal("ExpressionTooDeep", ex.Code);
        Assert.Contains("The expression nesting exceeds the maximum depth of 100", ex.MessageWithOffset, StringComparison.Ordinal);
        Assert.Contains("at offset", ex.MessageWithOffset, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(1_200)]
    [InlineData(5_000)]
    public void VeryDeepCalls_AreADiagnosticAtTheLimit_NotACrash(int depth)
    {
        var ex = Assert.Throws<ExpressionParseException>(() => ExpressionParser.Parse(NestedCalls(depth)));

        Assert.Equal("ExpressionTooDeep", ex.Code);
    }

    [Theory]
    [InlineData("[{0}1{1}]", "createArray(", ")")]
    [InlineData("[{0}'x'{1}]", "concat('a', ", ")")]
    public void DepthIsCountedThroughCallArgumentsOfAnyPosition(string template, string open, string close)
    {
        string Build(int depth)
        {
            return string.Format(System.Globalization.CultureInfo.InvariantCulture, template, string.Concat(Enumerable.Repeat(open, depth)), string.Concat(Enumerable.Repeat(close, depth)));
        }

        Assert.NotNull(ExpressionParser.Parse(Build(ExpressionParser.MaxNestingDepth - 1)));
        Assert.Equal("ExpressionTooDeep", Assert.Throws<ExpressionParseException>(() => ExpressionParser.Parse(Build(ExpressionParser.MaxNestingDepth))).Code);
    }

    [Fact]
    public void NestedIndexers_AreLimitedToo()
    {
        // x[y[y[...]]]: the index expression of an indexer is itself an indexed expression, repeatedly.
        string nestedIndex(int depth)
        {
            return "[" + string.Concat(Enumerable.Repeat("createArray(0)[", depth)) + "0" + new string(']', depth) + "]";
        }

        Assert.NotNull(ExpressionParser.Parse(nestedIndex(40)));
        var ex = Assert.Throws<ExpressionParseException>(() => ExpressionParser.Parse(nestedIndex(1_500)));
        Assert.Equal("ExpressionTooDeep", ex.Code);
    }

    [Fact]
    public void NestedLambdas_AreLimitedToo()
    {
        string lambdas(int depth)
        {
            return "[" + string.Concat(Enumerable.Repeat("map(createArray(1), lambda('x', ", depth)) + "1" + string.Concat(Enumerable.Repeat("))", depth)) + "]";
        }

        Assert.NotNull(ExpressionParser.Parse(lambdas(20)));
        var ex = Assert.Throws<ExpressionParseException>(() => ExpressionParser.Parse(lambdas(300)));
        Assert.Equal("ExpressionTooDeep", ex.Code);
    }

    [Fact]
    public void LongPropertyAndIndexChains_CountTowardsTheDepth()
    {
        // A chain builds a left-deep tree that the evaluator walks recursively, so it is limited like nesting is.
        string chain(int links)
        {
            return "[variables('v')" + string.Concat(Enumerable.Repeat(".p", links)) + "]";
        }

        Assert.NotNull(ExpressionParser.Parse(chain(50)));
        var ex = Assert.Throws<ExpressionParseException>(() => ExpressionParser.Parse(chain(5_000)));
        Assert.Equal("ExpressionTooDeep", ex.Code);
        Assert.NotNull(ex.Offset);
    }

    [Fact]
    public void ADeepTreeBuiltByHand_IsAnEvaluationDiagnostic_AcrossEveryWalker()
    {
        // The parser cannot produce this; a caller (or a future producer) can. 200,000 levels is far more than the stack takes.
        Expression deep = LiteralExpression.String(0, "x");
        for (int i = 0; i < 200_000; i++)
        {
            deep = new PropertyExpression(0, deep, "p");
        }

        var evaluation = Assert.Throws<ExpressionEvaluationException>(() => ExpressionEvaluator.Default.Evaluate(deep, Expr.Context()));
        var rendering = Assert.Throws<ExpressionEvaluationException>(() => deep.ToExpressionText());

        Assert.Equal("ExpressionTooDeep", evaluation.Code);
        Assert.Equal("ExpressionTooDeep", rendering.Code);
        Assert.Equal(200_001, deep.DescendantsAndSelf().Count());
    }

    [Fact]
    public void ADeepJsonTreeBuiltByHand_IsEvaluationDiagnostic_NotACrash()
    {
        JsonNode node = JsonValue.Create("[concat('a', 'b')]");
        for (int i = 0; i < 100_000; i++)
        {
            node = new JsonArray(node);
        }

        var ex = Assert.Throws<ExpressionEvaluationException>(() => ExpressionEvaluator.Default.EvaluateTree(node, Expr.Context()));

        Assert.Equal("ExpressionTooDeep", ex.Code);
    }

    [Theory]
    [InlineData("['it''s']", "it's")]
    [InlineData("['''']", "'")]
    [InlineData("['']", "")]
    [InlineData("['a''''b']", "a''b")]
    [InlineData("['[[not an expression]']", "[[not an expression]")]
    [InlineData("['ünï 日本語']", "ünï 日本語")]
    [InlineData("['line\nbreak']", "line\nbreak")]
    public void QuotedStrings_UnescapeDoubledQuotes(string text, string expected)
    {
        Assert.Equal(expected, Expr.Eval(text)!.GetValue<string>());
    }

    [Theory]
    [InlineData("it's", "['it''s']")]
    [InlineData("", "['']")]
    [InlineData("plain", "['plain']")]
    [InlineData("'", "['''']")]
    public void QuoteLiteral_ProducesAnExpressionThatEvaluatesBackToTheText(string text, string quoted)
    {
        Assert.Equal("'" + quoted[2..^2] + "'", ExpressionSyntax.QuoteLiteral(text));
        Assert.Equal(text, Expr.Eval("[" + ExpressionSyntax.QuoteLiteral(text) + "]")!.GetValue<string>());
    }

    [Fact]
    public void QuoteLiteral_RoundTripsRandomText()
    {
        var random = new Random(Seed + 3);
        for (int i = 0; i < 300; i++)
        {
            string text = new(Enumerable.Range(0, random.Next(0, 20)).Select(_ => Alphabet[random.Next(Alphabet.Length)]).ToArray());

            string roundTripped = Expr.Eval("[" + ExpressionSyntax.QuoteLiteral(text) + "]")!.GetValue<string>();

            Assert.True(roundTripped == text, Describe(text) + " -> " + Describe(roundTripped));
        }
    }
}
