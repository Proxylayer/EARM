using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using EntraArm.Core;
using EntraArm.Core.Json;
using EntraArm.Core.Secrets;
using EntraArm.Engine;
using EntraArm.Engine.Execution;
using EntraArm.Engine.Providers;
using EntraArm.Engine.State;
using EntraArm.Expressions.Values;
using Xunit;

namespace EntraArm.Tests.Engine;

/// <summary>The state file model (plan section 1.7): serialisation, identifiers, the secret guard, schema gating, hashing, names.</summary>
public sealed class StateSerializerTests
{
    private const string Name = "orders-dev";
    private const string Tenant = "11111111-1111-1111-1111-111111111111";
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

    internal static StateEntry Entry(string id, string type, string graphId, params string[] dependsOn)
    {
        var identifier = StateSerializer.ParseResourceId(id, type);
        Assert.NotNull(identifier);
        return new StateEntry
        {
            Id = identifier,
            Type = type,
            ApiVersion = "2026-10-01",
            Key = new Dictionary<string, string> { ["uniqueName"] = identifier.NameLeaf },
            GraphId = graphId,
            DependsOn = dependsOn,
            LastWriteOn = Now,
            ContentHash = "sha256:abc",
        };
    }

    private static StateFile Sample()
    {
        var state = StateFile.CreateNew(Name, Tenant, Now);
        state.Upsert(Entry("/providers/Microsoft.Entra/applications/zz", Fixture.AppType, "Z1", "/providers/Microsoft.Entra/users/u@contoso.com"));
        state.Upsert(Entry("/providers/Microsoft.Entra/applications/aa", Fixture.AppType, "A1"));
        state.Upsert(new StateEntry
        {
            Id = ResourceIdentifier.Create(Fixture.PasswordType, "aa/ci-secret"),
            Type = Fixture.PasswordType,
            ApiVersion = "2026-10-01-preview",
            Key = new Dictionary<string, string> { ["displayName"] = "ci-secret" },
            GraphId = "K1",
            ParentGraphId = "A1",
            ContentHash = "sha256:def",
            DependsOn = ["/providers/Microsoft.Entra/applications/zz", "/providers/Microsoft.Entra/applications/aa"],
            LastWriteOn = Now.AddMinutes(1),
            Extra = new JsonObject { ["keyId"] = "K1", ["endDateTime"] = "2027-10-01T00:00:00Z" },
            Relationships = new JsonObject { ["memberOf"] = new JsonArray("G1", "G2") },
        });
        state.Operations.Add(new OperationRecord
        {
            ResourceId = "/providers/Microsoft.Entra/applications/aa",
            Type = Fixture.AppType,
            Status = OperationStatus.Succeeded,
            ChangeType = ChangeType.Create,
            Batch = 2,
            StartedOn = Now,
            FinishedOn = Now.AddSeconds(3),
            GraphId = "A1",
        });
        state.Operations.Add(new OperationRecord
        {
            ResourceId = "/providers/Microsoft.Entra/applications/zz",
            Type = Fixture.AppType,
            Status = OperationStatus.Failed,
            ChangeType = ChangeType.Modify,
            Batch = 2,
            StartedOn = Now,
            FinishedOn = Now,
            Error = "Graph 403 Authorization_RequestDenied: Insufficient privileges",
            ErrorCode = EngineDiagnosticCodes.ResourceApplyFailed,
        });
        state.LastStatus = StateFile.StatusSucceeded;
        state.LastMode = "Incremental";
        state.LastTemplateHash = "sha256:template";
        state.LastDeployedOn = Now.AddMinutes(2);
        return state;
    }

    // ---- round trip ----

