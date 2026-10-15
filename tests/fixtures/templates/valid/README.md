# Valid template fixtures

Templates that `earm deployment validate` accepts (exit code 0 once every parameter has a value). They exist
for tests that need a specific shape rather than the full `samples/orders-api.entra.json`:

- `parameter-types.entra.json`: one parameter per type (`int`, `bool`, `object`, `array`, `securestring`,
  `secureObject`, `string` with a default) and a single resource. Used to exercise `-p name=value` conversion,
  `ParameterValueTypeMismatch`, `MissingParameterValue` and secure-value tainting.
- `orders-api.engine.entra.json`: `samples/orders-api.entra.json` with the Entra stub functions (`wellKnownApp`,
  `permissionScope`, `appRole`, `roleTemplate`, `map`/`lambda`, `c.slug`) replaced by literals so the engine tests
  (`tests/EArm.Tests/Engine`) can run a full what-if and create through `EchoProvider` against `FakeGraphClient`.
  It keeps the nested children, the FIC copy loop, the `condition`, the symbolic service principal name, the
  GUID-named assignments and the existing-resource lookups (`cio@contoso.com`, `contoso-all-staff`, the Microsoft
  Graph service principal, which the tests seed). Parameters `env` and `initialPassword` are passed inline.
