# Quick baseline fixture

Regression fixture for the SDK-free **Quick** analyzer (`analyzer/`), covering the
behavior that v0.1.0 must not regress. It is intentionally small, fully offline
(no `PackageReference`), and carries a known-correct expectation.

## Layout

```text
Baseline.sln                  solution with two solution folders ("src", "tests")
src/App                       app (Exe), refs Core, Util, and Extras (Release-only)
src/Core                      library, multi-TFM (net10.0;net8.0), refs Util
src/Util                      library, refs Core (project cycle with Core)
src/Extras                    library, referenced only by the conditional ref
tests/Core.Tests              test project (IsTestProject), refs Core
native/Native.vcxproj         non-C# project (Quick must keep listing it)
```

Deliberate properties:

- **Project cycle**: `Core` ↔ `Util`. Quick reports it as a declared
  `ProjectReference` cycle (v0.1.0 must keep reporting it, but as an inferred
  relationship rather than a proven one).
- **Conditional reference**: `App → Extras` only exists for `Configuration=Release`.
  Quick records the edge and warns that conditions are not evaluated.
- **Namespace cycle**: `Core` ↔ `Util` via `using`.
- **Inferred-only edge**: `Core.Tests → App.Services` exists because of an unused
  `using App.Services;`. Semantic analysis must *not* report it (FX-01).
- **Alias using**: `using Legacy = Core.Legacy;` must stay excluded from Quick edges.
- **Global using**: `global using Util;` in `src/Core/GlobalUsings.cs` applies to the
  other files of the Core project.
- **Multi-TFM**: Quick reports the first TFM (`net10.0`) for `Core`.

`dotnet build Baseline.sln` is expected to fail because of the deliberate project
cycle; the fixture is only meant to be parsed.

## Expected snapshot

`expected/quick-report.json` is the normalized analyzer output
(`--max-projects 60 --max-edges 200`, the extension defaults). Absolute paths are
replaced with `<FIXTURE_ROOT>` and separators are normalized to `/` by
`tests/helpers/quickReportNormalizer.ts`.

`expected/quick-v2-structure.json` is the report v2 structure (entity labels
instead of ids, so it is machine independent) produced by
`tests/helpers/quickV2Snapshot.ts`. Both files are generated artifacts and are
excluded from Prettier.

The snapshots are compared by `tests/analyzer/quickBaseline.test.ts` (v1) and
`tests/analyzer/quickV2.test.ts` (v2), which run the published DLL and therefore
skip when `analyzer/bin/**/code-map.dll` is absent.

Refresh it only for an intentional analyzer change:

```powershell
npm run build:analyzer
$env:SHARPDEPTS_UPDATE_BASELINE='1'; npm test -- tests/analyzer/quickBaseline.test.ts
```

A changed snapshot is a behavior change: explain the reason in the commit message.
