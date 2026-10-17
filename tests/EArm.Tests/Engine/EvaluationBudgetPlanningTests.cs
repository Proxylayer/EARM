using System;
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
/// </summary>
public sealed class EvaluationBudgetPlanningTests
{
    private const string TenantId = "00000000-0000-0000-0000-0000000000aa";

    /// <summary>The PointGuard what-if needs about 260,000 nodes; a regression back to copying whole variables needs well over this.</summary>
    private const long PointGuardNodeCeiling = 1_000_000;

    private static string Fixture(string file) => Path.Combine(TestPaths.RepoRoot, "tests", "fixtures", "budget", file);

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

    private static async Task<DeploymentPlan> PlanPointGuardAsync(PlanMode planMode)
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
