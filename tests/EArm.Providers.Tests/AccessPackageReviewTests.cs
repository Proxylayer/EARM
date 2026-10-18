using System;
using System.Linq;
using System.Threading.Tasks;
using EArm.Core.Diagnostics;
using EArm.Engine;
using EArm.Engine.Providers;
using EArm.Providers.Tests.Infrastructure;
using EArm.TestSupport;
using Xunit;

namespace EArm.Providers.Tests;

/// <summary>
/// Access reviews of an access package's assignments (docs/access-packages.md, "Access reviews"): an assignment policy's <c>reviewSettings</c> created,
/// NoChange against Graph's filled-in answer (defaults, empty lists, recurrence fields, a millisecond timestamp, reviewer descriptions), reviewers
/// compared as sets, a changed reviewer set written with the merged PUT, the what-if delta, the plan-time rule check and the licence refusal.
/// </summary>
public sealed class AccessPackageReviewTests
{
    private const string Root = FakeEntitlementManagement.Root;
    private const string CatalogName = "Ops Access";
    private const string PackageName = "Ops Package";
    private const string PolicyName = "Reviewed";

    private static (ProviderHost Host, string Approvers, string Fallback) NewTenant()
    {
        var host = new ProviderHost();
        string approvers = host.Directory.AddGroup("ops-approvers", "Ops Approvers")["id"]!.GetValue<string>();
        string fallback = host.Directory.AddGroup("tenant-approvers", "Tenant Approvers")["id"]!.GetValue<string>();
        return (host, approvers, fallback);
    }

    private static string Group(string id)
    {
        return "{ \"@odata.type\": \"#microsoft.graph.groupMembers\", \"groupId\": \"" + id + "\" }";
    }

    private const string Manager = "{ \"@odata.type\": \"#microsoft.graph.requestorManager\", \"managerLevel\": 1 }";

    internal static string Review(string primary, string fallback = "[]", string extra = "")
    {
        return """
            {
              "isEnabled": true,
              "expirationBehavior": "removeAccess",
              "isReviewerJustificationRequired": true,
              "isSelfReview": false,
              "schedule": {
                "startDateTime": "2026-11-01T00:00:00Z",
                "expiration": { "type": "afterDuration", "duration": "P14D" },
                "recurrence": { "pattern": { "type": "absoluteMonthly", "interval": 3 }, "range": { "type": "noEnd" } }
              },
              "primaryReviewers": PRIMARY,
              "fallbackReviewers": FALLBACK EXTRA
            }
            """.Replace("PRIMARY", primary, StringComparison.Ordinal).Replace("FALLBACK", fallback, StringComparison.Ordinal).Replace("EXTRA", extra, StringComparison.Ordinal);
    }

    private static string Resources(string review)
    {
        return string.Join(", ",
            Templates.Resource(AccessPackageCatalogsProvider.ResourceType, CatalogName, "{ \"description\": \"Ops\" }"),
            Templates.Resource(AccessPackagesProvider.ResourceType, PackageName, "{ \"catalogId\": \"[reference('" + CatalogName + "').id]\" }"),
            Templates.Resource(AssignmentPoliciesProvider.ResourceType, PackageName + "/" + PolicyName, """
                {
                  "allowedTargetScope": "allMemberUsers",
                  "expiration": { "type": "afterDuration", "duration": "P180D" },
                  "requestorSettings": { "enableTargetsToSelfAddAccess": true },
                  "requestApprovalSettings": { "isApprovalRequiredForAdd": false, "stages": [] },
                  "reviewSettings": REVIEW
                }
                """.Replace("REVIEW", review, StringComparison.Ordinal)));
    }

    [Fact]
    public async Task AQuarterlyReview_IsCreated_AndGraphsFilledInAnswerIsNoChange()
    {
        var (host, approvers, fallback) = NewTenant();
        var document = host.Resources(Resources(Review("[" + Group(approvers) + "]", "[" + Group(fallback) + "]")));

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var body = Check.Body(run.Writes.Single(w => w.Path == Root + "/assignmentPolicies"));
        var review = body["reviewSettings"]!.AsObject();
        Assert.True(review["isEnabled"]!.GetValue<bool>());
        Assert.Equal(3, review["schedule"]!["recurrence"]!["pattern"]!["interval"]!.GetValue<int>());
        Assert.Equal(approvers, review["primaryReviewers"]![0]!["groupId"]!.GetValue<string>());
        var live = host.Directory.EntitlementManagement.Policies.Single()["reviewSettings"]!;
        Assert.Equal("2026-11-01T00:00:00.000Z", live["schedule"]!["startDateTime"]!.GetValue<string>());
        Assert.Equal("Reviewer", live["primaryReviewers"]![0]!["description"]!.GetValue<string>());

        var rerun = await host.CreateAsync(document);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
        Check.NoWrites(rerun);
    }

