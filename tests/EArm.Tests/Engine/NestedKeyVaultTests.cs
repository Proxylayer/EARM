using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using EArm.Core;
using EArm.Core.Diagnostics;
using EArm.Core.Loading;
using EArm.Core.Secrets;
using EArm.Core.Types;
using EArm.Core.Validation;
using EArm.Engine;
using EArm.Engine.KeyVault;
using EArm.Engine.Permissions;
using EArm.Engine.Planning;
using EArm.Graph;
using EArm.Tests.Cli;
using EArm.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using static EArm.Tests.Engine.NestedDeploymentTests;

namespace EArm.Tests.Engine;

/// <summary>
/// Key Vault references among the parameters of nested deployments: an inline <c>reference</c> entry (with a dynamic vault id computed by the
/// parent) and a <c>reference</c> in a linked parameter file. Offline they stay unknown and are recorded; the planner resolves them with
/// <see cref="KeyVaultSecretResolver"/> and the value stays secure.
/// </summary>
public sealed class NestedKeyVaultTests
{
    private const string VaultId = "/subscriptions/00000000-0000-0000-0000-000000000000/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/contoso-kv";

    private const string InnerTemplate = """
        "parameters": { "secret": { "type": "securestring", "maxLength": 20 } },
        "resources": [ { "type": "Microsoft.Entra/groups", "apiVersion": "2026-10-01-preview", "name": "kv-g", "properties": { "displayName": "kv-g", "mailEnabled": false, "securityEnabled": true, "description": "[parameters('secret')]" } } ]
        """;

    private sealed class Vault : IDisposable
    {
        private readonly HttpClient _http;

        public Vault()
        {
            Handler = new ScriptedGraphHandler();
            _http = new HttpClient(Handler, disposeHandler: false);
            var client = new KeyVaultClient(_http, new FakeJwtTokenProvider(), new RetryPolicy(new RetryOptions(), new NoDelay(), NullLogger<RetryPolicy>.Instance), GraphCloud.Public);
            Resolver = new KeyVaultSecretResolver(client);
        }

        public ScriptedGraphHandler Handler { get; }

        public KeyVaultSecretResolver Resolver { get; }

        public Vault With(string vault, string secret, string value)
        {
            Handler.SetScript("{ \"responses\": [ { \"match\": { \"method\": \"GET\", \"urlContains\": \"https://" + vault + ".vault.azure.net/secrets/" + secret + "?\" }, \"status\": 200, \"body\": { \"value\": " + System.Text.Json.JsonSerializer.Serialize(value) + " } } ] }");
            return this;
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }

    private static string InlineReference(string vaultId = "[format('/subscriptions/{0}/resourceGroups/rg/providers/Microsoft.KeyVault/vaults/{1}', parameters('vaultSubscription'), parameters('vaultName'))]", string secretName = "[parameters('secretName')]")
    {
        return $$"""{ "secret": { "reference": { "keyVault": { "id": "{{vaultId}}" }, "secretName": "{{secretName}}" } } }""";
    }

    private static TemplateDocument ParentWithInlineReference(string? vaultName = "contoso-kv", string? references = null)
    {
        (string, string)[] values = vaultName is null ? [("secretName", "bg-initial")] : [("vaultName", vaultName), ("secretName", "bg-initial")];
        return Templates.Parse($$"""
            "parameters": { "vaultName": { "type": "string" }, "secretName": { "type": "string" }, "vaultSubscription": { "type": "string", "defaultValue": "00000000-0000-0000-0000-000000000000" } },
            "resources": [ {{Deployment("nested", Inline(InnerTemplate, "inner", references ?? InlineReference()))}} ]
            """, values);
    }

    private static PlannerOptions WithVault(EngineHost host, KeyVaultSecretResolver? resolver)
    {
        return host.Options() with { KeyVaultSecrets = resolver };
    }

