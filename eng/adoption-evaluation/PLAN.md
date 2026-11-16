# Proposed installation-effect comparison (not authorized to run)

Status: planning only. No new real-model request is authorized by this document.
Publication remains on hold until the owner reviews evidence of useful installed
behavior. Earlier negative results and the one-pass positive observations remain
separate historical studies, not a pooled estimate.

## Decision and stages

Evaluate whether a normally installed product provides useful evidence at lower
end-to-end cost without lowering answer/change quality. Tool usage or compression
activation alone is not success.

1. Independently review tasks and source-backed scoring before exposing a candidate
   to the task set. Freeze public prompts separately from private gold/paths.
2. Check installation, tool catalog, routing delivery, and fixed retrieval probes
   without a real model. Resolve failed discovery or missing evidence first.
3. Request owner approval for exact versions, tasks, repetitions, model, transmitted
   code/outputs, expected spend and total ceiling. Do not infer approval from an old
   study. Freeze inputs and execute only that approved matrix.
4. Score blinded answers and proposed changes, then unblind and report all attempts.
   Do not retry poor answers or silently replace difficult tasks.

## Provisional matrix

Three conditions: ordinary source tools; SharpDeps standard integration; CodeGraph
standard integration. Experimental dedup/context adapters are OFF and excluded from
this primary comparison. Do not make use of either product mandatory.

A provisional six-task set, two repetitions per condition, gives 36 sessions:

- Four primary tasks on large repositories, spanning deep call paths, multiple
  similarly named candidates, behavior-to-location discovery and a bounded change.
  EF Core and Orleans are candidates, not a claim that all their code is covered.
- One simple/small-repository control where direct reading may be sufficient.
- One genuine ambiguity/clarification control. A timely question may be optimal;
  do not count unnecessary searching as success.

Finalize the mix with independent task review. Keep large-repository results
primary; report every task and difficulty stratum separately. Two repetitions are
still exploratory, not a statistical equivalence or general performance claim.
Do not infer task difficulty just from vague wording or repository file count.
A small repository may still require semantic caller/variant evidence.

For change tasks, grade the proposed change against frozen source and deterministic
tests where available, including regressions and unsupported assumptions. Use
isolated copies; never let one condition's edits influence another. Do not leak
private target names, expected selectors, paths, tests or difficulty labels into the
public prompt. Accept source-supported equivalent paths and appropriate questions.

## Fairness and integrity

- Same host/version, provider/model, ordinary service tier, public prompt, source
  permissions and general read/search tools. Keep response, time, tool and spending
  limits fixed; distinguish instructed limits from hard runtime enforcement.
- Each product uses its supported ordinary integration, with recorded default
  configuration, actual tool catalog and instruction delivery. Different products
  legitimately have different schemas; count their overhead rather than deleting it.
- Do not manually duplicate native MCP initialization instructions or load a skill
  body twice. Distinguish skill listing, body loading, server connectivity and useful
  natural selection. A forced fixture call proves none of the latter.
- Freeze code commits, corpus revisions/hashes and index identity/coverage. Exclude
  each tool's CLI, generated index and configuration files from the searchable corpus.
  Keep index/setup time and size as separate amortization costs, not hidden costs and
  not repeated per-task costs without saying so.
- Interleave condition order across tasks/repetitions. Preserve independent session
  histories and record cache conditions; do not claim cold-cache behavior if unproven.
- Score correct evidence and task completion first. Count all attempts, refusals,
  failures, missing usage and budget stops. Keep protocol-compliant and all-run
  tables distinct. Stop before the next session when the approved ceiling is at risk.

## Measurements and acceptance questions

Record separately:

- Answer/change quality, each required criterion, critical errors, justified
  uncertainty and appropriate clarification. No minimum product-call count.
- Total input/output including disjoint cache-read/write and reasoning categories,
  provider/host cost estimate versus invoice when available, latency, request count,
  logical tool calls and displayed bytes. Do not double-count reasoning or caches.
