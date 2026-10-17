using Xunit;

namespace EArm.Tests;

/// <summary>
/// <c>items()</c> returns the entries sorted alphabetically by key, as ARM's evaluated output shows (Microsoft Learn: the <c>item002</c> /
/// <c>item001</c> example comes back as <c>item001</c>, <c>item002</c>), independent of the declaration order and of the letter case.
/// </summary>
public sealed class ItemsOrderTests
{
    [Fact]
    public void Learn_example_entries_come_back_sorted_by_key()
    {
        const string expression = "[items(json('{\"item002\":{\"enabled\":false,\"displayName\":\"Example item 2\",\"number\":200},\"item001\":{\"enabled\":true,\"displayName\":\"Example item 1\",\"number\":300}}'))]";

        Expr.AssertJson(
            "[{\"key\":\"item001\",\"value\":{\"enabled\":true,\"displayName\":\"Example item 1\",\"number\":300}},"
            + "{\"key\":\"item002\",\"value\":{\"enabled\":false,\"displayName\":\"Example item 2\",\"number\":200}}]",
            Expr.Eval(expression));
    }

    [Theory]
    [InlineData("[map(items(createObject('b', 1, 'C', 2, 'a', 3)), lambda('e', lambdaVariables('e').key))]", "[\"a\",\"b\",\"C\"]")]
    [InlineData("[map(items(createObject('zeta', 1, 'Alpha', 2, 'beta', 3, '10', 4, '9', 5)), lambda('e', lambdaVariables('e').key))]", "[\"10\",\"9\",\"Alpha\",\"beta\",\"zeta\"]")]
    public void Keys_are_sorted_alphabetically_ignoring_case(string expression, string expectedKeys)
    {
        Expr.AssertJson(expectedKeys, Expr.Eval(expression));
    }

    [Fact]
    public void The_order_does_not_depend_on_the_declaration_order()
    {
        Expr.AssertJson(
            Expr.Eval("[items(createObject('x', 1, 'm', 2, 'B', 3))]")!.ToJsonString(),
            Expr.Eval("[items(createObject('B', 3, 'x', 1, 'm', 2))]"));
    }
}
