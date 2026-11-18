# Offline evaluator interface

`evaluation.py` never invokes a model. It verifies a separately reviewed
execution controller's export, creates blind packets, freezes adjudicated
scores, then aggregates. Do not give this directory or PRIVATE files to solvers.

## Execution-controller export

One JSON manifest with exactly these fields:

```json
{
  "format": "f4-evaluation-export-v1",
  "protocol_revision": "f4-neutral-v1",
  "study_id": "new-unique-study",
  "mode": "synthetic",
  "attempts": [
    {"id":"unique-session", "task":"F4-R", "rep":1, "arm":"direct",
     "receipt":{"path":"receipts/session.json","sha256":"actual SHA256"}}
  ]
}
```

Include all **12** unique task × repetition × arm cells. Tasks are F4-R/F4-N,
repetitions 1/2, arms direct/generic/exception_checklist. The example is abbreviated.
Mode is synthetic or live; one study cannot mix modes. Every receipt must contain
exactly these fields, including null for absent answers:

```json
{
  "study_id":"new-unique-study", "mode":"synthetic",
  "id":"unique-session", "task":"F4-R", "rep":1, "arm":"direct",
  "state":"completed", "protocol_compliant":true, "usage_known":true,
  "host_cost_usd":"0.10", "settled_cost_usd":"0.12",
  "final":{"path":"answers/session-final.txt","sha256":"actual SHA256"},
  "precheck":null
}
```

All file paths are relative to the **manifest directory**, even paths in nested
receipts. Files must be regular local files without symlink/reparse ancestors.
SHA256 must match. No path traversal or absolute paths. Each completed receipt
must be protocol compliant with FINAL, plus PRECHECK for non-direct arms. Direct
must never have PRECHECK. Stopped/not-started receipts must be noncompliant;
their costs are retained, not dropped. Not-started requires known zero costs.
Costs are nonnegative decimal strings or null, never float. Unknown or unsettled
usage must be exported with `usage_known:false`; known partial amounts may be
retained as lower bounds but never reported as complete totals. Both cost
instruments are required when usage is known.

The producing controller must verify session identity, expected turn count,
prefix integrity, accepted evidence delivery, neutral instruction stack, stop
latches and budget accounting **before** attesting protocol_compliant. This
evaluator verifies identity and hashes, not those underlying host semantics.
Protocol checks must allow zero new evidence calls during a recheck. Hashes do
not establish truth or authorize paid execution. Keep host estimates separate
from ledger settlements; neither proves a provider invoice.

## Offline workflow

```powershell
python -B evaluation.py prepare --manifest path/to/manifest.json --output path/to/NEW-packets
# Give only public-final/answers.json, scores-template.json and the rubric to scorers.
# PRECHECK is a separate diagnostic packet. Do not give PRIVATE to scorers.
python -B evaluation.py freeze --output path/to/NEW-packets --scores path/to/adjudicated-scores.json --adjudication path/to/adjudication-log.json
python -B evaluation.py aggregate --output path/to/NEW-packets
python -B -m unittest test_evaluation
```

Combine adjudicated FINAL and PRECHECK score rows into the one scores file.
Each row follows the template: four integer 0/1/2 components, a rationale, and
a list of critical errors (empty allowed). Totals are derived, never trusted.
An empty adjudication log is allowed when there was no disagreement.

Preparation publishes an entire new directory only after all evidence validates;
existing outputs are never overwritten. Freeze and aggregate are exclusive
creates. After interruption, preserve partial output and use a new directory,
not an overwrite. Keep source manifest and referenced evidence immutable and
available until aggregation. Both FINAL and PRECHECK must be scored before
unblinding. Technical separation is not access control: the directory owner can
open PRIVATE, so genuine blindness depends on separate evaluator access.

Completed compliant answers without scored critical errors count as valid;
this is not a claim of perfect quality. Unknown costs produce null totals and
separate known lower bounds. All 12 slots remain accounted for. PRECHECK deltas
are descriptive, not shared-baseline causal effects. Synthetic mode is carried
into the aggregate and explicitly marked as not a measurement.

Before freeze and aggregation, mode, study ID and protocol revision are checked
again against the hashed source manifest. Relabelling PRIVATE provenance cannot
turn synthetic output into live results. Rubric `f4-quality-v1` has a reviewed
semantic JSON digest pinned in the evaluator; preparation records both that
digest and the exact file-byte hash, and freeze/aggregation revalidate them.
Changing the rubric requires a reviewed new version, not editing old packets.

New attempts/retries require a new reviewed design/export format: this interface
deliberately rejects additional cells rather than silently dropping their costs.