    [Fact]
    public void Serialize_WritesSortedResourceKeys_GraphIdAsId_ParentId_ExtraAndRelationships()
    {
        var state = Sample();
        state.Operations.Add(new OperationRecord { ResourceId = "/providers/Microsoft.Entra/applications/aa", Type = Fixture.AppType, Status = OperationStatus.Succeeded, ChangeType = ChangeType.Create, StartedOn = Now, FinishedOn = Now });

        string json = StateSerializer.Serialize(state);
        var document = JsonNode.Parse(json)!.AsObject();

        Assert.Equal(1, document["schemaVersion"]!.GetValue<int>());
        Assert.Equal(Name, document["deploymentName"]!.GetValue<string>());
        Assert.Equal(Tenant, document["tenantId"]!.GetValue<string>());
        Assert.Equal("Succeeded", document["lastStatus"]!.GetValue<string>());
        Assert.Equal("Incremental", document["lastMode"]!.GetValue<string>());
        Assert.Equal("sha256:template", document["lastTemplateHash"]!.GetValue<string>());

        var resources = document["resources"]!.AsObject();
        Assert.Equal(["/providers/Microsoft.Entra/applications/aa", "/providers/Microsoft.Entra/applications/aa/passwordCredentials/ci-secret", "/providers/Microsoft.Entra/applications/zz"], resources.Select(r => r.Key).ToList());
        Assert.True(json.IndexOf("applications/aa\"", StringComparison.Ordinal) < json.IndexOf("applications/zz\"", StringComparison.Ordinal));

        var aa = resources["/providers/Microsoft.Entra/applications/aa"]!.AsObject();
        Assert.Equal("A1", aa["id"]!.GetValue<string>());
        Assert.False(aa.ContainsKey("graphId"));
        Assert.False(aa.ContainsKey("parentId"), "null members are omitted");
        Assert.False(aa.ContainsKey("extra"));
        Assert.Equal("aa", aa["key"]!["uniqueName"]!.GetValue<string>());
        Assert.Equal(Fixture.AppType, aa["type"]!.GetValue<string>());

        var child = resources["/providers/Microsoft.Entra/applications/aa/passwordCredentials/ci-secret"]!.AsObject();
        Assert.Equal("K1", child["id"]!.GetValue<string>());
        Assert.Equal("A1", child["parentId"]!.GetValue<string>());
        Assert.Equal("K1", child["extra"]!["keyId"]!.GetValue<string>());
        Assert.Equal("[\"G1\",\"G2\"]", child["relationships"]!["memberOf"]!.ToJsonString());
        Assert.Equal(["/providers/Microsoft.Entra/applications/aa", "/providers/Microsoft.Entra/applications/zz"], child["dependsOn"]!.AsArray().Select(d => d!.GetValue<string>()).ToList());
        Assert.Equal("2026-10-01-preview", child["apiVersion"]!.GetValue<string>());

        var operations = document["operations"]!.AsArray();
        Assert.Equal(3, operations.Count);
        Assert.Equal("Failed", operations[1]!["status"]!.GetValue<string>());
        Assert.Equal("Modify", operations[1]!["changeType"]!.GetValue<string>());
        Assert.Equal(EngineDiagnosticCodes.ResourceApplyFailed, operations[1]!["errorCode"]!.GetValue<string>());
        Assert.Equal(2, operations[0]!["batch"]!.GetValue<int>());
    }

    [Fact]
    public void RoundTrip_PreservesEveryField()
    {
        var state = Sample();

        var loaded = StateSerializer.Deserialize(StateSerializer.Serialize(state), Name);

        Assert.Equal(Name, loaded.DeploymentName);
        Assert.Equal(Tenant, loaded.TenantId);
        Assert.Equal(1, loaded.SchemaVersion);
        Assert.Equal(Now, loaded.CreatedOn);
        Assert.Equal(Now.AddMinutes(2), loaded.LastDeployedOn);
        Assert.Equal(StateFile.StatusSucceeded, loaded.LastStatus);
        Assert.Equal("Incremental", loaded.LastMode);
        Assert.Equal("sha256:template", loaded.LastTemplateHash);
        Assert.Equal(3, loaded.Resources.Count);

        foreach (var pair in state.Resources)
        {
            var expected = pair.Value;
            var actual = loaded.Resources[pair.Key];
            Assert.Equal(expected.Id, actual.Id);
            Assert.Equal(expected.Type, actual.Type);
            Assert.Equal(expected.ApiVersion, actual.ApiVersion);
            Assert.Equal(expected.Key, actual.Key);
            Assert.Equal(expected.GraphId, actual.GraphId);
            Assert.Equal(expected.ParentGraphId, actual.ParentGraphId);
            Assert.Equal(expected.ContentHash, actual.ContentHash);
            Assert.Equal(expected.DependsOn.Order(StringComparer.Ordinal), actual.DependsOn);
            Assert.Equal(expected.LastWriteOn, actual.LastWriteOn);
            Assert.Equal(expected.Extra?.ToJsonString(), actual.Extra?.ToJsonString());
            Assert.Equal(expected.Relationships?.ToJsonString(), actual.Relationships?.ToJsonString());
        }

        Assert.Equal(2, loaded.Operations.Count);
        Assert.Equal(state.Operations.Select(o => (o.ResourceId, o.Status, o.ChangeType, o.Batch, o.StartedOn, o.FinishedOn, o.GraphId, o.Error, o.ErrorCode)), loaded.Operations.Select(o => (o.ResourceId, o.Status, o.ChangeType, o.Batch, o.StartedOn, o.FinishedOn, o.GraphId, o.Error, o.ErrorCode)));
        Assert.Equal(TimeSpan.FromSeconds(3), loaded.Operations[0].Duration);
        Assert.Equal('+', loaded.Operations[0].Symbol);
        Assert.Equal('!', loaded.Operations[1].Symbol);
    }

