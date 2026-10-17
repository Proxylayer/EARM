using System.Collections.Generic;
using System.Text.Json.Nodes;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Secrets;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Values;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// The functions brought in line with ARM, checked against the examples of the Microsoft Learn ARM template function reference and against values
/// produced by Azure Resource Manager itself: string ordering, <c>like</c>, <c>base64ToJson</c>, <c>dataUri</c> / <c>dataUriToString</c>,
/// <c>guid</c> / <c>uniqueString</c>, case-insensitive keys in <c>union</c> / <c>intersection</c>, <c>array</c>, <c>range</c>,
/// <c>deployment()</c>, <c>tenant()</c>, <c>deployer()</c> and <c>roleDefinitions()</c>.
/// </summary>
public sealed class ArmLearnExampleTests
{
    private const string GlobalReader = "f2ef992c-3afb-46b9-b7cf-a126ee74c451";
    private const string Tenant = "aaaabbbb-0000-cccc-1111-dddd2222eeee";

    // ---- comparison functions on strings (Learn: greater('A', 'a') is True) ----

    [Theory]
    [InlineData("[greater(1, 2)]", false)]
    [InlineData("[greater('A', 'a')]", true)]
    [InlineData("[greaterOrEquals(1, 2)]", false)]
    [InlineData("[greaterOrEquals('A', 'a')]", true)]
    [InlineData("[less(1, 2)]", true)]
    [InlineData("[less('A', 'a')]", false)]
    [InlineData("[lessOrEquals(1, 2)]", true)]
    [InlineData("[lessOrEquals('A', 'a')]", false)]
    [InlineData("[less('a', 'B')]", true)]
    [InlineData("[less('apple', 'Banana')]", true)]
    [InlineData("[lessOrEquals('abc', 'abc')]", true)]
    [InlineData("[greater('b', 'A')]", true)]
    public void Comparison_OfStrings_UsesArmsCultureOrdering(string expression, bool expected)
    {
        Expr.AssertJson(expected ? "true" : "false", Expr.Eval(expression));
    }

    // ---- like ----

    [Theory]
    [InlineData("*prod*", true)]
    [InlineData("prod*", true)]
    [InlineData("*01", true)]
    [InlineData("prod-eastus-vm01", true)]
    [InlineData("*westus*", false)]
    [InlineData("PROD-*-VM01", true)]
    [InlineData("*", true)]
    [InlineData("**", true)]
    [InlineData("prod*eastus*01", true)]
    [InlineData("prod*01*", true)]
    [InlineData("prod-eastus-vm0?", false)]
    [InlineData("prod", false)]
    [InlineData("vm01*prod", false)]
    [InlineData("prod-eastus-vm01*1", false)]
    public void Like_MatchesTheLearnExamples_CaseInsensitively(string pattern, bool expected)
    {
        var context = Expr.Context(Expr.Parameters(("resourceName", "prod-eastus-vm01"), ("pattern", pattern)));

        Expr.AssertJson(expected ? "true" : "false", Expr.Eval("[like(parameters('resourceName'), parameters('pattern'))]", context));
    }

    [Fact]
    public void Like_TreatsQuestionMarkLiterally_AndChecksItsArguments()
    {
        Expr.AssertJson("true", Expr.Eval("[like('a?c', 'a?c')]"));
        Expr.AssertJson("false", Expr.Eval("[like('abc', 'a?c')]"));
        Expr.AssertJson("true", Expr.Eval("[like('', '*')]"));
        Expr.AssertJson("false", Expr.Eval("[like('', 'a')]"));
        Expr.Fails("[like('a')]", DiagnosticCodes.InvalidFunctionArguments);
        Expr.Fails("[like(1, '*')]", DiagnosticCodes.InvalidFunctionArguments);
    }

    // ---- base64ToJson, dataUri, dataUriToString ----

    [Fact]
    public void Base64ToJson_DecodesAndParses()
    {
        Expr.AssertJson("{\"one\":\"a\",\"two\":\"b\"}", Expr.Eval("[base64ToJson('eyJvbmUiOiAiYSIsICJ0d28iOiAiYiJ9')]"));
        Expr.AssertJson("\"there\"", Expr.Eval("[base64ToJson(base64('{\"hi\": \"there\"}')).hi]"));
        Expr.AssertJson("[1,2]", Expr.Eval("[base64ToJson(base64('[1, 2]'))]"));
        Expr.Fails("[base64ToJson('!!')]", DiagnosticCodes.ExpressionEvaluationFailed);
        Expr.Fails("[base64ToJson(base64('{not json'))]", DiagnosticCodes.ExpressionEvaluationFailed);
        Expr.Fails("[base64ToJson(base64('{\"a\": 1, \"a\": 2}'))]", DiagnosticCodes.ExpressionEvaluationFailed);
    }

