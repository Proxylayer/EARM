using System;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Expressions.Evaluation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Review 1D / G-01: <c>appRole(wellKnownApp('MicrosoftGraph'), 'X')</c> and <c>permissionScope(...)</c> prefer the LIVE tenant answer over the
/// embedded table (which had a wrong GUID for <c>Application.ReadWrite.All</c>), and fall back to the table offline, when the tenant has no such
/// principal to ask, or when it cannot be asked.
/// </summary>
public sealed class Review1D_EntraLookupPrecedenceTests
{
    private const string Graph = WellKnownIds.MicrosoftGraphAppId;
    private const string RealApplicationReadWriteAll = "1bfefb4e-e0b5-418b-a88f-73c46d2cc8e9";
    private const string TenantAnswer = "99999999-0000-0000-0000-000000000001";

    private static EvaluationContext Context(IEntraLookup? lookup)
    {
        return new EvaluationContext
        {
            Parameters = Expr.Parameters(),
            Resources = Expr.Index(("Microsoft.Entra/applications", "app")),
            References = StaticReferenceResolver.Instance,
            EntraLookup = lookup ?? PlaceholderEntraLookup.Instance,
            Deployment = DeploymentInfo.Offline,
            Scope = EvaluationScope.Resource,
        }.Bind(Expr.Evaluator);
    }

    [Fact]
    public void ALiveGraphAnswer_WinsOverTheEmbeddedTable()
    {
        var lookup = new InMemoryEntraLookup().AddAppRole(Graph, "Application.ReadWrite.All", TenantAnswer).AddPermissionScope(Graph, "User.Read", TenantAnswer);

        Assert.Equal(TenantAnswer, Expr.AssertString(Expr.Eval("[appRole(wellKnownApp('MicrosoftGraph'), 'Application.ReadWrite.All')]", Context(lookup))));
        Assert.Equal(TenantAnswer, Expr.AssertString(Expr.Eval("[permissionScope(wellKnownApp('MicrosoftGraph'), 'User.Read')]", Context(lookup))));
    }

    [Fact]
    public void Offline_TheTableAnswers_WithTheCorrectedApplicationReadWriteAllId()
    {
        Assert.Equal(RealApplicationReadWriteAll, Expr.AssertString(Expr.Eval("[appRole(wellKnownApp('MicrosoftGraph'), 'Application.ReadWrite.All')]", Context(null))));
    }

    [Fact]
    public void WhenTheTenantHasNoGraphPrincipalToAsk_TheTableAnswers()
    {
        var lookup = new InMemoryEntraLookup().AddAppRole("aaaaaaaa-1111-2222-3333-444444444444", "Other", "x");

        Assert.Equal(RealApplicationReadWriteAll, Expr.AssertString(Expr.Eval("[appRole(wellKnownApp('MicrosoftGraph'), 'Application.ReadWrite.All')]", Context(lookup))));
    }

    [Fact]
    public void WhenTheTenantCannotBeAsked_TheTableStillAnswersForGraph_ButNotForOtherApplications()
    {
        var lookup = new ThrowingLookup();

        Assert.Equal(RealApplicationReadWriteAll, Expr.AssertString(Expr.Eval("[appRole(wellKnownApp('MicrosoftGraph'), 'Application.ReadWrite.All')]", Context(lookup))));
        Expr.Fails("[appRole('aaaaaaaa-1111-2222-3333-444444444444', 'r')]", DiagnosticCodes.ExpressionEvaluationFailed, Context(lookup));
    }

    [Fact]
    public void AGraphValueNotInTheTable_IsStillResolvedFromTheTenant()
    {
        var lookup = new InMemoryEntraLookup().AddAppRole(Graph, "Brand.New.Permission", TenantAnswer);

        Assert.Equal(TenantAnswer, Expr.AssertString(Expr.Eval("[appRole(wellKnownApp('MicrosoftGraph'), 'Brand.New.Permission')]", Context(lookup))));
    }

    private sealed class ThrowingLookup : IEntraLookup
    {
        public Task<EntraLookupResult> FindAppRoleIdAsync(string appId, string value)
        {
            return Task.FromException<EntraLookupResult>(new InvalidOperationException("boom"));
        }

        public Task<EntraLookupResult> FindPermissionScopeIdAsync(string appId, string value)
        {
            return Task.FromException<EntraLookupResult>(new InvalidOperationException("boom"));
        }

        public Task<EntraLookupResult> FindRoleDefinitionIdAsync(string displayName)
        {
            return Task.FromException<EntraLookupResult>(new InvalidOperationException("boom"));
        }
    }
}
