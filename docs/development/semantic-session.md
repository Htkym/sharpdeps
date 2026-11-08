# Semantic workspace lifetime

Status: candidate implementation. Performance and real-repository applicability are not measured.

`SemanticLoader.LoadAsync` keeps its public result and cold behavior. Internal cold loads separate workspace ownership, variant selection, compilation/reference completion, and report creation. A single writer can retain a committed solution and build a separate candidate without applying changes to source files.

Warm admission requires the same workspace/target/options key, committed generation, SDK/tool/environment/dependency fingerprint, complete directory and non-source inventory, actual compiler source membership, and all source owners. Only existing source bodies can change. A bounded UTF-16 replacement uses the previous compiler `SourceText.WithChanges`, preserving its encoding and checksum algorithm, then `Solution.WithDocumentText`. Collectors conservatively invalidate every owner and reverse dependent; semantic models and symbols are not transferred between trees.

The initial CLI profile keeps BOM-bearing inputs, generators, additional inputs, compiler-error baselines/candidates, and unsupported closure configurations cold. A rejected error candidate leaves the old committed solution intact until a successful cold publication. A generator reference is rejected even if its previous output was empty. The framework-free MSBuild fixture tests the internal prototype only; it does not grant production CLI admission or disable generators in user projects. UTF-8/BOM and UTF-16 coordinate tests are distinct from production warm eligibility.

The input proof additionally accepts unconditional compiler properties in `Directory.Build.props`. Conditions, imports, item changes, CPM, unknown SDK/environment inputs, and unsupported package profiles remain fail-closed. Full inventory guards include changed, added, removed, and absent configuration inputs. Restore remains a separate operation.

Candidates are adopted after the durable DB commit. Cancellation, supersession, staging/publication failure, and owner shutdown discard them. Adoption failure preserves the successful DB publication and returns the session to cold. Readers continue to use committed index generations and the existing freshness checks.

Initial retention limits are 32 adopted solution versions, 10,000 documents, 32 Mi UTF-16 code units across all owners per version, 128 Mi cumulative lineage code units, and five minutes of idle time checked at admission. These bound source retention, not total process RSS; memory limits need later measurement. Eviction permits cold recovery. Startup and periodic full resync remain enabled.
