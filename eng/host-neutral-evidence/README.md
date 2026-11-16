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

## Optional locations for ambiguous candidates

`candidate-location-verification.json` records a later display-only follow-up to
`0ca0381`. Non-Overview Agent candidate rows add kind, saved relative path and
`savedRawSpan(start,length)`, with explicit omitted-location counts. These UTF-16
saved spans are not line numbers, current-source verification or active-code proof.
Overview retains its existing Saved pointer metadata without duplicate suffixes.

Metadata is enabled only in final rendering. If it exceeds the existing wire
budget, the old row format is restored before removing existing information or
adding an output-limit status. Ranking, roots and ambiguity decisions are unchanged.
ExplorationTests passed 151/151; MCP Release build succeeded with zero warnings.
Across 21 saved-argument replays, 18 results are byte-identical. Three
PlacementStrategy results each increase by 550 UTF-8 bytes. Removing only the new
suffixes reproduces every original result object, including source, diagnostics,
freshness and the existing no-match response. Cleanup leaves no owned processes.

A separate saved `AutoDetectChangesEnabled` evidence condition (shared by two
paid calls) was also checked once against each artifact: 2,920 → 3,187 UTF-8 bytes
for two candidate rows. The suffix inverse preserves the full result object,
including selection and status; cleanup again leaves no owned processes. This
additional condition is not included in the original 21-call count.

At the time of the 21-call replay and separate evidence check above, the prior
real-AI comparison had evaluated `0ca0381`, **not this later display addition**.
Those offline checks made no new model calls or adoption, quality, token or cost
claims. The subsequently approved four-session study is recorded separately below.
Neither study is a new four-host native acceptance; plain-text additions may affect
external parsers.

## Four-session candidate-location real-AI follow-up

`candidate-location-real-ai.json` records the approved comparison of `0ca0381`
(before) and `631e830` (after). Two natural tasks, NH03 EF Core and NH04 Orleans,
were each run once per condition: four fresh sessions, OpenCode 2.2.0 with the same
`openai/gpt-6-sol`, xhigh reasoning and normal/default service tier. The request-reuse
adapter was OFF in both conditions. Task prompts and common instructions were
unchanged; use of the product or its hints was not forced. Order was NH03 before/after,
then NH04 after/before. There were no retries.

| Task | Condition | Tokens | Host-estimated USD | Requests | Tool calls | Tool-output bytes | Quality |
| --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| NH03 EF Core | Before | 49,433 | 0.0775171 | 5 | 9 | 74,801 | 3/3, complete |
| NH03 EF Core | After | 44,676 | 0.0673766 | 5 | 9 | 62,272 | 3/3, complete |
| NH04 Orleans | Before | 103,834 | 0.0889039 | 8 | 24 | 66,672 | 4/4, complete |
| NH04 Orleans | After | 58,320 | 0.0783662 | 5 | 17 | 67,817 | 4/4, complete |

Independent blinded grading against the unchanged rubric scored both conditions
**7/7**, with **2/2 complete answers and zero critical errors**. Pack and grade hashes
were sealed before the final condition mapping. Total tokens changed from 153,267
to 102,996 (-32.8%); host-estimated cost from USD 0.166421 to USD 0.1457428 (-12.4%).
All four sessions together cost an estimated **USD 0.3121638**; the actual provider
invoice was not verified. The five host token counters are disjoint; reasoning is
counted once, not added again to a reasoning-inclusive provider output total.
All four runtime/usage validations passed, with no protocol violations or remaining
owned processes; the execution gate was closed and all 371 frozen files rechecked.

The new suffix appeared in both after-condition outputs: two NH03 candidate rows
and four NH04 rows. However, NH03 **before also reached the same exact selector and
scope at call 2**, then the same candidate path at call 3. NH04 after read the candidate
path at call 5, but its preceding `source_list` call also supplied that path. The old
NH04 run read that path at call 23. These observations do not prove the hint caused
the selection or the efficiency difference.

This is a small directional observation, not a general quality, token, cost or latency
guarantee. Request repetition, batching, retrieval choices and cache state can affect
the totals. The earlier negative comparison and offline records remain unchanged;
this follow-up does not retrospectively turn them into successes or authorize release.

## Final CI candidate native acceptance

`final-candidate-host-verification.json` records separate no-inference acceptance
of the MCP CI package `0.5.0-ci.43890740269.1`, built from `631e830`.
Its nupkg SHA256 is
`8543c02264707a19ae52e4df584c8966a12ba44699a8bc35f1a696502c45b989`;
Actions ZIP and payload DLL hashes are retained in the JSON. This is a CI candidate,
not a stable main/tag publication asset. Earlier native records remain unchanged.

Forty local tool calls passed: four direct reference calls, sixteen normal-mode
calls across four hosts, sixteen guard/freshness calls, and four exact re-fetches
in new contexts/processes. Normal outputs match the direct reference. Guard mismatch
and require-fresh rejection return no source; terminal-newline and OpenCode error
wrapping differences are explicitly retained. All owned processes were cleaned up.

Codex and Copilot use model-free native MCP routes, including fresh empty
thread/session re-fetch. Claude Code and OpenCode use fixed loopback fake-provider
tool instructions, including fresh-process re-fetch; they perform no real inference.
These checks establish native transport/schema/output acceptance, not natural skill
selection or equal model-facing guidance delivery. The four comparison-session
schema/init receipts are a different check and are not four-host acceptance.
The experimental context-reuse adapter remains separate and disabled.

CLI and VSIX candidate assets also exist for the same CI head, with successful
package checks and installed-VSIX CI tests. Those observations are recorded
separately; this audit's downloaded-payload native MCP check applies only to the MCP
candidate. No merge, tag, release or publication is performed by these checks.

## Release gates

Four-host native results must be complete for the final assembly. The failed historical
real-AI comparison is retained above. Any further real-AI comparison of the follow-up
requires separate owner approval for its scope and spending ceiling, and must assess
task quality, tool selection, total input/output, latency and cost.
Release remains blocked until those results are presented and the owner decides to proceed.
