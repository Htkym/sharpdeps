# Semantic workspace lifetime

Status: candidate implementation. Performance and real-repository applicability are not measured.

`SemanticLoader.LoadAsync` keeps its public result and cold behavior. Internal cold loads separate workspace ownership, variant selection, compilation/reference completion, and report creation. A single writer can retain a committed solution and build a separate candidate without applying changes to source files.

Warm admission requires the same workspace/target/options key, committed generation, SDK/tool/environment/dependency fingerprint, complete directory and non-source inventory, actual compiler source membership, and all source owners. Only existing source bodies can change. A bounded UTF-16 replacement uses the previous compiler `SourceText.WithChanges`, preserving its encoding and checksum algorithm, then `Solution.WithDocumentText`. Collectors conservatively invalidate every owner and reverse dependent; semantic models and symbols are not transferred between trees.

The initial CLI profile keeps BOM-bearing inputs, generators, additional inputs, compiler-error baselines/candidates, and unsupported closure configurations cold. A rejected error candidate leaves the old committed solution intact until a successful cold publication. A generator reference is rejected even if its previous output was empty. The framework-free MSBuild fixture tests the internal prototype only; it does not grant production CLI admission or disable generators in user projects. UTF-8/BOM and UTF-16 coordinate tests are distinct from production warm eligibility.

The input proof additionally accepts this narrow compiler-property profile. Performance and real-repository applicability remain unmeasured.

| Configuration | Input-proof eligibility |
| --- | --- |
| Unconditional static compiler properties in `Directory.Build.props` | Supported |
| Direct `Import Project="build/Compiler.props"` from `Directory.Build.props` | Supported when the existing target is a root-contained, non-linked `.props` file containing only the same compiler properties |
| Missing targets, conditional or nested imports, import SDKs, expressions, globs, escapes, lists, absolute/external paths | Cold |
| Unreferenced custom `.props` files, custom `.targets`, items, targets, CPM | Cold |
| Unknown SDK/environment inputs, generators, unsupported package profiles | Cold |

Literal import paths use nonempty relative segments containing letters, digits, `.`, `-`, or `_`, separated by `/` (or `\` on Windows). Empty, `.` and `..` segments, trailing dots, whitespace, and excluded Git/output directories are rejected. An import has exactly one `Project` attribute and no child content. Literal path casing must match the enumerated file path exactly, including on case-insensitive Windows directories; a mismatch stays cold. Imported files cannot import other files. This does not widen the existing SDK, Git, package, or generator profiles.

The compiler-property set is `LangVersion`, `Nullable`, `ImplicitUsings`, `AllowUnsafeBlocks`, `DefineConstants`, `Optimize`, `CheckForOverflowUnderflow`, `WarningLevel`, `NoWarn`, `WarningsAsErrors`, `WarningsNotAsErrors`, `TreatWarningsAsErrors`, `GenerateAssemblyInfo`, `GenerateGlobalUsings`, `Deterministic`, `DebugType`, and `DebugSymbols`. Properties and property groups must be unconditional and attribute-free; property values cannot contain expressions, globs, or escapes.

Full inventory and publication guards bind file paths, bytes, directories, and existence, including added or removed imported files. A property or import change requires cold evaluation; source membership is discovered from that evaluation rather than inferred. Restore remains a separate operation. Proof eligibility alone does not grant semantic-session admission.

Candidates are adopted after the durable DB commit. Cancellation, supersession, staging/publication failure, and owner shutdown discard them. Adoption failure preserves the successful DB publication and returns the session to cold. Readers continue to use committed index generations and the existing freshness checks.

Initial retention limits are 32 adopted solution versions, 10,000 documents, 32 Mi UTF-16 code units across all owners per version, 128 Mi cumulative lineage code units, and five minutes of idle time checked at admission. These bound source retention, not total process RSS; memory limits need later measurement. Eviction permits cold recovery. Startup and periodic full resync remain enabled.
