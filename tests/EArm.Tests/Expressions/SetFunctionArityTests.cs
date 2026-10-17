using EArm.Core.Diagnostics;
using EArm.Expressions.Functions;
using Xunit;

namespace EArm.Tests;

/// <summary>ARM requires at least two arguments for <c>union()</c> and <c>intersection()</c>; a single argument is an argument error.</summary>
public sealed class SetFunctionArityTests
{
    [Theory]
    [InlineData("[union(createArray(1))]")]
    [InlineData("[union(createObject('a', 1))]")]
    [InlineData("[intersection(createArray(1))]")]
    [InlineData("[intersection(createObject('a', 1))]")]
    public void A_single_argument_is_rejected(string expression)
    {
        Expr.Fails(expression, DiagnosticCodes.InvalidFunctionArguments);
    }

    [Theory]
    [InlineData("union")]
    [InlineData("intersection")]
    public void The_arity_is_at_least_two(string name)
    {
        var descriptor = FunctionRegistry.CreateDefault().Find(name)!;

        Assert.Equal("at least 2", descriptor.ArityDescription);
        Assert.False(descriptor.AcceptsArgumentCount(1));
        Assert.True(descriptor.AcceptsArgumentCount(3));
    }
}