    [Fact]
    public void Base64ToJson_OfASecureValue_StaysSecure()
    {
        var context = Expr.Context(Expr.Parameters(("pw", SecretJson.CreateString("eyJrIjogInYifQ=="))));

        var result = Expr.Eval("[base64ToJson(parameters('pw'))]", context);

        Assert.True(UnknownJson.ContainsTaint(result));
    }

    [Theory]
    [InlineData("[dataUri('Hello')]", "data:text/plain;charset=utf8;base64,SGVsbG8=")]
    [InlineData("[dataUriToString('data:;base64,SGVsbG8sIFdvcmxkIQ==')]", "Hello, World!")]
    [InlineData("[dataUriToString(dataUri('abc'))]", "abc")]
    [InlineData("[dataUriToString('data:,Hello%2C%20World')]", "Hello, World")]
    [InlineData("[dataUriToString('DATA:text/plain;BASE64,SGk=')]", "Hi")]
    [InlineData("[dataUri('')]", "data:text/plain;charset=utf8;base64,")]
    public void DataUri_MatchesTheLearnExamples(string expression, string expected)
    {
        Assert.Equal(expected, Expr.AssertString(Expr.Eval(expression)));
    }

    [Theory]
    [InlineData("[dataUriToString('Hello')]")]
    [InlineData("[dataUriToString('data:text/plain;base64')]")]
    [InlineData("[dataUriToString('data:;base64,!!')]")]
    public void DataUriToString_RejectsWhatIsNotADataUri(string expression)
    {
        Expr.Fails(expression, DiagnosticCodes.ExpressionEvaluationFailed);
    }

    // ---- guid and uniqueString reproduce Azure Resource Manager's values ----

    [Theory]
    [InlineData("[guid('asdf')]", "228d27cc-6fdb-5934-b7ac-1efccebade04")]
    [InlineData("[guid('/subscriptions/11111111-1111-1111-1111-111111111111/resourcegroups/myrg', '22222222-2222-2222-2222-222222222222', '33333333-3333-3333-3333-333333333333')]", "eec47c62-a9a9-54dd-b8d8-f9e2839fab35")]
    [InlineData("[guid('/', '22222222-2222-2222-2222-222222222222', '33333333-3333-3333-3333-333333333333')]", "c095807d-5244-5404-8bce-e0e762e70618")]
    [InlineData("[guid('/providers/microsoft.management/managementgroups/mymg', '22222222-2222-2222-2222-222222222222', '33333333-3333-3333-3333-333333333333')]", "6aab5ec9-d102-539e-8b65-b053c060e64f")]
    [InlineData("[guid('/subscriptions/11111111-1111-1111-1111-111111111111', '99999999-9999-9999-9999-999999999999', '33333333-3333-3333-3333-333333333333')]", "54e40269-cc30-57bd-8031-5ca4efc75fd5")]
    public void Guid_ReproducesArmValues(string expression, string expected)
    {
        // Values computed by the Azure Resource Manager template engine (Bicep's evaluation baselines and its role assignment name tests).
        Assert.Equal(expected, Expr.AssertString(Expr.Eval(expression)));
    }

    [Theory]
    [InlineData("[uniqueString('')]", "aaaaaaaaaaaaa")]
    [InlineData("[uniqueString('test')]", "rbgf3xv4ufgzg")]
    [InlineData("[uniqueString('test', 'test2')]", "pivml2tg5alca")]
    [InlineData("[uniqueString('test', 'test2', 'test3')]", "bqgd334z2uj64")]
    [InlineData("[uniqueString('asd', 'asdf', 'asdf')]", "iizpqit7ih3cc")]
    public void UniqueString_ReproducesArmValues(string expression, string expected)
    {
        // Values returned by real deployments (a published Bicep deployment and Bicep's evaluation baselines).
        Assert.Equal(expected, Expr.AssertString(Expr.Eval(expression)));
    }

    // ---- union and intersection match object keys case-insensitively ----