- First useful evidence, discovery/ranking quality, ambiguity and scope correctness,
  required source returned/omitted, subsequent exact retrieval and why ordinary reads
  followed (missing body, freshness before edit, different evidence, or repetition).
- Installed guidance/catalog exposure and actual product use. A listed skill is not
  proof its body reached the model. Returned hints followed by matching reads do not
  alone show the hints caused those reads.
- Context overlap/residency when observable. Do not infer retained model context from
  a server receipt or force repeated retrieval merely to trigger deduplication.

Do not release on a pooled token decrease that hides a quality regression or a large
repository regression. Report useful effects and non-effects with their uncertainty.
Set practical release thresholds with the owner before using the results to decide.

## Experimental request-context reuse

The earlier OpenCode ON study changed no requests: its saved same-session histories
contained no eligible repeated full source blocks. This is a workload/eligibility
non-event, not evidence of savings and not, by itself, an installation failure.
Maintain strict same-request anchors and exact restoration; do not weaken safety to
force activation. Any future adapter study must be separate, pre-approved, use a
natural workload with a legitimate repetition opportunity, and measure reference
understanding, cache effects and total cost as well as serialized bytes. Keep an
OFF control and include genuine no-repeat cases.

## Automatic product-side selection (design requirement)

Routing guidance alone is not the requested solution. SharpDeps itself must select
a cheaper path only when query and index evidence justify it, with conservative
structural fallback for ambiguous candidates, incomplete coverage, stale state or
explicit relationship demands. Do not disable semantic evidence merely because a
repository is small. Classify discovery, requested evidence and structural expansion
separately, expose the chosen path and its limits, and preserve recoverable exact
retrieval. Server-visible query arguments do not necessarily contain the entire user
task: do not claim reliable intent inference without that information.

Evaluate both wrong-shortcut cases (missing required evidence) and unnecessary-heavy
cases. A reduction in response bytes is not proof of cheaper preparation or total
model use. Keep preparation, retrieval, rendered output and subsequent requests
separate. Guidance alignment is preparatory, not an automatic execution-path selector.
The first internal optimization skips unused lexical preparation for exact existing
identities and literal declaration lookup; see [the implementation boundaries](AUTOMATIC-PATHS.md).
It does not yet implement a broader small-workload evidence policy.

## Cost and current constraints

An initial planning estimate for the 36-session matrix is USD 3–6, with a proposed
USD 8 ceiling, based on prior runs rather than a quote or approval. Recalculate
against the finalized task difficulty and no-model retrieval sizes before requesting
permission. No real-model call is allowed until the owner approves the concrete plan.

Validation checkpoint: this dedicated branch is temporarily pushed with explicit
owner approval before C# execution, solely to transfer the reviewed candidate to
an isolated PC checkout. Static diff checks and the unchanged 14-test Node adapter
suite passed; C# compile/tests remain unverified at this checkpoint. The PC has
reconnected and has .NET 10 installed. No SDK installation is needed. The ordinary
Library transfer failed because its metadata helper does not support Windows
extended attributes; no helper modification or raw-download bypass was used.
No main change, merge or release is authorized by this transfer.

Subsequent PC verification of that same code/tree passed 234 focused C# tests.
The saved three-condition replay returned identical complete result objects before
and after the change. A separate EF diagnostic pass confirmed lexical preparation
is skipped for the cold exact request and deferred until the subsequent name query;
this is not proof of less total work or model tokens. See [verification.json](verification.json).
Natural adoption and comparative quality remain unmeasured for this candidate.

The later complete-name phrase ranking change passed 166 scoped ExplorationTests
on Windows with SDK 11.0.100 targeting net10.0, including six phrase boundary cases.
An independent static review found a signature-like fallback boundary; the original
query character check and `Order(user)` regression were added and the final review
passed. These tests do not establish natural-use adoption or token savings.
