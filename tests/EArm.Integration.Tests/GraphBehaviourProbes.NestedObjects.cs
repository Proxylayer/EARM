using System;
using System.Collections.Generic;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>Probe (g) of <see cref="GraphBehaviourProbes"/>: nested-object PATCH null handling per sub-property.</summary>
public sealed partial class GraphBehaviourProbes
{
    // ---- (g) nested-object PATCH null handling per sub-property ----

    [Fact]
    public async Task It_Probe_G_NestedObjectNullHandlingPerSubProperty()
    {
        Fixture.Require();
        var report = new ProbeReport("g");
        var application = await TenantObjects.CreateApplicationAsync(
            Fixture,
            "probe-nested",
            new JsonObject
            {
                ["web"] = new JsonObject
                {
                    ["homePageUrl"] = "https://earm-it.example.test/home",
                    ["logoutUrl"] = "https://earm-it.example.test/logout",
                    ["redirectUris"] = new JsonArray("https://earm-it.example.test/cb"),
                    ["implicitGrantSettings"] = new JsonObject { ["enableAccessTokenIssuance"] = true, ["enableIdTokenIssuance"] = true },
                },
                ["api"] = new JsonObject { ["requestedAccessTokenVersion"] = 2 },
                ["info"] = new JsonObject
                {
                    ["marketingUrl"] = "https://earm-it.example.test/m",
                    ["privacyStatementUrl"] = "https://earm-it.example.test/p",
                    ["supportUrl"] = "https://earm-it.example.test/s",
                    ["termsOfServiceUrl"] = "https://earm-it.example.test/t",
                },
                ["spa"] = new JsonObject { ["redirectUris"] = new JsonArray("https://earm-it.example.test/spa") },
                ["publicClient"] = new JsonObject { ["redirectUris"] = new JsonArray("https://earm-it.example.test/pc") },
            },
            Ct);
        string path = "/applications/" + application.RequireId();

        // Each sub-property is checked against the overlay (x-earm.nullable) and the engine's clearing rule (JsonDiff.MergeNested):
        // - overlay nullable: false -> the engine never clears it, so an explicit null must not clear it either (Graph keeps or rejects it);
        // - nullable collection     -> the engine clears it with [] (Graph rejects null for its non-nullable collections, such as
        //                              redirectUris): [] must clear it, and what an explicit null does is only recorded;
        // - nullable scalar/object  -> the engine clears it with null: the null must clear it.
        (string Parent, string Property)[] cases =
        [
            ("web", "homePageUrl"),
            ("web", "logoutUrl"),
            ("web", "redirectUris"),
            ("web", "implicitGrantSettings"),
            ("api", "requestedAccessTokenVersion"),
            ("info", "marketingUrl"),
            ("info", "privacyStatementUrl"),
            ("info", "supportUrl"),
            ("info", "termsOfServiceUrl"),
            ("spa", "redirectUris"),
            ("publicClient", "redirectUris"),
        ];

        var table = new List<string>();
        foreach (var (parent, property) in cases)
        {
            string dotted = parent + "." + property;
            bool nullable = OverlayNullable(dotted);
            string overlay = $"x-earm.nullable in {OverlayFile} (currently {(nullable ? "not listed, i.e. nullable" : "\"" + dotted + "\": false")})";

            var before = (await Graph.GetObjectAsync(new GraphRequest(path).WithSelect("id", parent), Ct)).Nested(parent);
            bool isCollection = before?[property] is JsonArray;
            string nullOutcome = await PatchAndClassifyAsync(path, parent, property, null, before);

            if (!nullable)
            {
                table.Add($"{dotted}: null {nullOutcome}");
                report.Expect(
                    nullOutcome != "Cleared",
                    $"An explicit null for {dotted} CLEARS it, but the overlay says it is not nullable: remove the \"{dotted}\": false entry from {overlay}.");
            }
            else if (isCollection)
            {
                var current = (await Graph.GetObjectAsync(new GraphRequest(path).WithSelect("id", parent), Ct)).Nested(parent);
                string emptyOutcome = await PatchAndClassifyAsync(path, parent, property, new JsonArray(), current);
                table.Add($"{dotted}: null {nullOutcome}; [] {emptyOutcome}");
                report.Expect(
                    emptyOutcome == "Cleared",
                    $"Clearing the collection {dotted} with [] (what JsonDiff.MergeNested sends) is {emptyOutcome}, not Cleared: set \"{dotted}\": false in {overlay} so the engine stops trying to clear it.");
            }
            else
            {
                table.Add($"{dotted}: null {nullOutcome}");
                report.Expect(
                    nullOutcome == "Cleared",
                    $"An explicit null for {dotted} is {nullOutcome}, not Cleared: set \"{dotted}\": false in {overlay} so the engine stops sending it.");
            }
        }

        report.Observe("Per sub-property result of {parent:{property:null}} (and {parent:{property:[]}} for collections):" + Environment.NewLine + string.Join(Environment.NewLine, table));
        report.Complete();
    }
}
