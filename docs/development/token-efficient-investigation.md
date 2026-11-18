# Token-efficient investigation follow-up

## Goal and evidence

The target is fewer tokens than ordinary source exploration **without SharpDeps**, with equal or
better answer quality. A faster SharpDeps revision alone does not establish that target.

The November 17, 2026 comparison covered four tasks, two repetitions and two SharpDeps revisions
(`1add6bbb` and `b998eb5a`). Both conditions received their respective guide explicitly. The newer
revision used 21.2% fewer total host tokens and 7.6% less host-estimated cost on average, with equal
reported quality totals. However, new-input counters (`input + cache_write`) increased 1.9%, and
cache reads accounted for most of the reduction. This was not a new comparison against ordinary
exploration, a test of automatic skill selection, or proof of quality non-inferiority.

Trace inspection found 49 versus 57 explore calls and 359,834 versus 452,910 characters in explore
responses. These are characters, not model tokens. Orleans traces included broad type/file evidence,
overlapping source reads, and subsequent detail requests. The initial EF task also showed avoidable
test searches after the requested investigation evidence had already been obtained. These findings
motivate the changes below; they do not establish their future model-level effect.

## Changes and boundaries

- Permit direct reads of known relevant files/ranges, including across files. Use structural queries
  for specific evidence still missing rather than as an obligatory extra step.
- Suggest overview for unfamiliar broad types/files, not for a known member that can be retrieved
  directly. Request additional bodies only when needed. This is guidance, not a forced tool sequence.
- Ask for decisive branch conditions when explaining behavior, preserving uncertainty. The prior
  placement answers sometimes omitted applicability conditions; this is not a request to enumerate
  unrelated edge cases or exhaust the repository.
- Default omitted MCP `responseMode` to `evidence`. This changes the response behavior of existing MCP
  callers which omitted that field: related bodies can remain deferred. They can request `detail`
  explicitly for additional bodies. CLI and typed Query omitted-mode behavior remains unchanged.
- Share fixed explanation text and the saved snapshot header within compact discovery responses.
  Every group retains its own scope, freshness, outcome, omissions, errors, aliases and source
  evidence. No persistent already-read state or cross-response source suppression is introduced.
- Keep the routine skill entry point short and move specialized interface contracts to a linked
  reference. Distribute the complete skill directory, including `references`, not only `SKILL.md`.
  Loading both files in full is not a total-size reduction; read reference sections only as needed.

Trust, scope ambiguity, freshness checks, finite budgets and update authorization are unchanged.
Fewer returned characters are not guaranteed for every query: deferred-body metadata can outweigh a
very small body. Do not hide omitted evidence or weaken the answer to meet a token target.

Existing managed installation blocks are preserved. Updating this source does not silently rewrite
an installed block; use the supported explicit uninstall/install workflow, preserving user content.
New `agent-export` packages use `routing-v4`; existing immutable `routing-v1`/`routing-v2`/`routing-v3` packages remain
recognized and removable. An export does not overwrite an older or edited package. Use explicit
`agent-remove` then export to adopt the new guidance, retaining the removal backup.

## Acceptance and remaining evaluation

Deterministic tests must retain requested declarations, related pointers, scoped uncertainty and
omission information; verify explicit detail recovery; distinguish MCP defaults from CLI defaults;
and exercise wire-budget boundaries and old managed-block compatibility. Character/byte reductions
in these tests are local output evidence, not an estimate of model token savings.

A local, model-free replay of `Placement` in overview mode against the fixed Orleans index returned
the same six saved pointers in three separate groups. The normalized response decreased from 11,327
to 10,455 characters (872 characters, 7.7%). Single-group replies retain their existing explanations
rather than paying for an additional shared-policy header. This is one deterministic output example,
not a measured reduction in answering tokens or cost.

A future paid comparison needs separate owner authorization. A candidate design is six tasks
(including two previously unseen tasks), two repetitions and two conditions: ordinary read/search
versus the updated normal SharpDeps integration. Use the same model, default service tier, read-only
source scope and comparable budgets. Do not force an exploration call or inject a long guide merely
to make the tool condition win. Record which guidance the native host actually supplies. Keep gold,
rubrics and prior answers out of the answering models' context and blind the scorer to conditions.

Report total tokens, new input, cache reads, output/reasoning, requests, tool calls, wall time and host
cost separately; retain failures and all spend. Include cost per valid answer, both with and without
interrupted-attempt reservations. Compare task/repetition pairs and show sensitivity to
outliers. Small samples and ceiling scores limit conclusions. A six-task study would still be an
initial generalization check, not a guarantee for arbitrary repositories.

Do not reuse answering-session history across independent tasks or conditions. Any warm-session
reuse study is a separate measurement axis with explicit context retention and task order; it cannot
silently replace the independent comparison. Likewise, host snapshot reuse is not evidence of model
cache reuse. Report cache behavior as observed rather than promising a discount.

No further paid model comparison is part of this implementation step. The previous follow-up's
$4 budget and its unsettled reservations do not authorize new spending. Merge and release remain
separate decisions.
