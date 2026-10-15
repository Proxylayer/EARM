using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EArm.Integration.Tests;

/// <summary>What a test needs besides the tenant itself; <see cref="IntegrationFixture.Require"/> skips the test when a requirement is unmet.</summary>
[Flags]
internal enum Needs
{
    /// <summary>A reachable tenant (<c>EARM_IT_TENANT</c>, a usable <c>az</c> session for that tenant).</summary>
    Tenant = 0,

    /// <summary>A verified domain for user principal names (<c>EARM_IT_DOMAIN</c>).</summary>
    Domain = 1,

    /// <summary>Conditional Access objects may be written (<c>EARM_IT_ALLOW_CA=1</c>).</summary>
    ConditionalAccess = 2,

    /// <summary>Directory roles, administrative units, app role assignments and consent grants may be written (<c>EARM_IT_ALLOW_ROLES=1</c>).</summary>
    Roles = 4,
}

/// <summary>
/// The environment-variable configuration of the real-tenant tests. Nothing is read from files; an unset
/// <see cref="TenantVariable"/> means "not configured" and every test skips.
/// </summary>
internal sealed record IntegrationSettings
{
    public const string TenantVariable = "EARM_IT_TENANT";
    public const string PrefixVariable = "EARM_IT_PREFIX";
    public const string DomainVariable = "EARM_IT_DOMAIN";
    public const string AzVariable = "EARM_IT_AZ";
    public const string AllowCaVariable = "EARM_IT_ALLOW_CA";
    public const string AllowRolesVariable = "EARM_IT_ALLOW_ROLES";
    public const string SweepVariable = "EARM_IT_SWEEP";

    /// <summary>The shortest prefix accepted; a short prefix would let the cleanup sweep match objects it does not own.</summary>
    public const int MinimumPrefixLength = 10;

    private static readonly Regex PrefixPattern = new("^[a-z][a-z0-9-]{9,39}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The tenant id the tests may write to, or null when the tests are not configured.</summary>
    public string? TenantId { get; init; }

    /// <summary>Name prefix of every object the run creates; the cleanup guard only ever touches objects whose name starts with it.</summary>
    public required string Prefix { get; init; }

    /// <summary>A verified domain for user principal names, or null.</summary>
    public string? Domain { get; init; }

    /// <summary>The <c>az</c> command override (a path to az, az.cmd or a script), or null.</summary>
    public string? AzCommand { get; init; }

    public bool AllowConditionalAccess { get; init; }

    public bool AllowRoles { get; init; }

    /// <summary><c>EARM_IT_SWEEP=1</c>: the manual cleanup test may run.</summary>
    public bool SweepRequested { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(TenantId);

    public static IntegrationSettings FromEnvironment()
    {
        string? tenant = Read(TenantVariable);
        string? prefix = Read(PrefixVariable);
        bool configured = tenant is not null;

        if (prefix is null)
        {
            prefix = "earm-it-" + RandomSuffix();
        }
        else if (configured && !IsValidPrefix(prefix))
        {
            throw new InvalidOperationException(string.Create(
                CultureInfo.InvariantCulture,
                $"{PrefixVariable}='{prefix}' is not usable: use {MinimumPrefixLength}-40 characters, lower-case letters, digits and '-', starting with a letter (a short prefix could make the cleanup sweep touch objects that are not part of this run)."));
        }

        return new IntegrationSettings
        {
            TenantId = tenant,
            Prefix = prefix,
            Domain = Read(DomainVariable),
            AzCommand = Read(AzVariable),
            AllowConditionalAccess = IsOn(AllowCaVariable),
            AllowRoles = IsOn(AllowRolesVariable),
            SweepRequested = IsOn(SweepVariable),
        };
    }

    public static bool IsValidPrefix(string prefix)
    {
        return PrefixPattern.IsMatch(prefix);
    }

    private static string RandomSuffix()
    {
        Span<byte> bytes = stackalloc byte[3];
        RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static string? Read(string name)
    {
        string? value = Environment.GetEnvironmentVariable(name);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static bool IsOn(string name)
    {
        return Read(name) is "1" or "true" or "TRUE" or "True" or "yes";
    }
}
