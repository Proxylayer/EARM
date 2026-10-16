using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Types;
using EArm.Core.Validation;
using Xunit;

namespace EArm.Tests;

/// <summary>
/// The per-type property schemas through the offline validator: table-driven bad-value injection into the four samples (one or more
/// invalid enum / pattern / type / required value per resource type), nested <c>required</c> findings, the envelope <c>required</c>
/// skip, name patterns, and the valid samples and fixtures.
/// </summary>
public sealed class TemplateSchemaValidationTests
{
    private const string Orders = "orders-api";
    private const string Tenant = "tenant-security";
    private const string App = "app-with-sp-and-secret";
    private const string Methods = "authentication-methods";
    private const string Pim = "pim-eligibility";
    private const string SchemaUri = "https://schema.earm.proxylayer.net/schemas/2026-10-01/deploymentTemplate.json#";

    private static readonly TemplateLoader Loader = new();
    private static readonly TemplateValidator Validator = new();
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    /// <summary>One injected defect: where it goes (the nth resource of a type in a sample), what replaces the value and what the validator must say.</summary>
    public sealed record Injection(string Sample, string Type, int Ordinal, object[] Segments, string BadJson, string Keyword, string ExpectedSuffix = "")
    {
        public override string ToString()
        {
            return $"{Type.Replace("Microsoft.Entra/", string.Empty, StringComparison.Ordinal)}#{Ordinal}.{string.Join('.', Segments)} -> {Keyword}";
        }
    }

    private static Injection I(string sample, string type, int ordinal, object[] segments, string badJson, string keyword, string suffix = "")
    {
        return new Injection(sample, "Microsoft.Entra/" + type, ordinal, segments, badJson, keyword, suffix);
    }

