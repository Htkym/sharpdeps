# Syntamark 2.0.0 adoption

## Selected pair

Normal CLI/MCP builds select the official [Syntamark 2.0.0 release](https://github.com/Htkym/syntamark/releases/tag/v2.0.0).
The runtime and Source references are both exact `[2.0.0]`. Source is restored
and byte-verified for provenance; its code is not compiled into SharpDeps.

- Source/tag: `47c997fb91315e8f73849f47610d2f2b21f811ba`, `v2.0.0`.
- [Producer run 43725418400](https://github.com/Htkym/syntamark/actions/runs/43725418400).
- [Publication run 43727344782](https://github.com/Htkym/syntamark/actions/runs/43727344782).
- The unchanged upstream `pair-manifest-2.0.0.json` in `eng/markdown/feed` has
  SHA256 `e3bbb8ed0d3a1c55ef0c43ce74225b8899ca4e74069588cf2d3420a8a05f958d`.
- `docs/contracts/markdown-runtime-pin.json` binds the package hashes, runtime
  DLL, source commit, parser stamp, contract `2.0`, and legacy profile
  `lithosharp-markdown/1`. Loaded facts must also match `native-yaml/1`.

The fixed feed contains the original archives published as GitHub Release assets.
The NuGet archives have different archive hashes because NuGet adds
`.signature.p7s`. All 12 runtime and 49 Source payload entries were compared with
the sealed originals and matched exactly. The signed archive hashes are
`527d0a1c601fcd694525af2a41961a0122275fed0010b62403a5adaca350e804` and
`841f9db81096cc88510f404f7a6f9e7847e149cecccd69107ac8e61bd0cd13f5`, respectively.
The repository feed keeps the original manifest and archive identity together;
it does not repackage either version. Historical feeds remain unchanged.

## API and behavior scope

The formal source has no C# implementation or public API changes relative to the
previous private candidate `51b36fa`. Its changes are documentation and CS1591
suppression in runtime project files. The canonical source hash and parser stamp
are unchanged from that candidate; package versions and runtime DLL bytes are
not the same. Equality of the canonical source hash is not a substitute for
consumer execution or installed-package verification.

Native YAML and its invalid-frontmatter checks now apply to ordinary builds.
The accepted conformance fixtures and their hashes remain unchanged. Added YAML
profile and disabled footnote capability assertions are checked separately.
The adapter requires parsed frontmatter before admitting titles, metadata or
explicit symbol bindings. The CLI rejects incomplete Markdown publication and
keeps the previous committed generation.

The `syntamark-docs/2` profile, footnotes and directive attributes are not enabled.
Guarded edits and composed source maps are not needed by this read-only indexer.
Selected-analysis APIs do not replace its all-facts graph projection. No
additional parser or speculative API adapter is introduced.

Incremental sessions remain opt-in with `SharpDepsUseMarkdownSessions=true`.
The old preview-selection property is retired. See [session cache](markdown-session-cache.md)
for lineage, cancellation, fallback and publication rules. Correctness evidence
does not establish lower latency or memory use.

## Consumer acceptance

On Windows with SDK 10.0.404 and testhost .NET 10.0.15, the frozen source change
passed 93 selected cases: 39 default Markdown, four default CLI, 49 session-enabled
Markdown and one session owner case. Each group required its exact selected TRX
inventory, with no failures, skips or aborted results. Both restored runtime/Source
pairs and the loaded DLL matched the formal pin. Session lockfiles remained
unchanged during locked restore. The five artifact-gate tests also passed.

These are source checks, not installed-package acceptance. Actual CLI/MCP tool
transport and Markdown update checks are still pending for the resulting committed
head. The historical private-candidate passes and the one non-reproducing semantic
three-test run do not count as formal-pair results. Correctness checks do not
establish latency or memory improvements. This source change is not a published
SharpDeps release.
