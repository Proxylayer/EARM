using System.Linq;
using System.Threading.Tasks;
using EArm.Engine.Execution;
using Xunit;
using static EArm.Tests.Engine.Review1A;

namespace EArm.Tests.Engine;

/// <summary>
/// <c>create</c> without <c>-y</c> executes the plan its what-if built, with the same variable store. A variable that reads a resource this run creates
/// (<c>reference(id, apiVersion)</c>, the only form a variable may use) was evaluated during the what-if, when the object did not exist yet; the run must
/// evaluate it again once the object exists instead of keeping the what-if outcome.
/// </summary>
public sealed class WhatIfVariableReuseTests
{
    private static EArm.Core.TemplateDocument Template(string variable)
    {
        return Templates.Parse(
            "\"variables\": { \"firstId\": \"" + variable + "\" }, "
            + "\"resources\": [ "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"g1\", \"properties\": { \"displayName\": \"g1\", \"mailEnabled\": false, \"securityEnabled\": true } }, "
            + "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + ApiVersion + "\", \"name\": \"g2\", \"dependsOn\": [ \"[resourceId('Microsoft.Entra/groups', 'g1')]\" ], "
            + "\"properties\": { \"displayName\": \"g2\", \"mailEnabled\": false, \"securityEnabled\": true, \"description\": \"[variables('firstId')]\" } } ]");
    }

    [Fact]
    public async Task AVariableReadingAnObjectThisRunCreates_IsEvaluatedAgainByTheRun()
    {
        var host = new EngineHost();
        var document = Template("[reference(resourceId('Microsoft.Entra/groups', 'g1'), '" + ApiVersion + "').id]");

        var plan = await host.WhatIfAsync(document);
        var result = await host.Executor().ExecuteAsync(plan, new ExecuteOptions(), Ct);

        Assert.True(result.Succeeded, string.Join("\n", result.Operations.Select(o => o.Error)));
        var first = host.Directory.Objects("/groups").Single(g => g["displayName"]!.GetValue<string>() == "g1");
        var second = host.Directory.Objects("/groups").Single(g => g["displayName"]!.GetValue<string>() == "g2");
        Assert.Equal(IdOf(first), second["description"]!.GetValue<string>());
    }
}
