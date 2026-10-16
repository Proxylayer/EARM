using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using EArm.Graph;
using Xunit;

namespace EArm.Integration.Tests;

/// <summary>
/// The authentication method configuration round trip through the CLI, under the restore guard (<see cref="AuthMethodRestoreGuard"/>): needs
/// <c>EARM_IT_ALLOW_AUTH_METHODS=1</c>. The live configurations are snapshotted first; the test changes ONLY safe toggles (Temporary Access Pass length and
/// lifetimes; the AAGUID list of a FIDO2 key restriction that is not enforced, in a tenant without passkey profiles; Software OATH's state, only when it
/// is disabled), deploys the snapshot values back with EARM, and the guard verifies (and if needed restores) every snapshot in a <c>finally</c>; the
/// fixture restores again at teardown. No method the tenant uses is ever disabled and no target is ever declared.
/// </summary>
[Collection(RealTenantCollection.Name)]
public sealed class AuthMethodScenarioTests : IntegrationTestBase
{
    /// <summary>A syntactically valid AAGUID no authenticator model uses; added to a NOT-enforced restriction only.</summary>
    internal const string TestAaGuid = "00000000-0000-4000-8000-0000ea12a000";

    private static readonly string[] Methods = ["temporaryAccessPass", "fido2", "softwareOath"];

    public AuthMethodScenarioTests(IntegrationFixture fixture)
        : base(fixture)
    {
    }

    [Fact]
    public async Task It_AuthMethods_SafeToggleRoundTripUnderTheRestoreGuard()
    {
        Fixture.Require(Needs.AuthMethods);
        var guard = Fixture.AuthMethods;
        var log = new List<string>();
        var snapshots = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        try
        {
            foreach (string method in Methods)
            {
                snapshots[method] = await guard.SnapshotAsync(method, Ct);
                Observe($"snapshot {method}: {AuthMethodRestoreGuard.StripAnnotations(snapshots[method])?.ToJsonString()}");
            }

            var notes = new List<string>();
            var changed = Variant(snapshots, change: true, notes);
            var original = Variant(snapshots, change: false, notes: null);
            notes.ForEach(Observe);
            var run = Fixture.NewRun("authm", "auth-methods.entra.json");
            var changedRun = run.WithTemplate(TemplateEditor.Write(Fixture.ScratchDirectory, "auth-methods.changed.entra.json", changed));
            var originalRun = run.WithTemplate(TemplateEditor.Write(Fixture.ScratchDirectory, "auth-methods.original.entra.json", original));

            // The snapshot values deployed as they are: nothing to do (the first run of a configuration is Modify or NoChange, never Create).
            await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(originalRun, Ct, "--exit-code-on-changes"), "what-if of the snapshot values");

            var preview = await Fixture.Cli.WhatIfAsync(changedRun, Ct, "--exit-code-on-changes");
            AssertChange(preview, "/temporaryAccessPass", "Modify", "what-if of the safe toggles");
            AssertCreated(await Fixture.Cli.CreateAsync(changedRun, Ct), "create with the safe toggles");

            var tap = await ReadAsync("temporaryAccessPass");
            foreach (var (name, value) in Declared(changed, "temporaryAccessPass"))
            {
                Assert.True(
                    AuthMethodRestoreGuard.Canonical(tap[name]) == AuthMethodRestoreGuard.Canonical(value),
                    $"temporaryAccessPass.{name}: expected {value?.ToJsonString()} but read {tap[name]?.ToJsonString()}");
            }

            if (HasResource(changed, "fido2"))
            {
                var fido2 = await ReadAsync("fido2");
                Assert.Contains(TestAaGuid, fido2.Nested("keyRestrictions").Strings("aaGuids"), StringComparer.OrdinalIgnoreCase);
            }

            if (HasResource(changed, "softwareOath"))
            {
                Assert.Equal("enabled", (await ReadAsync("softwareOath")).Text("state"), ignoreCase: true);
            }

            await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(changedRun, Ct, "--exit-code-on-changes"), "rerun of the safe toggles");