    public static IReadOnlyList<Injection> Injections { get; } =
    [
        // applications
        I(Orders, "applications", 0, ["signInAudience"], "\"Everyone\"", "enum"),
        I(Orders, "applications", 0, ["appRoles", 0, "allowedMemberTypes", 0], "\"Robot\"", "enum"),
        I(Orders, "applications", 0, ["appRoles", 0, "id"], "\"role-1\"", "pattern"),
        I(Orders, "applications", 0, ["requiredResourceAccess", 0], "{ \"resourceAppId\": \"00000003-0000-0000-c000-000000000000\" }", "required"),
        I(App, "applications", 0, ["api", "oauth2PermissionScopes", 0, "type"], "\"Everyone\"", "enum"),
        I(App, "applications", 0, ["requiredResourceAccess", 0, "resourceAccess", 0, "type"], "\"Permission\"", "enum"),
        I(App, "applications", 0, ["api", "requestedAccessTokenVersion"], "3", "enum"),

        // applications/federatedIdentityCredentials, applications/passwordCredentials (nested children)
        I(Orders, "applications/federatedIdentityCredentials", 0, ["audiences"], "\"api://x\"", "type"),
        I(Orders, "applications/passwordCredentials", 0, ["rotateBeforeDays"], "\"soon\"", "type"),

        // servicePrincipals
        I(Orders, "servicePrincipals", 0, ["preferredSingleSignOnMode"], "\"kerberos\"", "enum"),

        // users, with the edge entries
        I(Orders, "users", 0, ["usageLocation"], "\"United Kingdom\"", "pattern"),
        I(Orders, "users", 0, ["assignedLicenses", 0, "skuPartNumber"], "17", "type"),
        I(Orders, "users", 0, ["appRoleAssignments", 0, "resourceName"], "\"x\"", "additionalProperties"),
        I(Orders, "users", 0, ["appRoleAssignments", 0], "{ \"resourceAppId\": \"00000003-0000-0000-c000-000000000000\" }", "required"),

        // groups, with the directoryRoles edge
        I(Orders, "groups", 0, ["visibility"], "\"Secret\"", "enum"),
        I(Orders, "groups", 0, ["directoryRoles", 0, "directoryScopeId"], "\"tenant\"", "pattern"),
        I(Orders, "groups", 0, ["directoryRoles", 0], "{ \"directoryScopeId\": \"/\" }", "required"),
        I(Tenant, "groups", 0, ["mailNickname"], "\"security.admins\"", "pattern"),

        // roleDefinitions
        I(Orders, "roleDefinitions", 0, ["rolePermissions", 0, "allowedResourceActions"], "\"microsoft.directory/users/read\"", "type"),
        I(Tenant, "roleDefinitions", 0, ["rolePermissions", 0], "{ }", "required"),

        // conditionalAccess/*
        I(Orders, "conditionalAccess/authenticationStrengths", 0, ["allowedCombinations"], "\"fido2\"", "type"),
        I(Orders, "conditionalAccess/authenticationContextClassReferences", 0, ["isAvailable"], "\"yes\"", "type"),
        I(Orders, "conditionalAccess/namedLocations", 0, ["@odata.type"], "\"#microsoft.graph.fooNamedLocation\"", "enum"),
        I(Tenant, "conditionalAccess/namedLocations", 0, ["ipRanges"], "[ { \"@odata.type\": \"#microsoft.graph.iPv4CidrRange\" } ]", "required", "[0]"),
        I(Tenant, "conditionalAccess/namedLocations", 1, ["countryLookupMethod"], "\"gps\"", "enum"),
        I(Orders, "conditionalAccess/policies", 0, ["state"], "\"on\"", "enum"),
        I(Orders, "conditionalAccess/policies", 0, ["conditions", "clientAppTypes", 0], "\"desktop\"", "enum"),
        I(Orders, "conditionalAccess/policies", 0, ["grantControls", "operator"], "\"XOR\"", "enum"),
        I(Tenant, "conditionalAccess/policies", 1, ["grantControls", "builtInControls", 0], "\"requireMfa\"", "enum"),

        // the standalone assignment types and administrativeUnits
        I(Orders, "appRoleAssignments", 0, ["principalId"], "5", "type"),
        I(Orders, "oauth2PermissionGrants", 0, ["consentType"], "\"Everyone\"", "enum"),
        I(Orders, "administrativeUnits", 0, ["membershipType"], "\"Auto\"", "enum"),
        I(Orders, "roleAssignments", 0, ["directoryScopeId"], "\"tenant\"", "pattern"),

        // authenticationMethodConfigurations (tenant singletons, one declaration per method)
        I(Methods, "authenticationMethodConfigurations", 3, ["state"], "\"on\"", "enum"),
        I(Methods, "authenticationMethodConfigurations", 1, ["defaultLength"], "6", "minimum"),
        I(Methods, "authenticationMethodConfigurations", 0, ["passkeyProfiles", 0, "keyRestrictions", "aaGuids"], "[ \"not-a-guid\" ]", "pattern", "[0]"),
        I(Methods, "authenticationMethodConfigurations", 2, ["authenticationModeConfiguration", "x509CertificateAuthenticationDefaultMode"], "\"strong\"", "enum"),
        I(Methods, "authenticationMethodConfigurations", 1, ["includeTargets", 0], "{ \"targetType\": \"group\" }", "required"),

        // roleEligibilityScheduleRequests (PIM eligibility, ARM-styled)
        I(Pim, "roleEligibilityScheduleRequests", 0, ["roleDefinitionId"], "\"not-a-guid\"", "pattern"),
        I(Pim, "roleEligibilityScheduleRequests", 2, ["directoryScopeId"], "\"tenant\"", "pattern"),
        I(Pim, "roleEligibilityScheduleRequests", 1, ["scheduleInfo"], "[]", "type"),
    ];

    public static TheoryData<int> InjectionIndexes()
    {
        var data = new TheoryData<int>();
        for (int i = 0; i < Injections.Count; i++)
        {
            data.Add(i);
        }

        return data;
    }

    // ---- helpers ----

    private static string SampleText(string sample)
    {
        return File.ReadAllText(TestPaths.Sample(sample + ".entra.json"));
    }

    private static ParameterSet SampleParameters(string sample)
    {
        return ParameterSet.FromFile(TestPaths.Sample(sample + ".parameters.json"));
    }

    private static string Envelope(string body)
    {
        return "{ \"$schema\": \"" + SchemaUri + "\", \"contentVersion\": \"1.0.0.0\", " + body + " }";
    }

    private static IReadOnlyList<Diagnostic> ValidateText(string json, ParameterSet? parameters = null)
    {
        return Validator.Validate(Loader.Parse(json, "inline.entra.json", parameters ?? ParameterSet.Empty));
    }

    /// <summary>Finds the nth resource of a type (top-level or nested) and the JSON path prefix of it.</summary>
    private static (JsonObject Resource, string Path) Find(JsonNode root, string fullType, int ordinal)
    {
        var found = new List<(JsonObject, string)>();
        var resources = root["resources"]!.AsArray();
        for (int i = 0; i < resources.Count; i++)
        {
            var resource = resources[i]!.AsObject();
            string type = resource["type"]!.GetValue<string>();
            if (type.Equals(fullType, StringComparison.OrdinalIgnoreCase))
            {
                found.Add((resource, $"resources[{i}]"));
            }

            if (resource["resources"] is JsonArray children)
            {
                for (int j = 0; j < children.Count; j++)
                {
                    var child = children[j]!.AsObject();
                    if ((type + "/" + child["type"]!.GetValue<string>()).Equals(fullType, StringComparison.OrdinalIgnoreCase))
                    {
                        found.Add((child, $"resources[{i}].resources[{j}]"));
                    }
                }
            }
        }

        Assert.True(found.Count > ordinal, $"the sample has {found.Count} resource(s) of type {fullType}, wanted index {ordinal}");
        return found[ordinal];
    }

