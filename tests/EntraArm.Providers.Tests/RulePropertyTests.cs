using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Text.Json.Nodes;
using EntraArm.Core.Secrets;
using EntraArm.Expressions.Values;
using EntraArm.TestSupport;
using Xunit;

namespace EntraArm.Providers.Tests;

/// <summary>
/// Properties of the pure <c>*Rules</c> classes over seeded random input (plain loops; no property-testing package):
/// normalisation is idempotent and case-insensitive where documented, composite keys round-trip, scope sets ignore
/// order and duplicates, CIDR canonicalisation is a fixed point, dates round-trip at second precision.
/// </summary>
public sealed class RulePropertyTests
{
    private const int Seed = 4711;
    private const int Cases = 300;

    private static Guid RandomGuid(Random random)
    {
        byte[] bytes = new byte[16];
        random.NextBytes(bytes);
        return new Guid(bytes);
    }

    private static string RandomCase(string text, Random random)
    {
        return random.Next(3) switch
        {
            0 => text.ToUpperInvariant(),
            1 => text.ToLowerInvariant(),
            _ => new string(text.Select(c => random.Next(2) == 0 ? char.ToUpperInvariant(c) : char.ToLowerInvariant(c)).ToArray()),
        };
    }

    // ---- GUID-keyed rules ----

    [Fact]
    public void NormalizeId_IsIdempotent_CaseInsensitive_AndLowerCase_ForGuids()
    {
        foreach (var random in JsonGenerator.Cases(Seed, Cases))
        {
            string guid = RandomGuid(random).ToString(random.Next(3) switch { 0 => "D", 1 => "N", _ => "B" });
            string noisy = RandomCase(guid, random);

            string once = RoleAssignmentsRules.NormalizeId(noisy);

            Assert.Equal(once, RoleAssignmentsRules.NormalizeId(once));
            Assert.Equal(once, once.ToLowerInvariant());
            Assert.Equal(once, AppRoleAssignmentsRules.NormalizeId(noisy));
            Assert.Equal(36, once.Length);
        }

        Assert.Equal("not a guid", RoleAssignmentsRules.NormalizeId("  not a guid  "));
        Assert.Throws<ArgumentNullException>(() => RoleAssignmentsRules.NormalizeId(null!));
    }

