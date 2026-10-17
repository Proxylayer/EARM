using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Engine.Permissions;
using EArm.Engine.Planning;
using EArm.Engine.Providers;
using EArm.Expressions;
using EArm.Expressions.Evaluation;
using EArm.Expressions.Functions;
using EArm.Expressions.Static;
using EArm.Expressions.Values;
using EArm.Engine;
using EArm.Providers;
using EArm.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace EArm.Tests.Engine;

/// <summary>
/// The run budget measures real work: a variable is evaluated once per evaluation context (also when resources are planned in parallel), and
/// reading part of it (<c>variables('x')[copyIndex()].name</c>) costs the part, not a copy of the whole variable. Regression for the PointGuard
/// what-if that failed with 71 <c>EvaluationBudgetExceeded</c>: tests/fixtures/budget holds a copy of that template (about fifty variables built
/// with map / filter / flatten over data, copy loops whose names and properties index into them); the CLI's what-if path (offline expansion,
/// offline validation, then the plan with the preflight's tenant(), deployer() and environment()) used to spend 5,000,000 value nodes on it,
/// 3,940,000 of them copying <c>packagedGroups</c> (about 1,400 nodes) for each of its 2,768 element reads.
/// The long tests sit in nested classes: each is its own xUnit collection, so they run in parallel with each other and the rest of the class.
/// </summary>
public sealed class EvaluationBudgetPlanningTests
{
    private const string TenantId = "00000000-0000-0000-0000-0000000000aa";

    /// <summary>The PointGuard what-if needs about 260,000 nodes; a regression back to copying whole variables needs well over this.</summary>
    private const long PointGuardNodeCeiling = 1_000_000;

    private static string Fixture(string file) => Path.Combine(TestPaths.RepoRoot, "tests", "fixtures", "budget", file);

    /// <summary>The PointGuard plan (what-if and create) against the run budget.</summary>
    public sealed class PointGuardRunBudget
    {
        [Theory]
        [InlineData(PlanMode.WhatIf)]
        [InlineData(PlanMode.Create)]
        public async Task PointGuardTemplate_CliPathWithPreflightInfo_StaysWithinTheRunBudget(PlanMode planMode)
        {
            using var run = EvaluationBudget.BeginRun(EvaluationLimits.Default);

            var plan = await PlanPointGuardAsync(planMode);

            Assert.False(run.Budget.IsExceeded);
            Assert.DoesNotContain(plan.Diagnostics, d => d.Code == DiagnosticCodes.EvaluationBudgetExceeded);
            Assert.True(plan.Instances.Count > 100, $"{plan.Instances.Count} instances planned");
            Assert.True(run.Budget.Nodes < PointGuardNodeCeiling, $"the run produced {run.Budget.Nodes:N0} value nodes");
        }
    }

