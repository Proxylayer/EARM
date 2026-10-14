using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EntraArm.Expressions;
using EntraArm.Expressions.Functions;
using Xunit;

namespace EntraArm.Tests;

/// <summary>
/// Every registered function is called with the wrong number of arguments and with values of every kind (seeded, bounded
/// sample). Contract: the call yields a value or an <see cref="ExpressionException"/> carrying a code, never a raw
/// framework exception, and arity violations are always an expression error naming the function.
/// </summary>
public sealed class FunctionArgumentFuzzTests
{
    private const int Seed = 5150;

    // Small numbers only: functions such as padLeft / range / copyIndex legitimately honour big counts.
    private static readonly string[] Pool =
    [
        "'text'", "''", "'6d2f1c0e-2b7a-4c1e-9a1b-4f3f1f6a0001'", "0", "1", "-1", "3", "1.5", "true", "false", "null",
        "createArray('a', 'b')", "createArray(1, 2, 3)", "createArray()", "createObject('k', 1)", "createObject()", "json('{\"a\":[1,2]}')", "json('[]')", "string(7)",
    ];

    private static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        // Lambdas need real lambda syntax; their own tests cover them. They are still called with plain arguments below.
    };

    private static string Call(string name, IEnumerable<string> args)
    {
        return "[" + name + "(" + string.Join(", ", args) + ")]";
    }

    private static IEnumerable<FunctionDescriptor> Supported()
    {
        return FunctionRegistry.Default.Functions.Where(f => f.Kind == FunctionKind.Supported && !Skipped.Contains(f.Name)).OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
    }

    private static void Attempt(string expression, List<string> unexpected)
    {
        try
        {
            Expr.Eval(expression);
        }
        catch (ExpressionException ex)
        {
            if (string.IsNullOrWhiteSpace(ex.Code) || string.IsNullOrWhiteSpace(ex.Message))
            {
                unexpected.Add(expression + " -> ExpressionException without code or message");
            }
        }
        catch (Exception ex)
        {
            unexpected.Add(expression + " -> " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    [Fact]
    public void WrongArity_IsAlwaysAnExpressionErrorNamingTheFunction()
    {
        var problems = new List<string>();
        foreach (var function in Supported())
        {
            var counts = new List<int>();
            if (function.MinArgs > 0)
            {
                counts.Add(function.MinArgs - 1);
            }

            if (function.MaxArgs >= 0)
            {
                counts.Add(function.MaxArgs + 1);
            }

            foreach (int count in counts)
            {
                string expression = Call(function.Name, Enumerable.Repeat("'x'", count));
                try
                {
                    Expr.Eval(expression);
                    problems.Add(expression + " -> accepted although the arity is " + function.ArityDescription);
                }
                catch (ExpressionException ex)
                {
                    if (!ex.Message.Contains(function.Name, StringComparison.OrdinalIgnoreCase))
                    {
                        problems.Add(expression + " -> message does not name the function: " + ex.Message);
                    }
                }
                catch (Exception ex)
                {
                    problems.Add(expression + " -> " + ex.GetType().Name + ": " + ex.Message);
                }
            }
        }

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems.Take(25)));
    }

    [Fact]
    public void OneAndTwoArguments_OfEveryKind_NeverEscapeAsFrameworkExceptions()
    {
        var unexpected = new List<string>();
        foreach (var function in Supported())
        {
            foreach (string a in Pool)
            {
                if (function.AcceptsArgumentCount(1))
                {
                    Attempt(Call(function.Name, [a]), unexpected);
                }

                if (!function.AcceptsArgumentCount(2))
                {
                    continue;
                }

                foreach (string b in Pool)
                {
                    Attempt(Call(function.Name, [a, b]), unexpected);
                }
            }
        }

        Assert.True(unexpected.Count == 0, unexpected.Count + " unexpected outcome(s):" + Environment.NewLine + string.Join(Environment.NewLine, unexpected.Take(25)));
    }

    [Fact]
    public void ThreeAndFourArguments_SampledAtRandom_NeverEscapeAsFrameworkExceptions()
    {
        var random = new Random(Seed);
        var unexpected = new List<string>();
        foreach (var function in Supported())
        {
            foreach (int count in new[] { 3, 4 })
            {
                if (!function.AcceptsArgumentCount(count))
                {
                    continue;
                }

                for (int i = 0; i < 120; i++)
                {
                    Attempt(Call(function.Name, Enumerable.Range(0, count).Select(_ => Pool[random.Next(Pool.Length)])), unexpected);
                }
            }
        }

        Assert.True(unexpected.Count == 0, unexpected.Count + " unexpected outcome(s):" + Environment.NewLine + string.Join(Environment.NewLine, unexpected.Take(25)));
    }

    [Fact]
    public void RejectedFunctions_AreExpressionErrors_NamingTheAlternative_WhateverTheArguments()
    {
        var rejected = FunctionRegistry.Default.NamesOfKind(FunctionKind.Rejected);
        Assert.NotEmpty(rejected);

        foreach (string name in rejected)
        {
            var ex = Assert.ThrowsAny<ExpressionException>(() => Expr.Eval(Call(name, [])));
            Assert.Contains(name, ex.Message, StringComparison.OrdinalIgnoreCase);
            Assert.NotNull(FunctionRegistry.Default.Find(name)?.Note ?? ex.Message);
        }
    }

    [Fact]
    public void AnUnknownFunction_IsAnExpressionError_AndCaseDoesNotMatter()
    {
        Assert.ThrowsAny<ExpressionException>(() => Expr.Eval("[definitelyNotAFunction(1)]"));
        Assert.Equal(Expr.Eval("[concat('a', 'b')]")!.ToJsonString(), Expr.Eval("[CONCAT('a', 'b')]")!.ToJsonString());
        Assert.Equal(Expr.Eval("[length('abc')]")!.ToJsonString(), Expr.Eval("[LeNgTh('abc')]")!.ToJsonString());
        Assert.False(FunctionRegistry.Default.Contains("definitelyNotAFunction"));
        Assert.Throws<ArgumentNullException>(() => FunctionRegistry.Default.TryGet(null!, out _));
    }

    [Fact]
    public void Registry_RejectsASupportedFunctionWithoutAnImplementation()
    {
        var registry = FunctionRegistry.CreateDefault();

        Assert.Throws<ArgumentException>(() => registry.Register(new FunctionDescriptor { Name = "broken", Group = "test" }));
        Assert.Throws<ArgumentNullException>(() => registry.Register(null!));
        registry.Register(new FunctionDescriptor { Name = "staged", Group = "test", Kind = FunctionKind.NotImplementedYet });
        Assert.Contains("staged", registry.NamesOfKind(FunctionKind.NotImplementedYet));
        Assert.Equal(registry.Names.Count(), registry.Functions.Count);
    }
}
