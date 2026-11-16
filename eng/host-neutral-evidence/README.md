# Host-neutral selective evidence verification

The shared feature is the ordinary MCP `explore` tool. It does not rewrite model
requests, remember what a model has seen, or silently replace earlier source with
references. The separate OpenCode request-rewrite experiment is not this feature.

## Contract

- Omitted response mode preserves the legacy behavior. Explicit evidence mode is
  suitable for known targets; overview is optional discovery, not a mandatory first call.
- `includeEvidenceRefs` emits bounded saved-selection guards. A subsequent exact
  occurrence plus project/variant and `evidenceRef` validates the saved declaration
  set before reading source. It is not authorization or proof of current context.
- Every read still applies source hashes, trust, freshness and budgets. A mismatched
  guard yields no body. Re-fetch on a new connection does not depend on earlier text.
- Explicit agent overview/evidence defers verbose signatures and repeated metadata.
  Detail/audit can inspect these. If a requested type does not fit, eligible member
  ranges inside that same type can provide partial evidence. The omitted parent and
  the partial nature remain explicit; unrelated bodies and larger budgets are not implied.

## Offline output study

`offline-verification.json` records six saved queries across Zen.MVVM, EF Core and
Orleans, their frozen identities, tested assembly/source hashes, and all outcomes.
No model was called and no index/source was mutated. The initial guard-only negative
results are retained, including the type query that returned no source within budget.

| Path | Total UTF-8 output bytes |
| --- | ---: |
| Legacy, one call per query | 59,455 |
| Overview plus first emitted exact occurrence | 79,635 |
| Direct evidence, original name query | 48,847 |

Overview plus follow-up is **33.9% larger** than legacy in aggregate. It must not be
recommended unconditionally. Direct same-query evidence is 17.8% smaller, but
intentionally returns different amounts of source evidence. This is not an equal-quality,
token, price or latency comparison. The final explore schema adds 593 bytes, and guard
arguments add further input. Provider caching and model selection are unmeasured.

All six final round trips returned source, matched unguarded exact output, rejected an
altered guard without source, and returned identical evidence from a new server connection.
Choosing the first emitted occurrence is deterministic, not a claim of task relevance.

## Native host acceptance

`native-verification.json` records exact installed versions, routes and assembly hashes.
Native transport acceptance is distinct from the generic MCP study above. Codex and
Copilot use direct native MCP APIs without a model turn. OpenCode and Claude Code use
fixed loopback provider fixtures, not remote inference, to exercise native MCP paths.
Native Claude testing exposed rejection of the previous top-level `anyOf` schema. The portable schema removes that combinator while retaining conditional server-side input validation and regression tests.
Only the explicitly listed tested host editions/versions are covered; a Copilot CLI result
is not a VS Code extension or cloud-agent result.

The acceptance cases are overview omission plus guard, exact full evidence, altered
selection guard rejection, freshness rejection, and a new empty session/process re-fetch.
This last case simulates loss of prior context; it does not inspect a provider's compaction
implementation. Local fixture usage/cost estimates are not incurred AI charges.

## Release gates

Four-host native results must be complete for the final assembly. A separately approved
real-AI comparison must then assess task quality, tool selection, total input/output,
latency and cost. Its scope and spending ceiling need owner approval before execution.
Release remains blocked until those results are presented and the owner decides to proceed.
