# Changelog

All notable changes to EARM are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/).

## [Unreleased]

### Changed

- Users: the defaults (`accountEnabled: true`, the derived `mailNickname`,
  `passwordProfile.forceChangePasswordNextSignIn: true`) now apply only when EARM creates the user. An existing user
  that EARM finds by its userPrincipalName is no longer given them, so its undeclared properties are never written.
  Previously a template that declared a user with only edge properties (such as `memberOf` or
  `administrativeUnits`) re-enabled that user's account if it was disabled. Effect on existing templates: an existing
  disabled user is no longer re-enabled, and what-if no longer shows an `accountEnabled` change for it; templates that
  rely on EARM keeping existing accounts enabled must now declare `"accountEnabled": true`.
- Defaults are now create-only for every resource type: an existing object that EARM finds or adopts is changed only
  where the template declares a property. Types changed: `servicePrincipals` (`accountEnabled: true`,
  `appRoleAssignmentRequired: false`; also not written when a create turns into the adoption of a principal that
  appeared after the read), `conditionalAccess/policies` (`state: enabledForReportingButNotEnforced`),
  `conditionalAccess/namedLocations` (`isTrusted: false`; `includeUnknownCountriesAndRegions: false`,
  `countryLookupMethod: clientIpAddress`), `conditionalAccess/authenticationContextClassReferences`
  (`isAvailable: true`) and `administrativeUnits` (`membershipRuleProcessingState: On`, still applied when a unit becomes
  dynamic). Previously these defaults were part of the desired state on every run, so a property changed outside EARM
  but not declared in the template was silently reset (a disabled service principal re-enabled, an enabled CA policy
  switched to report-only, a trusted location untrusted, a paused dynamic unit resumed). Defaults still apply on create,
  on the soft-deleted restore that replaces a create, and on the replacement of a named location's kind change. Kept on
  every run, because they belong to a declared value: `conditions.clientAppTypes` and `grantControls.operator` inside a
  declared CA `conditions` / `grantControls` (Graph replaces those objects whole), and the scope, `scheduleInfo` and
  `justification` of PIM schedule requests. Effect on existing templates: what-if no longer shows changes for these
  undeclared properties; templates that relied on EARM resetting an undeclared property to its default must now declare
  it.
