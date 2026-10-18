using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace EArm.Integration.Tests;

/// <summary>What a test needs besides the tenant itself; <see cref="IntegrationFixture.Require"/> skips the test when a requirement is unmet.</summary>
[Flags]
internal enum Needs
{
    /// <summary>A reachable tenant (<c>EARM_IT_TENANT</c>, a working sign-in for that tenant).</summary>
    Tenant = 0,

    /// <summary>A verified domain for user principal names (<c>EARM_IT_DOMAIN</c>).</summary>
    Domain = 1,

    /// <summary>Conditional Access objects may be written (<c>EARM_IT_ALLOW_CA=1</c>).</summary>
    ConditionalAccess = 2,

    /// <summary>Directory roles, administrative units, app role assignments and consent grants may be written (<c>EARM_IT_ALLOW_ROLES=1</c>).</summary>
    Roles = 4,

    /// <summary>PIM eligibilities and time-bound active assignments may be written (<c>EARM_IT_ALLOW_PIM=1</c>; the tenant needs Microsoft Entra ID P2 or ID Governance).</summary>
    Pim = 8,

    /// <summary>
    /// The tenant-wide authentication methods policy may be changed (<c>EARM_IT_ALLOW_AUTH_METHODS=1</c>): only safe toggles, under the snapshot / restore
    /// guard (<see cref="AuthMethodRestoreGuard"/>).
    /// </summary>
    AuthMethods = 16,

    /// <summary>
    /// Tenant-wide PIM role management policies may be changed (<c>EARM_IT_ALLOW_PIM_POLICIES=1</c>, on top of <see cref="Pim"/>): only the Message Center
    /// Reader policy and the PIM for Groups policies of prefixed test groups, under the snapshot / restore guard (<see cref="PimPolicyRestoreGuard"/>).
    /// </summary>
    PimPolicies = 32,

    /// <summary>
    /// Entitlement management objects may be written (<c>EARM_IT_ALLOW_ACCESS_PACKAGES=1</c>): prefixed catalogs, catalog resources, access packages, role
    /// scopes and assignment policies (the tenant needs Microsoft Entra ID P2 or ID Governance; the signed-in identity needs Identity Governance Administrator).
    /// </summary>
    AccessPackages = 64,

    /// <summary>
    /// The tenant default app management policy may be changed (<c>EARM_IT_ALLOW_APP_MANAGEMENT=1</c>): only its description, under a snapshot / restore
    /// guard (<see cref="TenantPolicyRestoreGuard"/>); prefixed custom app management policies are created and deleted.
    /// </summary>
    AppManagement = 128,

    /// <summary>
    /// The tenant's cross-tenant access settings may be changed (<c>EARM_IT_ALLOW_CROSS_TENANT=1</c>): one inbound trust toggle of the default configuration
    /// under a snapshot / restore guard, and a partner configuration for <c>EARM_IT_PARTNER_TENANT</c> when that tenant has none yet.
    /// </summary>
    CrossTenant = 256,

    /// <summary>
    /// The tenant's group expiration policy may be written (<c>EARM_IT_ALLOW_GROUP_LIFECYCLE=1</c>): a Selected policy is created (and deleted) only when the
    /// tenant has none; an existing policy is only read.
    /// </summary>
    GroupLifecycle = 512,

    /// <summary>
    /// An existing SharePoint Online site the signed-in user administers (<c>EARM_IT_SP_SITE_URL</c>, with <c>EARM_IT_SP_SITE_ROLE</c> naming one of its
    /// groups, for example <c>Team Members</c>): the site is added to a prefixed catalog and removed again, never created or changed. Graph lists sites in
    /// a catalog only to delegated callers, so the run must be a user sign-in (SharePoint Administrator or site administrator).
    /// </summary>
    SharePointSite = 1024,

