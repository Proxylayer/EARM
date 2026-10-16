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
  rely on EARM keeping existing accounts enabled must now declare `"accountEnabled": true`. Service principal defaults
  are unchanged.
