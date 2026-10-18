#Requires -Version 7
<#
.SYNOPSIS
    Regenerates the property schemas of schemas/2026-10-01/types/Microsoft.Entra/*.json.
.DESCRIPTION
    The per-type schema files are hand-maintained in v1 but their property vocabularies are
    long and repetitive, so this script holds them in one compact DSL and rewrites every file: the header, the
    "properties" and the "$defs" are generated; the "x-earm" overlay (everything from that key to the end of the
    file) is carried over verbatim, except for "namePattern", which is set from the table below.

    Conventions of the generated schemas (docs/format.md):
      * Every leaf (and every array item, and every nested object) is wrapped as
        anyOf: [ <typed schema>, { "$ref": "#/$defs/expression" } ], so a "[...]" template expression validates anywhere.
      * Objects keep "additionalProperties": true (Graph passthrough: a property EARM does not model is still sent
        to Graph unchanged) except the edge item objects (appRoleAssignments, directoryRoles, assignedLicenses), which
        the edge handlers reject on unknown properties and which therefore say additionalProperties: false.
      * Enums only where Graph defines a closed set; open-ended sets (authentication method combinations, directory
        role actions) are documented in the description, not enforced.
    Run it from anywhere; it only touches the type schema files.
.EXAMPLE
    pwsh -File tools/New-TypeSchemas.ps1
.EXAMPLE
    pwsh -File tools/New-TypeSchemas.ps1 -WhatIf
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [string]$SchemaRoot = (Join-Path $PSScriptRoot '..' 'schemas' '2026-10-01' 'types' 'Microsoft.Entra')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# ---------------------------------------------------------------------------------------------------------------
# JSON writer: compact where it fits, one member per line where it does not.
# ---------------------------------------------------------------------------------------------------------------

$script:JsonOptions = [System.Text.Json.JsonSerializerOptions]::new()
$script:JsonOptions.Encoder = [System.Text.Encodings.Web.JavaScriptEncoder]::UnsafeRelaxedJsonEscaping

function ConvertTo-JsonString([string]$Value) {
    return [System.Text.Json.JsonSerializer]::Serialize($Value, $script:JsonOptions)
}

function Test-Container($Node) {
    return ($Node -is [System.Collections.IDictionary]) -or (($Node -is [System.Collections.IEnumerable]) -and ($Node -isnot [string]))
}

function Get-Inline($Node) {
    if ($null -eq $Node) { return 'null' }
    if ($Node -is [bool]) { return $(if ($Node) { 'true' } else { 'false' }) }
    if ($Node -is [string]) { return ConvertTo-JsonString $Node }
    if ($Node -is [int] -or $Node -is [long] -or $Node -is [double]) { return [string]::Format([cultureinfo]::InvariantCulture, '{0}', $Node) }
    if ($Node -is [System.Collections.IDictionary]) {
        if ($Node.Count -eq 0) { return '{}' }
        $parts = foreach ($key in $Node.Keys) { (ConvertTo-JsonString $key) + ': ' + (Get-Inline $Node[$key]) }
        return '{ ' + ($parts -join ', ') + ' }'
    }
    $items = @($Node)
    if ($items.Count -eq 0) { return '[]' }
    $parts = foreach ($item in $items) { Get-Inline $item }
    return '[ ' + ($parts -join ', ') + ' ]'
}

function Format-Node($Node, [int]$Level, [int]$PrefixLength, [switch]$Force) {
    $inline = Get-Inline $Node
    if (-not (Test-Container $Node)) { return $inline }
    if (-not $Force -and (($Level * 2) + $PrefixLength + $inline.Length) -le 150) { return $inline }
    if ($Node -is [System.Collections.IDictionary] -and $Node.Count -eq 0) { return '{}' }

    $pad = ' ' * (($Level + 1) * 2)
    $end = ' ' * ($Level * 2)
    if ($Node -is [System.Collections.IDictionary]) {
        $lines = foreach ($key in $Node.Keys) {
            $json = ConvertTo-JsonString $key
            $pad + $json + ': ' + (Format-Node $Node[$key] ($Level + 1) ($json.Length + 2))
        }
        return "{`n" + ($lines -join ",`n") + "`n" + $end + '}'
    }

    $lines = foreach ($item in @($Node)) { $pad + (Format-Node $item ($Level + 1) 0) }
    return "[`n" + ($lines -join ",`n") + "`n" + $end + ']'
}

# ---------------------------------------------------------------------------------------------------------------
# Schema DSL. Every helper returns an already wrapped property schema (anyOf: [typed, expression]).
# ---------------------------------------------------------------------------------------------------------------

$script:Expression = [ordered]@{ '$ref' = '#/$defs/expression' }
$script:GuidPattern = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'

function Wrap($Typed, [string]$Description) {
    $schema = [ordered]@{}
    if ($Description) { $schema['description'] = $Description }
    $schema['anyOf'] = @($Typed, $script:Expression)
    return $schema
}

function Str([string]$d, [string[]]$Enum, [string]$Pattern, [switch]$Nullable, [int]$Min = -1, [int]$Max = -1) {
    $typed = [ordered]@{ type = $(if ($Nullable) { @('string', 'null') } else { 'string' }) }
    if ($Enum) { $values = @($Enum); if ($Nullable) { $values += $null }; $typed['enum'] = $values }
    if ($Pattern) { $typed['pattern'] = $Pattern }
    if ($Min -ge 0) { $typed['minLength'] = $Min }
    if ($Max -ge 0) { $typed['maxLength'] = $Max }
    return Wrap $typed $d
}

function Bool([string]$d, [switch]$Nullable) {
    return Wrap ([ordered]@{ type = $(if ($Nullable) { @('boolean', 'null') } else { 'boolean' }) }) $d
}

function Int([string]$d, [int]$Min = [int]::MinValue, [int]$Max = [int]::MinValue, [int[]]$Enum, [switch]$Nullable) {
    $typed = [ordered]@{ type = $(if ($Nullable) { @('integer', 'null') } else { 'integer' }) }
    if ($Enum) { $values = @($Enum); if ($Nullable) { $values += $null }; $typed['enum'] = $values }
    if ($Min -ne [int]::MinValue) { $typed['minimum'] = $Min }
    if ($Max -ne [int]::MinValue) { $typed['maximum'] = $Max }
    return Wrap $typed $d
}

function Obj([string]$d, [System.Collections.IDictionary]$Props, [string[]]$Required, [switch]$Nullable, [switch]$Closed, [System.Collections.IDictionary]$Extra) {
    $typed = [ordered]@{ type = $(if ($Nullable) { @('object', 'null') } else { 'object' }) }
    if ($Props) { $typed['properties'] = $Props }
    if ($Required) { $typed['required'] = $Required }
    if ($Closed) { $typed['additionalProperties'] = $false }
    if ($Extra) { foreach ($key in $Extra.Keys) { $typed[$key] = $Extra[$key] } }
    return Wrap $typed $d
}

# A free-form object (Graph passthrough).
function FreeObj([string]$d, [switch]$Nullable) {
    return Wrap ([ordered]@{ type = $(if ($Nullable) { @('object', 'null') } else { 'object' }) }) $d
}

function Arr([string]$d, $Items, [int]$Min = -1, [switch]$Unique, [switch]$Nullable) {
    $typed = [ordered]@{ type = $(if ($Nullable) { @('array', 'null') } else { 'array' }) }
    if ($null -ne $Items) { $typed['items'] = $Items }
    if ($Min -ge 0) { $typed['minItems'] = $Min }
    return Wrap $typed $d
}

function StrArr([string]$d, [string[]]$Enum, [string]$Pattern, [switch]$Unique, [switch]$Nullable, [int]$Min = -1) {
    return Arr -d $d -Items (Str '' -Enum $Enum -Pattern $Pattern) -Unique:$Unique -Nullable:$Nullable -Min $Min
}

function Guid([string]$d, [switch]$Nullable) {
    return Str $d -Pattern $script:GuidPattern -Nullable:$Nullable
}

function Props { param([System.Collections.IDictionary]$Map) return $Map }

# ---------------------------------------------------------------------------------------------------------------
# Edge properties. Ids are plain strings: object ids, or expressions that evaluate to them.
# ---------------------------------------------------------------------------------------------------------------

function EdgeIds([string]$d) { return StrArr $d -Unique }

function EdgeManager {
    return Str 'Manager edge: the manager''s user object id (reference(...).id); null removes the manager, absent leaves it untouched.' -Nullable
}

function EdgeAppRoleAssignments {
    return Arr -d 'App role assignment edge, principal side (applied by the edge pass, tracked in state): [{ resourceId | resourceAppId, appRole | appRoleId }]. appRole is the role''s value, resolved against the resource service principal; 00000000-0000-0000-0000-000000000000 is default access.' -Items (Obj '' -Closed -Props (Props ([ordered]@{
        resourceId    = Str 'Object id of the resource service principal.'
        resourceAppId = Str 'Application (client) id of the resource; resolved to its service principal.'
        appRole       = Str 'Value of the app role on the resource, for example Orders.Admin.'
        appRoleId     = Str 'Id of the app role (00000000-0000-0000-0000-000000000000 = default access).'
    })) -Extra ([ordered]@{ allOf = @(
        [ordered]@{ anyOf = @([ordered]@{ required = @('resourceId') }, [ordered]@{ required = @('resourceAppId') }) },
        [ordered]@{ anyOf = @([ordered]@{ required = @('appRole') }, [ordered]@{ required = @('appRoleId') }) }) }))
}

function EdgeDirectoryRoles([string]$Who) {
    return Arr -d "Directory role assignment edge, principal side$Who (applied by the edge pass, tracked in state): [{ role | roleDefinitionId, directoryScopeId | administrativeUnit }]. role is a role template id, a built-in role display name, a custom role display name or its id. The scope defaults to '/' (tenant)." -Items (Obj '' -Closed -Props (Props ([ordered]@{
        role              = Str 'Role template id, built-in role name (Global Reader), custom role display name or custom role id.'
        roleDefinitionId  = Str 'The role template id (roleTemplate(''Display Name'')); use role for names.'
        directoryScopeId  = Str 'Scope: ''/'' (tenant, default), ''/administrativeUnits/{id}'' or ''/{applicationObjectId}''.' -Pattern '^/'
        administrativeUnit = Str 'Sugar for an administrative-unit scope: the unit''s object id or its template name (reference(''Orders AU'').id).'
    })) -Extra ([ordered]@{ anyOf = @([ordered]@{ required = @('role') }, [ordered]@{ required = @('roleDefinitionId') }) }))
}

function EdgeLicenses([string]$Who) {
    return Arr -d "Licenses$Who (action-backed, direct assignments only; group-inherited licenses are never touched): [{ skuId | skuPartNumber, disabledPlans }]." -Items (Obj '' -Closed -Props (Props ([ordered]@{
        skuId         = Str 'Subscribed SKU id.'
        skuPartNumber = Str 'SKU part number, for example ENTERPRISEPREMIUM; resolved with GET /subscribedSkus.'
        disabledPlans = StrArr 'Service plan ids to disable within the SKU.' -Unique
    })) -Extra ([ordered]@{ anyOf = @([ordered]@{ required = @('skuId') }, [ordered]@{ required = @('skuPartNumber') }) }))
}

# ---------------------------------------------------------------------------------------------------------------
# Per-type vocabularies
# ---------------------------------------------------------------------------------------------------------------

$uniqueNamePattern = '^\S(.{0,254}\S)?$'

$specs = [ordered]@{}

# --- applications -------------------------------------------------------------------------------------------------
$specs['applications'] = [ordered]@{
    NamePattern = $uniqueNamePattern
    Description = 'An application registration. The resource name is the Graph uniqueName (upsert via PATCH /applications(uniqueName=''...'') with Prefer: create-if-missing). Properties not listed here are passed through to Graph unchanged.'
    Props       = [ordered]@{
        displayName            = Str 'Required on create.'
        uniqueName             = Str 'Set from the resource name; declaring it is only allowed when it equals the name.'
        appId                  = Str 'Lookup key only (never written): adopts a brownfield application whose uniqueName is still unset.' -Pattern $script:GuidPattern
        description            = Str 'Free-text description (up to 1024 characters).' -Nullable
        signInAudience         = Str 'Who can sign in. Defaults to AzureADMyOrg.' -Enum 'AzureADMyOrg', 'AzureADMultipleOrgs', 'AzureADandPersonalMicrosoftAccount', 'PersonalMicrosoftAccount'
        identifierUris         = StrArr 'Identifier URIs; api://{appId} is written by a second PATCH after the application exists.' -Unique
        appRoles               = Arr -d 'App roles, matched by id; an omitted id is derived deterministically (UUIDv5 of uniqueName and value) and recorded in state. value must be unique across appRoles and oauth2PermissionScopes.' -Items (Obj '' -Props (Props ([ordered]@{
            id                 = Guid 'Role id (GUID); derived when omitted.'
            allowedMemberTypes = StrArr 'Who can be assigned the role.' -Enum 'User', 'Application' -Unique
            description        = Str 'Description shown in assignment UIs.'
            displayName        = Str 'Display name shown in assignment UIs.'
            isEnabled          = Bool 'Defaults to true; removed roles are disabled first, then removed.'
            value              = Str 'The value emitted in the roles claim (no spaces).'
            origin             = Str 'Server-managed (Application or ServicePrincipal); ignored.'
        })))
        api                    = Obj -d 'API settings of the application.' -Props (Props ([ordered]@{
            acceptMappedClaims           = Bool 'Accept claims mapping policies.' -Nullable
            knownClientApplications      = Arr 'Client application ids bundled with this API for combined consent.' (Guid '')
            requestedAccessTokenVersion  = Int 'Access token version: 1 or 2 (null is treated as 1).' -Enum 1, 2 -Nullable
            oauth2PermissionScopes       = Arr -d 'Delegated permission scopes, matched by id.' -Items (Obj '' -Props (Props ([ordered]@{
                id                       = Guid 'Scope id (GUID); derived when omitted.'
                adminConsentDescription  = Str 'Description shown on the admin consent screen.'
                adminConsentDisplayName  = Str 'Title shown on the admin consent screen.'
                isEnabled                = Bool 'Defaults to true.'
                type                     = Str 'Who can consent: User or Admin.' -Enum 'User', 'Admin'
                userConsentDescription   = Str 'Description shown on the user consent screen.' -Nullable
                userConsentDisplayName   = Str 'Title shown on the user consent screen.' -Nullable
                value                    = Str 'The scope value emitted in the scp claim (no spaces).'
                origin                   = Str 'Server-managed; ignored.'
            })))
            preAuthorizedApplications    = Arr -d 'Client applications authorised to request delegated scopes without consent.' -Items (Obj '' -Props (Props ([ordered]@{
                appId                    = Guid 'The pre-authorised client application id.'
                delegatedPermissionIds   = Arr 'Ids of the oauth2PermissionScopes the client is pre-authorised for.' (Guid '')
            })) -Required 'appId')
        }))
        web                    = Obj -d 'Web platform settings.' -Props (Props ([ordered]@{
            homePageUrl            = Str 'Home page URL.' -Nullable
            logoutUrl              = Str 'Front-channel logout URL.' -Nullable
            redirectUris           = StrArr 'Redirect URIs of the web platform.' -Unique
            implicitGrantSettings  = Obj -d 'Implicit grant flows.' -Props (Props ([ordered]@{
                enableAccessTokenIssuance = Bool 'Issue access tokens from the authorize endpoint.'
                enableIdTokenIssuance     = Bool 'Issue ID tokens from the authorize endpoint.'
            }))
        }))
        spa                    = Obj -d 'Single-page application platform settings.' -Props (Props ([ordered]@{
            redirectUris           = StrArr 'Redirect URIs of the SPA platform.' -Unique
        }))
        publicClient           = Obj -d 'Mobile and desktop (public client) platform settings.' -Props (Props ([ordered]@{
            redirectUris           = StrArr 'Redirect URIs of the public client platform.' -Unique
        }))
        requiredResourceAccess = Arr -d 'Permissions the application requests, per resource application.' -Items (Obj '' -Required 'resourceAppId', 'resourceAccess' -Props (Props ([ordered]@{
            resourceAppId  = Str 'Application (client) id of the resource, for example wellKnownApp(''MicrosoftGraph'').'
            resourceAccess = Arr -d 'The requested permissions.' -Items (Obj '' -Required 'id', 'type' -Props (Props ([ordered]@{
                id   = Str 'Permission id (appRole(...) / permissionScope(...) resolve it from a name).'
                type = Str 'Scope = delegated permission, Role = application permission.' -Enum 'Scope', 'Role'
            })))
        })))
        optionalClaims         = Obj -d 'Optional claims per token type.' -Nullable -Props (Props ([ordered]@{
            accessToken = Arr 'Optional claims in access tokens.' (FreeObj '')
            idToken     = Arr 'Optional claims in ID tokens.' (FreeObj '')
            saml2Token  = Arr 'Optional claims in SAML 2 tokens.' (FreeObj '')
        }))
        groupMembershipClaims  = Str 'Group claims to emit: None, SecurityGroup, All, DirectoryRole, ApplicationGroup, or a comma-separated combination.' -Nullable -Pattern '^(None|SecurityGroup|All|DirectoryRole|ApplicationGroup)(\s*,\s*(None|SecurityGroup|All|DirectoryRole|ApplicationGroup))*$'
        tags                   = StrArr 'Custom tags.' -Unique
        notes                  = Str 'Free-text notes.' -Nullable
        info                   = Obj -d 'Branding and support links.' -Props (Props ([ordered]@{
            marketingUrl           = Str 'Marketing page URL.' -Nullable
            privacyStatementUrl    = Str 'Privacy statement URL.' -Nullable
            supportUrl             = Str 'Support URL.' -Nullable
            termsOfServiceUrl      = Str 'Terms of service URL.' -Nullable
        }))
        isFallbackPublicClient = Bool 'Treat the application as a public client when no redirect URI matches.' -Nullable
        keyCredentials         = Arr -d 'Certificates and keys (passthrough; only diffed when declared).' -Items (FreeObj '')
        owners                 = EdgeIds 'Ownership edge, target side: the full desired set of owner object ids (reference(...).id). Owners not listed are removed.'
    }
}

# --- applications/federatedIdentityCredentials --------------------------------------------------------------------
$specs['applications.federatedIdentityCredentials'] = [ordered]@{
    NamePattern = '^[A-Za-z0-9][A-Za-z0-9_-]{2,119}$'
    Description = 'A federated identity credential of an application. The child name is the credential''s Graph name (3-120 characters: letters, digits, ''-'' and ''_'', starting with a letter or digit). Upsert via PATCH /applications(uniqueName=''parent'')/federatedIdentityCredentials(name=''child'') with Prefer: create-if-missing.'
    Props       = [ordered]@{
        name        = Str 'Set from the resource name; declaring it is only allowed when it equals the name.'
        issuer      = Str 'Required: the https issuer URL of the external identity provider.' -Min 1
        subject     = Str 'Required: the subject claim (case-sensitive); issuer + subject must be unique per application.' -Min 1
        audiences   = StrArr 'Defaults to ["api://AzureADTokenExchange"].' -Unique
        description = Str 'Description (up to 600 characters).' -Nullable
        claimsMatchingExpression = Obj -d 'Flexible federated identity credential expression. Beta apiVersion (2026-10-01-preview.beta) only.' -Nullable -Props (Props ([ordered]@{
            languageVersion = Int 'Expression language version (1).' -Enum 1
            value           = Str 'The claims matching expression.'
        }))
    }
}

# --- applications/passwordCredentials -----------------------------------------------------------------------------
$specs['applications.passwordCredentials'] = [ordered]@{
    Description = 'A client secret of an application. The child name is the credential name (must not contain ''#''); the live displayName is ''<name>#<rotationTag>'' when rotationTag is declared, else the name. Secrets are created with addPassword and removed with removePassword (never edited); the secret text is returned once as a securestring output.'
    Props       = [ordered]@{
        displayName      = Str 'Set from the resource name; declaring it is only allowed when it equals the name.'
        startDateTime    = Str 'Start of validity (ISO 8601, UTC); defaults to now.'
        endDateTime      = Str 'Expiry (ISO 8601, UTC); defaults to 180 days after creation, evaluated once at create.'
        rotationTag      = Str 'EARM-only: changing this value rotates the secret (the new one is added before the old one is removed). Stored in the credential''s displayName as ''<name>#<rotationTag>'' (no local state); must not contain ''#'', and the whole displayName is capped at 256 characters. An existing credential named without a tag is treated as matching (warning RotationTagNotStamped) until its next rotation.'
        rotateBeforeDays = Int 'EARM-only: rotate when fewer than this many days remain (default 30). Never sent to Graph.' -Min 0
        stampRotationTag = Bool 'EARM-only, opt-in (default false): when true and the live credential''s displayName carries no rotationTag (it was created before EARM kept the tag there), the secret is rotated once so the declared rotationTag is stamped into the new credential''s name; without it such a credential is kept (warning RotationTagNotStamped). Needs rotationTag; no effect once the tag is stamped. Never sent to Graph.'
    }
}

# --- servicePrincipals ---------------------------------------------------------------------------------------------
$specs['servicePrincipals'] = [ordered]@{
    NamePattern = '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
    Description = 'A service principal. The resource name is the application (client) id; the principal is read via GET /servicePrincipals(appId=''...'') and created with POST /servicePrincipals { appId }. Properties not listed here are passed through to Graph unchanged.'
    Props       = [ordered]@{
        appId                      = Str 'Set from the resource name; declaring it is only allowed when it equals the name.' -Pattern $script:GuidPattern
        displayName                = Str 'Display name (defaults to the application''s).'
        description                = Str 'Description shown to users.' -Nullable
        accountEnabled             = Bool 'Defaults to true.'
        appRoleAssignmentRequired  = Bool 'Defaults to false: users and groups must be assigned a role to sign in.'
        tags                       = StrArr 'Custom tags (WindowsAzureActiveDirectoryIntegratedApp makes the application show in the portal).' -Unique
        notes                      = Str 'Free-text notes.' -Nullable
        loginUrl                   = Str 'Sign-in URL for SSO from My Apps.' -Nullable
        logoutUrl                  = Str 'Sign-out URL.' -Nullable
        homepage                   = Str 'Home page URL.' -Nullable
        preferredSingleSignOnMode  = Str 'Single sign-on mode.' -Nullable -Enum 'oidc', 'password', 'saml', 'notSupported'
        notificationEmailAddresses = StrArr 'Addresses notified before the SAML signing certificate expires.' -Unique
        replyUrls                  = StrArr 'Reply URLs.' -Unique
        alternativeNames           = StrArr 'Alternative names.' -Unique
        samlSingleSignOnSettings   = Obj -d 'SAML single sign-on settings.' -Nullable -Props (Props ([ordered]@{
            relayState = Str 'Relay state of the SAML response.' -Nullable
        }))
        owners                     = EdgeIds 'Ownership edge, target side: the full desired set of owner object ids.'
        memberOf                   = EdgeIds 'Group membership edge, principal side: group object ids this principal must belong to (never dynamic groups).'
        ownerOf                    = EdgeIds 'Ownership edge, principal side: object ids of applications, service principals or groups this principal must own.'
        appRoleAssignments         = EdgeAppRoleAssignments
        directoryRoles             = EdgeDirectoryRoles ''
    }
}

# --- groups ---------------------------------------------------------------------------------------------------------
$specs['groups'] = [ordered]@{
    NamePattern = $uniqueNamePattern
    Description = 'A security, Microsoft 365, dynamic or role-assignable group. The resource name is the Graph uniqueName. Mail-enabled security groups cannot be created through Graph (adopt only). Properties not listed here are passed through to Graph unchanged.'
    Props       = [ordered]@{
        displayName                   = Str 'Required on create.' -Min 1 -Max 256
        uniqueName                    = Str 'Set from the resource name; declaring it is only allowed when it equals the name.'
        description                   = Str 'Description (up to 1024 characters).' -Nullable
        mailEnabled                   = Bool 'Required on create; immutable. True for Microsoft 365 groups.'
        mailNickname                  = Str 'Mail alias (up to 64 characters; none of @ ( ) \ [ ] " ; : . < > , or space). Derived from the name when omitted; can be changed later (Graph group update).' -Pattern '^[^@()\\\[\]";:.<>, ]+$' -Max 64
        securityEnabled               = Bool 'Required on create. True for security groups.'
        groupTypes                    = StrArr '[] security group, ["Unified"] Microsoft 365 group, plus "DynamicMembership" for dynamic groups. The Unified presence is immutable (no conversion between a security group and a Microsoft 365 group); DynamicMembership can be added or removed.' -Enum 'Unified', 'DynamicMembership' -Unique
        isAssignableToRole            = Bool 'Role-assignable group (immutable; never dynamic; securityEnabled must be true, and a Microsoft 365 group visibility Private - both defaulted on create; required for directoryRoles; up to 500 per tenant).' -Nullable
        visibility                    = Str 'Public, Private or HiddenMembership. Public and Private can be changed later; HiddenMembership can only be set at creation and never changed.' -Nullable -Enum 'Public', 'Private', 'HiddenMembership'
        membershipRule                = Str 'Dynamic membership rule (requires groupTypes to contain DynamicMembership; Entra ID P1).' -Nullable
        membershipRuleProcessingState = Str 'On (default for dynamic groups) or Paused.' -Enum 'On', 'Paused'
        members                       = EdgeIds 'Group membership edge, target side: the full desired set of member object ids (users, service principals, groups). Not allowed on dynamic groups; members not listed are removed.'
        owners                        = EdgeIds 'Ownership edge, target side: the full desired set of owner object ids.'
        memberOf                      = EdgeIds 'Nested group membership edge, principal side: group object ids this group must be a member of.'
        administrativeUnits           = EdgeIds 'Administrative unit membership edge, principal side: administrative unit object ids.'
        appRoleAssignments            = EdgeAppRoleAssignments
        directoryRoles                = EdgeDirectoryRoles ' (role-assignable groups only)'
        assignedLicenses              = EdgeLicenses ' for group-based licensing'
    }
}

# --- users ----------------------------------------------------------------------------------------------------------
$specs['users'] = [ordered]@{
    NamePattern = '^[^@\s]+@[^@\s]+$'
    Description = 'A member user. The resource name is the userPrincipalName. passwordProfile is create-only and never diffed; when omitted a random password is generated and returned as a securestring output. Properties not listed here are passed through to Graph unchanged.'
    Props       = [ordered]@{
        userPrincipalName  = Str 'Set from the resource name; declaring it is only allowed when it equals the name.'
        displayName        = Str 'Required on create.' -Min 1 -Max 256
        accountEnabled     = Bool 'Defaults to true.'
        mailNickname       = Str 'Defaults to the sanitised local part of the UPN.'
        givenName          = Str 'First name.' -Nullable
        surname            = Str 'Last name.' -Nullable
        jobTitle           = Str 'Job title.' -Nullable
        department         = Str 'Department.' -Nullable
        companyName        = Str 'Company name.' -Nullable
        officeLocation     = Str 'Office location.' -Nullable
        usageLocation      = Str 'Two-letter upper-case ISO 3166-1 country code; required for assignedLicenses.' -Nullable -Pattern '^[A-Z]{2}$'
        userType           = Str 'Member or Guest. EARM creates members; guests come from invitations.' -Enum 'Member', 'Guest'
        otherMails         = StrArr 'Additional email addresses.' -Unique
        businessPhones     = StrArr 'Business phone numbers (at most one).' -Unique
        mobilePhone        = Str 'Mobile phone.' -Nullable
        employeeId         = Str 'Employee id.' -Nullable
        employeeType       = Str 'Employee type.' -Nullable
        preferredLanguage  = Str 'Preferred language (ISO 639-1 with optional ISO 3166-1 region).' -Nullable
        city               = Str 'City.' -Nullable
        state              = Str 'State or province.' -Nullable
        country            = Str 'Country or region.' -Nullable
        postalCode         = Str 'Postal code.' -Nullable
        streetAddress      = Str 'Street address.' -Nullable
        passwordProfile    = Obj -d 'Create-only, write-only, never diffed. password is secure (rendered *** everywhere).' -Props (Props ([ordered]@{
            password                             = Str 'The initial password; use a securestring parameter or a Key Vault reference.'
            forceChangePasswordNextSignIn        = Bool 'Defaults to true.'
            forceChangePasswordNextSignInWithMfa = Bool 'Require MFA before the password change.'
        }))
        manager            = EdgeManager
        memberOf           = EdgeIds 'Group membership edge, principal side: group object ids (never dynamic groups).'
        ownerOf            = EdgeIds 'Ownership edge, principal side: object ids of applications, service principals or groups this user must own.'
        administrativeUnits = EdgeIds 'Administrative unit membership edge, principal side: administrative unit object ids.'
        appRoleAssignments = EdgeAppRoleAssignments
        directoryRoles     = EdgeDirectoryRoles ''
        assignedLicenses   = EdgeLicenses ' (requires usageLocation)'
    }
}

# --- roleDefinitions ------------------------------------------------------------------------------------------------
$specs['roleDefinitions'] = [ordered]@{
    NamePattern = $uniqueNamePattern
    Description = 'A custom directory role definition. The resource name is the displayName; it is looked up by displayName on every run (more than one match is an error). Built-in roles cannot be managed.'
    Props       = [ordered]@{
        displayName     = Str 'Set from the resource name; declaring it is only allowed when it equals the name.'
        description     = Str 'Description.' -Nullable
        isEnabled       = Bool 'Defaults to true.'
        resourceScopes  = StrArr 'Only "/" (tenant) is supported.' -Unique
        rolePermissions = Arr -d 'Required. The permissions granted; replaced whole on update. Allowed actions look like microsoft.directory/users/basic/update (the list is not enforced offline).' -Items (Obj '' -Required 'allowedResourceActions' -Props (Props ([ordered]@{
            allowedResourceActions  = StrArr 'Resource actions the role allows.' -Unique
            excludedResourceActions = StrArr 'Resource actions removed from the allowed set.' -Unique
            condition               = Str 'Optional condition on the permission.' -Nullable
        })))
    }
}

# --- conditionalAccess/authenticationContextClassReferences -------------------------------------------------------
$specs['conditionalAccess.authenticationContextClassReferences'] = [ordered]@{
    Description = 'An authentication context class reference. The resource name is the Graph id (c1..c99). Create and update are both PATCH; delete requires isAvailable: false first.'
    Props       = [ordered]@{
        id          = Str 'Set from the resource name (c1..c99); declaring it is only allowed when it equals the name.' -Pattern '^c([1-9]|[1-9][0-9])$'
        displayName = Str 'Required on create.'
        description = Str 'Description.' -Nullable
        isAvailable = Bool 'Defaults to true.'
    }
}

# --- conditionalAccess/authenticationStrengths --------------------------------------------------------------------
$specs['conditionalAccess.authenticationStrengths'] = [ordered]@{
    NamePattern = $uniqueNamePattern
    Description = 'A custom authentication strength policy. The resource name is the displayName (at most 30 characters); built-in strengths are read-only and can only be referenced.'
    Props       = [ordered]@{
        displayName         = Str 'Set from the resource name; declaring it is only allowed when it equals the name. At most 30 characters (Graph: "Policy DisplayName length cannot be longer than 30 characters").' -Max 30
        description         = Str 'Description.' -Nullable
        allowedCombinations = Arr -d 'Required: authentication method combinations, compared as a set. A combination is one method or a comma-separated list: password, voice, hardwareOath, softwareOath, sms, fido2, windowsHelloForBusiness, microsoftAuthenticatorPush, deviceBasedPush, temporaryAccessPassOneTime, temporaryAccessPassMultiUse, email, x509CertificateSingleFactor, x509CertificateMultiFactor, federatedSingleFactor, federatedMultiFactor, for example fido2 or password,microsoftAuthenticatorPush.' -Unique -Items (Str '' -Min 1)
    }
}

# --- appRoleAssignments (standalone) --------------------------------------------------------------------------------
$specs['appRoleAssignments'] = [ordered]@{
    NamePattern = $script:GuidPattern
    Description = 'An app role assignment (principal -> app role on a resource service principal). The resource name is a deterministic GUID chosen with guid(); the object is matched by (principalId, resourceId, appRoleId) and looked up live on every run. Equivalent to an appRoleAssignments entry on the principal.'
    Props       = [ordered]@{
        principalId   = Str 'Required: object id of the user, group or service principal.'
        resourceId    = Str 'Object id of the resource service principal (or use resourceAppId).'
        resourceAppId = Str 'Sugar: application (client) id of the resource; resolved to resourceId.'
        appRoleId     = Str 'The app role id (00000000-0000-0000-0000-000000000000 for default access), or use appRole.'
        appRole       = Str 'Sugar: the app role''s value, for example Orders.Admin; resolved to appRoleId.'
    }
}

# --- oauth2PermissionGrants -----------------------------------------------------------------------------------------
$specs['oauth2PermissionGrants'] = [ordered]@{
    NamePattern = $script:GuidPattern
    Description = 'A delegated permission grant (admin consent). The resource name is a deterministic GUID chosen with guid(); the object is matched by (clientId, resourceId, consentType, principalId). Only scope is mutable.'
    Props       = [ordered]@{
        clientId    = Str 'Required: object id of the client service principal.'
        resourceId  = Str 'Required: object id of the resource service principal.'
        consentType = Str 'Required: AllPrincipals (admin consent for everyone) or Principal (one user).' -Enum 'AllPrincipals', 'Principal'
        principalId = Str 'Required when consentType is Principal; null for AllPrincipals.' -Nullable
        scope       = Str 'Space-separated scope values, for example ''User.Read openid''. Use scope or scopes.'
        scopes      = StrArr 'Sugar for scope: the scope values; validated against the resource''s oauth2PermissionScopes.' -Unique
    }
}

# --- conditionalAccess/namedLocations -------------------------------------------------------------------------------
$specs['conditionalAccess.namedLocations'] = [ordered]@{
    NamePattern = $uniqueNamePattern
    Description = 'An IP or country named location. The resource name is the displayName; it is looked up by displayName on every run (more than one match is an error). @odata.type selects the kind and is immutable (a kind change recreates the location).'
    Props       = [ordered]@{
        displayName = Str 'Set from the resource name; declaring it is only allowed when it equals the name.'
        '@odata.type' = Str 'Required: #microsoft.graph.ipNamedLocation or #microsoft.graph.countryNamedLocation.' -Enum '#microsoft.graph.ipNamedLocation', '#microsoft.graph.countryNamedLocation'
        isTrusted   = Bool 'IP locations only; defaults to false.'
        ipRanges    = Arr -d 'IP locations: CIDR ranges ({ cidrAddress }); the IPv4/IPv6 @odata.type is inferred when omitted. Host bits must be zero.' -Items (Obj '' -Required 'cidrAddress' -Props (Props ([ordered]@{
            '@odata.type' = Str 'Range family.' -Enum '#microsoft.graph.iPv4CidrRange', '#microsoft.graph.iPv6CidrRange'
            cidrAddress   = Str 'CIDR block, for example 203.0.113.0/24 or 2001:db8::/32.' -Min 1
        })))
        countriesAndRegions               = StrArr 'Country locations: ISO 3166-1 alpha-2 codes.' -Unique
        includeUnknownCountriesAndRegions = Bool 'Country locations: include IP addresses that cannot be mapped to a country. Defaults to false.'
        countryLookupMethod               = Str 'Country locations: how the country is determined. Defaults to clientIpAddress.' -Enum 'clientIpAddress', 'authenticatorAppGps'
    }
}

# --- conditionalAccess/policies -------------------------------------------------------------------------------------
$riskLevels = 'low', 'medium', 'high', 'hidden', 'none', 'unknownFutureValue'
$filterRule = { param($what) Obj -d "Filter on $what." -Nullable -Required 'mode', 'rule' -Props (Props ([ordered]@{
    mode = Str 'include or exclude.' -Enum 'include', 'exclude'
    rule = Str 'The filter rule expression.' -Min 1
})) }

$specs['conditionalAccess.policies'] = [ordered]@{
    NamePattern = $uniqueNamePattern
    Description = 'A conditional access policy. The resource name is the displayName; it is looked up by displayName on every run (more than one match is an error). state: enabled is applied only with --allow-ca-enforce; without it a new policy is created report-only and an existing policy keeps its live state. Reference users and groups by object id, applications by application (client) id, locations by named location id, roles by role template id.'
    Props       = [ordered]@{
        displayName   = Str 'Set from the resource name; declaring it is only allowed when it equals the name.'
        state         = Str 'Defaults to enabledForReportingButNotEnforced on create. enabled is applied only with --allow-ca-enforce (otherwise report-only on create, live state kept on update).' -Enum 'enabled', 'disabled', 'enabledForReportingButNotEnforced'
        conditions    = Obj -d 'Required: conditions.users and conditions.applications must each include at least one entry; clientAppTypes defaults to ["all"].' -Props (Props ([ordered]@{
            clientAppTypes   = StrArr 'Client app types the policy applies to.' -Enum 'all', 'browser', 'mobileAppsAndDesktopClients', 'exchangeActiveSync', 'easSupported', 'other' -Unique
            users            = Obj -d 'Users the policy applies to.' -Props (Props ([ordered]@{
                includeUsers  = StrArr 'User object ids, or All, None, GuestsOrExternalUsers.' -Unique
                excludeUsers  = StrArr 'User object ids, or GuestsOrExternalUsers.' -Unique
                includeGroups = StrArr 'Group object ids.' -Unique
                excludeGroups = StrArr 'Group object ids.' -Unique
                includeRoles  = StrArr 'Directory role template ids.' -Unique
                excludeRoles  = StrArr 'Directory role template ids.' -Unique
                includeGuestsOrExternalUsers = FreeObj 'Guest or external user types (guestOrExternalUserTypes, externalTenants).' -Nullable
                excludeGuestsOrExternalUsers = FreeObj 'Guest or external user types (guestOrExternalUserTypes, externalTenants).' -Nullable
            }))
            applications     = Obj -d 'Cloud apps, user actions or authentication contexts the policy applies to.' -Props (Props ([ordered]@{
                includeApplications = StrArr 'Application (client) ids, or All, None, Office365, MicrosoftAdminPortals.' -Unique
                excludeApplications = StrArr 'Application (client) ids, or Office365, MicrosoftAdminPortals.' -Unique
                includeUserActions  = StrArr 'urn:user:registersecurityinfo or urn:user:registerdevice.' -Unique
                includeAuthenticationContextClassReferences = StrArr 'Authentication context ids (c1..c99); reference a declared one by its name.' -Unique
                applicationFilter   = & $filterRule 'application custom security attributes'
            }))
            userRiskLevels          = StrArr 'User risk levels (Entra ID P2).' -Enum $riskLevels -Unique
            signInRiskLevels        = StrArr 'Sign-in risk levels (Entra ID P2).' -Enum $riskLevels -Unique
            servicePrincipalRiskLevels = StrArr 'Service principal risk levels (workload identities).' -Enum $riskLevels -Unique
            platforms        = Obj -d 'Device platforms.' -Nullable -Props (Props ([ordered]@{
                includePlatforms = StrArr 'Included platforms.' -Enum 'android', 'iOS', 'windows', 'windowsPhone', 'macOS', 'linux', 'all', 'unknownFutureValue' -Unique
                excludePlatforms = StrArr 'Excluded platforms.' -Enum 'android', 'iOS', 'windows', 'windowsPhone', 'macOS', 'linux', 'all', 'unknownFutureValue' -Unique
            }))
            locations        = Obj -d 'Named locations.' -Nullable -Props (Props ([ordered]@{
                includeLocations = StrArr 'Named location ids, or All, AllTrusted.' -Unique
                excludeLocations = StrArr 'Named location ids, or AllTrusted.' -Unique
            }))
            devices          = Obj -d 'Device filter.' -Nullable -Props (Props ([ordered]@{
                deviceFilter = & $filterRule 'device attributes'
            }))
            clientApplications = Obj -d 'Workload identities (service principals) the policy applies to.' -Nullable -Props (Props ([ordered]@{
                includeServicePrincipals = StrArr 'Service principal object ids, or ServicePrincipalsInMyTenant.' -Unique
                excludeServicePrincipals = StrArr 'Service principal object ids.' -Unique
                servicePrincipalFilter   = & $filterRule 'service principal attributes'
            }))
        }))
        grantControls = Obj -d 'Required unless sessionControls is set. operator defaults to OR; block must be alone; mfa and authenticationStrength are mutually exclusive. null removes the control.' -Nullable -Props (Props ([ordered]@{
            operator                      = Str 'AND or OR (default OR).' -Enum 'AND', 'OR'
            builtInControls               = StrArr 'Built-in controls to require. riskRemediation is an evolvable-enum member that Graph returns only to a read sent with Prefer: include-unknown-enum-members (EARM sends it).' -Enum 'block', 'mfa', 'compliantDevice', 'domainJoinedDevice', 'approvedApplication', 'compliantApplication', 'passwordChange', 'unknownFutureValue', 'riskRemediation' -Unique
            customAuthenticationFactors   = StrArr 'Custom controls (ids).' -Unique
            termsOfUse                    = StrArr 'Terms of use ids.' -Unique
            authenticationStrength        = Obj -d 'Authentication strength policy: reference a declared one with { "id": "[reference(''name'').id]" } or a built-in id (00000000-0000-0000-0000-000000000002 MFA, ...0003 passwordless, ...0004 phishing-resistant).' -Required 'id' -Props (Props ([ordered]@{
                id = Str 'The authentication strength policy id.' -Min 1
            }))
        }))
        sessionControls = Obj -d 'Session controls; required when grantControls is not set. null removes the control.' -Nullable -Props (Props ([ordered]@{
            applicationEnforcedRestrictions = Obj -d 'Use app-enforced restrictions.' -Nullable -Props (Props ([ordered]@{ isEnabled = Bool 'Enable app-enforced restrictions.' }))
            cloudAppSecurity                = Obj -d 'Microsoft Defender for Cloud Apps session control.' -Nullable -Props (Props ([ordered]@{
                isEnabled          = Bool 'Enable the control.'
                cloudAppSecurityType = Str 'Control type.' -Enum 'mcasConfigured', 'monitorOnly', 'blockDownloads', 'unknownFutureValue'
            }))
            signInFrequency                 = Obj -d 'Sign-in frequency.' -Nullable -Props (Props ([ordered]@{
                isEnabled          = Bool 'Enable the control.'
                type               = Str 'Unit of value.' -Enum 'days', 'hours'
                value              = Int 'Number of units.' -Min 1
                authenticationType = Str 'Which authentication the frequency applies to.' -Enum 'primaryAndSecondaryAuthentication', 'secondaryAuthentication'
                frequencyInterval  = Str 'timeBased or everyTime.' -Enum 'timeBased', 'everyTime'
            }))
            persistentBrowser               = Obj -d 'Persistent browser session.' -Nullable -Props (Props ([ordered]@{
                isEnabled = Bool 'Enable the control.'
                mode      = Str 'always or never.' -Enum 'always', 'never'
            }))
            continuousAccessEvaluation      = Obj -d 'Continuous access evaluation. Beta apiVersion (2026-10-01-preview.beta) only.' -Nullable -Props (Props ([ordered]@{
                mode = Str 'disabled or strictEnforcement.' -Enum 'disabled', 'strictEnforcement', 'unknownFutureValue'
            }))
            disableResilienceDefaults       = Bool 'Disable resilience defaults.' -Nullable
        }))
    }
}

# --- administrativeUnits --------------------------------------------------------------------------------------------
$specs['administrativeUnits'] = [ordered]@{
    NamePattern = $uniqueNamePattern
    Description = 'An administrative unit. The resource name is the displayName; it is looked up by displayName on every run (more than one match is an error). Dynamic administrative units reject explicit members.'
    Props       = [ordered]@{
        displayName                   = Str 'Set from the resource name; declaring it is only allowed when it equals the name.'
        description                   = Str 'Description.' -Nullable
        visibility                    = Str 'HiddenMembership hides the unit''s members from everyone but its administrators; null (default) is public.' -Nullable
        membershipType                = Str 'Assigned (default) or Dynamic.' -Enum 'Assigned', 'Dynamic'
        membershipRule                = Str 'Dynamic membership rule (requires membershipType: Dynamic).' -Nullable
        membershipRuleProcessingState = Str 'On (default for dynamic units) or Paused.' -Enum 'On', 'Paused'
        isMemberManagementRestricted  = Bool 'Restricted management unit; immutable after create. Assign scoped roles before adding members.' -Nullable
        members                       = EdgeIds 'Administrative unit membership edge, target side: the full desired set of member object ids (users, groups, devices). Not allowed on dynamic units; members not listed are removed.'
    }
}

# --- roleAssignments (standalone) -----------------------------------------------------------------------------------
$specs['roleAssignments'] = [ordered]@{
    NamePattern = $script:GuidPattern
    Description = 'A permanent active directory role assignment. The resource name is a deterministic GUID chosen with guid(); the object is matched by (roleDefinitionId, principalId, directoryScopeId). Equivalent to a directoryRoles entry on the principal.'
    Props       = [ordered]@{
        roleDefinitionId   = Str 'The role template id (use roleTemplate(''Display Name'')), or use role.'
        role               = Str 'Sugar: role template id, built-in role name, custom role display name or id; resolved to roleDefinitionId.'
        principalId        = Str 'Required: object id of the user, role-assignable group or service principal.'
        directoryScopeId   = Str 'Scope: ''/'' (tenant, default), ''/administrativeUnits/{id}'' or ''/{applicationObjectId}''.' -Pattern '^/'
        administrativeUnit = Str 'Sugar for an administrative-unit scope: the unit''s object id or template name.'
    }
}

# --- authenticationMethodConfigurations ---------------------------------------------------------------------------
$authMethodTargetProps = [ordered]@{
    id                     = Str 'Required: a group object id, or all_users. Members are matched by id (a set).' -Min 1
    targetType             = Str 'group (or user).' -Enum 'group', 'user', 'unknownFutureValue'
    isRegistrationRequired = Bool 'Whether users are nudged to register the method.'
    authenticationMode     = Str 'microsoftAuthenticator only: any, push or deviceBasedPush.' -Enum 'any', 'push', 'deviceBasedPush', 'unknownFutureValue'
    allowedPasskeyProfiles = StrArr 'fido2 only: ids of the passkey profiles this target may use.' -Unique
}
$keyRestrictions = { param($d) Obj -d $d -Props (Props ([ordered]@{
    isEnforced      = Bool 'Whether the AAGUID list is enforced.'
    enforcementType = Str 'allow (only the listed models) or block (every model but the listed ones). An enforced allow-list needs at least one AAGUID.' -Enum 'allow', 'block'
    aaGuids         = StrArr 'Authenticator model AAGUIDs, compared as a set and normalised to lower case.' -Pattern $script:GuidPattern -Unique
})) }

$specs['authenticationMethodConfigurations'] = [ordered]@{
    NamePattern = '^(?i:fido2|temporaryAccessPass|x509Certificate|microsoftAuthenticator|sms|voice|email|softwareOath)$'
    Description = 'An authentication method configuration of the tenant authentication methods policy (a tenant singleton). The resource name is the method id (fido2, temporaryAccessPass, x509Certificate, microsoftAuthenticator, sms, voice, email, softwareOath; case-insensitive); @odata.type is derived from it. Never created: a missing configuration is an error. Updated with a merge PATCH; undeclared sub-properties of declared objects keep their live values. Delete resets fido2, temporaryAccessPass and x509Certificate to the Microsoft default; the other methods are kept (NotDeletable). Properties not listed here are passed through to Graph unchanged.'
    Props       = [ordered]@{
        id            = Str 'Set from the resource name; declaring it is only allowed when it names the same method.'
        '@odata.type' = Str 'Derived from the resource name; declaring it is only allowed when it matches the method.' -Enum '#microsoft.graph.fido2AuthenticationMethodConfiguration', '#microsoft.graph.temporaryAccessPassAuthenticationMethodConfiguration', '#microsoft.graph.x509CertificateAuthenticationMethodConfiguration', '#microsoft.graph.microsoftAuthenticatorAuthenticationMethodConfiguration', '#microsoft.graph.smsAuthenticationMethodConfiguration', '#microsoft.graph.voiceAuthenticationMethodConfiguration', '#microsoft.graph.emailAuthenticationMethodConfiguration', '#microsoft.graph.softwareOathAuthenticationMethodConfiguration'
        state         = Str 'enabled or disabled.' -Enum 'enabled', 'disabled'
        includeTargets = Arr -d 'Groups (or all_users) enabled for the method; compared as a set by id.' -Items (Obj '' -Required 'id' -Props (Props $authMethodTargetProps))
        excludeTargets = Arr -d 'Groups excluded from the method; compared as a set by id.' -Items (Obj '' -Required 'id' -Props (Props ([ordered]@{
            id         = Str 'Required: a group object id.' -Min 1
            targetType = Str 'group.' -Enum 'group', 'user', 'unknownFutureValue'
        })))
        isSelfServiceRegistrationAllowed = Bool 'fido2: whether users can register new passkeys.'
        isAttestationEnforced = Bool 'fido2 (deprecated by Microsoft, removed October 2027; use passkeyProfiles): enforce attestation at registration. Declaring it raises DeprecatedAuthenticationMethodProperty.'
        keyRestrictions       = & $keyRestrictions 'fido2 (deprecated by Microsoft, removed October 2027; use passkeyProfiles): the AAGUID allow- or block-list. Declaring it raises DeprecatedAuthenticationMethodProperty.'
        passkeyProfiles       = Arr -d 'fido2: passkey profiles, compared as a set by id.' -Items (Obj '' -Required 'id' -Props (Props ([ordered]@{
            id                     = Str 'Required: the profile id (the default profile is 00000000-0000-0000-0000-000000000001).' -Min 1
            name                   = Str 'Profile name.'
            passkeyTypes           = Str 'Comma-separated passkey types, for example deviceBound,synced.'
            attestationEnforcement = Str 'Attestation enforcement, for example disabled or registrationOnly.'
            keyRestrictions        = & $keyRestrictions 'The profile AAGUID allow- or block-list.'
        })))
        defaultPasskeyProfile = Str 'fido2: the id of the baseline passkey profile.'
        defaultLifetimeInMinutes = Int 'temporaryAccessPass: default lifetime; between minimumLifetimeInMinutes and maximumLifetimeInMinutes.' -Min 10 -Max 43200
        defaultLength            = Int 'temporaryAccessPass: default pass length (8-48 characters).' -Min 8 -Max 48
        minimumLifetimeInMinutes = Int 'temporaryAccessPass: minimum lifetime (10-43200 minutes).' -Min 10 -Max 43200
        maximumLifetimeInMinutes = Int 'temporaryAccessPass: maximum lifetime (10-43200 minutes).' -Min 10 -Max 43200
        isUsableOnce             = Bool 'temporaryAccessPass: restrict every pass to one-time use.'
        authenticationModeConfiguration = Obj -d 'x509Certificate: the default binding and the binding rules. Undeclared rules keep their live value (EARM resends them, since Graph replaces the object whole); an explicit [] clears them.' -Props (Props ([ordered]@{
            x509CertificateAuthenticationDefaultMode = Str 'x509CertificateSingleFactor or x509CertificateMultiFactor.' -Enum 'x509CertificateSingleFactor', 'x509CertificateMultiFactor'
            rules = Arr -d 'Binding rules (policy OID or issuer subject to an authentication mode).' -Items (Obj '' -Props (Props ([ordered]@{
                x509CertificateRuleType           = Str 'issuerSubject, policyOID, issuerSubjectAndPolicyOID...'
                identifier                        = Str 'The issuer subject or policy OID.'
                x509CertificateAuthenticationMode = Str 'x509CertificateSingleFactor or x509CertificateMultiFactor.' -Enum 'x509CertificateSingleFactor', 'x509CertificateMultiFactor'
            })))
        }))
        certificateUserBindings    = Arr -d 'x509Certificate: certificate field to user attribute bindings (by priority).' -Items (FreeObj '')
        certificateAuthorityScopes = Arr -d 'x509Certificate: issuing CA scopes.' -Items (FreeObj '')
        crlValidationConfiguration = FreeObj 'x509Certificate: certificate revocation list validation.'
        issuerHintsConfiguration   = FreeObj 'x509Certificate: issuer hints for the certificate picker.'
        featureSettings            = FreeObj 'microsoftAuthenticator: feature settings (displayAppInformationRequiredState, displayLocationInformationRequiredState...).'
        isSoftwareOathEnabled      = Bool 'microsoftAuthenticator: allow OTP codes from the Authenticator app.'
        isOfficePhoneAllowed       = Bool 'voice: allow office phone numbers.'
        allowExternalIdToUseEmailOtp = Str 'email: default, enabled or disabled.' -Enum 'default', 'enabled', 'disabled'
    }
}

# --- roleEligibilityScheduleRequests (PIM eligibility, ARM-styled) ------------------------------------------------
$specs['roleEligibilityScheduleRequests'] = [ordered]@{
    NamePattern = $script:GuidPattern
    Description = 'A PIM eligible directory role assignment, shaped like ARM''s Microsoft.Authorization/roleEligibilityScheduleRequests. The resource name is a deterministic GUID chosen with guid() (a label, not the identity); the eligibility is matched by (principalId, roleDefinitionId, directoryScopeId or appScopeId) and read live on every run. Created with action adminAssign, changed in place with adminUpdate, removed with adminRemove; justification and ticketInfo are request metadata, sent on every request and never compared. condition, conditionVersion and the target*ScheduleId properties of ARM are refused.'
    Props       = [ordered]@{
        principalId        = Guid 'Required: object id of the user, role-assignable group or service principal made eligible.'
        roleDefinitionId   = Guid 'The role template id (use roleTemplate(''Display Name'')), or use role. Not both.'
        role               = Str 'EARM sugar: role template id, built-in role name, custom role display name or id; resolved to roleDefinitionId. Not with roleDefinitionId.' -Min 1
        directoryScopeId   = Str 'Scope: ''/'' (tenant, default), ''/administrativeUnits/{id}'' or ''/{objectId}''. At most one of directoryScopeId, administrativeUnit and appScopeId.' -Pattern '^/'
        administrativeUnit = Str 'EARM sugar for an administrative-unit scope: the unit''s object id or its displayName; resolved to directoryScopeId.' -Min 1
        appScopeId         = Str 'An app-specific scope instead of a directory scope (''/'' for tenant-wide app scopes).' -Min 1
        requestType        = Str 'Optional and only AdminAssign (any casing), so ARM snippets paste unchanged; EARM chooses adminAssign / adminUpdate / adminRemove itself.'
        scheduleInfo       = Obj -d 'The eligibility period. Absent: permanent (expiration type NoExpiration).' -Props (Props ([ordered]@{
            startDateTime = Str 'ISO 8601 start; absent or in the past means now (a past start is never drift).'
            expiration    = Obj -d 'When the eligibility ends.' -Props (Props ([ordered]@{
                type        = Str 'NoExpiration, AfterDateTime (needs endDateTime) or AfterDuration (needs duration); any casing, sent in Graph''s camelCase.'
                endDateTime = Str 'AfterDateTime: the ISO 8601 end, after startDateTime. Compared with a 1 s tolerance.'
                duration    = Str 'AfterDuration: an ISO 8601 duration such as P365D or PT8H. Applied when the eligibility is created and never compared afterwards.'
            }))
        }))
        justification      = Str 'Request metadata, sent on every request and never compared (default "Deployed by earm"); PIM policy may require it.'
        ticketInfo         = Obj -d 'Request metadata, sent on every request and never compared.' -Props (Props ([ordered]@{
            ticketNumber = Str 'Ticket number.'
            ticketSystem = Str 'Ticket system.'
        }))
    }
}

# --- privilegedAccess/groupEligibilityScheduleRequests (PIM for Groups eligibility) -------------------------------
$specs['privilegedAccess.groupEligibilityScheduleRequests'] = [ordered]@{
    NamePattern = $script:GuidPattern
    Description = 'A PIM for Groups eligibility: a user or group made eligible for the membership (accessId member) or ownership (accessId owner) of a group. The resource name is a deterministic GUID chosen with guid() (a label, not the identity); the eligibility is matched by (groupId, principalId, accessId) and read live on every run. Created with action adminAssign, changed in place with adminUpdate, removed with adminRemove; justification and ticketInfo are request metadata, sent on every request and never compared. The first request onboards the group in PIM for Groups permanently; dynamic membership and on-premises synced groups are refused.'
    Props       = [ordered]@{
        groupId       = Guid 'The target group''s object id (for example [reference(resourceId(''Microsoft.Entra/groups'', ''ops-admins'')).id]), or use group. Not both.'
        group         = Str 'EARM sugar: the target group''s uniqueName or object id; resolved to groupId. Not with groupId.' -Min 1
        principalId   = Guid 'Required: object id of the user or group made eligible.'
        accessId      = Str 'Required: member (eligible membership) or owner (eligible ownership); any casing, sent in Graph''s lowercase.'
        requestType   = Str 'Optional and only AdminAssign (any casing); EARM chooses adminAssign / adminUpdate / adminRemove itself.'
        scheduleInfo  = Obj -d 'The eligibility period. Absent: permanent (expiration type NoExpiration), which a group''s PIM policy may forbid.' -Props (Props ([ordered]@{
            startDateTime = Str 'ISO 8601 start; absent or in the past means now (a past start is never drift).'
            expiration    = Obj -d 'When the eligibility ends.' -Props (Props ([ordered]@{
                type        = Str 'NoExpiration, AfterDateTime (needs endDateTime) or AfterDuration (needs duration); any casing, sent in Graph''s camelCase.'
                endDateTime = Str 'AfterDateTime: the ISO 8601 end, after startDateTime. Compared with a 1 s tolerance.'
                duration    = Str 'AfterDuration: an ISO 8601 duration such as P365D or PT8H. Applied when the eligibility is created and never compared afterwards.'
            }))
        }))
        justification = Str 'Request metadata, sent on every request and never compared (default "Deployed by earm"); PIM policy may require it.'
        ticketInfo    = Obj -d 'Request metadata, sent on every request and never compared.' -Props (Props ([ordered]@{
            ticketNumber = Str 'Ticket number.'
            ticketSystem = Str 'Ticket system.'
        }))
    }
}

# --- roleAssignmentScheduleRequests (PIM time-bound active assignment, ARM-styled) --------------------------------
$specs['roleAssignmentScheduleRequests'] = [ordered]@{
    NamePattern = $script:GuidPattern
    Description = 'A PIM time-bound ACTIVE directory role assignment, shaped like ARM''s Microsoft.Authorization/roleAssignmentScheduleRequests. The resource name is a deterministic GUID chosen with guid() (a label, not the identity); the assignment is matched by (principalId, roleDefinitionId, directoryScopeId or appScopeId) and read live on every run. Created with action adminAssign, changed in place with adminUpdate, removed with adminRemove; justification and ticketInfo are request metadata, sent on every request and never compared. The schedule must be time-bound (AfterDateTime or AfterDuration): a permanent active assignment is Microsoft.Entra/roleAssignments, and the same principal, role and scope may not be declared by both. condition, conditionVersion and the target*/linked*ScheduleId properties of ARM are refused.'
    Props       = [ordered]@{
        principalId        = Guid 'Required: object id of the user, role-assignable group or service principal the role is assigned to.'
        roleDefinitionId   = Guid 'The role template id (use roleTemplate(''Display Name'')), or use role. Not both.'
        role               = Str 'EARM sugar: role template id, built-in role name, custom role display name or id; resolved to roleDefinitionId. Not with roleDefinitionId.' -Min 1
        directoryScopeId   = Str 'Scope: ''/'' (tenant, default), ''/administrativeUnits/{id}'' or ''/{objectId}''. At most one of directoryScopeId, administrativeUnit and appScopeId.' -Pattern '^/'
        administrativeUnit = Str 'EARM sugar for an administrative-unit scope: the unit''s object id or its displayName; resolved to directoryScopeId.' -Min 1
        appScopeId         = Str 'An app-specific scope instead of a directory scope (''/'' for tenant-wide app scopes).' -Min 1
        requestType        = Str 'Optional and only AdminAssign (any casing), so ARM snippets paste unchanged; EARM chooses adminAssign / adminUpdate / adminRemove itself.'
        scheduleInfo       = Obj -d 'Required: the assignment period, time-bound (NoExpiration is refused: use Microsoft.Entra/roleAssignments for a permanent assignment).' -Props (Props ([ordered]@{
            startDateTime = Str 'ISO 8601 start; absent or in the past means now (a past start is never drift).'
            expiration    = Obj -d 'Required: when the assignment ends.' -Props (Props ([ordered]@{
                type        = Str 'AfterDateTime (needs endDateTime) or AfterDuration (needs duration); any casing, sent in Graph''s camelCase. NoExpiration is refused.'
                endDateTime = Str 'AfterDateTime: the ISO 8601 end, after startDateTime. Compared with a 1 s tolerance.'
                duration    = Str 'AfterDuration: an ISO 8601 duration such as P30D or PT8H. Applied when the assignment is created and never compared afterwards.'
            }))
        }))
        justification      = Str 'Request metadata, sent on every request and never compared (default "Deployed by earm"); PIM policy may require it.'
        ticketInfo         = Obj -d 'Request metadata, sent on every request and never compared.' -Props (Props ([ordered]@{
            ticketNumber = Str 'Ticket number.'
            ticketSystem = Str 'Ticket system.'
        }))
    }
}