    [Fact]
    public void Deserialize_TakesTheDeploymentNameFromTheDocument_OrTheCaller()
    {
        var named = StateSerializer.Deserialize("{ \"schemaVersion\": 1, \"deploymentName\": \"from-file\", \"resources\": {}, \"operations\": [] }", "from-caller");
        var unnamed = StateSerializer.Deserialize("{ \"schemaVersion\": 1, \"resources\": {} }", "from-caller");

        Assert.Equal("from-file", named.DeploymentName);
        Assert.Equal("from-caller", unnamed.DeploymentName);
        Assert.Empty(unnamed.Resources);
        Assert.Empty(unnamed.Operations);
    }

    [Fact]
    public void Deserialize_ToleratesCommentsAndTrailingCommas_AndUnknownOperationValues()
    {
        var loaded = StateSerializer.Deserialize("{ /* note */ \"schemaVersion\": 1, \"deploymentName\": \"x\", \"resources\": {}, \"operations\": [ { \"resourceId\": \"r\", \"type\": \"t\", \"status\": \"Weird\", \"changeType\": \"Odd\" }, ], }", "x");

        var operation = Assert.Single(loaded.Operations);
        Assert.Equal(OperationStatus.Failed, operation.Status);
        Assert.Null(operation.ChangeType);
        Assert.Null(operation.Duration);
    }

    [Fact]
    public void Deserialize_ALoneSurrogateEscapeInTheState_IsReadAsTheReplacementCharacter()
    {
        var loaded = StateSerializer.Deserialize("{ \"schemaVersion\": 1, \"deploymentName\": \"x\", \"resources\": {}, \"operations\": [ { \"resourceId\": \"r\", \"type\": \"t\", \"status\": \"Failed\", \"error\": \"name \\ud800 broke\" } ] }", "x");

        var operation = Assert.Single(loaded.Operations);
        Assert.Equal("name � broke", operation.Error);
        Assert.Equal("name � broke", Assert.Single(StateSerializer.Deserialize(StateSerializer.Serialize(loaded), "x").Operations).Error);
    }

    // ---- resource ids ----

    [Theory]
    [InlineData("/providers/Microsoft.Entra/applications/app", "Microsoft.Entra/applications", "app")]
    [InlineData("/providers/Microsoft.Entra/applications/app/federatedIdentityCredentials/gh", "Microsoft.Entra/applications/federatedIdentityCredentials", "app/gh")]
    [InlineData("/providers/Microsoft.Entra/conditionalAccess/policies/CA-1", "Microsoft.Entra/conditionalAccess/policies", "CA-1")]
    [InlineData("/providers/microsoft.entra/APPLICATIONS/App", "Microsoft.Entra/applications", "App")]
    [InlineData("/providers/Microsoft.Entra/future/things/x", "Microsoft.Entra/future/things", "x")]
    [InlineData("/providers/Microsoft.Entra/future/parent/things/child", "Microsoft.Entra/future/things", "parent/child")]
    [InlineData("/providers/Microsoft.Entra/users/bg-orders@contoso.com", "Microsoft.Entra/users", "bg-orders@contoso.com")]
    public void ParseResourceId_RebuildsTheIdentifierFromTheType(string id, string type, string expectedName)
    {
        var identifier = StateSerializer.ParseResourceId(id, type);

        Assert.NotNull(identifier);
        Assert.Equal(expectedName, identifier.Name);
        Assert.True(ResourceIdentifier.TypeEquals(type, identifier.Type));
        Assert.Equal(expectedName.Split('/').Length, identifier.NameSegments.Count);
    }

