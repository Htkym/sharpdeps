# Offline advertised-contract gate

Standard-library Python checker for an evaluation harness, not a new SharpDeps
tool. It performs no provider requests, native tool execution, installation,
index updates or automatic correction. No credentials are required.

Two past failure classes are covered by regression tests:

* Advertised `maxChars=1000000`, enforced maximum `12000`.
* Advertised names / `discover`, while the response consumer accepts only exact
  occurrence selectors in explicit scopes.

## Inputs and invocation

`--advertised` takes the actual model-visible JSON envelope with a `tools` list
(each tool has `name` and `inputSchema`). A saved `model-visible-contract.json`
is suitable. Do not use the upstream native schema when an adapter changes it.

`--effective` takes an **independently maintained** declaration of the enforced
input contract, with the same `tools` structure plus:

```json
{
  "format": "sharpdeps-effective-contract-v1",
  "enforcement_source": "Reviewed guard/consumer revision and boundary-test references",
  "tools": []
}
```

Replace the empty list with every advertised tool's enforced schema. Empty or
partial tool lists fail. Include all numeric minima/maxima/defaults, required
fields, enums, patterns and allowed properties. Declare
`additionalProperties: false` explicitly. Derive this file from reviewed runtime
guards and consumer support, **not by copying the advertisement**. The test
fixture shows the exact-selector shape but is not a production policy.

```powershell
python -B comparison/contract-check/check_contract.py --advertised model-visible-contract.json --effective effective-contract.json
if ($LASTEXITCODE -ne 0) { throw 'Contract mismatch: do not start provider execution' }
```

Run this before provider authorization in the caller. Exit 0 means declaration
equality; exit 2 means mismatch, malformed input, or unsupported schema. The
checker cannot stop a caller that ignores its exit code. Freeze both input
hashes (returned on success) with the run and verify that those same inputs are
used at launch; never repair, clamp, or expand budgets after a failure.

Supported schema vocabulary is deliberately small: explicit primitive/object/
array types, closed properties, required, enum, numeric bounds, string length
and pattern, array items/length, and defaults. Unknown constraints, `$ref` and
schema combinators fail closed. Descriptions/titles/examples/comments are
ignored; required/enum order is immaterial. Patterns are compared literally,
not proven semantically equivalent. Default differences are contract failures.

## Validation and limits

```powershell
Set-Location comparison/contract-check
python -B -m unittest -v
```

Tests are synthetic and include the two historical mismatch shapes plus CLI
exit-code checks. No frozen `sdfx*` kit is modified. This checker does not claim
full JSON Schema validation, prove that Python/.NET implement a declared guard,
validate response provenance, or establish native/consumer compatibility for
all successful requests. Keep actual boundary calls, native response replay,
consumer validation, same-session host smoke, and paid-budget gates as separate
requirements. A string naming an enforcement source is traceability metadata,
not cryptographic proof of implementation. No completed measurement or model
quality/cost result is produced by these tests.