    /// <summary>The PointGuard what-if plans the groups with their data.</summary>
    public sealed class PointGuardWhatIfGroups
    {
        /// <summary>
        /// The what-if of the PointGuard template against a tenant holding the break-glass users and the delete-protection and database groups its scripts
        /// created (display name, no uniqueName). Every group but the emergency group declares its whole <c>properties</c> as one expression
        /// (<c>"[variables('approverGroups')[copyIndex()].properties]"</c>): each must be planned with the properties of its data, not <c>properties: {}</c>
        /// (the planner used to evaluate only an object literal and planned 54 of 55 groups empty, "required property displayName missing").
        /// </summary>
        [Fact]
        public async Task PointGuardTemplate_WhatIf_CreatesTheGroupsWithTheirDataAndAdoptsTheScriptGroups()
        {
            var plan = await PlanPointGuardAsync(PlanMode.WhatIf, SeedScriptGroups);

            var groups = plan.Instances.Where(i => i.Type == GroupType).ToList();
            Assert.Equal(55, groups.Count);
            var adopted = Evaluate(plan, "[variables('adoptedGroups')]")!.AsArray().Select(g => g!["uniqueName"]!.GetValue<string>()).ToHashSet(StringComparer.OrdinalIgnoreCase);
            Assert.Equal(15, adopted.Count);
            foreach (var group in groups)
            {
                var change = plan.ChangeFor(group);
                Assert.NotNull(change);
                Assert.False(change.HasError, $"{group.Name}: {change.UnsupportedReason}");
                string? displayName = change.After?["properties"]?["displayName"]?.GetValue<string>() ?? change.After?["displayName"]?.GetValue<string>();
                Assert.False(string.IsNullOrEmpty(displayName), $"{group.Name}: {change.After?.ToJsonString()}");
                Assert.Equal(group.Properties!["displayName"]!.GetValue<string>(), displayName);
                Assert.Equal(true, group.Properties["securityEnabled"]?.GetValue<bool>());
                Assert.Equal(false, group.Properties["mailEnabled"]?.GetValue<bool>());
                Assert.NotNull(group.Properties["description"]);
                Assert.NotNull(group.Properties["isAssignableToRole"]);
                if (adopted.Contains(group.Name))
                {
                    Assert.Equal(ChangeType.Modify, change.Type);
                    Assert.Contains(change.Deltas, d => d.HasEffect && d.Path.ToString().Contains("uniqueName", StringComparison.Ordinal));
                    Assert.Contains(change.Reasons, r => r.Contains("GroupAdoptedByDisplayName", StringComparison.Ordinal));
                }
                else
                {
                    Assert.Equal(ChangeType.Create, change.Type);
                }
            }

            // The data of each loop, as the template declares it: the approver groups' display names and the database groups' mailNickname.
            Assert.Equal("Security - Approvers - Dev", Single(groups, "pg-dev-approvers").Properties!["displayName"]!.GetValue<string>());
            Assert.Equal("Security - Tenant Approvers", Single(groups, "pg-tenant-approvers").Properties!["displayName"]!.GetValue<string>());
            Assert.Equal("db-sit0-owner", Single(groups, "pg-sit-database-owner").Properties!["mailNickname"]!.GetValue<string>());
            Assert.Equal("Emergency Access - GA-PRA", Single(groups, "pg-emergency-access-ga-pra").Properties!["displayName"]!.GetValue<string>());

            // A read-only role group holds its role through the directoryRoles edge, which its properties expression declares (shallowMerge): an edge, not an object property.
            var readOnlyRoleGroup = groups.First(g => g.EdgeProperties?["directoryRoles"] is not null);
            Assert.Null(readOnlyRoleGroup.Properties!["directoryRoles"]);

            Assert.DoesNotContain(plan.Diagnostics, d => d.IsError);
            Assert.DoesNotContain(plan.Diagnostics, d => d.Code == EngineDiagnosticCodes.OrphanReportIncomplete && d.Message.Contains("roleEligibilityScheduleRequests", StringComparison.Ordinal));
        }
    }