# --- privilegedAccess/groupAssignmentScheduleRequests (PIM for Groups time-bound active assignment) ---------------
$specs['privilegedAccess.groupAssignmentScheduleRequests'] = [ordered]@{
    NamePattern = $script:GuidPattern
    Description = 'A PIM for Groups time-bound ACTIVE assignment: a user or group made an active member (accessId member) or owner (accessId owner) of a group until a date or for a duration. The resource name is a deterministic GUID chosen with guid() (a label, not the identity); the assignment is matched by (groupId, principalId, accessId) and read live on every run. Created with action adminAssign, changed in place with adminUpdate, removed with adminRemove; justification and ticketInfo are request metadata, sent on every request and never compared. The schedule must be time-bound (a permanent membership is the group''s members / owners), and the target group may not declare an authoritative members (owners) list, nor the principal list the group in memberOf (ownerOf). The first request onboards the group in PIM for Groups permanently; dynamic membership and on-premises synced groups are refused.'
    Props       = [ordered]@{
        groupId       = Guid 'The target group''s object id (for example [reference(resourceId(''Microsoft.Entra/groups'', ''ops-admins'')).id]), or use group. Not both.'
        group         = Str 'EARM sugar: the target group''s uniqueName or object id; resolved to groupId. Not with groupId.' -Min 1
        principalId   = Guid 'Required: object id of the user or group made an active member or owner.'
        accessId      = Str 'Required: member (active membership) or owner (active ownership); any casing, sent in Graph''s lowercase.'
        requestType   = Str 'Optional and only AdminAssign (any casing); EARM chooses adminAssign / adminUpdate / adminRemove itself.'
        scheduleInfo  = Obj -d 'Required: the assignment period, time-bound (NoExpiration is refused: declare a permanent membership in the group''s members / owners).' -Props (Props ([ordered]@{
            startDateTime = Str 'ISO 8601 start; absent or in the past means now (a past start is never drift).'
            expiration    = Obj -d 'Required: when the assignment ends.' -Props (Props ([ordered]@{
                type        = Str 'AfterDateTime (needs endDateTime) or AfterDuration (needs duration); any casing, sent in Graph''s camelCase. NoExpiration is refused.'
                endDateTime = Str 'AfterDateTime: the ISO 8601 end, after startDateTime. Compared with a 1 s tolerance.'
                duration    = Str 'AfterDuration: an ISO 8601 duration such as P30D or PT8H. Applied when the assignment is created and never compared afterwards.'
            }))
        }))
        justification = Str 'Request metadata, sent on every request and never compared (default "Deployed by earm"); PIM policy may require it.'
        ticketInfo    = Obj -d 'Request metadata, sent on every request and never compared.' -Props (Props ([ordered]@{
            ticketNumber = Str 'Ticket number.'
            ticketSystem = Str 'Ticket system.'
        }))
    }
}