    [Fact]
    public async Task Reviewers_AreComparedAsSets_IncludingReviewersWithoutAnId()
    {
        var (host, approvers, fallback) = NewTenant();
        Check.Succeeded(await host.CreateAsync(host.Resources(Resources(Review("[" + Manager + ", " + Group(approvers) + "]", "[" + Group(fallback) + "]")))));

        var reordered = await host.WhatIfAsync(host.Resources(Resources(Review("[" + Group(approvers) + ", " + Manager + "]", "[" + Group(fallback) + "]"))));

        Assert.Equal(ChangeType.NoChange, reordered.Change(PolicyName).Type);
    }

    [Fact]
    public async Task AChangedReviewerSet_IsAModify_WrittenWithTheMergedPut_AndShownInTheWhatIf()
    {
        var (host, approvers, fallback) = NewTenant();
        Check.Succeeded(await host.CreateAsync(host.Resources(Resources(Review("[" + Group(approvers) + "]")))));
        host.ClearRequests();
        var document = host.Resources(Resources(Review("[" + Group(approvers) + ", " + Group(fallback) + "]")));

        var whatIf = await host.WhatIfAsync(document);
        var row = whatIf.Change(PolicyName);
        Assert.Equal(ChangeType.Modify, row.Type);
        Assert.Contains(Flatten(row.Deltas), d => d.Path.ToString().StartsWith("properties.reviewSettings.primaryReviewers", StringComparison.Ordinal));
        Assert.Contains("reviewSettings", whatIf.WhatIfTextOutput, StringComparison.Ordinal);

        var run = await host.CreateAsync(document);

        Check.Succeeded(run);
        var put = run.Writes.Single(w => w.Method == "PUT");
        var reviewers = Check.Body(put)["reviewSettings"]!["primaryReviewers"]!.AsArray();
        Assert.Equal(2, reviewers.Count);
        Assert.All(reviewers, r => Assert.False(r!.AsObject().ContainsKey("description")));
        Assert.Equal("removeAccess", Check.Body(put)["reviewSettings"]!["expirationBehavior"]!.GetValue<string>());
        var rerun = await host.CreateAsync(document);
        Assert.All(rerun.Executed.Operations, o => Assert.Equal(ChangeType.NoChange, o.ChangeType));
    }

    [Fact]
    public async Task AnEnabledReviewWithoutReviewers_FromAParameter_IsRefusedAtPlanTime()
    {
        var (host, _, _) = NewTenant();
        var document = host.Resources(Resources(Review("\"[parameters('reviewers')]\"")), parameters: "\"reviewers\": { \"type\": \"array\", \"defaultValue\": [] }");

        var whatIf = await host.WhatIfAsync(document);

        var row = whatIf.Change(PolicyName);
        Assert.Equal(DiagnosticCodes.InvalidAccessPackageReviewSettings, row.UnsupportedCode);
        Assert.Contains("primaryReviewers", row.UnsupportedReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ATenantWithoutTheLicence_IsGovernanceLicenseRequired()
    {
        var (host, approvers, _) = NewTenant();
        host.Directory.EntitlementManagement.ReviewsLicensed = false;

        var run = await host.CreateAsync(host.Resources(Resources(Review("[" + Group(approvers) + "]"))));

        var op = run.Op(PolicyName);
        Assert.Equal(EngineDiagnosticCodes.GovernanceLicenseRequired, op.ErrorCode);
        Assert.Contains("isEnabled to false", op.Error, StringComparison.Ordinal);
    }

    private static System.Collections.Generic.IEnumerable<PropertyDelta> Flatten(System.Collections.Generic.IEnumerable<PropertyDelta> deltas)
    {
        foreach (var delta in deltas)
        {
            yield return delta;
            foreach (var child in Flatten(delta.Children ?? []))
            {
                yield return child;
            }
        }
    }
}
