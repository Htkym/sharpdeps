# SharpDeps / CodeGraph comparison preparation

This directory holds the reproducible preparation for the fixed Small/Large
comparison between SharpDeps and CodeGraph. It prepares inputs and entry
points only; it does not run the comparison and publishes no results.

## Fixed data

- `pins.json` — corpus repositories and commits, target solutions, the
  CodeGraph package version and the registry integrity values (main package
  plus the win32-x64 and linux-x64 platform packages).
- `questions.json` — evaluator-only fixed questions and expected facts.
  This file must never be copied into an indexed corpus copy or passed to
  either tool.

## Entry points

- `prepare.ps1` — fetches each pinned corpus once, creates one isolated
  copy per tool, installs the pinned CodeGraph package into a private
  prefix/cache with `--ignore-scripts` (integrity checked against
  `pins.json` first), and builds `SharpDeps.Cli` from this checkout,
  recording the source commit in `build-info.json`. Every step logs its
  exit code and output under `<Root>/o/logs` and stops at the first
  failure.
- `smoke.ps1` — builds a minimal two-file C# fixture and runs the real
  runner against it, verifying the collection path, the byte/character
  counting, and the stop conditions (sufficiency and the follow-up cap).
- `run.ps1` — the comparison runner for a later phase: per corpus it
  separates setup (restore/index, CodeGraph init) from query calls,
  runs one initial native call per question and tool, then evidence-gated
  follow-ups (at most 2 per question, at most 36 calls in total), and
  records raw UTF-8 bytes and decoded UTF-16 code units of non-TTY
  stdout per call. CodeGraph is initialized with a plain `init` — never
  `--yes`, which would auto-install git sync hooks when the file watcher
  is unavailable.

Local layout under the root (`D:\scg` by default on Windows, or
`-Root`/`$env:SCG_ROOT`): `i` inputs, `t` tools, `c` caches, `o` outputs.

The GitHub workflow `.github/workflows/codegraph-comparison.yml` exposes
the same prepare/smoke stages by explicit `workflow_dispatch` or by a
`[comparison]` marker in a push's head commit message; a normal push
never starts it.
