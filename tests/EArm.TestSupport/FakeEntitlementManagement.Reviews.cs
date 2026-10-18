using System;
using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using EArm.Graph;

namespace EArm.TestSupport;

/// <summary>
/// The access review settings of an assignment policy (Learn: resources/accesspackageassignmentreviewsettings): Graph answers with every member filled in
/// (the booleans, the empty reviewer lists, every recurrence pattern and range field, a reviewer's read-only <c>description</c>) and writes the review's
/// <c>startDateTime</c> back with milliseconds; so does this fake. Without <see cref="ReviewsLicensed"/> a policy with enabled reviews is a 400.
/// </summary>
public sealed partial class FakeEntitlementManagement
{
    /// <summary>Whether the tenant has the licence access reviews need (Microsoft Entra ID Governance / P2); false refuses enabled reviews.</summary>
    public bool ReviewsLicensed { get; set; } = true;

    private void FillReviewSettings(JsonObject policy, RetryHint hint)
    {
        if (policy["reviewSettings"] is not JsonObject review)
        {
            policy["reviewSettings"] ??= null;
            return;
        }

        if (review["isEnabled"] is JsonValue enabled && enabled.GetValue<bool>() && !ReviewsLicensed)
        {
            throw new GraphException(HttpStatusCode.BadRequest, "InvalidRequest", "The tenant does not have a valid license (SKU) for access reviews.", hint);
        }

        review["expirationBehavior"] ??= "keepAccess";
        review["isRecommendationEnabled"] ??= true;
        review["isReviewerJustificationRequired"] ??= true;
        review["isSelfReview"] ??= false;
        review["primaryReviewers"] ??= new JsonArray();
        review["fallbackReviewers"] ??= new JsonArray();
        foreach (string list in new[] { "primaryReviewers", "fallbackReviewers" })
        {
            foreach (var subject in (review[list] as JsonArray)!)
            {
                if (subject is JsonObject reviewer)
                {
                    reviewer["description"] ??= "Reviewer";
                }
            }
        }

        if (review["schedule"] is not JsonObject schedule)
        {
            return;
        }

        if (schedule["startDateTime"] is JsonValue start && DateTimeOffset.TryParse(start.GetValue<string>(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var instant))
        {
            schedule["startDateTime"] = instant.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        }

        if (schedule["expiration"] is JsonObject expiration)
        {
            expiration["endDateTime"] ??= null;
            expiration["duration"] ??= null;
        }

        if (schedule["recurrence"] is JsonObject recurrence)
        {
            if (recurrence["pattern"] is JsonObject pattern)
            {
                pattern["month"] ??= 0;
                pattern["dayOfMonth"] ??= 0;
                pattern["daysOfWeek"] ??= new JsonArray();
                pattern["firstDayOfWeek"] ??= "sunday";
                pattern["index"] ??= "first";
            }

            if (recurrence["range"] is JsonObject range)
            {
                range["numberOfOccurrences"] ??= 0;
                range["recurrenceTimeZone"] ??= null;
                range["startDate"] ??= null;
                range["endDate"] ??= null;
            }
        }
    }
}
