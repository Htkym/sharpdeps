# Exact SDK package and Git reuse profiles

Package and Git reuse proofs admit only the reviewed Windows .NET SDK 10.0.402 and
10.0.403 payloads. The SDK directory name alone is insufficient. Every provider
payload listed in `CliReuseProof.Sdk.cs` must match its SHA256, and the existing
full SDK, runtime, tool, environment, workspace, and dependency inventories still
apply. Unknown SDK versions or modified provider payloads keep full extraction.
Other existing profiles without package or Git inputs are unchanged.

## Supported closure

The existing singleton path permits exactly one direct `PackageReference` across
the workspace, with a literal package ID and fixed three-part version. Project
references may propagate that package. Each project's conventional assets and
lock files must agree with the same private cache, archive, extracted files, and
content hash. Only the existing managed DLL/reference/runtime file shape is
accepted. Package MSBuild assets, analyzers, generators, central package management,
and unknown metadata are not admitted. Multiple direct declarations use only the
separate bounded extension described below.
The ResolvePackageAssets v12 cache remains validated, including the decision
whether its timestamp is newer than its assets file.

The Git profile permits an ordinary `.git` directory at the workspace root.
Its bounded inventory includes configuration, loose and packed refs, and exclude
files. Linked worktrees, ancestor repositories, submodules, alternate object
stores, reftables, external configuration includes, and external ignore paths are
not admitted. A literal `GitRepositoryConfigurationScope` of `local` isolates the
SDK provider from user/system configuration; otherwise the existing external
configuration absence checks must pass. Workspace ignore files remain part of
the input fingerprint, including under `obj` and `bin`.

These profiles may be combined. Failure of either closure disables the proof;
it does not relax the other profile or authorize arbitrary MSBuild evaluation.
Staged proofs are checked again before publication. Changed dependencies reject
publication and preserve the committed index generation.

## Source and payload audit

