# Host-neutral selective evidence verification

The shared feature is the ordinary MCP `explore` tool. It does not rewrite model
requests, remember what a model has seen, or silently replace earlier source with
references. The separate OpenCode request-rewrite experiment is not this feature.

## Contract

- No response mode is forced when omitted. The 27fba1e study preserved legacy
  selection; the follow-up now prioritizes search-selected member roots over related
  bodies in omitted/detail mode. This can change the returned source set within the
  same budgets. Explicit evidence is suitable for known targets; overview remains
  optional discovery, not a mandatory first call.
- `includeEvidenceRefs` emits bounded saved-selection guards. A subsequent exact
  occurrence plus project/variant and `evidenceRef` validates the saved declaration
  set before reading source. It is not authorization or proof of current context.
- Every read still applies source hashes, trust, freshness and budgets. A mismatched
  guard yields no body. Re-fetch on a new connection does not depend on earlier text.
- Explicit agent overview/evidence defers verbose signatures and repeated metadata.
  Detail/audit can inspect these. If a requested type does not fit, eligible member
  ranges inside that same type can provide partial evidence. The omitted parent and
  the partial nature remain explicit; unrelated bodies and larger budgets are not implied.

## Offline output study (27fba1e baseline)

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
token, price or latency comparison. At the 27fba1e study revision, the explore schema
added 593 bytes relative to its main baseline, and guard arguments added further input.
That historical delta is not the final follow-up schema size. Provider caching and
model selection were unmeasured in this offline study.

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

## Real-AI result and follow-up (not yet a quality re-test)

The approved 12-session OpenCode comparison of main 40a5b5b and candidate 27fba1e
was negative on the intended efficiency/quality goal: all-session total tokens rose
2.9%, tool-output bytes rose 6.0%, and rubric scores fell from 48/57 to 47/57.
The all-session totals include two tool-call-limit violations; they are not presented
as a protocol-compliant success. These figures describe that completed study, not a
quality re-test of later changes.

`follow-up-verification.json` keeps the subsequent local checks separate:

- A five-line selection-priority correction gives search-selected member roots their
  turn before related bodies for omitted/detail mode, without changing matches, scopes or budgets. Overview/evidence selection is unchanged.
  On 14 saved calls, 11 outputs are identical; three EF Core calls now include missing
  requested declarations. Bytes change from 115,919 to 115,559. Those additions cover
  118 lines explicitly cited in the saved answer; removed lines were not cited there.
  Uncited does not mean unnecessary, and future tool-call or quality gains are untested.
- MCP wording is shorter: schema 3,567 to 3,028 bytes, tool description 1,707 to 1,182,
  initialization instructions 2,116 to 465 (2,715 bytes across these measured fields).
  The known-target example now names evidence explicitly; that is intentional model guidance, not merely text shortening. Runtime defaults remain unchanged. This is not a measured provider-token or per-session saving.
- The default Member-metadata dedup and startup default-evidence flag experiments
  were not adopted; their evidence/patches were preserved separately. No startup
  flag forces evidence mode. The priority correction changes source selection and
  must not be described as lossless compression.

Scoped ExplorationTests passed 141/141 and McpTests/McpFlowTests 28/28. All 14 replay calls returned without
error; the 383 frozen comparison inputs remain unchanged. Final native acceptance is recorded in `native-follow-up-verification.json`: normal outputs agree with direct MCP across all four hosts, with documented host wrapping/newline differences for rejected calls. These checks do
not overturn the failed real-AI comparison or authorize release.

## Short-type candidates and overview row consolidation

`query-output-verification.json` records a later deterministic check using the same
14 saved queries plus five queries from the subsequent natural-use study. It does
not modify either paid dataset or measure model quality for these new changes.

- Exact type short-name aliases now participate alongside same-named members using
  the existing prepared lookup. A collision remains ambiguous, not type-first.
  Eighteen of 19 type-only replay results are identical. `PlacementStrategy` now
  includes the previously hidden type in its scope's choices; one property row moves
  to explicit omission under the unchanged display budget. This changes candidate
  selection and is not a lossless-selection claim.
- Agent overview combines a root's symbol metadata with its saved pointer, retaining
  selector, label, signature, scope, path, hash and span. Related information remains.
  Other response modes and audit are unchanged. The plain-text row shape changes;
  external parser compatibility is not guaranteed. Inverse row comparison preserves
  all prior information except the updated relation-omission count in one case.
- Against the separate type-only baseline, the 14-query total changes from 115,559
  to 115,159 UTF-8 bytes; the natural five-query total changes from 38,510 to 38,151.
  One `Detect` result **increases 121 bytes** because two related relations, two symbol
  aliases and two file rows now fit; relation omissions fall from 31 to 29. Root
  candidate counts, source ranges/text and important diagnostics are unchanged.
  Total bytes are not guaranteed to decrease monotonically.

The full MCP Release build succeeded, ExplorationTests passed 147/147, and the
limited McpTests/McpFlowTests passed 28/28. All 19 full-build argument/result objects
match the separately checked final isolated artifact. Independent code and inverse
replay review passed; no AI calls occurred and cleanup left no owned processes.
This is not a new four-host acceptance or evidence of model-quality/token/cost gains.
After the CLI packaging freeze, the normal `UseAppHost=false` build and the same
147/28 tests passed again; all 19 argument/result objects matched the previous
full-build results. The refresh hashes are retained separately in the JSON record.

## Release gates

Four-host native results must be complete for the final assembly. The failed historical
real-AI comparison is retained above. Any further real-AI comparison of the follow-up
requires separate owner approval for its scope and spending ceiling, and must assess
task quality, tool selection, total input/output, latency and cost.
Release remains blocked until those results are presented and the owner decides to proceed.