    /// <summary>
    /// Standalone access reviews may be written (<c>EARM_IT_ALLOW_ACCESS_REVIEWS=1</c>): prefixed access review definitions of prefixed groups, created and
    /// deleted (the tenant needs Microsoft Entra ID Governance or P2; the signed-in identity Identity Governance Administrator or User Administrator).
    /// </summary>
    AccessReviews = 2048,
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
    public const string AllowCaVariable = "EARM_IT_ALLOW_CA";
    public const string AllowRolesVariable = "EARM_IT_ALLOW_ROLES";
    public const string AllowPimVariable = "EARM_IT_ALLOW_PIM";
    public const string AllowAuthMethodsVariable = "EARM_IT_ALLOW_AUTH_METHODS";
    public const string AllowPimPoliciesVariable = "EARM_IT_ALLOW_PIM_POLICIES";
    public const string AllowAccessPackagesVariable = "EARM_IT_ALLOW_ACCESS_PACKAGES";
    public const string AllowAppManagementVariable = "EARM_IT_ALLOW_APP_MANAGEMENT";
    public const string AllowCrossTenantVariable = "EARM_IT_ALLOW_CROSS_TENANT";
    public const string AllowGroupLifecycleVariable = "EARM_IT_ALLOW_GROUP_LIFECYCLE";
    public const string AllowAccessReviewsVariable = "EARM_IT_ALLOW_ACCESS_REVIEWS";
    public const string PartnerTenantVariable = "EARM_IT_PARTNER_TENANT";
    public const string SharePointSiteUrlVariable = "EARM_IT_SP_SITE_URL";
    public const string SharePointSiteRoleVariable = "EARM_IT_SP_SITE_ROLE";
    public const string SweepVariable = "EARM_IT_SWEEP";
    public const string InteractiveVariable = "EARM_IT_INTERACTIVE";

    /// <summary>The shortest prefix accepted; a short prefix would let the cleanup sweep match objects it does not own.</summary>
    public const int MinimumPrefixLength = 10;

    /// <summary>
    /// The longest prefix the Conditional Access scenarios accept: Graph limits an authentication strength's displayName to 30
    /// characters, and the longest strength name the tests build is <c>&lt;prefix&gt;-probe-str</c> (10 more characters).
    /// </summary>
    public const int MaximumConditionalAccessPrefixLength = 20;

