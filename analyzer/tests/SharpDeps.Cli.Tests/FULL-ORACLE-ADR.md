# SDN-10: Bounded full-extraction oracle

Status: proposed for review. This harness is implemented; the broader plan and corpus release gate remain open.

## Context

Incremental extraction must preserve the semantic index produced by forced full extraction for the same final workspace, settings, workspace UUID and previous document/section identity lineage. In-process reuse alone cannot establish that independence. Fresh random snapshot and Markdown identities also make otherwise equal runs appear different.

## Decision

Run the existing CLI publication pipeline in two separate, bounded test processes. One allows semantic/project reuse and the reference disables semantic reuse. Commit each result into a separate real SQLite index and observe the saved snapshot, ordered FTS hits and production QueryService responses. Keep requests/results outside the workspace inventory and retain raw evidence, including failures.

Use a scoped internal identity allocator only in the harness. Preserve the public three-argument and existing internal four-argument entry points, their return types and default GUID allocation. The five-argument internal overload is not a public API or an additional friend grant. Both comparison paths receive the same allocator sequence, including snapshot-bound transient Markdown identities. The initial baseline exercises the pre-existing entry point.

Compare all serialized semantic snapshot fields and exact ordered search results. Separate only the explicit field/code allowlist documented in FULL-ORACLE.md and FullBuildOracle.cs. Do not discard the complete diagnostic collection or introduce a wildcard metrics exclusion. Query diagnostic removal must match the actual saved-diagnostic projection, including its suffix and long-code fallback, before removing allowlisted execution counters. Unknown or changed diagnostics remain comparison input.

The single files[].reuse.projects.structureHash field is execution proof rather than semantic output for this comparison. A Markdown-only reuse retains the old structure proof while full extraction refreshes it. Retain both raw values, normalize only that field, and independently assert the resulting conservative full fallback on the following C# edit. Do not change the production proof or its acceptance rules to make the test pass. All remaining reuse/catalog/collector proof payloads stay compared.

Restore the small fixture with a temporary installed .NET 10 SDK pin and an external offline restore configuration. Remove the temporary global.json before extraction because the existing closed-input proof rejects it. Require the actual captured SDK proof on every verification host and assert it matches the restore SDK. Set the same explicit absolute NuGet package root outside the workspace for offline restore and extraction children; a host-default root can produce NuGet props containing $(UserProfile), which the existing static-input proof deliberately rejects. Record proof admission and extraction/reuse counters without changing production admission or allowing an OS-specific bypass. Sanitize only the child process environment to the existing supported profile; do not invoke private proof helpers or modify production environment validation.

## Alternatives considered

- Compare only counts or remap semantic IDs: rejected because wrong identity lineage, locations, edge certainty or ranked output could be hidden.
- Add a production deterministic-ID mode: rejected because SDN-10 is verification infrastructure, not an identity-contract migration.
- Exclude all diagnostics or reuse proofs: rejected because semantic regressions and failed invalidation would escape detection.
- Claim a production incremental-parser oracle: rejected because the retained Roslyn session and Syntamark delta parser are not implemented.

## Evidence and consequences

The bounded three-project fixture covers no-change reuse, Markdown edits with new document/section identities, a C# edit with compiler errors, source membership and project configuration changes. It asserts actual reuse/extraction counters as well as semantic equality. Separate tests mutate edge certainty, source spans, compiler diagnostics, an unknown execution-looking diagnostic and FTS rank to prove that comparison failures are observable. Process tests cover nonzero exit, pre-cancellation, cancellation of a started process, large redirected-stream drain and termination. The P2 regression adds a normally exited parent whose owned descendant retains both stdout/stderr handles. Bound post-exit drain to two seconds, cancel the actual reads and close both readers on expiry/cancellation, bound read cleanup to one additional second, and observe eventual task faults. Bound exit cleanup after Kill separately to three seconds because HasExited and WaitForExit do not establish descendant termination. The test finally kills and waits for its recorded descendant; that child also self-exits after 30 seconds if the test runner dies. No general orphan-reaper capability is claimed.

Parser checks compare Roslyn WithChangedText with an independent full parse and prepared Markdown syntax facts with canonical full Parse, including Unicode/CRLF and bounded-output cases. They establish API conformance only, not production incremental parser reuse or performance. The Index persists aggregate compiler diagnostics, so unpublished detailed compiler reports cannot be compared here.

The internal overload and test executable entry point increase test infrastructure surface. Existing entry-point signatures and friend grants remain unchanged, and existing ProjectIncrementalTests are included in the targeted validation. New serialized metrics must receive a field-specific decision and assertions rather than being silently ignored.

The fixture is intentionally small. Full corpus comparison and performance measurement remain separate gates. The ordinary CI test matrix must pass for the final candidate before acceptance. Retained requests for earlier successful steps describe their historical input state; the retained final workspace directly replays only the final step. Rerunning the integration test reconstructs the full sequence.