    [Theory]
    [InlineData("/providers/Microsoft.Entra/groups/g", "Microsoft.Entra/applications")]
    [InlineData("/subscriptions/x/providers/Microsoft.Entra/applications/app", "Microsoft.Entra/applications")]
    [InlineData("/providers/Microsoft.Entra/applications/app/extra", "Microsoft.Entra/applications")]
    [InlineData("/providers/Microsoft.Entra/applications", "Microsoft.Entra/applications")]
    [InlineData("/providers/Microsoft.Entra/applications/", "Microsoft.Entra/applications")]
    [InlineData("/providers/Microsoft.Entra/applications/app/federatedIdentityCredentials", "Microsoft.Entra/applications/federatedIdentityCredentials")]
    [InlineData("/providers/Microsoft.Entra/applications/app", "Microsoft.Entra")]
    public void ParseResourceId_RejectsMismatches(string id, string type)
    {
        Assert.Null(StateSerializer.ParseResourceId(id, type));
    }

    [Fact]
    public void Deserialize_RejectsEntriesThatAreNotResourceIdsOfTheirType_OrLackAGraphId()
    {
        var badKey = Assert.Throws<StateSchemaUnsupportedException>(() => StateSerializer.Deserialize("{ \"schemaVersion\": 1, \"resources\": { \"/providers/Microsoft.Entra/groups/g\": { \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01\", \"id\": \"A1\" } } }", Name));
        var noId = Assert.Throws<StateSchemaUnsupportedException>(() => StateSerializer.Deserialize("{ \"schemaVersion\": 1, \"resources\": { \"/providers/Microsoft.Entra/applications/a\": { \"type\": \"Microsoft.Entra/applications\", \"apiVersion\": \"2026-10-01\" } } }", Name));

        Assert.Contains("is not a resource id of type", badKey.Message);
        Assert.Contains("has no type or Graph id", noId.Message);
        Assert.Equal(EngineDiagnosticCodes.StateSchemaUnsupported, badKey.Code);
        Assert.Equal(Name, badKey.DeploymentName);
    }

    // ---- secret guard ----

    [Fact]
    public void AssertPersistable_RefusesSecretsAndPlaceholders()
    {
        var withSecret = StateFile.CreateNew(Name, Tenant, Now);
        withSecret.Upsert(Entry("/providers/Microsoft.Entra/applications/a", Fixture.AppType, "A1") with { Extra = new JsonObject { ["secretText"] = SecretJson.CreateString("s") } });
        var withUnknown = StateFile.CreateNew(Name, Tenant, Now);
        withUnknown.Upsert(Entry("/providers/Microsoft.Entra/applications/a", Fixture.AppType, "A1") with { Relationships = new JsonObject { ["memberOf"] = new JsonArray(UnknownJson.Create("ref(x).id", UnknownOrigins.Reference)) } });
        var clean = Sample();

        var secret = Assert.Throws<InvalidOperationException>(() => StateSerializer.AssertPersistable(withSecret));
        var unknown = Assert.Throws<InvalidOperationException>(() => StateSerializer.AssertPersistable(withUnknown));
        StateSerializer.AssertPersistable(clean);

        Assert.Contains("carries a secret value", secret.Message);
        Assert.Contains("carries a deploy-time placeholder", unknown.Message);
        Assert.Throws<InvalidOperationException>(() => StateSerializer.Serialize(withSecret));
        Assert.Throws<InvalidOperationException>(() => StateSerializer.Serialize(withUnknown));
    }

    // ---- migrator ----

    [Theory]
    [InlineData("{ \"schemaVersion\": 2, \"deploymentName\": \"x\" }", "schemaVersion 2")]
    [InlineData("{ \"deploymentName\": \"x\", \"resources\": {} }", "no integer 'schemaVersion'")]
    [InlineData("{ \"schemaVersion\": \"1\" }", "no integer 'schemaVersion'")]
    [InlineData("{ \"schemaVersion\": 0 }", "expected 1 or higher")]
    [InlineData("[ 1, 2 ]", "not a JSON object")]
    [InlineData("\"text\"", "not a JSON object")]
    public void StateMigrator_RefusesUnsupportedDocuments(string json, string expectedFragment)
    {
        var error = Assert.Throws<StateSchemaUnsupportedException>(() => StateMigrator.Read(JsonNode.Parse(json), Name));

        Assert.Contains(expectedFragment, error.Message);
        Assert.Contains(Name, error.Message);
        Assert.Equal(EngineDiagnosticCodes.StateSchemaUnsupported, error.Code);
    }