    /// <summary>The PointGuard what-if leaves the variables unchanged.</summary>
    public sealed class PointGuardWhatIfVariables
    {
        /// <summary>
        /// Values read from variables are never changed through the instances planned from them: after the what-if (providers strip, add and hold back
        /// properties of the instances they plan) every memoised variable still holds the template's data, and evaluating an instance again gives the same properties.
        /// </summary>
        [Fact]
        public async Task PointGuardTemplate_WhatIf_LeavesTheVariablesUnchanged_AndReevaluatesInstancesIdentically()
        {
            var plan = await PlanPointGuardAsync(PlanMode.WhatIf, SeedScriptGroups);

            foreach (string variable in new[] { "approverGroups", "accessGroups", "roleGroups", "adoptedGroups" })
            {
                var data = Evaluate(plan, $"[variables('{variable}')]")!.AsArray();
                Assert.NotEmpty(data);
                foreach (var entry in data)
                {
                    var properties = entry!["properties"]!.AsObject();
                    Assert.False(string.IsNullOrEmpty(properties["displayName"]?.GetValue<string>()), $"{variable}: {entry.ToJsonString()}");
                    Assert.NotNull(properties["description"]);
                    Assert.NotNull(properties["securityEnabled"]);
                    var instance = Single(plan.Instances.Where(i => i.Type == GroupType), entry["uniqueName"]!.GetValue<string>());
                    var declared = JsonValues.Clone(properties)!.AsObject();
                    foreach (var edge in instance.EdgeProperties!)
                    {
                        declared.Remove(edge.Key);
                    }

                    Assert.True(JsonNode.DeepEquals(declared, instance.Properties), $"{instance.Name}: data {declared.ToJsonString()}, planned {instance.Properties!.ToJsonString()}");
                }
            }

            foreach (var instance in plan.Instances.Where(i => i.Type is GroupType or "Microsoft.Entra/roleManagementPolicies" or "Microsoft.Entra/conditionalAccessPolicies" && !i.IsIgnored).Take(80))
            {
                InstanceEvaluator.EvaluateProperties(plan.Evaluator, instance);
                string first = instance.Properties!.ToJsonString() + instance.EdgeProperties!.ToJsonString();
                InstanceEvaluator.EvaluateProperties(plan.Evaluator, instance);
                Assert.Equal(first, instance.Properties!.ToJsonString() + instance.EdgeProperties!.ToJsonString());
            }
        }
    }

    [Fact]
    public void APropertiesExpression_IsSplitIntoOneDeclaredValuePerKey_PerCopyIteration()
    {
        var expanded = ExpandProperties(
            """{ "groups": [ { "name": "a", "properties": { "displayName": "A", "it's": "quoted", "memberOf": [ "x" ] } }, { "name": "b", "properties": { "displayName": "B" } } ] }""",
            "[variables('groups')[copyIndex()].properties]");

        // Known, non-secure values are declared as literals (WholePropertiesApplyOrderTests covers the values kept as expressions).
        Assert.Equal(2, expanded.Count);
        Expr.AssertJson("""{ "displayName": "A", "it's": "quoted", "memberOf": [ "x" ] }""", expanded[0].Declaration.Properties);
        Expr.AssertJson("""{ "displayName": "B" }""", expanded[1].Declaration.Properties);

        var evaluator = new ExpressionEvaluator();
        InstanceEvaluator.EvaluateProperties(evaluator, expanded[0]);
        Expr.AssertJson("""{ "displayName": "A", "it's": "quoted" }""", expanded[0].Properties);
        Expr.AssertJson("""{ "memberOf": [ "x" ] }""", expanded[0].EdgeProperties);
        InstanceEvaluator.EvaluateProperties(evaluator, expanded[1]);
        Expr.AssertJson("""{ "displayName": "B" }""", expanded[1].Properties);
    }

    /// <summary>The PointGuard offline validation against the run budget.</summary>
    public sealed class PointGuardOfflineValidation
    {
        [Fact]
        public async Task PointGuardTemplate_OfflineValidation_StaysWithinTheRunBudget()
        {
            using var run = EvaluationBudget.BeginRun(EvaluationLimits.Default);
            var catalog = ResourceTypeCatalog.Default;
            var document = await new TemplateLoader(catalog).LoadAsync(Fixture("pointguard.entra.json"), ParameterSet.FromFile(Fixture("pointguard.parameters.json")), TestContext.Current.CancellationToken);

            var diagnostics = new DeploymentPlanner(ProviderDiscovery.CreateRegistry(catalog), new FakeGraphClient(), new PlannerOptions { Catalog = catalog }, NullLogger<DeploymentPlanner>.Instance).Validate(document, PlanMode.WhatIf);

            Assert.DoesNotContain(diagnostics, d => d.IsError);
            Assert.True(run.Budget.Nodes < PointGuardNodeCeiling / 4, $"validation produced {run.Budget.Nodes:N0} value nodes");
        }
    }