    private static string PathOf(string prefix, IEnumerable<object> segments)
    {
        var builder = new StringBuilder(prefix).Append(".properties");
        foreach (object segment in segments)
        {
            builder.Append(segment switch
            {
                int index => $"[{index}]",
                string name when name.Contains('@', StringComparison.Ordinal) => $"['{name}']",
                string name => "." + name,
                _ => throw new InvalidOperationException(),
            });
        }

        return builder.ToString();
    }

    private static void SetPath(JsonObject resource, object[] segments, string badJson)
    {
        JsonNode node = resource["properties"]!;
        for (int i = 0; i < segments.Length - 1; i++)
        {
            node = segments[i] is int index ? node.AsArray()[index]! : node.AsObject()[(string)segments[i]]!;
        }

        var bad = JsonNode.Parse(badJson);
        if (segments[^1] is int last)
        {
            node.AsArray()[last] = bad;
        }
        else
        {
            node.AsObject()[(string)segments[^1]] = bad;
        }
    }

    // ---- bad-value injection ----

    [Fact]
    public void InjectionTable_CoversEveryResourceType_AndEveryKeywordFamily()
    {
        var covered = Injections.Select(i => i.Type).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var definition in ResourceTypeCatalog.Default.Types)
        {
            Assert.Contains(definition.Type, covered);
        }

