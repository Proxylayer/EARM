# Fake Azure CLI for contract tests

`tests/fake-az/fake-az.ps1` stands in for the real `az` in `tests/EntraArm.Az.Tests`. It is pure PowerShell 7
(no Python, no `.cmd`), works on Windows and Linux, and is selected by pointing `AzOptions.AzCommand`
(`--az-command` / `ENTRAARM_AZ_COMMAND`) at its absolute path. This file is the contract the `testcase` agent
builds it against; the production side (`src/EntraArm.Az`) is already written to it.

## How the fake is invoked

`PwshAzRunner` starts exactly one process per call:

```
pwsh -NoProfile -NonInteractive -NoLogo -ExecutionPolicy Bypass -File <tmp>/Invoke-AzRest-<sha16>.ps1 -RequestFile <tmp>/req-<id>.json -AzCommand <abs path>/fake-az.ps1
```

`Invoke-AzRest.ps1` (embedded in `EntraArm.Az`, extracted to the temp dir) reads the request file, builds `$azArgs`
as a string array and runs `& <fake-az.ps1> @azArgs`. Because `$AzCommand` is a `.ps1`, **the fake runs in-process
inside the host script**, which has these consequences:

| Concern | Rule for the fake |
|---|---|
| Arguments | Do **not** declare a `param()` block; read `$args`. Every element is one az argv entry, in order, e.g. `rest`, `--method`, `GET`, `--url`, `https://graph.microsoft.com/v1.0/users`, `--url-parameters`, `$select=id`, `--headers`, `Prefer=create-if-missing`, `--body`, `@C:\...\body-<id>.json`, `--only-show-errors`, `-o`, `json`. |
| stdout | Emit with `Write-Output` (strings). The host captures the pipeline output and writes it to the request's `outputFile` as UTF-8 without BOM; the runner reads that file back as `AzResult.StandardOutput`. Writing straight to `[Console]::Out` also works (the runner falls back to the pipe) but bypasses the UTF-8 file path, so prefer `Write-Output`. |
| stderr | Emit with `[Console]::Error.WriteLine(...)`. Never use `Write-Error`/`throw`: the host runs with `$ErrorActionPreference = 'Stop'` and would report exit 70 instead of your scripted exit code. |
| exit code | `exit <n>` at the end; the host forwards it as az's exit code. A script that falls off the end is exit 0. |
| Encoding | `[Console]::OutputEncoding` is already UTF-8 (no BOM) in the host; non-ASCII in stdout/stderr round-trips. |
| State | Do not touch global variables or preferences; the host is `Set-StrictMode -Version Latest`. Do not assume the current directory. |
| Timing | A `Start-Sleep` longer than `AzOptions.Timeout` (or `AzRequest.Timeout`) gets the whole pwsh tree killed; the runner reports `TimedOut = true`, exit code -1. |

The argv shape the fake sees is exactly `AzRequest.BuildAzArguments(bodyFile)` for the request (the host script and
that method are kept in lock-step; tests may compare the recorded argv against it after substituting the body path).

Environment the fake can assert on (set by the runner): `AZURE_CORE_OUTPUT=json`, `AZURE_CORE_NO_COLOR=1`,
`POWERSHELL_TELEMETRY_OPTOUT=1`, `POWERSHELL_UPDATECHECK=Off`. Tests pass their own steering variables through
`AzOptions.AdditionalEnvironment`, which the runner copies into the child's environment per call (so a test can
change the fixture between calls on the same runner instance).

## Steering variables (expected names)

| Variable | Meaning |
|---|---|
| `ENTRAARM_FAKE_AZ_ARGV_FILE` | Path of a JSON-lines file. The fake **appends** one line per invocation: `{"argv":[...],"bodyFile":"<path or null>","body":"<file content or null>","env":{"AZURE_CORE_OUTPUT":"json","AZURE_CORE_NO_COLOR":"1","POWERSHELL_TELEMETRY_OPTOUT":"1","POWERSHELL_UPDATECHECK":"Off"},"cwd":"..."}` (UTF-8, no BOM, compact). Read the body file **immediately** — the runner zeroes and deletes it as soon as the call returns. |
| `ENTRAARM_FAKE_AZ_FIXTURE` | Path to a fixture JSON file (or a directory of `NNN.json` files consumed in name order; each file is either `{"responses":[...]}` or a single response object). Absent: reply `{}` with exit 0. |
| `ENTRAARM_FAKE_AZ_STATE_FILE` | Where the fake remembers which responses were consumed (one zero-based index per line) so a fixture sequence survives across process launches. Default: `<argv file>.state` (or `<fixture>.state` without an argv file). Tests delete it between cases. |
| `ENTRAARM_FAKE_AZ_LINK_DIR` | Optional. When set and the call carries `--body @<dir>/body-<id>.json`, the fake hard-links `req-<id>.json`, `body-<id>.json` and `out-<id>.json` from that directory into this one (pre-creating the output file, which the host then rewrites in place). The links outlive the runner's delete, so a test can read them back and assert the runner zeroed the files first. Relies on the `req-/body-/out-<id>.json` naming of `AzRequestFiles`. |