    [Fact]
    public void ElementReadsOfALargeVariable_ChargeTheElementNotTheVariable()
    {
        using var run = EvaluationBudget.BeginRun(EvaluationLimits.Default);
        var context = Expr.Context(variables: Expr.Parameters(("big", JsonValue.Create("[range(0, 10000)]"))));

        for (int i = 0; i < 2_000; i++)
        {
            Assert.Equal(i.ToString(System.Globalization.CultureInfo.InvariantCulture), Expr.Render(Expr.Eval($"[variables('big')[{i}]]", context)));
        }

        // 10,001 nodes for the variable once, a few per read; copying the variable for every read would be 20,000,000.
        Assert.True(run.Budget.Nodes < 100_000, $"{run.Budget.Nodes:N0} nodes");
        Assert.False(run.Budget.IsExceeded);
    }

    [Fact]
    public void PropertyChainsIntoAVariable_ReturnCopies()
    {
        var context = Expr.Context(variables: Expr.Parameters(("obj", JsonNode.Parse("""{ "items": [ { "name": "a", "tags": [ "x" ] } ] }"""))));

        var tags = Expr.Eval("[variables('obj').items[0].tags]", context)!.AsArray();
        tags.Add("mutated");

        Expr.AssertJson("""{ "items": [ { "name": "a", "tags": [ "x" ] } ] }""", Expr.Eval("[variables('obj')]", context));
        Expr.AssertJson("\"a\"", Expr.Eval("[variables('obj')['ITEMS'][0].Name]", context));
    }