    [Fact]
    public void StateMigrator_ReadsVersionOne()
    {
        var document = StateMigrator.Read(JsonNode.Parse("{ \"schemaVersion\": 1, \"deploymentName\": \"x\", \"tenantId\": \"t\", \"resources\": {}, \"operations\": [] }"), Name);

        Assert.Equal(1, document.SchemaVersion);
        Assert.Equal("x", document.DeploymentName);
        Assert.Equal("t", document.TenantId);
    }

    [Fact]
    public void Deserialize_InvalidJson_IsStateSchemaUnsupported()
    {
        var error = Assert.Throws<StateSchemaUnsupportedException>(() => StateSerializer.Deserialize("{ not json", Name));

        Assert.Contains("not valid JSON", error.Message);
        Assert.IsAssignableFrom<System.Text.Json.JsonException>(error.InnerException);
    }

    // ---- hashing ----

    [Fact]
    public void ContentHash_IgnoresSecretAndUnknownLeaves_AndKeyOrder()
    {
        var withHidden = new JsonObject
        {
            ["b"] = 1,
            ["passwordProfile"] = new JsonObject { ["password"] = SecretJson.CreateString("s3cret"), ["forceChangePasswordNextSignIn"] = true },
            ["later"] = UnknownJson.Create("ref(x).id", UnknownOrigins.Reference),
            ["list"] = new JsonArray(SecretJson.CreateString("x"), 2),
            ["a"] = "v",
        };
        var plain = JsonNode.Parse("{ \"a\": \"v\", \"b\": 1, \"list\": [2], \"passwordProfile\": { \"forceChangePasswordNextSignIn\": true } }");
        var other = JsonNode.Parse("{ \"a\": \"w\", \"b\": 1, \"list\": [2], \"passwordProfile\": { \"forceChangePasswordNextSignIn\": true } }");

        string hash = StateHashing.ContentHash(withHidden);

        Assert.StartsWith("sha256:", hash, StringComparison.Ordinal);
        Assert.Equal(StateHashing.ContentHash(plain), hash);
        Assert.NotEqual(StateHashing.ContentHash(other), hash);
        Assert.Equal("sha256:" + CanonicalJson.Sha256("{}"), StateHashing.ContentHash(new JsonObject()));
        Assert.Equal("{\"b\":1,\"passwordProfile\":{\"forceChangePasswordNextSignIn\":true},\"list\":[2],\"a\":\"v\"}", StateHashing.Strip(withHidden)!.ToJsonString());
    }

    // ---- names ----