The relevant official source was compared at
[10.0.402, a8dd0577](https://github.com/dotnet/dotnet/tree/a8dd0577) and
[10.0.403, 5a2162bea6845eabc1fc721624a43568c65db3b1](https://github.com/dotnet/dotnet/tree/5a2162bea6845eabc1fc721624a43568c65db3b1).
The reviewed package resolver/cache and lock-file stream parser sources, and the
Git task/data-reader sources and build imports, have matching Git blobs across
these commits. PackageSpec experimental macro expansion remains excluded by the
strict environment allowlist. This audit concerns the admitted paths only.

The exact payload pins are maintained in
[`CliReuseProof.Sdk.cs`](../../analyzer/src/SharpDeps.Cli/CliReuseProof.Sdk.cs).
Source equivalence does not automatically admit another build or SDK version.

## Bounded package-set extension

The package-set extension preserves the zero-package and singleton paths,
ordinary Git behavior, index format, full extraction, query revalidation and
publication guards. A failed check returns no reuse proof and keeps full
extraction under the existing trusted-indexing rules.

The extension is gated to Windows local-drive paths, `net10.0`, and the same exact
10.0.402/10.0.403 provider descriptors. It permits 2 through 32 direct declarations
across at most 128 project owners and 512 literal project edges, with at most 8
case-consistent package IDs and one literal three-part version per ID. Several
independent packages may be direct in one project; the same ID/version may be
direct in several owners. Conflicting versions/casing and duplicate direct IDs
within one owner reject. These are admission limits after ordinary bounded
workspace/project parsing, not early parsing limits. These ceilings do not
guarantee admission of every combination: shared budgets and other checks may
make smaller sets cold.

Every owner retains its own known acyclic in-root ProjectReference closure and
exact `net10.0` identity. Project identity comes from a single literal AssemblyName
or csproj basename, with the admitted default 1.0.0 project version. Unknown,
missing, substituted or wrong-TFM projects/edges, collisions and ambiguous owners
reject. There is no owner/TFM coalescing. Assets and locks must contain the exact
owner-reachable package/project set, literal direct dependencies and SDK-shaped
paths and metadata. Lock Project key casing follows the audited SDK basename/name
rule rather than a case-insensitive fallback.

Only each selected package ID/version directory is admitted. The full configured
NuGet cache is never an allowed root. Root sets use a canonical sorted string
representation with value equality. Package bytes, exact roots, owners, literal
edges, assets, locks, caches and the cache-newer-than-assets choice are included
in the proof. Both complete inventories must agree. Noncanonical textual forms
such as dot segments, trailing dots/spaces, UNC/device paths and unsupported
escaped item specs reject, as do duplicate or case-conflicting package/file
identities and unexpected package payloads. Ordinary Windows case equivalence is
used for identity comparisons; this does not claim elimination of every Windows
path alias, including DOS 8.3 aliases.

Resource limits apply to the whole set, not separately to each package.
Retained/expanded byte caps are logical accounting limits, not peak-memory or
RSS limits:
- Per inventory: at most 2048 retained package files, 64 MiB retained package bytes
  and 64 MiB declared archive expanded bytes.
- Shared across BOTH inventories, SDK pin reads and owner inputs: 8192 visited,
  read or archive-directory entries and 256 MiB cumulative read/declared-expanded
  bytes, with a cooperative 5-second checkpoint budget.
- Metadata and SHA512-marker files have 4096/128-byte pre-read limits regardless of
  admitted filename casing. ResolvePackageAssets cache input is at most 4 MiB.
- Cache v12 keeps 17 groups, at most 1024 items/group, 4096 metadata strings and 128
  metadata pairs/item. Encoded strings are bounded to 65536 bytes and 16384 UTF-16
  characters before decoding allocation. Existing XML, UTF-16, per-file, global
  entry/byte and cancellation limits are not increased.

Enumeration charges before adding items. Remaining byte budgets are checked
before allocating file arrays. Bounded central/local ZIP validation precedes
ZipArchive entry materialization, and every expanded stream, including inert
metadata, is read only to its declared length plus a sentinel. Exotic ZIP forms
such as ZIP64, multidisk, extra fields, comments, encryption and data descriptors
are not admitted by this narrow path. The five-second budget is cooperative;
individual synchronous I/O, decoding and hashing calls are not hard-cancelled.
This contract does not claim independently implemented ZIP payload CRC checking.

Cache groups and required metadata must equal the set derived from validated
owner assets. In particular, group 8 contains all owner-reachable packages;
group 9 contains OWNER-DIRECT packages only; group 15 contains known indirect,
non-direct project references. Compile/runtime and related documentation/symbol
sets must be exact, and all unneeded groups are empty. Missing package 2, empty or
partial cache files, cache references to retained-but-unselected DLLs, unknown or
cross-package metadata and duplicate Windows file identities reject. Unselected
DLLs may remain in an admitted package; all retained DLLs are inspected and hashed. The 32-byte SDK settings
hash remains an opaque discriminator; complete decoded cache outputs are checked
and the complete bytes are fingerprinted, rather than claiming a reconstruction
of SDK HashSettings.

Hash domains remain distinct. The SHA512 marker always binds the raw archive.
For an unsigned archive without exact canonical `.signature.p7s`, metadata
contentHash must also equal that raw digest. A canonical signed archive preserves
NuGet's separate signed-content hash domain: recorded metadata remains bound to
assets/lock independently of the raw marker. Noncanonical signature-name case
aliases reject. This is not certificate/signature verification. The distinction
is specified by the pinned
[PackageExtractor](https://github.com/dotnet/dotnet/blob/5a2162bea6845eabc1fc721624a43568c65db3b1/src/nuget-client/src/NuGet.Core/NuGet.Packaging/PackageExtractor.cs#L510-L524)
and
[PackageArchiveReader](https://github.com/dotnet/dotnet/blob/5a2162bea6845eabc1fc721624a43568c65db3b1/src/nuget-client/src/NuGet.Core/NuGet.Packaging/PackageArchiveReader.cs#L458-L480).

CPM, package-to-package transitive dependencies, framework-free N workspaces,
arbitrary/unknown imports, analyzers/generators, native/content/build-task
payloads, unknown SDK/OS profiles and unproved external I/O remain unsupported
and cold. Package propagation through known project edges does not admit package
dependency edges, even if their IDs are also direct elsewhere. Proof construction
does not execute code from package/source data or restore dependencies.
