using System;
using System.Globalization;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// Review 3J, finding 1, through the planner (what-if and create plan the same way): a resource whose property reads the end of a chain of 400 or
/// more variables hung the command (0 CPU, no diagnostic). The resource now reports the <c>VariableChainTooDeep</c> failure as its change within seconds.
/// </summary>
public sealed class Review3J_VariableChainPlanTests
{
    private static string Template(int length)
    {
        var body = new StringBuilder("\"variables\": { \"v0\": \"x\"");
        for (int i = 1; i <= length; i++)
        {
            body.Append(CultureInfo.InvariantCulture, $", \"v{i}\": \"[concat(variables('v{i - 1}'), 'a')]\"");
        }

        body.Append(" }, \"resources\": [ ");
        body.Append(Review1C.Group("\"g\"", $"{{ \"displayName\": \"[variables('v{length}')]\", \"mailEnabled\": false, \"securityEnabled\": true }}"));
        body.Append(" ]");
        return body.ToString();
    }

    [Theory]
    [InlineData(250)]
    [InlineData(340)]
    [InlineData(400)]
    [InlineData(1000)]
    [InlineData(2000)]
    public async Task APlan_ThatReadsTheEndOfALongChain_FinishesWithinThirtySeconds(int length)
    {
        var plan = new EngineHost().WhatIfAsync(Templates.Parse(Template(length)));
        var finished = await Task.WhenAny(plan, Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken));

        Assert.Same(plan, finished);
        var done = await plan;
        var change = Assert.Single(done.Changes);
        if (length < 128)
        {
            Assert.False(change.HasError);
        }
        else
        {
            Assert.True(change.HasError);
            Assert.Contains("levels deep", change.UnsupportedReason, StringComparison.Ordinal);
        }
    }
}