    [Fact]
    public async Task An_inline_reference_with_a_dynamic_id_is_resolved_by_the_planner_and_stays_secure()
    {
        using var vault = new Vault().With("contoso-kv", "bg-initial", "s3cret-value");
        var host = new EngineHost();

        var plan = await host.ExpandAsync(ParentWithInlineReference(), options: WithVault(host, vault.Resolver));

        var group = EngineHost.Evaluated(plan, "/providers/Microsoft.Entra/groups/kv-g");
        Assert.True(SecretJson.IsSecret(group.Properties!["description"], out var secret));
        Assert.Equal("s3cret-value", secret.Value);
        Assert.Equal("\"***\"", group.Properties["description"]!.ToJsonString());
    }

    [Fact]
    public async Task A_reference_in_a_linked_parameter_file_is_resolved()
    {
        using var directory = new TestDirectory(nameof(A_reference_in_a_linked_parameter_file_is_resolved));
        File.WriteAllText(directory.File("child.json"), "{ \"$schema\": \"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#\", \"contentVersion\": \"1.0.0.0\", " + InnerTemplate + " }");
        File.WriteAllText(directory.File("child.parameters.json"), "{ \"contentVersion\": \"1.0.0.0\", \"parameters\": { \"secret\": { \"reference\": { \"keyVault\": { \"id\": \"" + VaultId + "\" }, \"secretName\": \"bg-initial\" } } } }");
        string main = directory.File("main.entra.json");
        File.WriteAllText(main, $$"""{ "$schema": "{{Templates.SchemaUri}}", "contentVersion": "1.0.0.0", "resources": [ {{Deployment("child", """{ "mode": "Incremental", "templateLink": { "relativePath": "child.json" }, "parametersLink": { "uri": "child.parameters.json" } }""")}} ] }""");
        var document = await new TemplateLoader().LoadAsync(main, ParameterSet.Empty, TestContext.Current.CancellationToken);
        using var vault = new Vault().With("contoso-kv", "bg-initial", "from-the-file");
        var host = new EngineHost();

        var plan = await host.ExpandAsync(document, options: WithVault(host, vault.Resolver));

        var group = EngineHost.Evaluated(plan, "/providers/Microsoft.Entra/groups/kv-g");
        Assert.True(SecretJson.IsSecret(group.Properties!["description"], out var secret));
        Assert.Equal("from-the-file", secret.Value);

        // Offline the same reference is recorded, not resolved.
        var expansion = OfflineTemplateExpansion.Run(document, ResourceTypeCatalog.Default);
        Assert.True(expansion.Succeeded);
        var (parameter, reference) = Assert.Single(expansion.NestedKeyVaultReferences);
        Assert.Equal("child/secret", parameter);
        Assert.Equal("contoso-kv", reference.VaultName);
    }

    [Fact]
    public void Offline_the_reference_is_recorded_and_its_value_is_unknown()
    {
        var expansion = OfflineTemplateExpansion.Run(ParentWithInlineReference(), ResourceTypeCatalog.Default);

        Assert.True(expansion.Succeeded, string.Join("; ", expansion.Errors.Select(e => e.Message)));
        var (parameter, reference) = Assert.Single(expansion.NestedKeyVaultReferences);
        Assert.Equal("nested/secret", parameter);
        Assert.Equal("contoso-kv", reference.VaultName);
        Assert.Equal("bg-initial", reference.SecretName);
        Assert.Contains(expansion.KeyVaultReferences, r => r.Parameter == "nested/secret");
        Assert.Contains(expansion.Instances, i => i.PlannedId.Id == "/providers/Microsoft.Entra/groups/kv-g");

        // The run needs a Key Vault token for it (delegated sign-in asks for the vault scope too).
        Assert.True(RunScopes.Compute(expansion, EchoProvider.RegistryForCatalog(ResourceTypeCatalog.Default), RunVerb.WhatIf).NeedsKeyVault);
    }

    [Fact]
    public void Offline_a_reference_whose_vault_is_not_known_yet_is_still_expanded()
    {
        var expansion = OfflineTemplateExpansion.Run(ParentWithInlineReference(vaultName: null), ResourceTypeCatalog.Default);

        Assert.True(expansion.Succeeded, string.Join("; ", expansion.Errors.Select(e => e.Message)));
        var (_, reference) = Assert.Single(expansion.NestedKeyVaultReferences);
        Assert.Equal("(known at deployment)", reference.VaultName);
        Assert.Equal("bg-initial", reference.SecretName);
    }

