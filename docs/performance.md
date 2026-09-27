# SharpDeps performance measurements

Measured with `npm run perf` (SD-028). Numbers come from a fixed synthetic fixture and a
real solution; nothing here is estimated. Re-run the script to reproduce them on another
machine, because wall time depends on the CPU and disk.

## Fixture

- `.local/perf/big`: 400 generated C# files, 40 namespaces × 10 types, each type using two
  types from the previous namespace and one from its own (a deterministic fan, so edges and
  cycles are exercised).
- Semantic runs against `tests/fixtures/semantic-baseline/SemanticBaseline.sln` (6 project
  variants, one of them multi-TFM, plus an in-memory source generator).

## Results (2026-09-27, Windows 11, .NET 10 SDK)

| Measurement | Value |
|---|---|
| Quick, 400 files, median of 3 | **559 ms** (min 523, max 578) |
| Quick report size | 135 KB (`report.json`) |
| Semantic, baseline solution (5 runs averaged by the tool) | **5.4 s** |
| Semantic peak working set | **170 MiB** |
| Semantic output (report + evidence + declarations) | 116 KB |
| dotnet processes before/after the measurement | 8 / 8 (no leftovers) |
| Measurement directories left behind | 0 |

### What was not measured

- **Peak memory of the Quick host**: the process finishes in well under the sampling
  interval (250 ms), so the sampler records 0. Treat Quick memory as unmeasured.
- **Cancel response time and UI blocking**: covered structurally (the controller stops
  cooperatively and then kills the tree; the webview only receives messages) and by the
  unit tests, not by a timed measurement.
- **Large solutions**: the fixture is deliberately synthetic; a several-thousand-file
  solution has not been measured.

## Cleanup

The controller creates one run directory per analysis under the work root and keeps the
newest two, removing older ones. The store reads evidence from the newest directories on
demand, so deleting them immediately (as an earlier version did) would break evidence
paging; `analysisController.test.ts` pins both the retention and the pruning.