        Assert.Equal(17, ResourceTypeCatalog.Default.Types.Count);
        foreach (string keyword in new[] { "enum", "pattern", "type", "required", "additionalProperties" })
        {
            Assert.Contains(Injections, i => i.Keyword == keyword);
        }
    }

    [Theory]
    [MemberData(nameof(InjectionIndexes))]
    public void BadValue_IsReportedAsASchemaViolation_AtTheRightPath(int index)
    {
        var injection = Injections[index];
        var root = JsonNode.Parse(SampleText(injection.Sample))!;
        var (resource, prefix) = Find(root, injection.Type, injection.Ordinal);
        SetPath(resource, injection.Segments, injection.BadJson);
        string expectedPath = PathOf(prefix, injection.Segments) + injection.ExpectedSuffix;

        var diagnostics = ValidateText(root.ToJsonString(), SampleParameters(injection.Sample));

        var hits = diagnostics.Where(d => d.Code == DiagnosticCodes.SchemaViolation && d.Path!.ToString() == expectedPath).ToList();
        Assert.True(hits.Count > 0, $"{injection}: no SchemaViolation at {expectedPath}; got " + string.Join("; ", diagnostics.Select(d => d.Code + "@" + d.Path)));
        string expectedMessage = injection.Keyword == "additionalProperties" ? "is not allowed here (schema keyword 'additionalProperties')" : $"(schema keyword '{injection.Keyword}')";
        Assert.Contains(hits, d => d.Message.Contains(expectedMessage, StringComparison.Ordinal));
        Assert.All(hits, d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.All(hits, d => Assert.NotNull(d.Location));
    }

    [Theory]
    [MemberData(nameof(InjectionIndexes))]
    public void BadValue_IsTheOnlyErrorInTheTemplate(int index)
    {
        var injection = Injections[index];
        var root = JsonNode.Parse(SampleText(injection.Sample))!;
        var (resource, _) = Find(root, injection.Type, injection.Ordinal);
        SetPath(resource, injection.Segments, injection.BadJson);

        var diagnostics = ValidateText(root.ToJsonString(), SampleParameters(injection.Sample));

        Assert.All(diagnostics.Where(d => d.IsError), d => Assert.Equal(DiagnosticCodes.SchemaViolation, d.Code));
    }

    // ---- nested required ----

    [Theory]
    [InlineData("users", "\"displayName\": \"U\", \"appRoleAssignments\": [ { \"resourceAppId\": \"00000003-0000-0000-c000-000000000000\" } ]", "resources[0].properties.appRoleAssignments[0]")]
    [InlineData("groups", "\"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true, \"isAssignableToRole\": true, \"directoryRoles\": [ { \"directoryScopeId\": \"/\" } ]", "resources[0].properties.directoryRoles[0]")]
    [InlineData("applications", "\"displayName\": \"A\", \"requiredResourceAccess\": [ { \"resourceAppId\": \"00000003-0000-0000-c000-000000000000\" } ]", "resources[0].properties.requiredResourceAccess[0]")]
    [InlineData("conditionalAccess/namedLocations", "\"@odata.type\": \"#microsoft.graph.ipNamedLocation\", \"ipRanges\": [ { \"@odata.type\": \"#microsoft.graph.iPv4CidrRange\" } ]", "resources[0].properties.ipRanges[0]")]
    [InlineData("roleDefinitions", "\"rolePermissions\": [ { } ]", "resources[0].properties.rolePermissions[0]")]
    public void RequiredMembersInsideProperties_AreReportedWithTheirPath(string type, string properties, string expectedPath)
    {
        string name = type == "users" ? "u@contoso.com" : "n";
        var diagnostics = ValidateText(Envelope("\"resources\": [ { \"type\": \"Microsoft.Entra/" + type + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { " + properties + " } } ]"));

        var hit = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.SchemaViolation);
        Assert.Equal(expectedPath, hit.Path!.ToString());
        Assert.Contains("(schema keyword 'required')", hit.Message, StringComparison.Ordinal);
        Assert.NotNull(hit.Location);
    }

    [Fact]
    public void RequiredMemberOfANestedChildsProperties_IsReportedWithTheNestedPath()
    {
        var diagnostics = ValidateText(Envelope(
            """
            "resources": [ { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "app", "properties": { "displayName": "A" },
              "resources": [ { "type": "federatedIdentityCredentials", "apiVersion": "2026-10-01-preview", "name": "fic1",
                               "properties": { "issuer": "https://x.example", "subject": "s", "audiences": ["a"], "claimsMatchingExpression": { "languageVersion": "one" } } } ] } ]
            """));

        // Whatever the children schema says about claimsMatchingExpression, a finding below the child's properties carries the nested path.
        Assert.All(diagnostics.Where(d => d.Code == DiagnosticCodes.SchemaViolation), d => Assert.StartsWith("resources[0].resources[0].properties", d.Path!.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public void RequiredOnCreateMembers_AreNotSchemaRequired_ByDesign()
    {
        // docs/format.md: a template that only updates an object legitimately omits displayName, issuer, ...; the provider reports them on create.
        var diagnostics = ValidateText(Envelope(
            """
            "resources": [
              { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "g", "properties": { } },
              { "type": "Microsoft.Entra/applications", "apiVersion": "2026-10-01-preview", "name": "app", "properties": { },
                "resources": [ { "type": "federatedIdentityCredentials", "apiVersion": "2026-10-01-preview", "name": "fic1", "properties": { } } ] } ]
            """));

        Assert.Empty(diagnostics);
    }

    [Theory]
    [InlineData("2026-10-01-preview", false)]
    [InlineData("2026-10-01-preview.beta", false)]
    [InlineData("2026-10-01", true)]
    [InlineData("v1.0", true)]
    public void ApiVersionLabels_OnlyThePreviewAndPreviewBetaLabelsAreAccepted(string apiVersion, bool rejected)
    {
        var diagnostics = ValidateText(Envelope("\"resources\": [ { \"type\": \"Microsoft.Entra/groups\", \"apiVersion\": \"" + apiVersion + "\", \"name\": \"g\", \"properties\": { \"displayName\": \"G\", \"mailEnabled\": false, \"securityEnabled\": true } } ]"));

        if (rejected)
        {
            var error = Assert.Single(diagnostics, d => d.IsError);
            Assert.Equal(DiagnosticCodes.UnknownApiVersion, error.Code);
            Assert.Equal("resources[0].apiVersion", error.Path!.ToString());
        }
        else
        {
            Assert.DoesNotContain(diagnostics, d => d.IsError);
        }
    }

    // ---- envelope required skip ----

    [Fact]
    public void MissingEnvelopeMembers_AreReportedWithTheirOwnCodes_NeverAsSchemaRequired()
    {
        var diagnostics = ValidateText("{ \"$schema\": \"" + SchemaUri + "\", \"resources\": [ { \"apiVersion\": \"2026-10-01-preview\", \"name\": \"g\" }, { \"type\": \"Microsoft.Entra/groups\", \"name\": \"h\" } ] }");

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.SchemaViolation && d.Message.Contains("'required'", StringComparison.Ordinal));
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.MissingRequiredProperty);
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.MissingResourceType);
        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.MissingApiVersion);
    }

    // ---- name patterns ----

    [Theory]
    [InlineData("applications", " leading-space")]
    [InlineData("applications", "trailing-space ")]
    [InlineData("groups", " g")]
    [InlineData("conditionalAccess/policies", "p ")]
    [InlineData("conditionalAccess/namedLocations", " n")]
    [InlineData("conditionalAccess/authenticationStrengths", "s ")]
    [InlineData("administrativeUnits", " au")]
    [InlineData("roleDefinitions", "r ")]
    [InlineData("conditionalAccess/authenticationContextClassReferences", "c0")]
    [InlineData("conditionalAccess/authenticationContextClassReferences", "c100")]
    [InlineData("conditionalAccess/authenticationContextClassReferences", "10")]
    [InlineData("conditionalAccess/authenticationContextClassReferences", "C1")]
    [InlineData("users", "no-at-sign")]
    [InlineData("users", "two@at@signs")]
    [InlineData("servicePrincipals", "not-a-guid")]
    [InlineData("appRoleAssignments", "not-a-guid")]
    [InlineData("oauth2PermissionGrants", "1234")]
    [InlineData("roleAssignments", "not-a-guid")]
    public void NamePatternViolations_AreReportedAsInvalidResourceName(string type, string name)
    {
        var diagnostics = ValidateText(Envelope("\"resources\": [ { \"type\": \"Microsoft.Entra/" + type + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { } } ]"));

        var error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.InvalidResourceName);
        Assert.Equal("resources[0].name", error.Path!.ToString());
        Assert.Equal(DiagnosticSeverity.Error, error.Severity);
        Assert.NotNull(error.Location);
    }

    [Theory]
    [InlineData("applications", "orders-api")]
    [InlineData("groups", "contoso-prod-admins")]
    [InlineData("conditionalAccess/authenticationContextClassReferences", "c1")]
    [InlineData("conditionalAccess/authenticationContextClassReferences", "c99")]
    [InlineData("users", "a.b@contoso.com")]
    [InlineData("servicePrincipals", "00000003-0000-0000-c000-000000000000")]
    [InlineData("roleAssignments", "00000003-0000-0000-c000-000000000000")]
    public void ConformingNames_AreAccepted(string type, string name)
    {
        var diagnostics = ValidateText(Envelope("\"resources\": [ { \"type\": \"Microsoft.Entra/" + type + "\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + name + "\", \"properties\": { } } ]"));

        Assert.DoesNotContain(diagnostics, d => d.Code == DiagnosticCodes.InvalidResourceName);
    }

    [Theory]
    [InlineData("ab")]
    [InlineData("_underscore")]
    [InlineData("has space")]
    [InlineData("-dash")]
    public void NestedFederatedCredentialNames_FollowTheChildPattern(string childName)
    {
        var diagnostics = ValidateText(Envelope(
            "\"resources\": [ { \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"app\", \"properties\": { \"displayName\": \"A\" }, " +
            "\"resources\": [ { \"type\": \"federatedIdentityCredentials\", \"apiVersion\": \"2026-10-01-preview\", \"name\": \"" + childName + "\", \"properties\": { } } ] } ]"));

        var error = Assert.Single(diagnostics, d => d.Code == DiagnosticCodes.InvalidResourceName);
        Assert.Equal("resources[0].resources[0].name", error.Path!.ToString());
    }

    // ---- the samples and fixtures ----

    [Theory]
    [InlineData("orders-api.entra.json", "orders-api.parameters.json")]
    [InlineData("orders-api.flat.entra.json", "orders-api.parameters.json")]
    [InlineData("app-with-sp-and-secret.entra.json", "app-with-sp-and-secret.parameters.json")]
    [InlineData("tenant-security.entra.json", "tenant-security.parameters.json")]
    public async Task EverySample_WithItsParameterFile_ValidatesWithZeroDiagnostics(string template, string parameters)
    {
        var document = await Loader.LoadAsync(TestPaths.Sample(template), ParameterSet.FromFile(TestPaths.Sample(parameters)), TestContext.Current.CancellationToken);

        Assert.Empty(Validator.Validate(document));
    }

    [Fact]
    public async Task ValidFixtures_HaveNoErrors()
    {
        var files = Directory.EnumerateFiles(TestPaths.ValidFixtureDirectory, "*.entra.json").ToList();
        Assert.NotEmpty(files);

        foreach (string file in files)
        {
            var diagnostics = Validator.Validate(await Loader.LoadAsync(file, ParameterSet.Empty, TestContext.Current.CancellationToken));

            Assert.DoesNotContain(diagnostics, d => d.IsError);
        }
    }
}