    [Theory]
    [InlineData("[union(createObject('one', 'a', 'two', 'b', 'three', 'c1'), createObject('three', 'c2', 'four', 'd', 'five', 'e'))]", "{\"one\":\"a\",\"two\":\"b\",\"three\":\"c2\",\"four\":\"d\",\"five\":\"e\"}")]
    [InlineData("[union(createObject('a', 1), createObject('A', 2))]", "{\"a\":2}")]
    [InlineData("[union(createObject('Props', createObject('x', 1)), createObject('props', createObject('X', 3, 'y', 2)))]", "{\"Props\":{\"x\":3,\"y\":2}}")]
    [InlineData("[union(createObject('a', 1), createObject('B', 2), createObject('b', 3))]", "{\"a\":1,\"B\":3}")]
    [InlineData("[intersection(createObject('one', 'a', 'two', 'b', 'three', 'c'), createObject('one', 'a', 'two', 'z', 'three', 'c'))]", "{\"one\":\"a\",\"three\":\"c\"}")]
    [InlineData("[intersection(createObject('one', 'a', 'two', 'b'), createObject('ONE', 'a', 'Two', 'b'), createObject('one', 'a', 'TWO', 'x'))]", "{\"one\":\"a\"}")]
    [InlineData("[intersection(createObject('one', 'a'), createObject('ONE', 'A'))]", "{}")]
    [InlineData("[union(createArray('one', 'two', 'three'), createArray('three', 'four'))]", "[\"one\",\"two\",\"three\",\"four\"]")]
    [InlineData("[intersection(createArray('one', 'two', 'three'), createArray('two', 'three'))]", "[\"two\",\"three\"]")]
    public void UnionAndIntersection_FollowArm(string expression, string expectedJson)
    {
        Expr.AssertJson(expectedJson, Expr.Eval(expression));
    }

    // ---- array and range ----

    [Theory]
    [InlineData("[array(1)]", "[1]")]
    [InlineData("[array('efgh')]", "[\"efgh\"]")]
    [InlineData("[array(createObject('a', 'b', 'c', 'd'))]", "[{\"a\":\"b\",\"c\":\"d\"}]")]
    [InlineData("[array(createArray())]", "[]")]
    [InlineData("[array(createArray(1, 2))]", "[1,2]")]
    [InlineData("[range(5, 3)]", "[5,6,7]")]
    public void ArrayAndRange_MatchTheLearnExamples(string expression, string expectedJson)
    {
        Expr.AssertJson(expectedJson, Expr.Eval(expression));
    }

    [Fact]
    public void Array_OfAnUnknown_IsUnknown()
    {
        Expr.AssertUnknown(Expr.Eval("[array(tenant())]"));
    }

    // ---- deployment() ----

    [Fact]
    public void Deployment_HasArmsShape_AndKeepsTheFlatAliases()
    {
        var info = new DeploymentInfo("orders", "sha256:abc", Tenant, "Complete")
        {
            Parameters = new JsonObject { ["groupName"] = new JsonObject { ["value"] = "Orders" } },
        };
        var context = Expr.Context(deployment: info);

        Assert.Equal("orders", Expr.AssertString(Expr.Eval("[deployment().name]", context)));
        Assert.Equal("sha256:abc", Expr.AssertString(Expr.Eval("[deployment().properties.templateHash]", context)));
        Assert.Equal("Complete", Expr.AssertString(Expr.Eval("[deployment().properties.mode]", context)));
        Assert.Equal("Orders", Expr.AssertString(Expr.Eval("[deployment().properties.parameters.groupName.value]", context)));
        Assert.Equal("sha256:abc", Expr.AssertString(Expr.Eval("[deployment().templateHash]", context)));
        Assert.Equal("Complete", Expr.AssertString(Expr.Eval("[deployment().mode]", context)));
        Assert.Equal(Tenant, Expr.AssertString(Expr.Eval("[deployment().tenantId]", context)));
        Expr.AssertJson("false", Expr.Eval("[contains(deployment().properties, 'templateLink')]", context));
        Expr.AssertJson("false", Expr.Eval("[contains(deployment().properties, 'template')]", context));
    }

    [Fact]
    public void SuppliedParameters_ListOnlySuppliedNonSecureValues()
    {
        var bound = new Dictionary<string, BoundParameter>
        {
            ["fromFile"] = Bound("fromFile", "string", "Orders", ParameterValueSource.File),
            ["inline"] = Bound("inline", "int", JsonValue.Create(3), ParameterValueSource.Inline),
            ["defaulted"] = Bound("defaulted", "string", "[concat('a', 'b')]", ParameterValueSource.Default),
            ["password"] = Bound("password", "securestring", SecretJson.CreateString("s3cret"), ParameterValueSource.File),
            ["pending"] = Bound("pending", "string", null, ParameterValueSource.File) with { PendingReference = new KeyVaultReference("/subscriptions/x/resourceGroups/y/providers/Microsoft.KeyVault/vaults/v", "s", null) },
        };

        var parameters = DeploymentInfo.SuppliedParameters(bound);

        Expr.AssertJson("{\"fromFile\":{\"value\":\"Orders\"},\"inline\":{\"value\":3}}", parameters);
    }

