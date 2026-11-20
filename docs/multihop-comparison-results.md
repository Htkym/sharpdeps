# Historical multihop comparison, November 18, 2026

This is the natural-use cohort `multihop20261118v1`, not the later forced-verification diagnostic or the F4 checklist pilot. It measured product commit `46fe3551a3bf61778b8628522150e1ebb15e1378`, before routing-v5. No new model run was performed for this publication.

## Conditions and observed results

Two selected multi-stage tasks (EF Core `M-EF-1`, Orleans `M-ORL-1`), two repetitions and three arms produced 12 completed, valid, scored answers, four per arm. The model was `openai/gpt-6-sol#comparison-normal-xhigh`, default service tier, fresh processes, deduplication off, with the existing index. Fresh processes do not imply cold provider or OS caches. Each session had limits of 900 seconds, 8,192 output tokens and 36 requests.

- **Normal:** source read/search/list only, without SharpDeps or a routing guide.
- **Typed-only:** the same source tools plus SharpDeps find/read/relations and its experimental routing guide.
- **Typed + explore:** typed/source tools plus explore availability and the corresponding guide. Explore was called zero times.

| Metric                            |    Normal |  Typed-only | Typed + explore |
| --------------------------------- | --------: | ----------: | --------------: |
| Valid / attempted answers         |     4 / 4 |       4 / 4 |           4 / 4 |
| Mean quality, out of 12           |     11.25 |       11.50 |           11.50 |
| Answers with critical errors      |         0 |           0 |               0 |
| Total host tokens                 | 1,732,916 |     980,193 |       1,046,924 |
| Host-estimated cost, USD          | 1.1661400 |   0.7666267 |       0.8321439 |
| Host-estimated USD / valid answer |  0.291535 | 0.191656675 |     0.208035975 |
| Provider requests                 |        56 |          40 |              40 |

For typed-only versus normal, `100 × (1 − 980193 / 1732916)` gives **43.4% fewer total host tokens**; `100 × (1 − 0.191656675 / 0.291535)` gives **34.3% lower host-estimated cost per valid answer**, rounded to one decimal. These are ratios of pooled counters/costs over four matched task/repetition pairs, not averages of per-session percentage reductions. Host total tokens sum input, cache read, cache write, disjoint output and reasoning counters. They are not a billing-equivalent token count. Most token savings came from cache reads.

All planned attempts completed; none had unknown/unsettled usage or failed-attempt costs. Across all three arms, conservative ledger settlement was $8.1108000, a different instrument from host estimates. Provider invoices were not verified.

## Interpretation and public evidence

This small, selected sample supports a historical observation, not a population estimate, current-product savings guarantee, quality superiority or statistical equivalence. The +0.25 quality difference rests on one answer. All Orleans answers reached the rubric ceiling; every EF answer omitted release-failure logging and finally-cleanup exception replacement. The current final-answer checklist's effect remains unconfirmed and is not represented by these percentages.

This was a controlled harness comparison: native initialization instructions were replaced with neutral instructions; guides and advertised tool schemas were part of the treatment and their token overhead was counted. It does **not** establish the effect of adopting the unmodified product. It is not pooled with earlier typed-API/routing studies or later forced checks. Explore's capability cannot be inferred from an arm that never called it.

The [public numerical extract](multihop-comparison-results.json) includes all 12 cells, token components, costs and scores, so the table and percentage arithmetic can be checked. It was checked against the original session-export/receipt hashes and counters, final-score hash and aggregate. Its provenance records SHA-256 hashes of the owner-supplied aggregate, scoring report, scores, adjudication log and study definition. The original report describes blind scoring by two scorers, one blind adjudication and score freezing before unblinding.

Only a numerical extract is published: original answers, complete traces, private paths and connection details are not redistributed. The hashes identify retained originals; they are not download links or proof that an independent reader can reproduce the runs or re-score the answers. This evidence boundary limits independent verification to the published data and described protocol.