    [Theory]
    [InlineData("orders-dev", true)]
    [InlineData("a", true)]
    [InlineData("A.b_c-1", true)]
    [InlineData("1x", true)]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("-x", false)]
    [InlineData(".x", false)]
    [InlineData("a b", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("ünïcode", false)]
    public void StateNames_FollowTheFileNameRule(string? name, bool valid)
    {
        Assert.Equal(valid, StateNames.IsValid(name));
        if (valid)
        {
            StateNames.Validate(name);
        }
        else
        {
            var error = Assert.Throws<ArgumentException>(() => StateNames.Validate(name));
            Assert.Contains("is not valid", error.Message);
        }
    }

    [Fact]
    public void StateNames_LengthLimitIs128()
    {
        Assert.True(StateNames.IsValid(new string('a', 128)));
        Assert.False(StateNames.IsValid(new string('a', 129)));
    }

    // ---- state file lifecycle ----

    [Fact]
    public void EnsureTenant_AdoptsAMissingTenant_AndRefusesAnotherOne()
    {
        var fresh = StateFile.CreateNew(Name, null, Now);
        var pinned = StateFile.CreateNew(Name, Tenant, Now);

        fresh.EnsureTenant(Tenant);
        pinned.EnsureTenant(Tenant.ToUpperInvariant());
        var error = Assert.Throws<TenantMismatchException>(() => pinned.EnsureTenant("22222222-2222-2222-2222-222222222222"));

        Assert.Equal(Tenant, fresh.TenantId);
        Assert.Equal(Tenant, pinned.TenantId);
        Assert.Equal(EngineDiagnosticCodes.TenantMismatch, error.Code);
        Assert.Equal(Tenant, error.StateTenantId);
        Assert.Equal("22222222-2222-2222-2222-222222222222", error.CurrentTenantId);
        Assert.Contains("az login --tenant " + Tenant + " --allow-no-subscriptions", error.Message);
        Assert.Equal(Name, error.DeploymentName);
    }

    [Fact]
    public void BeginRun_MarksRunningAndClearsOperations_CompleteRun_RecordsTheOutcome()
    {
        var state = Sample();
        Assert.Equal(2, state.Operations.Count);

        state.BeginRun("Complete", "sha256:new", Now.AddHours(1));

        Assert.Equal(StateFile.StatusRunning, state.LastStatus);
        Assert.Equal("Complete", state.LastMode);
        Assert.Equal("sha256:new", state.LastTemplateHash);
        Assert.Empty(state.Operations);
        Assert.Equal(Now, state.CreatedOn);
        Assert.Equal(Now.AddMinutes(2), state.LastDeployedOn);

        state.CompleteRun(false, "Complete", "sha256:new", Now.AddHours(2));

        Assert.Equal(StateFile.StatusFailed, state.LastStatus);
        Assert.Equal(Now.AddHours(2), state.LastDeployedOn);
        Assert.Equal("A1", state.GraphIdOf("/providers/Microsoft.Entra/applications/aa"));
        Assert.Null(state.GraphIdOf("/providers/Microsoft.Entra/applications/missing"));
        Assert.True(state.Remove("/providers/Microsoft.Entra/applications/aa"));
        Assert.False(state.Remove("/providers/Microsoft.Entra/applications/aa"));
        Assert.Null(state.Find("/providers/Microsoft.Entra/applications/aa"));
    }

    [Fact]
    public void ToResourceKey_CarriesKeyParentAndApi()
    {
        var entry = Sample().Resources["/providers/Microsoft.Entra/applications/aa/passwordCredentials/ci-secret"];

        var key = entry.ToResourceKey(EntraArm.Core.Types.ResourceTypeCatalog.Default);

        Assert.Equal(Fixture.PasswordType, key.Type);
        Assert.Equal("ci-secret", key.Single);
        Assert.Equal("A1", key.ParentGraphId);
        Assert.Equal(EntraArm.Az.GraphApi.Beta, key.Api);
        Assert.True(key.IsResolved);
    }

    [Theory]
    [InlineData(OperationStatus.Failed, ChangeType.Create, '!')]
    [InlineData(OperationStatus.Skipped, null, '?')]
    [InlineData(OperationStatus.Ignored, ChangeType.Ignore, '*')]
    [InlineData(OperationStatus.Detached, ChangeType.Ignore, '*')]
    [InlineData(OperationStatus.Succeeded, ChangeType.Create, '+')]
    [InlineData(OperationStatus.Succeeded, ChangeType.Modify, '~')]
    [InlineData(OperationStatus.Succeeded, ChangeType.Delete, '-')]
    [InlineData(OperationStatus.Succeeded, ChangeType.NoChange, '=')]
    [InlineData(OperationStatus.Succeeded, null, '*')]
    public void Symbols_FollowTheArmWhatIfVocabulary(OperationStatus status, ChangeType? changeType, char expected)
    {
        var record = new OperationRecord { ResourceId = "r", Type = "t", Status = status, ChangeType = changeType };
        var operation = new ResourceOperation(ResourceIdentifier.Create(Fixture.AppType, "a"), Fixture.AppType, 1, status, changeType, null, null, null, null, TimeSpan.Zero);

        Assert.Equal(expected, record.Symbol);
        Assert.Equal(expected, operation.Symbol);
    }

    [Fact]
    public void StateLockInfo_DescribesTheHolder()
    {
        var info = new StateLockInfo(4242, "HOST", "alice", Now);

        Assert.Equal("process 4242 on HOST (alice) since 2026-10-14 10:00:00Z", info.Describe());
        Assert.Equal("-", StateSerializer.Timestamp(null));
    }
}