    private static readonly Regex PrefixPattern = new("^[a-z][a-z0-9-]{9,39}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    /// <summary>The tenant id the tests may write to, or null when the tests are not configured.</summary>
    public string? TenantId { get; init; }

    /// <summary>Name prefix of every object the run creates; the cleanup guard only ever touches objects whose name starts with it.</summary>
    public required string Prefix { get; init; }

    /// <summary>A verified domain for user principal names, or null.</summary>
    public string? Domain { get; init; }

    public bool AllowConditionalAccess { get; init; }

    public bool AllowRoles { get; init; }

    /// <summary><c>EARM_IT_ALLOW_PIM=1</c>: PIM eligibility and time-bound active assignment writes (Entra ID P2 / ID Governance).</summary>
    public bool AllowPim { get; init; }

    /// <summary><c>EARM_IT_ALLOW_AUTH_METHODS=1</c>: safe, snapshot-guarded changes of the tenant authentication methods policy.</summary>
    public bool AllowAuthMethods { get; init; }

    /// <summary><c>EARM_IT_ALLOW_PIM_POLICIES=1</c>: snapshot-guarded changes of low-risk PIM role management policies (needs <see cref="AllowPim"/> too).</summary>
    public bool AllowPimPolicies { get; init; }

    /// <summary><c>EARM_IT_ALLOW_ACCESS_PACKAGES=1</c>: writes of prefixed entitlement management objects (catalogs, packages, policies).</summary>
    public bool AllowAccessPackages { get; init; }

    /// <summary><c>EARM_IT_ALLOW_APP_MANAGEMENT=1</c>: snapshot-guarded description change of the tenant default app management policy, prefixed custom policies.</summary>
    public bool AllowAppManagement { get; init; }

    /// <summary><c>EARM_IT_ALLOW_CROSS_TENANT=1</c>: snapshot-guarded inbound trust toggle of the default cross-tenant configuration, a partner configuration.</summary>
    public bool AllowCrossTenant { get; init; }

    /// <summary><c>EARM_IT_ALLOW_GROUP_LIFECYCLE=1</c>: a Selected group expiration policy, only in a tenant that has none.</summary>
    public bool AllowGroupLifecycle { get; init; }

    /// <summary><c>EARM_IT_ALLOW_ACCESS_REVIEWS=1</c>: prefixed standalone access review definitions (ID Governance).</summary>
    public bool AllowAccessReviews { get; init; }

    /// <summary><c>EARM_IT_PARTNER_TENANT</c>: the tenant id of a real other tenant for the partner configuration scenario, or null.</summary>
    public string? PartnerTenant { get; init; }

    /// <summary><c>EARM_IT_SP_SITE_URL</c>: an existing SharePoint Online site the access package tests may add to a prefixed catalog, or null.</summary>
    public string? SharePointSiteUrl { get; init; }

    /// <summary><c>EARM_IT_SP_SITE_ROLE</c>: the display name of one of that site's groups (for example <c>Team Members</c>), or null.</summary>
    public string? SharePointSiteRole { get; init; }

    /// <summary><c>EARM_IT_SWEEP=1</c>: the manual cleanup test may run.</summary>
    public bool SweepRequested { get; init; }

    /// <summary>
    /// <c>EARM_IT_INTERACTIVE=1</c>: a person is at the machine, so a user sign-in that lacks consent for the run's scopes may open ONE consent prompt
    /// (browser) at the start of the run. Off (the default, and always in CI): the run skips with the <c>earm auth login --scopes ...</c> command instead.
    /// A test host has no console to detect this from, so it is an explicit opt-in.
    /// </summary>
    public bool InteractiveConsent { get; init; }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(TenantId);

    /// <summary>True when the configuration enables tests with <paramref name="needs"/> (the gates of <see cref="IntegrationFixture.Require"/>, without the tenant check).</summary>
    public bool Meets(Needs needs)
    {
        return (!needs.HasFlag(Needs.Domain) || Domain is not null)
            && (!needs.HasFlag(Needs.ConditionalAccess) || AllowConditionalAccess)
            && (!needs.HasFlag(Needs.Roles) || AllowRoles)
            && (!needs.HasFlag(Needs.Pim) || AllowPim)
            && (!needs.HasFlag(Needs.AuthMethods) || AllowAuthMethods)
            && (!needs.HasFlag(Needs.PimPolicies) || AllowPimPolicies)
            && (!needs.HasFlag(Needs.AccessPackages) || AllowAccessPackages)
            && (!needs.HasFlag(Needs.AppManagement) || AllowAppManagement)
            && (!needs.HasFlag(Needs.CrossTenant) || AllowCrossTenant)
            && (!needs.HasFlag(Needs.GroupLifecycle) || AllowGroupLifecycle)
            && (!needs.HasFlag(Needs.AccessReviews) || AllowAccessReviews)
            && (!needs.HasFlag(Needs.SharePointSite) || (SharePointSiteUrl is not null && SharePointSiteRole is not null));
    }

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
            AllowConditionalAccess = IsOn(AllowCaVariable),
            AllowRoles = IsOn(AllowRolesVariable),
            AllowPim = IsOn(AllowPimVariable),
            AllowAuthMethods = IsOn(AllowAuthMethodsVariable),
            AllowPimPolicies = IsOn(AllowPimPoliciesVariable),
            AllowAccessPackages = IsOn(AllowAccessPackagesVariable),
            AllowAppManagement = IsOn(AllowAppManagementVariable),
            AllowCrossTenant = IsOn(AllowCrossTenantVariable),
            AllowGroupLifecycle = IsOn(AllowGroupLifecycleVariable),
            AllowAccessReviews = IsOn(AllowAccessReviewsVariable),
            PartnerTenant = Read(PartnerTenantVariable),
            SharePointSiteUrl = Read(SharePointSiteUrlVariable),
            SharePointSiteRole = Read(SharePointSiteRoleVariable),
            SweepRequested = IsOn(SweepVariable),
            InteractiveConsent = IsOn(InteractiveVariable),
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
