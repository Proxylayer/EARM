using System;
using System.Collections.Generic;
using System.Linq;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// Offline validation of an access package assignment policy's <c>reviewSettings</c> (<see cref="AccessPackageReviewRules"/>,
/// <see cref="DiagnosticCodes.InvalidAccessPackageReviewSettings"/>): reviewers when enabled and not a self-review, the schedule, the expiration duration
/// format and the recurrence shape; computed values are left to the provider.
/// </summary>
public sealed class AccessPackageReviewValidationTests
{
    private const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";
    private const string Approvers = "1b2c3d4e-0000-4000-8000-000000000100";
    private const string Reviewers = "[ { \"@odata.type\": \"#microsoft.graph.groupMembers\", \"groupId\": \"" + Approvers + "\" } ]";
    private const string Quarterly = "{ \"pattern\": { \"type\": \"absoluteMonthly\", \"interval\": 3 }, \"range\": { \"type\": \"noEnd\" } }";

    private static readonly TemplateLoader Loader = new();
    private static readonly TemplateValidator Validator = new();

    private static string Review(string reviewers = Reviewers, string expiration = "{ \"type\": \"afterDuration\", \"duration\": \"P14D\" }", string recurrence = Quarterly, string extra = "")
    {
        return "{ \"isEnabled\": true, \"expirationBehavior\": \"keepAccess\", \"schedule\": { \"startDateTime\": \"2026-11-01T00:00:00Z\", \"expiration\": " + expiration
            + ", \"recurrence\": " + recurrence + " }, \"primaryReviewers\": " + reviewers + extra + " }";
    }

    private static List<Diagnostic> Validate(string reviewSettings)
    {
        string resource = "{ \"type\": \"" + AccessPackageReviewRules.ResourceType + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"Ops Package/Reviewed\", \"properties\": { \"allowedTargetScope\": \"allMemberUsers\", \"reviewSettings\": " + reviewSettings + " } }";
        string json = "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"reviewers\": { \"type\": \"array\", \"defaultValue\": [] } }, \"resources\": [ " + resource + " ] }";
        return Validator.Validate(Loader.Parse(json, "inline.entra.json", ParameterSet.Empty)).Where(d => d.IsError).ToList();
    }

    [Theory]
    [InlineData(Reviewers, "")]
    [InlineData("[]", ", \"isSelfReview\": true")]
    [InlineData("\"[parameters('reviewers')]\"", "")]
    [InlineData("[ { \"@odata.type\": \"#microsoft.graph.requestorManager\", \"managerLevel\": 1 } ]", ", \"fallbackReviewers\": " + Reviewers)]
    public void AValidOrComputedReview_Passes(string reviewers, string extra)
    {
        Assert.Empty(Validate(Review(reviewers, extra: extra)));
    }

    [Fact]
    public void ADisabledReview_NeedsNothingElse()
    {
        Assert.Empty(Validate("{ \"isEnabled\": false }"));
    }

    [Theory]
    [InlineData("[]", "", "primaryReviewers", "at least one reviewer")]
    [InlineData("[ { \"@odata.type\": \"#microsoft.graph.groupMembers\" } ]", "", "primaryReviewers[0]", "without groupId")]
    [InlineData(Reviewers, ", \"fallbackReviewers\": [ { \"@odata.type\": \"#microsoft.graph.singleUser\" } ]", "fallbackReviewers[0]", "without userId")]
    public void Reviewers_AreChecked(string reviewers, string extra, string pathSuffix, string message)
    {
        var error = Assert.Single(Validate(Review(reviewers, extra: extra)));

        Assert.Equal(DiagnosticCodes.InvalidAccessPackageReviewSettings, error.Code);
        Assert.EndsWith(pathSuffix, error.Path!.ToString(), StringComparison.Ordinal);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"type\": \"afterDuration\", \"duration\": \"14 days\" }", "expiration.duration", "ISO 8601 duration")]
    [InlineData("{ \"type\": \"afterDuration\" }", "expiration.duration", "required for type afterDuration")]
    [InlineData("{ \"type\": \"afterDateTime\" }", "expiration.endDateTime", "required for type afterDateTime")]
    public void TheReviewExpiration_IsChecked(string expiration, string pathSuffix, string message)
    {
        var error = Assert.Single(Validate(Review(expiration: expiration)));

        Assert.EndsWith(pathSuffix, error.Path!.ToString(), StringComparison.Ordinal);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{ \"pattern\": { \"interval\": 3 }, \"range\": { \"type\": \"noEnd\" } }", "pattern.type", "is required")]
    [InlineData("{ \"pattern\": { \"type\": \"weekly\", \"interval\": 1 }, \"range\": { \"type\": \"noEnd\" } }", "pattern.daysOfWeek", "at least one day")]
    [InlineData("{ \"pattern\": { \"type\": \"absoluteYearly\", \"interval\": 1, \"dayOfMonth\": 1 }, \"range\": { \"type\": \"noEnd\" } }", "pattern.month", "1 to 12")]
    [InlineData("{ \"pattern\": { \"type\": \"absoluteMonthly\", \"interval\": 3 }, \"range\": { \"type\": \"endDate\", \"endDate\": \"next year\" } }", "range.endDate", "yyyy-MM-dd")]
    [InlineData("{ \"pattern\": { \"type\": \"absoluteMonthly\", \"interval\": 3 }, \"range\": { \"type\": \"numbered\" } }", "range.numberOfOccurrences", "at least 1")]
    public void TheRecurrence_IsChecked(string recurrence, string pathSuffix, string message)
    {
        var error = Assert.Single(Validate(Review(recurrence: recurrence)));

        Assert.EndsWith(pathSuffix, error.Path!.ToString(), StringComparison.Ordinal);
        Assert.Contains(message, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AnEnabledReviewWithoutASchedule_IsRefused()
    {
        var error = Assert.Single(Validate("{ \"isEnabled\": true, \"primaryReviewers\": " + Reviewers + " }"));

        Assert.EndsWith("reviewSettings.schedule", error.Path!.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void AnUnknownExpirationBehavior_IsASchemaViolation()
    {
        var error = Assert.Single(Validate(Review().Replace("keepAccess", "deleteEverything", StringComparison.Ordinal)));

        Assert.Equal(DiagnosticCodes.SchemaViolation, error.Code);
    }
}
