# F4 exception-path verification pilot (not yet measured)

Two small synthetic C# tasks, three arms, two independent repetitions: 12 sessions. This is a diagnostic **material set**, not a paid model launcher. No model results or spending approval are included. It does not establish a benefit of mandatory rechecking.

- `runner/`: tasks, frozen arm plan, generic and exception-specific recheck prompts.
- `corpus/`: two permitted solver source files and a minimal Library project (`F4Solver.csproj`) for semantic indexing. Stage an isolated workspace containing only this corpus directory; do not index this repository or the evaluator directory.
- `evaluator-only/`: rubric and deterministic oracle. Never give the oracle, test project, expected traces, scores or implementation history to solving agents.

## Why three arms

The prior forced study compared two recheck instruments, not rechecking against no recheck. The direct arm adds that missing comparison. Keep typed/source tools, product commit, source, model, normal service tier, tool budgets and final answer limits identical across arms. Generic and exception-checklist arms use the same two-turn boundary. Independent PRECHECKs are not identical baselines: report final comparisons and initial levels separately; do not call within-session deltas causal effects. Four samples per arm are diagnostic, not an equivalence or superiority test. Use the same neutral typed/source-only base guide for all arms, with no final-check or exception-checklist instruction; do not inject routing-v5 or MCP server instructions containing that checklist into the direct arm. Freeze and audit the entire model-visible instruction stack. Only the assigned follow-up prompt introduces checking. If the host cannot suppress the common checklist, label direct as no-extra-turn instead of no-recheck and do not claim a no-recheck comparison.

## Before any paid execution

1. Build/index `corpus/F4Solver.csproj` in the isolated workspace; expose only its two authored C# files to the solver, not generated `obj` files. Run the deterministic source test below and the separate `comparison/contract-check` checker against the **actual advertised schema plus enforced/consumer contract**. Run a no-model transport smoke on both source files and failure cases, not only a convenient symbol.
2. Freeze source/guide/tool-schema/product hashes, exact model and current prices. Use fresh session IDs. Configure a shared total monetary cap with fresh owner authorization, default service tier, no automatic retries. Do not reuse any sdfx approval or ledger.
3. Verify that the chosen model runner can deny evaluator paths, persist phase exports, join accepted tool responses to actual delivery, and stop before the next provider request on failure. This material set does not itself implement that paid controller. If missing, stop and adapt a separately reviewed controller first.
4. Randomize arm order within task/repetition. Direct gets one answer; the other arms get PRECHECK then their assigned prompt. Preserve every attempt and all costs, including failures. Stop the batch on a contract/delivery/budget anomaly.
5. Blind final answers using independent random labels, score and adjudicate before unblinding. PRECHECK scores may be a separate blind diagnostic. Use the same rubric for all arms. Report per-task scores, critical errors, valid-answer counts, input/cache/output tokens, host cost estimates and conservative settlements separately. No claimed provider invoice unless actually verified.

## No-model source validation

```powershell
dotnet run --project comparison/f4-verification/evaluator-only/F4Fixture.csproj --configuration Release
```

Expected: `PASS 16 deterministic source cases; no model calls`. This checks source semantics, not whether any AI will find or explain them. The fixture is deliberately small/public and targets known exception-path errors; do not label it an unseen real-world benchmark or generalize token savings from it.

Existing sdfx1118/r2/r3 artifacts and frozen scores remain historical and must not be rewritten or pooled into this new design.