# --- roleManagementPolicies (PIM policy rules, ARM-styled) --------------------------------------------------------
$policyApprover = Obj '' -Required 'id', 'userType' -Props (Props ([ordered]@{
    id          = Guid 'Required: the object id of the approving user or group.'
    userType    = Str 'Required: User (a single user approves; Graph singleUser) or Group (any member approves; Graph groupMembers). Any casing.'
    description = Str 'ARM display text; read-only in Graph, never compared or sent.'
    isBackup    = Bool 'Whether the approver is a backup approver. Only false (the default, the same as absent) is accepted: Graph does not return isBackup when the policy is read, so true could never be verified.'
}))
$specs['roleManagementPolicies'] = [ordered]@{
    NamePattern = $script:GuidPattern
    Description = 'The PIM policy (role settings) of a directory role or of a PIM for Groups membership / ownership, shaped like ARM''s Microsoft.Authorization/roleManagementPolicies. The resource name is a deterministic GUID chosen with guid() (a label, not the identity); the policy is named by role (a directory role), or by group and accessId (a group), or by scopeType, scopeId and roleDefinitionId, and read live on every run. Only the rules declared in rules[] (matched by id) are managed: each is filled from the live rule and changed in place with one PATCH per changed rule; undeclared rules are left as they are. Never created (every role and onboarded group has a policy; a group policy may opt in to onboarding the group with onboarding) and never deleted (kept as NotDeletable). Applied before the PIM schedule requests of the same role or group in the template.'
    Props       = [ordered]@{
        role             = Str 'EARM sugar for a directory role policy: role template id, built-in role name, custom role display name or id; resolved to roleDefinitionId (scopeType DirectoryRole, scopeId ''/''). Not with group or roleDefinitionId.' -Min 1
        group            = Str 'EARM sugar for a group policy: the group''s uniqueName or object id; resolved to scopeId (scopeType Group). Not with role or scopeId.' -Min 1
        accessId         = Str 'With group: member (the policy of the group''s membership) or owner (of its ownership); resolved to roleDefinitionId. Any casing.'
        scopeType        = Str 'DirectoryRole (or Directory) for a directory role policy, Group for a PIM for Groups policy. Implied by role / group.'
        scopeId          = Str '''/'' for a directory role policy; the group''s object id for a group policy.' -Min 1
        roleDefinitionId = Str 'The directory role template id, or member / owner for a group policy.' -Min 1
        onboarding       = Obj -d 'EARM opt-in, group policies only: lets the deployment onboard the group in PIM for Groups when Graph lists no policy for it (PIM onboards a group only on its first eligibility or assignment). A temporary member eligibility (one hour) is created for temporaryEligiblePrincipalId, the policy is waited for, the eligibility is removed, and the rules are applied, all in the same run. Ignored when the group is onboarded already, or when a schedule request of the same deployment onboards it. Never sent to Graph. Needs PrivilegedEligibilitySchedule.ReadWrite.AzureADGroup.' -Nullable -Required 'temporaryEligiblePrincipalId' -Props (Props ([ordered]@{
            temporaryEligiblePrincipalId = Guid 'Required: the object id of the user or service principal made eligible (member) for the onboarding, typically [reference(...).id]. The group''s default policy must allow it to be made eligible.'
        }))
        rules            = Arr -d 'Required: the rules to manage, matched by id (Expiration_EndUser_Assignment, Approval_EndUser_Assignment...). Every other rule of the policy is left as it is.' -Min 1 -Items (Obj '' -Required 'id' -Props (Props ([ordered]@{
            id                   = Str 'Required: the rule id: Expiration_, Enablement_, Approval_ or AuthenticationContext_ + Admin_ or EndUser_ + Assignment or Eligibility, or Notification_ + Admin_, Requestor_ or Approver_ + the same caller and level.' -Min 1
            ruleType             = Str 'The ARM rule type; optional (implied by the id) and, when declared, must match it. Mapped to Graph''s @odata.type (#microsoft.graph.unifiedRoleManagementPolicy...Rule). Any casing.'
            target               = Obj -d 'Who and what the rule applies to; undeclared members keep their live values. caller and level must agree with the id.' -Props (Props ([ordered]@{
                caller              = Str 'Admin or EndUser (any casing).'
                operations          = StrArr 'All, Activate, Deactivate, Assign, Update, Remove, Extend, Renew (any casing).'
                level               = Str 'Assignment or Eligibility (any casing).'
                targetObjects       = Arr -d 'Graph passthrough.' -Items (FreeObj '') -Nullable
                inheritableSettings = StrArr 'Graph passthrough.' -Nullable
                enforcedSettings    = StrArr 'Graph passthrough.' -Nullable
            }))
            isExpirationRequired = Bool 'Expiration rules: whether an expiration is required (false allows permanent assignment or eligibility).'
            maximumDuration      = Str 'Expiration rules: the ISO 8601 maximum duration (PT8H, P365D); compared by value.'
            enabledRules         = StrArr 'Enablement rules: MultiFactorAuthentication, Justification, Ticketing (any casing); [] clears them.'
            setting              = Obj -d 'Approval rules: the approval settings; undeclared members keep their live values.' -Props (Props ([ordered]@{
                isApprovalRequired               = Bool 'Whether activation needs approval; when true, every stage needs at least one primary approver.'
                isApprovalRequiredForExtension   = Bool 'Whether an extension needs approval.'
                isRequestorJustificationRequired = Bool 'Whether the requestor must justify the request.'
                approvalMode                     = Str 'SingleStage, Serial, Parallel or NoApproval (any casing).'
                approvalStages                   = Arr -d 'One or two approval stages.' -Items (Obj '' -Props (Props ([ordered]@{
                    approvalStageTimeOutInDays      = Int 'Days a request may wait before it is denied.'
                    isApproverJustificationRequired = Bool 'Whether the approver must justify the decision.'
                    escalationTimeInMinutes         = Int 'Minutes before escalation.'
                    isEscalationEnabled             = Bool 'Whether escalation is enabled (needs escalationApprovers).'
                    primaryApprovers                = Arr -d 'The approvers (users or groups), compared as a set.' -Items $policyApprover
                    escalationApprovers             = Arr -d 'The escalation approvers, compared as a set.' -Items $policyApprover -Nullable
                })))
            }))
            isEnabled                  = Bool 'Authentication context rules: whether activation must satisfy an authentication context.'
            claimValue                 = Str 'Authentication context rules: the authentication context id, c1 to c99.' -Nullable
            notificationType           = Str 'Notification rules: Email.'
            recipientType              = Str 'Notification rules: Requestor, Approver or Admin; must agree with the id.'
            notificationLevel          = Str 'Notification rules: None, Critical or All.'
            notificationRecipients     = StrArr 'Notification rules: additional e-mail recipients; [] clears them.' -Nullable
            isDefaultRecipientsEnabled = Bool 'Notification rules: whether the default recipients are notified.'
        })))
    }
}

# --- entitlementManagement (access packages, docs/access-packages.md) ----------------------------------------------
$subjectSet = Obj '' -Props (Props ([ordered]@{
    '@odata.type' = Str 'The subject set type: #microsoft.graph.groupMembers, #microsoft.graph.singleUser, #microsoft.graph.requestorManager, #microsoft.graph.internalSponsors, #microsoft.graph.externalSponsors, #microsoft.graph.targetUserSponsors, #microsoft.graph.connectedOrganizationMembers ...'
    groupId       = Str 'groupMembers: the group''s object id.'
    userId        = Str 'singleUser: the user''s object id.'
}))

$specs['entitlementManagement.catalogs'] = [ordered]@{
    NamePattern = $uniqueNamePattern
    Description = 'An entitlement management catalog (accessPackageCatalog). The resource name is the displayName, unique in the tenant.'
    Props       = [ordered]@{
        displayName         = Str 'Set from the resource name; declaring it is only allowed when it equals the name.'
        description         = Str 'Description.' -Nullable
        state               = Str 'published (default on create) or unpublished.' -Enum 'published', 'unpublished'
        isExternallyVisible = Bool 'Whether users of connected organizations can request packages of the catalog. Defaults to false on create.'
    }
}

$specs['entitlementManagement.catalogResources'] = [ordered]@{
    NamePattern = $script:GuidPattern
    Description = 'A group added to an entitlement management catalog (accessPackageResource, written through accessPackageResourceRequest adminAdd / adminRemove). Found by the tuple (catalogId, originId); the GUID name is a label.'
    Props       = [ordered]@{
        catalogId    = Str 'Required: the catalog''s object id, for example [reference(''catalog'').id].' -Min 1
        originId     = Str 'Required: the group''s object id, for example [reference(''group'').id].' -Min 1
        originSystem = Str 'AadGroup (the default, and the only origin EARM manages).' -Enum 'AadGroup'
    }
}

$specs['entitlementManagement.accessPackages'] = [ordered]@{
    NamePattern = $uniqueNamePattern
    Description = 'An access package. The resource name is the displayName, unique in the tenant (EARM refuses two packages with one name in any catalogs).'
    Props       = [ordered]@{
        displayName = Str 'Set from the resource name; declaring it is only allowed when it equals the name.'
        catalogId   = Str 'Required on create, immutable: the catalog''s object id, for example [reference(''catalog'').id].' -Min 1
        description = Str 'Description.' -Nullable
        isHidden    = Bool 'Whether the package is hidden from the My Access portal (requestable only by a direct link). Defaults to false on create.'
    }
}

$specs['entitlementManagement.resourceRoleScopes'] = [ordered]@{
    NamePattern = $script:GuidPattern
    Description = 'A group role an access package grants (accessPackageResourceRoleScope). Found by the tuple (accessPackageId, resourceOriginId, role); the GUID name is a label. The group must be a resource of the package''s catalog.'
    Props       = [ordered]@{
        accessPackageId  = Str 'Required: the access package''s object id, for example [reference(''package'').id].' -Min 1
        resourceOriginId = Str 'Required: the group''s object id, for example [reference(''group'').id].' -Min 1
        role             = Str 'Required: the group role''s display name. Eligible Member and Eligible Owner (PIM for Groups eligibility) are offered only for a group onboarded in PIM for Groups.' -Enum 'Member', 'Owner', 'Eligible Member', 'Eligible Owner'
        originSystem     = Str 'AadGroup (the default, and the only origin EARM manages).' -Enum 'AadGroup'
    }
}

$specs['entitlementManagement.accessPackages.assignmentPolicies'] = [ordered]@{
    NamePattern = $uniqueNamePattern
    Description = 'An assignment policy of an access package (accessPackageAssignmentPolicy). A child of the package; the child name is the policy''s displayName, unique within the package. Only declared properties are compared; an update is a PUT of the live policy with the declared values merged in.'
    Props       = [ordered]@{
        displayName             = Str 'Set from the child name; declaring it is only allowed when it equals the name.'
        description             = Str 'Description.' -Nullable
        allowedTargetScope      = Str 'Who may request or be assigned: notSpecified, specificDirectoryUsers, specificConnectedOrganizationUsers, specificDirectoryServicePrincipals, allMemberUsers, allDirectoryUsers, allDirectoryServicePrincipals, allConfiguredConnectedOrganizationUsers, allExternalUsers.'
        specificAllowedTargets  = Arr -d 'Subject sets for the specific* scopes; compared order-insensitively.' -Items $subjectSet -Nullable
        expiration              = Obj -d 'expirationPattern. Keep a duration within the eligibility maximum of a PIM for Groups role the package grants.' -Nullable -Props (Props ([ordered]@{
            type        = Str 'notSpecified, noExpiration, afterDateTime or afterDuration.' -Enum 'notSpecified', 'noExpiration', 'afterDateTime', 'afterDuration'
            endDateTime = Str 'afterDateTime: when the assignment ends (ISO 8601).' -Nullable
            duration    = Str 'afterDuration: the ISO 8601 duration, for example P180D.' -Nullable
        }))
        requestorSettings       = FreeObj 'accessPackageAssignmentRequestorSettings (enableTargetsToSelfAddAccess, enableTargetsToSelfRemoveAccess, onBehalfRequestors ...).' -Nullable
        requestApprovalSettings = Obj -d 'accessPackageAssignmentApprovalSettings.' -Nullable -Props (Props ([ordered]@{
            isApprovalRequiredForAdd         = Bool 'Whether a request to add access needs approval.'
            isApprovalRequiredForUpdate      = Bool 'Whether a request to update access needs approval.'
            isRequestorJustificationRequired = Bool 'Whether the requestor must justify the request.'
            stages                           = Arr -d 'One to three approval stages ([] when no approval is required).' -Items (Obj '' -Props (Props ([ordered]@{
                durationBeforeAutomaticDenial   = Str 'ISO 8601 duration before the request is denied, for example P14D.'
                isApproverJustificationRequired = Bool 'Whether the approver must justify the decision.'
                isEscalationEnabled             = Bool 'Whether the request escalates.'
                durationBeforeEscalation        = Str 'ISO 8601 duration before escalation.'
                primaryApprovers                = Arr -d 'The approvers, compared as a set.' -Items $subjectSet
                fallbackPrimaryApprovers        = Arr -d 'The fallback approvers.' -Items $subjectSet -Nullable
                escalationApprovers             = Arr -d 'The escalation approvers.' -Items $subjectSet -Nullable
                fallbackEscalationApprovers     = Arr -d 'The fallback escalation approvers.' -Items $subjectSet -Nullable
            })))
        }))
        reviewSettings          = FreeObj 'accessPackageAssignmentReviewSettings (optional access reviews).' -Nullable
        automaticRequestSettings = FreeObj 'accessPackageAutomaticRequestSettings (auto-assignment policies only).' -Nullable
        questions               = Arr -d 'accessPackageQuestion array.' -Items (FreeObj '') -Nullable
        notificationSettings    = FreeObj 'accessPackageNotificationSettings.' -Nullable
    }
}

# ---------------------------------------------------------------------------------------------------------------
# Rewrite the files
# ---------------------------------------------------------------------------------------------------------------

$schemaRoot = (Resolve-Path -LiteralPath $SchemaRoot).Path
$written = 0
foreach ($name in $specs.Keys) {
    $path = Join-Path $schemaRoot "$name.json"
    if (-not (Test-Path -LiteralPath $path)) { throw "No schema file for '$name' at $path." }

    $text = Get-Content -LiteralPath $path -Raw
    $marker = $text.IndexOf('  "x-earm": {', [StringComparison]::Ordinal)
    if ($marker -lt 0) { throw "$path has no x-earm overlay." }
    $overlay = $text.Substring($marker)
    $existing = $text | ConvertFrom-Json -AsHashtable
    $spec = $specs[$name]

    # namePattern lives in the overlay (the executor reads it); set it from the spec.
    if ($spec.Contains('NamePattern')) {
    $patternJson = if ($spec.NamePattern) { ConvertTo-JsonString $spec.NamePattern } else { 'null' }
    $overlay = [regex]::Replace($overlay, '"namePattern":\s*(null|"(?:[^"\\]|\\.)*")', { param($m) '"namePattern": ' + $patternJson }, 'None', [timespan]::FromSeconds(2))
    }

    $header = [ordered]@{
        '$schema'   = 'https://json-schema.org/draft/2020-12/schema'
        '$id'       = $existing['$id']
        title       = $existing['title']
        description = $spec.Description
        type        = 'object'
    }

    $out = [System.Text.StringBuilder]::new()
    [void]$out.Append("{`n")
    foreach ($key in $header.Keys) {
        [void]$out.Append('  ' + (ConvertTo-JsonString $key) + ': ' + (ConvertTo-JsonString $header[$key]) + ",`n")
    }

    [void]$out.Append('  "properties": ' + (Format-Node $spec.Props 1 0 -Force) + ",`n")
    [void]$out.Append('  "additionalProperties": true,' + "`n")
    [void]$out.Append('  "$defs": {' + "`n")
    [void]$out.Append('    "expression": { "type": "string", "pattern": "^\\[.*\\]$", "description": "A template expression, for example [parameters(''env'')]." }' + "`n")
    [void]$out.Append("  },`n")
    [void]$out.Append($overlay)

    $json = $out.ToString().Replace("`r`n", "`n")
    # Validate before writing: the result must parse and keep the overlay intact.
    $parsed = $json | ConvertFrom-Json -AsHashtable
    if ($parsed['x-earm']['type'] -ne $existing['x-earm']['type']) { throw "Overlay of $name changed." }

    if ($PSCmdlet.ShouldProcess($path, 'Rewrite type schema')) {
        [System.IO.File]::WriteAllText($path, $json, [System.Text.UTF8Encoding]::new($false))
        $written++
    }
}

Write-Host "Rewrote $written type schema file(s) under $schemaRoot."