            // Back to the snapshot with EARM itself: a Modify that the guard then finds already restored.
            AssertChange(await Fixture.Cli.WhatIfAsync(originalRun, Ct, "--exit-code-on-changes"), "/temporaryAccessPass", "Modify", "what-if back to the snapshot");
            AssertCreated(await Fixture.Cli.CreateAsync(originalRun, Ct), "create back to the snapshot");
            await AssertNoChangesEventuallyAsync(() => Fixture.Cli.WhatIfAsync(originalRun, Ct, "--exit-code-on-changes"), "rerun of the snapshot values");
            foreach (string method in Methods)
            {
                var live = await ReadAsync(method);
                var differing = AuthMethodRestoreGuard.Differences(snapshots[method], live);
                Assert.True(differing.Count == 0, $"{method} differs from its snapshot after EARM deployed the snapshot values back: {string.Join(", ", differing)}");
            }
        }
        finally
        {
            foreach (string method in Methods)
            {
                await guard.RestoreAsync(method, log, CancellationToken.None);
            }

            log.ForEach(Observe);
        }

        Assert.True(guard.Pending.Count == 0, "Authentication method configurations could not be restored to their snapshots (the fixture retries at teardown):" + Environment.NewLine + string.Join(Environment.NewLine, log));
    }

    /// <summary>
    /// The scenario template with values derived from the snapshots: <paramref name="change"/> true writes the safe toggles, false the snapshot values.
    /// A method whose toggle is not safe in this tenant is left out (and <paramref name="notes"/> says why).
    /// </summary>
    internal static JsonObject Variant(IReadOnlyDictionary<string, JsonObject> snapshots, bool change, List<string>? notes)
    {
        var template = TemplateEditor.Load(IntegrationFixture.TemplatePath("auth-methods.entra.json"));
        bool fido2Safe = IsFido2ToggleSafe(snapshots["fido2"], out string fido2Reason);
        bool oathSafe = string.Equals(snapshots["softwareOath"].Text("state"), "disabled", StringComparison.OrdinalIgnoreCase);
        if (!fido2Safe)
        {
            notes?.Add("fido2 left out: " + fido2Reason);
        }

        if (!oathSafe)
        {
            notes?.Add($"softwareOath left out: its state is '{snapshots["softwareOath"].Text("state")}', and only a disabled method is toggled (enabled -> disabled could lock users out).");
        }

        TemplateEditor.Retain(template, r => TemplateEditor.TypeOf(r) != "Microsoft.Entra/authenticationMethodConfigurations"
            || (string?)r["name"] switch
            {
                "fido2" => fido2Safe,
                "softwareOath" => oathSafe,
                _ => true,
            });

        foreach (var resource in template["resources"]!.AsArray().OfType<JsonObject>())
        {
            var properties = new JsonObject();
            switch ((string?)resource["name"])
            {
                case "temporaryAccessPass":
                    var tap = snapshots["temporaryAccessPass"];
                    var live = (Length: (int?)tap["defaultLength"] ?? 8, Min: (int?)tap["minimumLifetimeInMinutes"] ?? 60, Default: (int?)tap["defaultLifetimeInMinutes"] ?? 60, Max: (int?)tap["maximumLifetimeInMinutes"] ?? 480);
                    var lifetimes = new[] { (Min: 60, Default: 60, Max: 480), (Min: 15, Default: 30, Max: 120) }.First(c => c != (live.Min, live.Default, live.Max));
                    (int Length, int Min, int Default, int Max) values = change ? (live.Length == 8 ? 9 : 8, lifetimes.Min, lifetimes.Default, lifetimes.Max) : live;
                    properties["defaultLength"] = values.Length;
                    properties["minimumLifetimeInMinutes"] = values.Min;
                    properties["defaultLifetimeInMinutes"] = values.Default;
                    properties["maximumLifetimeInMinutes"] = values.Max;
                    break;
                case "fido2":
                    var aaGuids = snapshots["fido2"].Nested("keyRestrictions").Strings("aaGuids");
                    if (change)
                    {
                        aaGuids.Add(TestAaGuid);
                    }

                    properties["keyRestrictions"] = new JsonObject { ["isEnforced"] = false, ["aaGuids"] = new JsonArray([.. aaGuids.Select(g => (JsonNode?)JsonValue.Create(g))]) };
                    break;
                case "softwareOath":
                    properties["state"] = change ? "enabled" : "disabled";
                    break;
            }

            resource["properties"] = properties;
        }

        return template;
    }

    /// <summary>
    /// Changing FIDO2 AAGUIDs is safe only on a key restriction that is NOT enforced (the list then has no effect on sign-in or registration) and when the
    /// tenant has no passkey profiles (how the legacy keyRestrictions and passkeyProfiles interact is an open question; see the AM probes).
    /// </summary>
    internal static bool IsFido2ToggleSafe(JsonObject fido2, out string reason)
    {
        if (fido2["passkeyProfiles"] is JsonArray { Count: > 0 } profiles)
        {
            reason = $"the tenant has {profiles.Count} passkey profile(s); the legacy keyRestrictions are not touched while passkeyProfiles are in use.";
            return false;
        }

        if (fido2.Nested("keyRestrictions") is not { } restrictions)
        {
            reason = "the live configuration has no keyRestrictions.";
            return false;
        }

        if ((bool?)restrictions["isEnforced"] != false)
        {
            reason = "the live key restriction is enforced (isEnforced is not false); its AAGUID list decides which keys can register, so it is never changed.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool HasResource(JsonObject template, string name)
    {
        return template["resources"]!.AsArray().OfType<JsonObject>().Any(r => (string?)r["name"] == name);
    }

    private static IEnumerable<KeyValuePair<string, JsonNode?>> Declared(JsonObject template, string name)
    {
        var resource = template["resources"]!.AsArray().OfType<JsonObject>().Single(r => (string?)r["name"] == name);
        return resource["properties"]!.AsObject().ToList();
    }

    private async Task<JsonObject> ReadAsync(string method)
    {
        return await Graph.GetObjectAsync(new GraphRequest(AuthMethodRestoreGuard.EntityPath(AuthMethodRestoreGuard.MethodOf(method))), Ct)
            ?? throw new InvalidOperationException($"The {method} configuration cannot be read.");
    }
}
