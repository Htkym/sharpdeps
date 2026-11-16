# Automatic work selection: implementation boundaries

Status: design and local implementation work, not measured adoption benefit.
No new real-model calls or publication are authorized by this document.

## Existing behavior versus proposed changes

| Capability | Existing implementation | Next decision |
| --- | --- | --- |
| Candidate discovery | Exact aliases, then bounded component/path matching; ambiguous saved candidates remain explicit | Preserve ambiguity; test candidate family diversity before changing ranking |
| Requested evidence | Requested declarations precede related bodies, including bounded contained-member fallback | Preserve requested-root priority and budget accounting |
| Retrieval depth | Explicit overview/evidence/detail; exact selectors requested as overview already use evidence | Do not describe these existing modes as new automatic routing |
| Scope and provenance | Separate project/variant groups, saved identities, hashes, spans, freshness and partial markers | Required in every route, including lightweight ones |
| Repeated source | Same-response source admission and experimental host-specific same-request references | No server-side inference that a model still holds previous bodies |
| Preparation | Every explore currently prepares the lexical search index, including exact identities and literal declarations | Skip only provably unnecessary lexical preparation automatically; preserve snapshot validation and structural expansion |

## First implementation boundary: demand-driven preparation

An exact saved-identity request can resolve through existing dictionaries without
lexical matching. Literal declaration lookup has its own prepared index. Avoiding
an unrelated lexical index for these requests is an internal decision: no new flag,
no model instruction and no repository-size threshold. Ordinary names, mixed terms,
wildcards and discovery continue preparing the lexical index before the query budget.

Keep full snapshot validation, derived graph indexes, scopes, trust, source hash
verification, freshness, guards, cancellation and structural expansion unchanged.
The hosted owner prepares validated graph state; required lexical construction
remains in the caller preparation phase, before the query clock starts. The new
host test covers cancellation before preparation, not interruption mid-build.
Compare cold exact results with the same request after an ordinary query has warmed
the lexical index. Include unknown identities, cross-scope IDs, path endpoints,
literal declarations and explicit depth modes. Preparation metrics distinguish the
cost avoided; reply equality is stronger evidence than a shorter-looking response.

This optimization is not the full requested small-workload policy. It does not
reduce schema tokens or automatically choose whether a user's task needs callers.
The server receives query arguments, not necessarily the whole task. It must not
pretend to infer missing intent.

## Next policy experiment: candidate versus body cost

Use saved queries before introducing a new default. Classify queries by observed
ambiguity, retained scopes, declaration availability, coverage and requested graph
path, not repository size alone. Check whether candidate metadata can remain compact
without losing exact selectors, discriminating kind/path/span, omission counts or
freshness. Related bodies may be deferred only under an explicit, recoverable output
contract; never label a deferred or unexamined relationship as absent.

A roots-first default is a policy change, not lossless compression. Measure the
subsequent retrievals needed to finish the task, not only first-response bytes.
Respect explicit responseMode requests and demonstrate bounded fallback when the
requested declaration is unavailable. A single match or single file is not proof
that cross-file evidence is unnecessary.

The first matrix should include small/simple controls, small/semantic controls,
large/exact requests and large/ambiguous deep discovery. Keep equivalent source
access and deterministic quality checks. If the narrower preparation change has
negligible cost impact, report that instead of adding more machinery to make it fire.

## Research integration

Use other tools' architectural ideas as hypotheses, not measured SharpDeps effects:
CodeGraph's bounded staged context, Serena's candidate-then-body workflow, Aider's
query-related map, and GitNexus's sectioned results and freshness checks suggest
staged evidence selection. SharpDeps already implements parts of this pattern.
Prefer improving its current boundaries over adding parallel frameworks. Do not
copy incompatible-license implementations. Embedding services or model summaries
would add new runtime, privacy and cost assumptions and are not part of this change.

## Acceptance

No missing evidence, altered ambiguity or false freshness claims. Existing explicit
mode/scope/error contracts must remain compatible. Report preparation, source reads,
wire bytes, model tokens/cache and final quality separately. Local static review is
not a .NET test pass; no performance or token reduction is established until measured.

### Primary-source checkpoints

The following implementation revisions were read for this design. They support
architectural observations, not SharpDeps savings claims:

- [Serena symbol tool](https://github.com/oraios/serena/blob/0cef825f802d5323f8cb4bda3a5af52bf704770f/src/serena/tools/symbol_tools.py#L75-L148): body excluded by default; explicit scope, kinds, depth and match limits.
- [Aider repository map](https://github.com/Aider-AI/aider/blob/5dc9490bb35f9729ef2c95d00a19ccd30c26339c/aider/repomap.py#L420-L550): query-mentioned files/identifiers personalize graph ranking. Its host knows chat files; SharpDeps must not assume the same context visibility.
- [GitNexus query tool](https://github.com/abhigyanpatwari/GitNexus/blob/9b77866c879124e81caf2eabbf274404497d73b6/gitnexus/src/mcp/tools.ts#L170-L255): section selection retains omission metadata, content appears once per repository/symbol in one response, explicit follow-up retrieves content. Its hybrid embedding architecture is not being adopted here.
- [CodeGraph bounded context](https://github.com/colbymchenry/codegraph/blob/6560052a6f856855d3f71eee838fd66ccfa4285d/src/mcp/tools.ts#L287-L379): separately reviewed by the comparison workstream. Keep its selected defaults fixed in comparative runs rather than tuning only the competitor.
