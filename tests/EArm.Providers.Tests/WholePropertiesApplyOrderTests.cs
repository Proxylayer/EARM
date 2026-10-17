using System.Linq;
using System.Threading.Tasks;
using EArm.Engine.Planning;
using EArm.Providers.Tests.Infrastructure;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// A resource whose whole <c>properties</c> is one expression (<c>"properties": "[variables('policy')]"</c>) is ordered like the same resource
/// declared with an object literal: the split at expansion declares the known values of that expression as literals, so the apply-order helpers
/// that match declared literals (a group PIM policy and the eligibility requests of the same group) see them. They used to see one expression per
/// key and found nothing to order.
/// </summary>
public sealed class WholePropertiesApplyOrderTests
{
    private const string GroupId = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
    private const string PrincipalId = "1b2c3d4e-5f60-4b7c-8d9e-0f1a2b3c4d5e";
    private const string PolicyName = "9a8b7c6d-5e4f-4a3b-8c2d-1e0f9a8b7c6d";
    private const string EligibilityName = "7a2c9b3f-4d5e-4f60-9b0c-1d2e3f4a5b6c";

    private const string PolicyProperties = "{ \"group\": \"" + GroupId + "\", \"accessId\": \"member\", \"rules\": [ { \"id\": \"Expiration_EndUser_Assignment\", \"maximumDuration\": \"PT4H\" } ] }";

    private static string Template(string policyProperties)
    {
        string policy = Templates.Resource(RoleManagementPoliciesProvider.ResourceType, PolicyName, policyProperties);
        string eligibility = Templates.Resource(GroupEligibilityScheduleRequestsProvider.ResourceType, EligibilityName, "{ \"groupId\": \"" + GroupId + "\", \"principalId\": \"" + PrincipalId + "\", \"accessId\": \"member\" }");
        return "{ \"$schema\": \"" + Templates.Schema + "\", \"contentVersion\": \"1.0.0.0\", \"variables\": { \"policy\": " + PolicyProperties + " }, \"resources\": [ " + policy + ", " + eligibility + " ] }";
    }

    private static async Task<bool> EligibilityWaitsForPolicyAsync(string policyProperties)
    {
        var host = new ProviderHost();
        var plan = await host.ExpandAsync(host.Parse(Template(policyProperties)));
        var eligibility = plan.Instances.Single(i => i.Id.NameLeaf == EligibilityName);
        return plan.Graph.DependenciesOf(eligibility).Any(i => i.Id.NameLeaf == PolicyName);
    }

    [Fact]
    public async Task An_object_literal_policy_is_applied_before_the_eligibility_of_its_group()
    {
        Assert.True(await EligibilityWaitsForPolicyAsync(PolicyProperties));
    }

    [Fact]
    public async Task A_whole_properties_expression_policy_is_applied_before_the_eligibility_of_its_group()
    {
        Assert.True(await EligibilityWaitsForPolicyAsync("\"[variables('policy')]\""));
    }

    [Fact]
    public async Task The_split_declares_known_values_as_literals_and_keeps_secure_or_deploy_time_values_as_expressions()
    {
        var host = new ProviderHost();
        string resources = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\", "
            + "\"properties\": \"[variables('base')]\" }";
        string template = "{ \"$schema\": \"" + Templates.Schema + "\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { " + Templates.SecureParameter("secret") + " }, "
            + "\"variables\": { \"base\": { \"displayName\": \"[[not an expression]\", \"mailNickname\": \"g\", \"tags\": [ \"[[x]\", \"y\" ], \"description\": \"[parameters('secret')]\" } }, \"resources\": [ " + resources + " ] }";

        var plan = await host.ExpandAsync(host.Parse(template, ("secret", "s3cr3t")));

        var declared = plan.Instances.Single().Declaration.Properties!.AsObject();
        Assert.Equal("[[not an expression]", declared["displayName"]!.GetValue<string>());
        Assert.Equal("g", declared["mailNickname"]!.GetValue<string>());
        Assert.Equal("[[x]", declared["tags"]![0]!.GetValue<string>());
        Assert.StartsWith("[variables(", declared["description"]!.GetValue<string>(), System.StringComparison.Ordinal);
        Assert.DoesNotContain("s3cr3t", declared.ToJsonString(), System.StringComparison.Ordinal);

        var instance = plan.Instances.Single();
        InstanceEvaluator.EvaluateProperties(plan.Evaluator, instance);
        Assert.Equal("[not an expression]", instance.Properties!["displayName"]!.GetValue<string>());
        Assert.Equal("[x]", instance.Properties!["tags"]![0]!.GetValue<string>());
        Assert.True(EArm.Core.Secrets.SecretJson.IsSecret(instance.Properties!["description"]));
    }
}
