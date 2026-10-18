using System.Text;
using System.Threading.Tasks;
using EArm.Engine.WhatIf;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>The pretty what-if JSON is LF-only on every OS, straight from <see cref="WhatIfJson.Serialize"/> (no test-side normalisation).</summary>
public sealed class WhatIfJsonLineEndingTests
{
    public static TheoryData<string> Scenarios()
    {
        var data = new TheoryData<string>();
        foreach (var scenario in WhatIfScenario.All())
        {
            data.Add(scenario.Name);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task PrettyJson_ContainsNoCarriageReturnByte_AndEqualsTheGoldenText(string name)
    {
        var run = await WhatIfScenario.Find(name).PlanAsync();

        string pretty = WhatIfJson.Serialize(WhatIfJson.Build(run.Report, run.Scenario.ResultFormat), prettyPrint: true);
        byte[] bytes = new UTF8Encoding(false).GetBytes(pretty);

        Assert.DoesNotContain((byte)0x0D, bytes);
        Assert.Contains((byte)0x0A, bytes);
        Assert.Equal(run.Scenario.ReadExpectedJson(), pretty + "\n");
    }

    [Theory]
    [MemberData(nameof(Scenarios))]
    public async Task CompactJson_IsASingleLine(string name)
    {
        var run = await WhatIfScenario.Find(name).PlanAsync();

        string compact = WhatIfJson.Serialize(WhatIfJson.Build(run.Report, run.Scenario.ResultFormat), prettyPrint: false);

        Assert.DoesNotContain('\n', compact);
        Assert.DoesNotContain('\r', compact);
        Assert.Contains('{', compact);
    }
}