## Fixture format

```json
{
  "responses": [
    {
      "match":      { "method": "GET", "urlContains": "/applications(uniqueName='", "argvContains": "", "bodyContains": "" },   // optional; omitted = wildcard; every present field must fit
      "exitCode":   0,
      "stdoutJson": { "id": "11111111-2222-3333-4444-555555555555", "displayName": "Ünïcødé" },  // re-serialised by ConvertTo-Json: compact, key order kept, non-ASCII raw
      "stdout":     "",          // alternative to stdoutJson (only used when stdoutJson is absent/null): raw text
      "stderr":     "",
      "sleepSeconds": 0          // fractional seconds allowed; the sleep happens after the call was recorded
    },
    {
      "exitCode": 1,
      "stderr": "ERROR: Too Many Requests({\"error\":{\"code\":\"TooManyRequests\",\"message\":\"Too many requests. Retry after 3 seconds.\"}})"
    }
  ]
}
```

Selection rule: the first **unconsumed** response whose `match` fits the current call (`method` compares the value
after `--method`, case-insensitively; `urlContains` is a substring test on the value after `--url`; `argvContains` is a
substring test on the whole argv joined with spaces; `bodyContains` is a substring test on the body file content); a
response is consumed when replayed (its index is appended to the state file). When nothing is left the fake writes
`fake-az: no fixture response left for <argv>` to stderr and exits 99, so a mis-scripted test fails loudly rather
than hanging in a retry loop (a malformed response object exits 98).

The C# side builds dynamic fixtures with `FakeScenario` / `FakeResponse` (`tests/EntraArm.Az.Tests/Infrastructure`);
static ones live in `tests/EntraArm.Az.Tests/Fixtures/` (`graph-errors.json` holds the canonical stderr shapes below and
is shared by the parser tests and the scenarios; `preflight-ok.json`, `preflight-not-logged-in.json`,
`pagination-three-pages.json`, `non-ascii-user.json`). `FakeAzContext` gives every test its own folder under the test
output directory with the argv file, state file, fixture, link directory and the runner's temp directory.

## az output shapes to replay faithfully

These are what `GraphErrorParser` / `AzCliException.Classify` expect (derived from azure-cli's
`send_raw_request`: `HTTPError("<reason phrase>(<response text>)")`, logged as `ERROR: ...` on stderr, exit 1):

```
ERROR: Not Found({"error":{"code":"Request_ResourceNotFound","message":"Resource '...' does not exist or one of its queried reference-property objects are not present.","innerError":{"date":"2026-10-14T10:00:00","request-id":"...","client-request-id":"..."}}})
ERROR: Bad Request({"error":{"code":"Request_BadRequest","message":"Another object with the same value for property uniqueName already exists.","innerError":{...}}})
ERROR: Conflict({"error":{"code":"Request_MultipleObjectsWithSameKeyValue","message":"...","innerError":{...}}})
ERROR: Forbidden({"error":{"code":"Authorization_RequestDenied","message":"Insufficient privileges to complete the operation.","innerError":{...}}})
ERROR: Unauthorized({"error":{"code":"InvalidAuthenticationToken","message":"Access token has expired or is not yet valid.","innerError":{...}}})
ERROR: Too Many Requests({"error":{"code":"TooManyRequests","message":"Too many requests. Retry after 3 seconds."}})
ERROR: Service Unavailable({"error":{"code":"ServiceUnavailable","message":"Service unavailable."}})
ERROR: Please run 'az login' to setup account.
ERROR: HTTPSConnectionPool(host='graph.microsoft.com', port=443): Max retries exceeded with url: /v1.0/users (Caused by NewConnectionError(...))
```

Successful calls print the JSON body to stdout (exit 0); a 204 prints nothing. `az account show`, `az version` and
`az account get-access-token --resource-type ms-graph` are plain `<command words> --only-show-errors -o json` argv and
reply with their usual JSON objects (`{"tenantId":"...","user":{"name":"...","type":"user"},"environmentName":"AzureCloud",...}`,
`{"azure-cli":"2.70.0",...}`, `{"accessToken":"...","expiresOn":"..."}`).

