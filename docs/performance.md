# SharpDeps performance measurements

Measured on 2026-09-27 with SDK 10.0.300, Windows, Intel Core Ultra 7 258V (8 logical processors). The checked-in evidence contains raw samples, environment, fixture definition and host hash. These measurements do not predict other hardware.

## Fixed Medium input

`npm run perf` creates 30 projects and 3,000 C# files. The project chain, local cycles, external types and a repeated relation produce 102,535 evidence records. Three fresh analyzer processes run the same input. Restore is excluded; the first run and subsequent file-cache-warm runs are retained. No claim of cold OS caches is made.

| Measurement | Result | Budget |
|---|---|---|
| Medium Semantic, three runs | 19.12 / 16.18 / 16.17 s | 120 s |
| Sampled analyzer tree peak | 631 / 991 / 531 MiB | 2 GiB |
| Host search p95, 25 samples | 0.315 ms | 250 ms |
| Host entity detail p95 | 0.075 ms | 100 ms |
| Host local projection p95 | 210 ms | 2 s |
| Host evidence page, 100 actual records, p95 | 0.740 ms | 300 ms |
| ELK + SVG, 100 nodes / 200 edges, maximum of 3 | 327.2 ms | 2 s |
| ELK + SVG, 300 nodes / 1,000 edges, maximum of 3 | 1,030.6 ms | 5 s |
| Browser selection-to-frame p95, 300 nodes | 18.0 ms | 100 ms |
| Actual VS Code Stop-to-cancelled, maximum of 20 | 23.8 ms | 250 ms |
| Owned process stop + verification, maximum of 20 | 1,052 ms | 5 s |

The browser fixtures use an actual ELK worker, not a mocked layout. A timer continues running while layout executes. Layout cancellation and table fallback are browser-tested. Host timings include cached indexes after one warm-up; they do not include a VS Code message round trip. UI selection timings measure DOM update through the next animation frame. Memory samples may miss peaks between samples.

Raw data: [Medium](implementation/v0.1.0/evidence/sd-028-acceptance.json), [browser](implementation/v0.1.0/evidence/sd-028-browser.json), [UI and Stop](implementation/v0.1.0/evidence/sd-030-experience.json), [owned processes](implementation/v0.1.0/evidence/sd-028-lifecycle.json).

## Bounds and retention

The Medium report is about 59 MB. The report cap is 128 MiB, with independent evidence/declaration bounds; see [ADR-0004](adr/0004-medium-result-capacity.md). Analysis is never truncated to satisfy the display budget.

The controller retains the latest two successful results and the latest two other attempts, plus in-flight runs. Evidence remains available after a cancellation or failed analysis. Twenty real Semantic cancellation cycles left no captured owned PID and no more than four completed run directories. This count is based on owned process IDs, not unrelated dotnet processes on the machine.

The earlier 400-file Quick benchmark is historical and is not used as Medium acceptance evidence. Reproduce the current checks using `npm run perf`, `npx playwright test performance --workers=1`, and `node scripts/measure-lifecycle.js` on Windows. Keep other test/build workloads idle during timing runs.

## CI browser verification

The Ubuntu 24.04 run for product commit `c0902fa` passed the same unchanged budgets: maximum layout plus SVG serialization 526.2 ms for 100/200 and 1,940.7 ms for 300/1,000; selection p95 16.8 and 19.5 ms respectively. Selection now changes only the affected SVG elements’ selection attributes. Browser tests run with one worker to isolate the timing fixture. These CI measurements are separate from the Windows results above. [Raw CI samples](implementation/v0.1.0/evidence/sd-028-browser-linux-ci.json).
