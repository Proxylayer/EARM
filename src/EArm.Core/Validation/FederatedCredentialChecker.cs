using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json.Nodes;
using EArm.Core.Diagnostics;
using EArm.Core.Json;

namespace EArm.Core.Validation;

/// <summary>
/// The offline checks of federated identity credential declarations (<c>Microsoft.Entra/applications/federatedIdentityCredentials</c>) for the
/// authoring mistakes Graph refuses with <c>400 Request contains a property with duplicate values</c> or its issuer+subject uniqueness rule:
/// <list type="bullet">
/// <item>a literal <c>audiences</c> array that lists the same value twice (<see cref="DiagnosticCodes.DuplicateFederatedCredentialAudience"/>);</item>
/// <item>two credentials of the same application declaring the same literal <c>issuer</c> and <c>subject</c>, or a <c>copy</c> loop of two or more
/// iterations whose <c>issuer</c> and <c>subject</c> are both literal, so every iteration declares the same pair
/// (<see cref="DiagnosticCodes.DuplicateFederatedCredentialSubject"/>).</item>
/// </list>
/// Only literal values are judged (pairs are compared ordinally, as Graph compares the case-sensitive subject); values computed by expressions
/// (<c>copyIndex()</c>, parameters) are checked by the provider at plan time, once they are evaluated.
/// </summary>
internal sealed class FederatedCredentialChecker
{
    internal const string CredentialsType = "Microsoft.Entra/applications/federatedIdentityCredentials";

    private readonly TemplateDocument _document;
    private readonly List<Diagnostic> _diagnostics;

    public FederatedCredentialChecker(TemplateDocument document, List<Diagnostic> diagnostics)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(diagnostics);
        _document = document;
        _diagnostics = diagnostics;
    }

    public void Run()
    {
        var pairsByParent = new Dictionary<string, Dictionary<(string Issuer, string Subject), ResourceDeclaration>>(StringComparer.OrdinalIgnoreCase);
        foreach (var resource in _document.Resources)
        {
            if (resource.Type is null || !ResourceIdentifier.TypeEquals(resource.Type, CredentialsType) || resource.Properties is not JsonObject properties)
            {
                continue;
            }

            var propertiesPath = resource.Path.Append("properties");
            CheckAudiences(properties, propertiesPath);

            if (LiteralPair(properties) is not { } pair)
            {
                continue;
            }

            if (resource.Copy is { } copy)
            {
                if (!TemplateJson.IsExpression(copy.Count) && TemplateJson.AsInt64(copy.Count) is long count && count >= 2)
                {
                    Error(DiagnosticCodes.DuplicateFederatedCredentialSubject, $"The {resource.Describe()} is a copy loop of {count.ToString(CultureInfo.InvariantCulture)} credentials whose issuer and subject are both literal, so every iteration declares issuer '{pair.Issuer}' and subject '{pair.Subject}'; Graph requires the pair to be unique per application. Make the subject depend on copyIndex().", propertiesPath.Append("subject"));
                }

                continue;
            }

            if (ParentKey(resource) is not { } parent)
            {
                continue;
            }

            if (!pairsByParent.TryGetValue(parent, out var pairs))
            {
                pairs = [];
                pairsByParent[parent] = pairs;
            }

            if (pairs.TryGetValue(pair, out var first))
            {
                Error(DiagnosticCodes.DuplicateFederatedCredentialSubject, $"The {resource.Describe()} declares issuer '{pair.Issuer}' and subject '{pair.Subject}', which the {first.Describe()} of the same application already declares (at {first.Path}); Graph requires the pair to be unique per application.", propertiesPath.Append("subject"));
            }
            else
            {
                pairs[pair] = resource;
            }
        }
    }

    private void CheckAudiences(JsonObject properties, JsonPath propertiesPath)
    {
        if (Property(properties, "audiences") is not (string key, JsonArray audiences))
        {
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < audiences.Count; i++)
        {
            if (TemplateJson.LiteralString(audiences[i]) is { } audience && !seen.Add(audience))
            {
                Error(DiagnosticCodes.DuplicateFederatedCredentialAudience, $"'audiences' lists '{audience}' more than once; Graph refuses a federated identity credential with duplicate values (\"Request contains a property with duplicate values\"). List each audience once.", propertiesPath.Append(key).Append(i));
            }
        }
    }

    /// <summary>The literal issuer and subject, or <c>null</c> when either is missing or computed by an expression.</summary>
    private static (string Issuer, string Subject)? LiteralPair(JsonObject properties)
    {
        string? issuer = Property(properties, "issuer") is (_, var issuerNode) ? TemplateJson.LiteralString(issuerNode) : null;
        string? subject = Property(properties, "subject") is (_, var subjectNode) ? TemplateJson.LiteralString(subjectNode) : null;
        return issuer is { Length: > 0 } && subject is { Length: > 0 } ? (issuer, subject) : null;
    }

    /// <summary>The application a credential belongs to, when it is known without evaluation: the parent of a literal id, or the nesting parent's literal id.</summary>
    private string? ParentKey(ResourceDeclaration resource)
    {
        if (resource.StaticId?.Parent is { } parent)
        {
            return parent.Id;
        }

        if (resource.NestedParentPath is { } parentPath)
        {
            foreach (var candidate in _document.Resources)
            {
                if (candidate.Path == parentPath)
                {
                    return candidate.StaticId?.Id;
                }
            }
        }

        return null;
    }

    private static (string Key, JsonNode? Value)? Property(JsonObject properties, string name)
    {
        foreach (var (key, value) in properties)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return (key, value);
            }
        }

        return null;
    }

    private void Error(string code, string message, JsonPath path)
    {
        _diagnostics.Add(Diagnostic.Error(code, message, path, _document.LocationOf(path), _document.SourcePath));
    }
}