A `$batch` reply is `{"responses":[{"id":"1","status":204,"headers":{},"body":null},{"id":"2","status":404,"headers":{},"body":{"error":{"code":"Request_ResourceNotFound","message":"..."}}}]}`;
sub-request ids are the 1-based positions in the **whole** request list, as strings (`AzGraphClient.WireId`): the
first chunk of 20 carries `"1"`–`"20"`, the second chunk `"21"`–`"25"`, and a reply must echo those ids or the
sub-request is treated as unanswered and retried individually.

## What the Az contract tests should cover (WI-04 acceptance)

1. argv shape for `rest` and non-rest commands equals `AzRequest.BuildAzArguments` (body path substituted); `--url` never contains `?`.
2. Quoting through real pwsh: `$filter=displayName eq 'a b' & c`, a `%` in a value, `"` in a value, non-ASCII, and `'` doubling via `GraphPath.AlternateKey` all arrive unchanged in the recorded argv.
3. Non-ASCII stdout round-trips into `AzResult.StandardOutput` / `GraphResponse.Body`.
4. Body file: recorded content equals `request.Body.ToJsonString()` for ASCII bodies and is semantically identical otherwise (the file uses relaxed escaping: BMP characters raw, astral characters such as emoji surrogate-escaped by System.Text.Json); request, body and output files are zeroed (verified through `ENTRAARM_FAKE_AZ_LINK_DIR`) and gone after the call (and after a timeout, and after cancellation).
5. Timeout: `AzRequest.Timeout = 2 s` + `sleepSeconds: 20` → `TimedOut`, exit -1, elapsed ≈ 2 s, no leftovers.
6. Error mapping: each stderr shape above → `GraphException` with the expected `Status`, `Code`, `IsTransient`, `IsAlreadyExists`, `IsReplication` (only with `RetryHint.ExpectReplicationLag`), `RetryAfter = 3 s`; `az login` → `AzCliException.Failure == NotLoggedIn`; connection error → `Transient`; exit 127 → `NotInstalled`.
7. Retry table via fixture sequences with a fake `IDelayProvider` that records delays: 429×2 then 200 → delays `[3 s (Retry-After)]` or `[2 s, 4 s]`; 503×6 → throws after 5 retries; replication 404×3 then 200 under `ExpectReplicationLag` → three 5 s delays, and under `Default` → `GetAsync` returns null with no retry; 401 then 200 → one `account get-access-token` call in the argv file; `already exists` → thrown immediately with `IsAlreadyExists`.
8. Pagination: first reply carries an absolute `@odata.nextLink` with `$skiptoken=...`; the second recorded argv has `--url` without query and `--url-parameters` containing `$skiptoken=...` (decoded) plus the original headers.
9. `$batch`: 25 requests → two `POST .../$batch` argv entries with 20 and 5 sub-requests (ids `"1"`–`"20"` and `"21"`–`"25"`); a failed sub-response (status 429 or 404) is retried as an individual call; an `already exists` sub-response is not retried and surfaces as `BatchResponse.Error.IsAlreadyExists`.
10. Only pwsh is launched: `PwshAzRunner.BuildStartInfo(...)` (internal, visible to tests) has `FileName` ending in `pwsh`/`pwsh.exe`, `UseShellExecute == false`, and an `ArgumentList` beginning `-NoProfile -NonInteractive -NoLogo -ExecutionPolicy Bypass -File`; no `cmd.exe` anywhere.
11. No secret in logs: a capturing `ILogger` at Trace sees neither the body's `password` value nor the `secretText` from the reply nor the `Authorization` header value; `SecretRedactor` unit cases.
12. `PwshLocator`: `TryParseVersion("7.6.6")`, `"7.5.0-preview.3"`; a bogus `AzOptions.PwshPath` → `ToolchainException`.
13. `AzPreflight`: scripted `az version` + `az account show` + `GET /organization` → result populated; `az login` failure → `NotLoggedIn` whose message contains `az login --allow-no-subscriptions`.

Where they live (`tests/EntraArm.Az.Tests`): 1-4 `AzArgvContractTests`; 4, 5, 10 and exit 127 `AzRunnerLifecycleTests`;
6 `GraphErrorMappingTests`; 7 `RetryPolicyTests` (every row of plan §3.6 through `Decide` and `ExecuteAsync`) and
`AzGraphClientRetryTests` (the fixture sequences through the fake); 8 and status inference `AzGraphClientTests`;
9 `AzGraphClientRetryTests`; `GraphPath`/`GraphRequest` `GraphPathAndRequestTests`; 11 `SecretHandlingTests`;
12 `PwshLocatorTests`; 13 `AzPreflightTests`. Run them with `pwsh -File test.ps1 -Target EntraArm.Az.Tests`
(add `-Filter "FullyQualifiedName~<class>"` to narrow, `-Detailed` for full output).
