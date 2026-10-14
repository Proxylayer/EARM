# Invalid template fixtures

Each `<case>.entra.json` is a template that `entraarm deployment validate` must reject. The sidecar
`<case>.expected.json` states what the run must produce:

```json
{ "expectedCodes": ["MissingApiVersion"], "exitCode": 2 }
```

- `expectedCodes`: every listed diagnostic code must appear at least once in the output (`-o json`
  `diagnostics[].code`); extra diagnostics are allowed.
- `exitCode`: the process exit code (2 = validation errors, 1 = the file could not be loaded).

Quick check from the repo root:

```powershell
Get-ChildItem tests/fixtures/templates/invalid/*.entra.json | ForEach-Object {
  $expected = Get-Content ($_.FullName -replace '\.entra\.json$', '.expected.json') | ConvertFrom-Json
  $json = dotnet run --project src/EntraArm.Cli -- deployment validate -f $_.FullName -o json | ConvertFrom-Json
  $missing = $expected.expectedCodes | Where-Object { $_ -notin $json.diagnostics.code }
  "{0,-48} exit={1} {2}" -f $_.Name, $LASTEXITCODE, ($(if ($missing) { "MISSING: $missing" } else { 'ok' }))
}
```