    [Fact]
    public void NormalizeScope_IsIdempotent_AndEquivalentScopesCompareEqual()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 1, Cases))
        {
            string unit = RandomGuid(random).ToString("D");
            string[] scopes =
            [
                "/", " / ", string.Empty, "  ", "/administrativeUnits/" + RandomCase(unit, random), "/AdministrativeUnits/" + unit + "/", "/" + RandomCase(unit, random), "/some/custom/scope",
            ];

            foreach (string scope in scopes)
            {
                string once = RoleAssignmentsRules.NormalizeScope(scope);
                Assert.Equal(once, RoleAssignmentsRules.NormalizeScope(once));
                Assert.True(RoleAssignmentsRules.ScopeEquals(scope, once));
                Assert.True(RoleAssignmentsRules.ScopeEquals(once, scope));
            }

            Assert.True(RoleAssignmentsRules.ScopeEquals("/administrativeUnits/" + unit.ToUpperInvariant(), "/AdministrativeUnits/" + unit));
            Assert.False(RoleAssignmentsRules.ScopeEquals("/administrativeUnits/" + unit, "/"));
        }
    }

    [Fact]
    public void RoleAssignmentTargetKeys_RoundTrip_ThroughTheirCanonicalForm()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 2, Cases))
        {
            string role = RandomCase(RandomGuid(random).ToString("D"), random);
            string scope = random.Next(2) == 0 ? "/" : "/administrativeUnits/" + RandomCase(RandomGuid(random).ToString("D"), random);

            string key = RoleAssignmentsRules.TargetKey(role, scope);

            Assert.True(RoleAssignmentsRules.TryParseTargetKey(key, out var parsedRole, out var parsedScope), key);
            Assert.Equal(RoleAssignmentsRules.NormalizeId(role), parsedRole);
            Assert.Equal(RoleAssignmentsRules.NormalizeScope(scope), parsedScope);
            Assert.Equal(key, RoleAssignmentsRules.TargetKey(parsedRole, parsedScope));
            Assert.Equal(
                RoleAssignmentsRules.Identity(role, role, scope),
                RoleAssignmentsRules.Identity(role.ToUpperInvariant(), role.ToLowerInvariant(), RoleAssignmentsRules.NormalizeScope(scope).ToUpperInvariant()));
        }

        foreach (string bad in new[] { string.Empty, "@", "x@/", "not-a-guid@/", Guid.NewGuid() + "@", Guid.NewGuid() + "@no-slash", "@/" })
        {
            Assert.False(RoleAssignmentsRules.TryParseTargetKey(bad, out _, out _), bad);
        }
    }

    [Fact]
    public void AppRoleAssignmentTargetKeys_RoundTrip_AndIdentityIgnoresCase()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 3, Cases))
        {
            string resource = RandomCase(RandomGuid(random).ToString("D"), random);
            string role = RandomCase(RandomGuid(random).ToString("D"), random);
            string principal = RandomCase(RandomGuid(random).ToString("D"), random);

            string key = AppRoleAssignmentsRules.TargetKey(resource, role);

            Assert.True(AppRoleAssignmentsRules.TryParseTargetKey(key, out var parsedResource, out var parsedRole), key);
            Assert.Equal(AppRoleAssignmentsRules.NormalizeId(resource), parsedResource);
            Assert.Equal(AppRoleAssignmentsRules.NormalizeId(role), parsedRole);
            Assert.Equal(key, AppRoleAssignmentsRules.TargetKey(parsedResource, parsedRole));
            Assert.Equal(AppRoleAssignmentsRules.Identity(principal, resource, role), AppRoleAssignmentsRules.Identity(principal.ToUpperInvariant(), resource.ToLowerInvariant(), role.ToUpperInvariant()));
            Assert.NotEqual(AppRoleAssignmentsRules.Identity(principal, resource, role), AppRoleAssignmentsRules.Identity(resource, principal, role));
        }
    }

    // ---- permission grants ----

    [Fact]
    public void ScopeStrings_AreSetsOfDistinctTokens_InsensitiveToOrderSpacingAndRepeats()
    {
        string[] pool = ["User.Read", "openid", "profile", "Mail.Send", "Group.ReadWrite.All", "offline_access", "user.read"];
        foreach (var random in JsonGenerator.Cases(Seed + 4, Cases))
        {
            var chosen = Enumerable.Range(0, random.Next(0, 7)).Select(_ => pool[random.Next(pool.Length)]).ToList();
            string plain = string.Join(' ', chosen);
            var shuffled = chosen.Concat(chosen.Take(random.Next(chosen.Count + 1))).OrderBy(_ => random.Next()).ToList();
            string messy = " " + string.Join(random.Next(2) == 0 ? "  " : "\t", shuffled) + "\n";

            var tokens = OAuth2PermissionGrantsRules.Tokens(plain);

            Assert.Equal(tokens.Count, tokens.Distinct(StringComparer.Ordinal).Count());
            Assert.All(tokens, t => Assert.Equal(t.Trim(), t));
            Assert.True(OAuth2PermissionGrantsRules.SameSet(plain, messy), $"'{plain}' vs '{messy}'");
            Assert.True(OAuth2PermissionGrantsRules.SameSet(messy, plain));
            string normalized = OAuth2PermissionGrantsRules.Normalize(messy);
            Assert.Equal(normalized, OAuth2PermissionGrantsRules.Normalize(normalized));
            Assert.Equal(OAuth2PermissionGrantsRules.Tokens(normalized), OAuth2PermissionGrantsRules.Tokens(OAuth2PermissionGrantsRules.Join(OAuth2PermissionGrantsRules.Tokens(normalized))));
            Assert.Equal(tokens.Count == 0, string.IsNullOrEmpty(normalized));
        }

        Assert.False(OAuth2PermissionGrantsRules.SameSet("User.Read", "user.read"));
        Assert.False(OAuth2PermissionGrantsRules.SameSet("a b", "a"));
        Assert.True(OAuth2PermissionGrantsRules.SameSet(null, "  "));
        Assert.Empty(OAuth2PermissionGrantsRules.Tokens(null));
    }

    [Theory]
    [InlineData("AllPrincipals", "AllPrincipals")]
    [InlineData("allprincipals", "AllPrincipals")]
    [InlineData("  ALLPRINCIPALS  ", "AllPrincipals")]
    [InlineData("principal", "Principal")]
    [InlineData("PRINCIPAL", "Principal")]
    [InlineData("Principals", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ConsentTypes_NormaliseToTheCanonicalCasing(string? input, string? expected)
    {
        Assert.Equal(expected, OAuth2PermissionGrantsRules.NormalizeConsentType(input));
        if (expected is not null)
        {
            Assert.Equal(expected, OAuth2PermissionGrantsRules.NormalizeConsentType(expected));
        }
    }

    // ---- named locations: CIDR ----

    [Fact]
    public void RandomNetworkAddresses_AreValidCidrs_AndCanonicalisationIsAFixedPoint_Ipv4()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 5, Cases))
        {
            int prefix = random.Next(0, 33);
            byte[] bytes = new byte[4];
            random.NextBytes(bytes);
            for (int i = 0; i < 4; i++)
            {
                int keep = Math.Clamp(prefix - (i * 8), 0, 8);
                bytes[i] &= keep == 0 ? (byte)0 : (byte)(0xFF << (8 - keep));
            }

            string text = new IPAddress(bytes) + "/" + prefix.ToString(CultureInfo.InvariantCulture);

            Assert.Null(NamedLocationsRules.CidrProblem(text, out var canonical, out bool v6));
            Assert.False(v6);
            Assert.Equal(text, canonical);
            Assert.Null(NamedLocationsRules.CidrProblem(" " + canonical + " ", out var again, out _));
            Assert.Equal(canonical, again);
        }
    }

    [Fact]
    public void RandomNetworkAddresses_AreValidCidrs_AndCanonicalisationIsAFixedPoint_Ipv6()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 6, Cases))
        {
            int prefix = random.Next(0, 129);
            byte[] bytes = new byte[16];
            random.NextBytes(bytes);
            for (int i = 0; i < 16; i++)
            {
                int keep = Math.Clamp(prefix - (i * 8), 0, 8);
                bytes[i] &= keep == 0 ? (byte)0 : (byte)(0xFF << (8 - keep));
            }

            string text = new IPAddress(bytes).ToString().ToUpperInvariant() + "/" + prefix.ToString(CultureInfo.InvariantCulture);

            Assert.Null(NamedLocationsRules.CidrProblem(text, out var canonical, out bool v6));
            Assert.True(v6);
            Assert.Equal(canonical, canonical.ToLowerInvariant());
            Assert.Null(NamedLocationsRules.CidrProblem(canonical, out var again, out _));
            Assert.Equal(canonical, again);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("/24")]
    [InlineData("10.0.0.0/33")]
    [InlineData("10.0.0.0/-1")]
    [InlineData("10.0.0.0/abc")]
    [InlineData("10.0.0.0/")]
    [InlineData("10.0.0.1/24")]
    [InlineData("10.0.0/24")]
    [InlineData("10/8")]
    [InlineData("256.0.0.0/8")]
    [InlineData("::1%eth0/128")]
    [InlineData("fe80::1/129")]
    [InlineData("2001:db8::1/32")]
    [InlineData("not an address/8")]
    [InlineData("10.0.0.0/8/8")]
    public void MalformedOrHostBitCidrs_AreProblems(string text)
    {
        Assert.NotNull(NamedLocationsRules.CidrProblem(text, out var canonical, out _));
        Assert.Equal(text, canonical);
    }

    [Fact]
    public void ABareAddress_GetsTheFullLengthPrefix()
    {
        Assert.Null(NamedLocationsRules.CidrProblem("203.0.113.9", out var v4, out bool isV6));
        Assert.Equal("203.0.113.9/32", v4);
        Assert.False(isV6);
        Assert.Null(NamedLocationsRules.CidrProblem("2001:DB8::1", out var v6, out bool isV6b));
        Assert.Equal("2001:db8::1/128", v6);
        Assert.True(isV6b);
        Assert.Equal("iPv4CidrRange", NamedLocationsRules.RangeTypeFor(false).Replace("#microsoft.graph.", string.Empty, StringComparison.Ordinal), ignoreCase: true);
        Assert.Contains("v6", NamedLocationsRules.RangeTypeFor(true), StringComparison.OrdinalIgnoreCase);
    }

    // ---- users ----

    [Fact]
    public void GeneratedUserPrincipalNames_AreAccepted_AndMutatedOnesAreRejected()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 7, Cases))
        {
            string local = "u" + random.Next(1000).ToString(CultureInfo.InvariantCulture) + (random.Next(2) == 0 ? "_ext#EXT#" : ".name");
            string domain = "d" + random.Next(100).ToString(CultureInfo.InvariantCulture) + ".onmicrosoft.com";
            string upn = local + "@" + domain;

            Assert.Null(UsersRules.ValidateUserPrincipalName(upn));
            Assert.Equal(local, UsersRules.LocalPart(upn));
            Assert.NotNull(UsersRules.ValidateUserPrincipalName(upn + "@" + domain));
            Assert.NotNull(UsersRules.ValidateUserPrincipalName(local));
            Assert.NotNull(UsersRules.ValidateUserPrincipalName("@" + domain));
            Assert.NotNull(UsersRules.ValidateUserPrincipalName(local + "@"));
            Assert.NotNull(UsersRules.ValidateUserPrincipalName(upn.Insert(random.Next(1, upn.Length), random.Next(2) == 0 ? " " : "\t")));
        }
    }

    // ---- dates ----

    [Fact]
    public void DateTimes_FormatToUtcSecondPrecision_AndRoundTripThroughTheParser()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 8, Cases))
        {
            var instant = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero).AddSeconds(random.NextInt64(0, 400_000_000)).AddMilliseconds(random.Next(1000));
            var local = instant.ToOffset(TimeSpan.FromMinutes(random.Next(-12 * 4, (14 * 4) + 1) * 15));

            string text = PasswordCredentialsRules.Format(local);

            Assert.EndsWith("Z", text, StringComparison.Ordinal);
            Assert.True(PasswordCredentialsRules.TryParseDateTime(JsonValue.Create(text), out var parsed), text);
            Assert.True(PasswordCredentialsRules.SameInstant(parsed, instant), $"{text} vs {instant:O}");
            Assert.True(PasswordCredentialsRules.SameInstant(instant, parsed));
            Assert.Equal(text, PasswordCredentialsRules.Format(parsed));
            Assert.True(PasswordCredentialsRules.TryParseDateTime(JsonValue.Create(local.ToString("O", CultureInfo.InvariantCulture)), out var fromOffset));
            Assert.True(PasswordCredentialsRules.SameInstant(fromOffset, instant));
        }

        Assert.False(PasswordCredentialsRules.TryParseDateTime(JsonValue.Create("not a date"), out _));
        Assert.False(PasswordCredentialsRules.TryParseDateTime(JsonValue.Create(5), out _));
        Assert.False(PasswordCredentialsRules.TryParseDateTime(null, out _));
        Assert.False(PasswordCredentialsRules.TryParseDateTime(UnknownJson.Create("reference('x').end", UnknownOrigins.Reference), out _));
        Assert.False(PasswordCredentialsRules.TryParseDateTime(SecretJson.CreateString("2030-01-01T00:00:00Z"), out _));
        Assert.True(PasswordCredentialsRules.TryParseDateTime(JsonValue.Create("2030-01-01"), out var dateOnly));
        Assert.Equal(TimeSpan.Zero, dateOnly.Offset);
    }

    [Fact]
    public void SameInstant_IsSymmetric_AndToleratesOneSecond()
    {
        var now = new DateTimeOffset(2026, 10, 14, 10, 0, 0, TimeSpan.Zero);

        Assert.True(PasswordCredentialsRules.SameInstant(now, now.AddMilliseconds(999)));
        Assert.True(PasswordCredentialsRules.SameInstant(now.AddMilliseconds(999), now));
        Assert.False(PasswordCredentialsRules.SameInstant(now, now.AddSeconds(2)));
        Assert.True(PasswordCredentialsRules.SameInstant(null, null));
        Assert.False(PasswordCredentialsRules.SameInstant(now, null));
        Assert.False(PasswordCredentialsRules.SameInstant(null, now));
    }

    [Fact]
    public void KeyIds_CompareCaseInsensitively_AndNullsNeverMatchValues()
    {
        foreach (var random in JsonGenerator.Cases(Seed + 9, Cases))
        {
            string id = RandomGuid(random).ToString("D");

            Assert.True(PasswordCredentialsRules.KeyIdEquals(id, id.ToUpperInvariant()));
            Assert.True(PasswordCredentialsRules.KeyIdEquals(id.ToLowerInvariant(), id));
            Assert.False(PasswordCredentialsRules.KeyIdEquals(id, RandomGuid(random).ToString("D")));
            Assert.False(PasswordCredentialsRules.KeyIdEquals(id, null));
        }

        Assert.True(PasswordCredentialsRules.KeyIdEquals(null, null));
    }
}