    [Fact]
    public void AVariableReadManyTimes_IsEvaluatedOnce()
    {
        int calls = 0;
        var registry = FunctionRegistry.CreateDefault();
        registry.Register(new FunctionDescriptor { Name = "countCalls", Group = "test", MinArgs = 0, MaxArgs = 0, Implementation = _ => JsonValue.Create(Interlocked.Increment(ref calls)) });
        var context = Expr.Context(variables: Expr.Parameters(("v", JsonNode.Parse("""{ "n": "[countCalls()]", "list": [ 1, 2, 3 ] }"""))), functions: registry);

        for (int i = 0; i < 50; i++)
        {
            Expr.Eval("[variables('v').n]", context);
            Expr.Eval("[variables('v').list[1]]", context);
            Expr.Eval("[length(variables('v').list)]", context);
            Expr.Eval("[variables('v')]", context);
        }

        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task AVariableReadConcurrently_IsEvaluatedOnce()
    {
        int calls = 0;
        var registry = FunctionRegistry.CreateDefault();
        registry.Register(new FunctionDescriptor
        {
            Name = "slowCount",
            Group = "test",
            MinArgs = 0,
            MaxArgs = 0,
            Implementation = _ =>
            {
                int call = Interlocked.Increment(ref calls);
                Thread.Sleep(200);
                return JsonValue.Create(call);
            },
        });
        var context = Expr.Context(variables: Expr.Parameters(("v", JsonValue.Create("[slowCount()]")), ("w", JsonValue.Create("[add(variables('v'), 1)]"))), functions: registry);

        var readers = Enumerable.Range(0, 8).Select(i => Task.Run(() => Expr.Eval(i % 2 == 0 ? "[variables('v')]" : "[variables('w')]", context), TestContext.Current.CancellationToken)).ToArray();
        var results = await Task.WhenAll(readers);

        Assert.Equal(1, calls);
        Assert.All(results.Where((_, i) => i % 2 == 0), r => Assert.Equal("1", Expr.Render(r)));
        Assert.All(results.Where((_, i) => i % 2 == 1), r => Assert.Equal("2", Expr.Render(r)));
    }

    [Fact]
    public void AVariableCycle_IsStillReported()
    {
        var context = Expr.Context(variables: Expr.Parameters(("a", JsonValue.Create("[variables('b')[0]]")), ("b", JsonValue.Create("[createArray(variables('a'))]"))));

        Expr.Fails("[variables('a')]", DiagnosticCodes.VariableCycle, context);
    }

    [Fact]
    public void IndexedVariableReads_KeepTheRulesOfVariables()
    {
        var inFunction = Expr.Context(variables: Expr.Parameters(("v", JsonNode.Parse("[1]"))), scope: EvaluationScope.UserFunction);

        Expr.Fails("[variables('v')[0]]", DiagnosticCodes.FunctionNotAllowedHere, inFunction);
        Expr.Fails("[variables('missing')[0]]", DiagnosticCodes.UndefinedVariable, Expr.Context(variables: Expr.Parameters(("v", JsonNode.Parse("[1]")))));
        Expr.Fails("[variables('v')[1]]", DiagnosticCodes.ExpressionEvaluationFailed, Expr.Context(variables: Expr.Parameters(("v", JsonNode.Parse("[1]")))));
    }

    /// <summary>Whole-variable copies against the run budget.</summary>
    public sealed class WholeVariableCopies
    {
        [Fact]
        public void WholeVariableCopies_StillCountAgainstTheRunBudget()
        {
            // length(variables('big')) copies the variable for the function: 10,001 nodes per call, so 600 calls pass 5,000,000.
            using var run = EvaluationBudget.BeginRun(EvaluationLimits.Default);
            var context = Expr.Context(variables: Expr.Parameters(("big", JsonValue.Create("[range(0, 10000)]"))));

            var exception = Assert.Throws<ExpressionEvaluationException>(() =>
            {
                for (int i = 0; i < 600; i++)
                {
                    Expr.Eval("[length(variables('big'))]", context);
                }
            });

            Assert.Equal(DiagnosticCodes.EvaluationBudgetExceeded, exception.Code);
            Assert.True(run.Budget.IsExceeded);
        }
    }

    private const string GroupType = "Microsoft.Entra/groups";

    private static JsonNode? Evaluate(DeploymentPlan plan, string expression) => plan.Evaluator.EvaluateTree(JsonValue.Create(expression), plan.Context);

    private static ResourceInstance Single(IEnumerable<ResourceInstance> instances, string name) => Assert.Single(instances, i => string.Equals(i.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The groups Set-DeleteProtection.ps1 and Set-EnvironmentSecurityGroups.ps1 created: display name, no uniqueName.</summary>
    private static void SeedScriptGroups(FakeGraphClient client)
    {
        var displayNames = new[] { "Security - Delete Protection - Shared", "Security - Delete Protection - Uat", "Security - Delete Protection - Prd" }
            .Concat(from env in new[] { "dev", "uat", "sit0", "prd" } from tier in new[] { "reader", "writer", "owner" } select $"db-{env}-{tier}");
        foreach (string displayName in displayNames)
        {
            client.Directory.Add("/groups", new JsonObject
            {
                ["displayName"] = displayName,
                ["mailEnabled"] = false,
                ["mailNickname"] = displayName.StartsWith("db-", StringComparison.Ordinal) ? displayName : "script-" + Guid.NewGuid().ToString("N")[..8],
                ["securityEnabled"] = true,
                ["isAssignableToRole"] = null,
                ["groupTypes"] = new JsonArray(),
            });
        }
    }

    private static IReadOnlyList<ResourceInstance> ExpandProperties(string variables, string propertiesExpression)
    {
        var catalog = ResourceTypeCatalog.Default;
        string resource = "{ \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"[variables('groups')[copyIndex()].name]\", "
            + "\"copy\": { \"name\": \"c\", \"count\": \"[length(variables('groups'))]\" }, \"properties\": \"" + propertiesExpression + "\" }";
        var document = new TemplateLoader(catalog).Parse(Review1C.Template("\"variables\": " + variables + ", \"resources\": [ " + resource + " ]"), "split.entra.json", ParameterSet.Empty);
        var staticContext = new StaticEvaluator().Evaluate(document);
        var diagnostics = new List<Diagnostic>();
        var instances = new ResourceExpander(ExpressionEvaluator.Default, catalog).Expand(staticContext, staticContext.Context, diagnostics);
        Assert.DoesNotContain(diagnostics, d => d.IsError);
        return instances;
    }

    private static async Task<DeploymentPlan> PlanPointGuardAsync(PlanMode planMode, Action<FakeGraphClient>? seed = null)
    {
        var ct = TestContext.Current.CancellationToken;
        var catalog = ResourceTypeCatalog.Default;
        var registry = ProviderDiscovery.CreateRegistry(catalog);
        foreach (var definition in catalog.Types.Where(d => !registry.Contains(d.Type)))
        {
            registry.Register(new EchoProvider(definition, catalog));
        }

        // The existing break-glass users the template adopts, the onboarding account it creates (present from an earlier run) and the signed-in admin.
        var client = new FakeGraphClient();
        client.Directory.Add("/users", User("33333333-0000-0000-0000-000000000001", "breakglass@pgcfo.com", "Break Glass", accountEnabled: true));
        client.Directory.Add("/users", User("33333333-0000-0000-0000-000000000002", "Gnarwhal@pgcfo.com", "Gnarwhal", accountEnabled: true));
        client.Directory.Add("/users", User("33333333-0000-0000-0000-000000000003", "pim-onboarding@pgcfo.com", "PG PIM Onboarding (disabled)", accountEnabled: false));
        client.Directory.Add("/users", User("33333333-0000-0000-0000-000000000004", "admin@pgcfo.com", "Admin", accountEnabled: true));
        seed?.Invoke(client);
        client.ClearRequests();

        var document = await new TemplateLoader(catalog).LoadAsync(Fixture("pointguard.entra.json"), ParameterSet.FromFile(Fixture("pointguard.parameters.json")), ct);

        // The CLI's order (PlannedDeployment): offline expansion for the run's scopes, offline validation, then the plan with the preflight's information.
        Assert.True(OfflineTemplateExpansion.Run(document, catalog).Succeeded);
        new DeploymentPlanner(registry, client, new PlannerOptions { DeploymentName = "pointguard", Catalog = catalog }, NullLogger<DeploymentPlanner>.Instance).Validate(document, PlanMode.WhatIf);

        var options = new PlannerOptions
        {
            DeploymentName = "pointguard",
            TenantId = TenantId,
            Tenant = new JsonObject { ["tenantId"] = TenantId, ["countryCode"] = "US", ["displayName"] = "PointGuard" },
            Deployer = new JsonObject { ["objectId"] = "33333333-0000-0000-0000-000000000004", ["tenantId"] = TenantId, ["userPrincipalName"] = "admin@pgcfo.com" },
            SignedInPrincipalId = "33333333-0000-0000-0000-000000000004",
            Catalog = catalog,
            EdgeKinds = ProviderDiscovery.CreateEdgeKinds(catalog),
            KeyAbsenceCheck = new KeyAbsenceCheck { Delay = new RecordingDelayProvider() },
            ConfirmKeyAbsence = false,
        };
        return await new DeploymentPlanner(registry, client, options, NullLogger<DeploymentPlanner>.Instance).PlanAsync(document, DeploymentMode.Incremental, planMode, ct);
    }

    private static JsonObject User(string id, string userPrincipalName, string displayName, bool accountEnabled)
    {
        return new JsonObject { ["id"] = id, ["userPrincipalName"] = userPrincipalName, ["displayName"] = displayName, ["mailNickname"] = userPrincipalName.Split('@')[0], ["accountEnabled"] = accountEnabled };
    }
}