    // ---- tenant() and deployer() ----

    [Fact]
    public void TenantAndDeployer_AreAllowedInVariablesAndParameterDefaults()
    {
        var info = new DeploymentInfo("d", null, Tenant, "Incremental")
        {
            Tenant = new JsonObject { ["countryCode"] = "US", ["displayName"] = "Contoso", ["id"] = "/tenants/" + Tenant, ["tenantId"] = Tenant, ["defaultDomain"] = "contoso.com" },
            Deployer = new JsonObject { ["objectId"] = "aaaaaaaa-0000-1111-2222-bbbbbbbbbbbb", ["tenantId"] = Tenant, ["userPrincipalName"] = "john.doe@contoso.com" },
        };

        foreach (var scope in new[] { EvaluationScope.Variable, EvaluationScope.ParameterDefault, EvaluationScope.Output, EvaluationScope.Resource })
        {
            var context = Expr.Context(scope: scope, deployment: info);
            Assert.Equal("US", Expr.AssertString(Expr.Eval("[tenant().countryCode]", context)));
            Assert.Equal("/tenants/" + Tenant, Expr.AssertString(Expr.Eval("[tenant().id]", context)));
            Assert.Equal("john.doe@contoso.com", Expr.AssertString(Expr.Eval("[deployer().userPrincipalName]", context)));
        }
    }

    [Fact]
    public void Deployer_IsUnknownOffline_AndTakesNoArguments()
    {
        var unknown = Expr.AssertUnknown(Expr.Eval("[deployer().objectId]"));

        Assert.Equal("deployer().objectId", unknown.Symbol);
        Assert.Equal(UnknownOrigins.Deployment, unknown.Origins);
        Expr.Fails("[deployer(1)]", DiagnosticCodes.InvalidFunctionArguments);
    }

    // ---- roleDefinitions() for directory roles ----

    [Fact]
    public void RoleDefinitions_ResolvesABuiltInDirectoryRoleByName()
    {
        var context = EntraContext(PlaceholderEntraLookup.Instance);

        Expr.AssertJson(
            "{\"id\":\"" + GlobalReader + "\",\"roleDefinitionId\":\"" + GlobalReader + "\",\"name\":\"Global Reader\",\"templateId\":\"" + GlobalReader + "\"}",
            Expr.Eval("[roleDefinitions('Global Reader')]", context));
        Assert.Equal(GlobalReader, Expr.AssertString(Expr.Eval("[roleDefinitions('Global Reader').id]", context)));
    }

    [Fact]
    public void RoleDefinitions_ResolvesACustomRoleFromTheTenant_AndIsUnknownOffline()
    {
        const string custom = "12345678-1234-1234-1234-123456789012";
        var online = EntraContext(new InMemoryEntraLookup().AddRoleDefinition("Orders Reader", custom));

        Assert.Equal(custom, Expr.AssertString(Expr.Eval("[roleDefinitions('Orders Reader').roleDefinitionId]", online)));
        Expr.AssertUnknown(Expr.Eval("[roleDefinitions('Orders Reader')]", EntraContext(PlaceholderEntraLookup.Instance)));
        Expr.Fails("[roleDefinitions('Nope Role')]", DiagnosticCodes.ExpressionEvaluationFailed, EntraContext(new InMemoryEntraLookup()));
        Expr.Fails("[roleDefinitions('Global Reader')]", DiagnosticCodes.FunctionNotAllowedHere, EntraContext(PlaceholderEntraLookup.Instance, EvaluationScope.Variable));
    }

    private static EvaluationContext EntraContext(IEntraLookup lookup, EvaluationScope scope = EvaluationScope.Resource)
    {
        return new EvaluationContext
        {
            Parameters = Expr.Parameters(),
            Resources = TemplateResourceIndex.Empty,
            References = StaticReferenceResolver.Instance,
            EntraLookup = lookup,
            Deployment = DeploymentInfo.Offline,
            Scope = scope,
        }.Bind(Expr.Evaluator);
    }

    private static BoundParameter Bound(string name, string type, JsonNode? value, ParameterValueSource source)
    {
        return new BoundParameter
        {
            Name = name,
            Definition = new ParameterDefinition { Name = name, Type = type, Source = new JsonObject { ["type"] = type }, Path = EArm.Core.Json.JsonPath.Root },
            Value = value,
            Source = source,
        };
    }
}
