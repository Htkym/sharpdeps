# SharpDeps — Copilot instructions

SharpDeps is a VS Code extension that visualizes a .NET solution's dependencies as an
interactive Mermaid graph (project- and namespace-level) and flags circular dependencies.
It spans two languages that are built and shipped together:

- **TypeScript** — the extension host (`src/`) and the webview client (`media/viewer.ts`).
- **C#** — a standalone Roslyn analyzer (`analyzer/code-map.cs`) that parses the solution and
  emits a JSON report. It does **not** use MSBuild; it reads `.sln`/`.csproj` with
  `Microsoft.CodeAnalysis.CSharp` only, so it runs on a .NET runtime without the SDK.

## Build and validate

```bash
npm install
npm run build:analyzer   # analyzer/code-map.cs -> analyzer/bin/code-map.dll (needs .NET SDK 10)
npm run build            # esbuild: bundles host + webview (add --production; npm run watch for --watch)
npm run compile          # tsc --noEmit — the type-check gate; run this after any TS change
npm run lint             # eslint (flat config) over src/ and media/*.ts
npm run format:check     # prettier --check (npm run format to apply); markdown is excluded
npm run test             # vitest run — unit tests for pure logic (npm run test:watch to watch)
npm run package          # VSIX via vsce (vscode:prepublish reruns build:analyzer + a production build)
```

- The automated validation gates are `npm run lint`, `npm run format:check`, `npm run compile`
  (strict `tsc --noEmit`), and `npm run test` (Vitest). Run them before considering a TypeScript
  change done; CI (`.github/workflows/ci.yml`) runs the same gates plus `npm run package`.
- **Unit tests cover vscode-free logic only.** Pure functions (e.g. `view/viewModel.ts`,
  `diagnostics/cycleAnchoring.ts`) are unit-tested with Vitest; modules that import `vscode` are
  not. Keep testable logic in vscode-free modules and have the thin `vscode` wrapper consume them
  (see `cycleAnchoring.ts` feeding `cycleDiagnostics.ts`).
- Press **F5** ("Run SharpDeps Extension") to launch an Extension Development Host. Its
  `preLaunchTask` is `npm: build` (esbuild only) and does **not** rebuild the analyzer DLL — run
  `npm run build:analyzer` yourself after editing `analyzer/code-map.cs`.
- Building the analyzer DLL requires the **.NET SDK 10**; end users only need the .NET runtime.

## Architecture

The flow lives in `src/extension.ts` and chains single-purpose modules:

```
command / .sln right-click
  -> solution/resolveTarget.ts   pick the .sln (arg, active editor, workspace, QuickPick)
  -> runtime/ensureDotnet.ts     resolve a dotnet executable
  -> analyzer/runAnalyzer.ts     run the analyzer, parse report.json into CodeMapReport
  -> view/viewModel.ts           CodeMapReport -> CodeMapViewModel
  -> view/codeMapPanel.ts        singleton webview tab; media/viewer.ts renders Mermaid
  -> diagnostics/cycleDiagnostics.ts   publish cycles to the Problems panel
```

`esbuild.js` produces two independent bundles with different targets:

- `src/extension.ts` -> `out/extension.js` (Node, CJS, `vscode` external).
- `media/viewer.ts` -> `media/viewer.js` (browser, IIFE, **Mermaid bundled inline** so rendering
  works offline with no CDN).

## Key conventions

- **`src/view/protocol.ts` is the single source of truth** for host↔webview messages and the view
  model. The host (`src/`) and the webview (`media/viewer.ts`) both import it. When you change a
  message or view-model shape, update both sides in the same change.
- **`src/analyzer/types.ts` mirrors the analyzer's JSON** (camelCase, serialized by
  `CodeMapJsonContext` in `code-map.cs`). If you change the C# report shape, update these TS types
  to match. `media/viewer.ts` type-imports from both `../src/view/protocol` and `../src/analyzer/types`.
- **`analyzer/code-map.cs` is the authoritative analyzer.** It is a file-based C# app whose
  `#:package` / `#:property` directives drive `scripts/build-analyzer.js`. After editing it, run
  `npm run build:analyzer` to regenerate the DLL. `tsconfig.json` excludes `analyzer/` (it is C#).
- **Analyzer resolution / dev fallback:** `locateAnalyzer` prefers the shipped
  `analyzer/bin/code-map.dll` and falls back to running `analyzer/code-map.cs` source via
  `dotnet run` when the DLL is absent (the pre-build dev path).
- **`representativeFile` is set only on namespace diagram nodes; it is null for project nodes.**
  Cycle diagnostics anchor accordingly: project cycles to the `.csproj` (relative path joined to
  the solution dir), namespace cycles to `representativeFile`; unanchored namespace cycles go to the
  SharpDeps output channel.
- **Webview security:** `view/html.ts` emits a strict nonce-based CSP with **no `unsafe-eval`**, and
  `localResourceRoots` is limited to `media/`. Keep Mermaid working under this CSP — do not introduce
  `eval`/`new Function` paths.
- **One panel at a time:** `CodeMapPanel` is a singleton. It toggles the `sharpdeps.panelActive`
  context key, which gates the refresh/copy/export palette commands in `package.json`.
- **Generated artifacts are gitignored** and must not be committed: `out/`, `media/viewer.js(.map)`,
  `analyzer/bin/`, `analyzer/obj/`, `*.vsix`.