    [Fact]
    public async Task Without_a_resolver_the_planner_reports_the_reference()
    {
        var host = new EngineHost();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.ExpandAsync(ParentWithInlineReference(), options: WithVault(host, null)));

        var error = Assert.Single(ex.Diagnostics, d => d.Code == EngineDiagnosticCodes.KeyVaultReferenceUnresolved);
        Assert.Equal("resources[0].properties.parameters.secret.reference", error.Path?.ToString());
    }

    [Fact]
    public async Task A_secret_that_cannot_be_read_is_KeyVaultReferenceUnresolved_at_the_entry()
    {
        using var vault = new Vault();
        vault.Handler.SetScript("{ \"responses\": [ { \"status\": 404, \"body\": { \"error\": { \"code\": \"SecretNotFound\", \"message\": \"A secret with (name/id) bg-initial was not found.\" } } } ] }");
        var host = new EngineHost();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.ExpandAsync(ParentWithInlineReference(), options: WithVault(host, vault.Resolver)));

        var error = Assert.Single(ex.Diagnostics, d => d.Code == EngineDiagnosticCodes.KeyVaultReferenceUnresolved);
        Assert.Contains("Nested deployment 'nested'", error.Message, StringComparison.Ordinal);
        Assert.Contains("the vault has no such secret", error.Message, StringComparison.Ordinal);
        Assert.Equal("resources[0].properties.parameters.secret.reference", error.Path?.ToString());
    }

    [Fact]
    public async Task The_resolved_value_must_satisfy_the_parameter_constraints_and_is_never_shown()
    {
        using var vault = new Vault().With("contoso-kv", "bg-initial", "a-secret-far-longer-than-twenty");
        var host = new EngineHost();

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.ExpandAsync(ParentWithInlineReference(), options: WithVault(host, vault.Resolver)));

        var error = Assert.Single(ex.Diagnostics, d => d.Code == EngineDiagnosticCodes.KeyVaultReferenceUnresolved);
        Assert.Contains("maxLength", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("far-longer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Online_the_vault_must_be_known_before_deployment()
    {
        using var vault = new Vault().With("contoso-kv", "bg-initial", "x");
        var host = new EngineHost();
        var document = Templates.Parse($$"""
            "resources": [ {{Group("vault-owner")}}, {{Deployment("nested", Inline(InnerTemplate, "inner", InlineReference(vaultId: "[reference('vault-owner').id]", secretName: "bg-initial")))}} ]
            """);

        var ex = await Assert.ThrowsAsync<ValidationFailedException>(() => host.ExpandAsync(document, options: WithVault(host, vault.Resolver)));

        Assert.Contains(ex.Diagnostics, d => d.Code == EngineDiagnosticCodes.NestedDeploymentNotStatic && d.Path?.ToString() == "resources[1].properties.parameters.secret.reference.keyVault.id");
        Assert.DoesNotContain(ex.Diagnostics, d => d.Code == DiagnosticCodes.MissingParameterValue);
    }

    [Theory]
    [InlineData("""{ "secret": { "value": "x", "reference": { "keyVault": { "id": "kv" }, "secretName": "s" } } }""", "both 'value' and 'reference'")]
    [InlineData("""{ "secret": { "reference": { "keyVault": { "id": "kv" } } } }""", "'secretName'")]
    [InlineData("""{ "secret": { "reference": { "secretName": "s" } } }""", "'keyVault'")]
    [InlineData("""{ "secret": { "reference": { "keyVault": { "id": "kv" }, "secretName": "s", "secretVersion": 2 } } }""", "'secretVersion' must be a string")]
    public void A_malformed_reference_entry_is_InvalidNestedDeployment(string parameters, string expected)
    {
        var document = Templates.Parse($$"""
            "resources": [ {{Deployment("nested", Inline(InnerTemplate, "inner", parameters))}} ]
            """);

        var diagnostics = new TemplateValidator(ResourceTypeCatalog.Default).Validate(document);

        Assert.Contains(diagnostics, d => d.Code == DiagnosticCodes.InvalidNestedDeployment && d.Message.Contains(expected, StringComparison.Ordinal));
    }
}
