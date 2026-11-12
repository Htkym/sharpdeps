# Attach-only process observation

`scripts/observe-process.ps1` is a separate, opt-in observer for SDN-09 and SDN-18.
It requires PowerShell 7 on Windows with identified local-volume paths and an
explicitly supplied, already running PID. It never
launches, signals, suspends, kills or changes the target, follows no descendants,
and does not write to product stdout, the index or the product's metrics sink.
Run it separately; do not combine its exit status with the product command.

```powershell
pwsh -NoProfile -File scripts/observe-process.ps1 -TargetProcessId 12345 `
  -OutputDirectory D:\private-review\new-observation `
  -WorkspaceRoot D:\workspace `
  -InputManifestPath D:\private-review\run-manifest.json `
  -DurationSeconds 120 -IntervalMilliseconds 1000 `
  -DatabasePath D:\workspace\.sharpdeps\index.sqlite `
  -PhaseDirectory D:\workspace\.sharpdeps\new-phase-sink `
  -MarkerPath D:\private-review\watch-markers.jsonl `
  -StopFile D:\private-review\stop-observer
```

The destination must be a new absolute directory, disjoint from the supplied
workspace tree, every protected input root and every explicitly observed input.
Ancestor and descendant overlap are both rejected. Existing reparse-point ancestors
are refused for output, workspace, protected roots and observed inputs, including
existing file links. The observer rejects aliases conservatively rather than
resolving junctions or reading the target command line. All guards and output are
syntax-checked before any attribute, Exists or file-open call. UNC, device and
ambiguous paths are refused without probing them. The local DOS-device namespace
must identify the current root mapping as a local disk volume; mapped network,
SUBST and unknown devices are refused before file access. Only the first mapping
is current; historical mappings never grant access. The volume GUID mapping must
match it. Subsequent probes use that volume namespace, not the drive letter.
Ancestor checks and native opens walk root to leaf and stop at a reparse point
before accessing any descendant. All guards and output are then mapped to the
same physical local-volume namespace using read-attributes-only handles from
the root through the nearest existing ancestor and normalized volume-GUID final paths.
Validated missing suffixes are appended afterward. This also catches 8.3/short-name
aliases; lookup failure refuses launch. Reparse rejection remains in
place. Network/unknown-volume, device and ambiguous suffix paths are refused rather
than compared lexically. The fixed native helper is loaded lazily and compiled in
memory only when the opt-in observer or path tests run; importing the module alone
does not compile it. Input links are checked
again when sampling file metadata or tailing diagnostics.

The PID, workspace root, bounded caller manifest, destination and duration are
required. Other inputs are optional. The workspace root must be an existing
absolute directory. The caller manifest must contain this explicit guard contract:

```json
{
  "protectedInputRoots": {
    "toolPayloadRoots": ["D:\\tool-payload"],
    "dotnetRoots": ["C:\\Program Files\\dotnet"],
    "runtimeRoots": ["C:\\Program Files\\dotnet\\shared"],
    "sdkRoots": ["C:\\Program Files\\dotnet\\sdk"],
    "nugetRoots": ["D:\\nuget-caches"],
    "packageRoots": ["D:\\nuget-caches\\packages"],
    "packageInputs": "configured"
  }
}
```

Supply the complete target-specific roots from the run manifest, including extra
SDK/tool trees, all NuGet/fallback/cache and package input locations. Every role is
mandatory and requires an explicit array. Missing, unknown, relative, empty or
inaccessible guards fail closed before creating output. If the target has no
NuGet/package inputs, use `packageInputs: "none"` together with explicit empty
`nugetRoots` and `packageRoots` arrays. Otherwise both package roles must be
nonempty. An unrelated string list cannot replace the named role contract.
The manifest is limited to 64 KiB. Root values and unrelated manifest fields are
never exported. The observer does not infer whether caller declarations are
complete: if those roots are unknown, do not launch it.

Database and WAL reads use file length metadata only, never SQLite queries,
database-content reads, checkpoints or file hashing.
The WAL path is the database path plus `-wal`. An absent WAL is null, not zero.
The observer binds the original process start identity; exit or identity changes
stop observation without looking up a replacement PID. Failure to establish that
identity is recorded and stops collection.

`observations.jsonl` retains timestamped raw samples, sanitized lifecycle evidence
and a final summary. Collection failures remain observer failures and cannot
change the separately running product's result. Ctrl+C, or creation of the supplied
stop file, stops only this observer. Completed writes are flushed individually;
forced observer termination can leave no final record. A missing final record is
unfinished evidence. The observer never claims a product exit status or success.

## Metric meaning

- `workingSetBytes` is one current process gauge. `intervalSampledMaxWorkingSetBytes`
  is the maximum observed gauge during attachment, not an exact interval peak.
- `osLifetimePeakWorkingSetBytes` is the OS process-lifetime high-water value. It
  can include work before attachment. Its observed maximum is labelled separately.
- `processWideCpuAbsoluteMilliseconds` is accumulated process-wide CPU, including
  pre-attachment work and concurrent requests. Adjacent available samples retain
  both CPU delta and `cpuDeltaSamplingStartIntervalMilliseconds`; missing samples
  can widen that reference interval.
  The summary is last minus first available CPU within attachment. A regression
  makes that summary unavailable. `cpuObservedSamplingStartIntervalMilliseconds`
  is the first-to-last reference interval. CPU is not attributed exclusively to a phase.
- `processWideIo` retains the target process's cumulative read/write/other operation
  counts and transfer bytes, from local PID-filtered `Win32_Process` CIM properties.
  Only those six counters and `CreationDate` are requested; no command line,
  executable path, environment or descendant is requested or exported by the observer. Creation time is checked
  against the original process identity at CIM's microsecond precision, with the
  retained process object and exact original start time checked before and after
  each query. Unsupported,
  unavailable or changed identities retain null with fixed reasons. Zero is valid
  only when observed. UInt64 absolute counts and decimal differences retain exact
  low bits. The first absolute sample is lifetime data, never an attachment delta.
  Adjacent available deltas and first-to-last summaries retain each counter's
  `deltaSamplingStartIntervalMilliseconds`, including gaps; regressions make summaries
  unavailable. These are reference intervals between observer loop sampling starts,
  using the monotonic stopwatch (`intervalBasis: observer-loop-sampling-start-monotonic`).
  They are not counter acquisition windows. `counterTimestamp` remains null with
  `not-provided-by-source`: neither kernel nor CIM-provider counter time is known.
  Unequal CIM/collection delays can shift acquisitions relative to these starts.
  Do not use these intervals as measured disk-I/O rates. Counters are
  process-wide, include concurrent work and non-disk I/O, and are not per-file,
  physical-disk, phase-exclusive or DB/WAL write attribution. No privileges are
  elevated. A refused or unavailable local CIM query remains missing evidence.
- Database/WAL lengths and signed observed length changes are size gauges. They
  do not measure writes, fsyncs, write amplification or cumulative bytes written.
  Both `*BytesWritten` fields remain null with `not-measured`. WAL truncation or a
  same-length overwrite cannot be interpreted as zero bytes written. File-specific
  cumulative writes would require additional file-I/O tracing (for example ETW with
  appropriate access/trace configuration) or instrumented SQLite/VFS boundaries,
  with their own overhead and validation. Neither is enabled here; process-wide
  counters do not complete the SDN-18 file-write acceptance requirement.
- Null metrics always have a fixed reason. Zero is retained only when observed.
  CPU/I/O reference intervals, sizes and process reads are sequential, not an atomic snapshot.

Sample `utc` and `elapsedMilliseconds` label the observer loop sampling start
(`timestampBasis`), before sequential process, CIM and file reads. UTC is a client
wall clock; elapsed values and CPU/I/O reference intervals use a monotonic stopwatch.
They are not interchangeable. No field guarantees when the provider acquired its counters.

The observer's requested interval is a delay between reads, not a sampling rate
guarantee. Reads and JSONL processing add overhead; duration can overrun by one
interval plus collection time. Local CIM calls request a one-second operation timeout,
whose host/provider behavior needs real acceptance; this is not a hard loop deadline. Observer overhead needs its own later control run.
No performance baseline, overhead result or resource cap follows from these scripts.

## Watch lifecycle evidence

With `-PhaseDirectory`, the observer tails top-level producer-named JSONL files.
It projects validated sequence/run framing, run start/end, root run duration,
`watch.settle`, `watch.input-scan`, `watch.update-wait`, `watch.idle-wait`, and the
fixed reconciliation/notification/wakeup decisions emitted by `watch-cycle` runs.
Producer `run.end` and decision records do not contain UTC timestamps: these keep
null producer UTC and a separate observation timestamp. Span UTC is reconstructed
from the producer's run-start UTC and monotonic offset. Wall-clock adjustments and
cross-process clock alignment remain caller concerns. Spans can overlap or nest;
raw spans are not added together or reported as exclusive costs.

There is no inferred idle or resync state when the producer emits no such evidence.
Older product metrics can observe extraction runs without proving idle/resync.
A caller can append explicit markers to `-MarkerPath`, one newline-terminated JSON
object per record, using this schema:

```json
{"schema":"sharpdeps-observer-marker/1","sequence":1,"utc":"2026-11-08T00:00:00.0000000+00:00","kind":"idle-start"}
```

Allowed kinds are `checkout-start`, `checkout-end`, `acquisition-start`,
`acquisition-end`, `restore-start`, `restore-end`, `watch-start`, `watch-stop`,
`update-start`, `update-end`,
`idle-start`, `idle-end`, `resync-start`, `resync-end`, `cancelled` and `failure`.
Sequence starts at one and is contiguous. Caller markers are explicit claims,
not product-confirmed events. C0 setup boundaries separate checkout, acquisition
and restore only when the caller supplies them. The observer never launches or
infers setup, acquisition or restore. The observer does not generate markers or edit the
product. Use a fresh phase sink and marker file to avoid unrelated earlier runs;
pre-existing records keep their original producer timestamps and are not treated
as attachment-interval resource measurements. Events are retained in file-read
order, with no fabricated cross-file ordering. No phase aggregates are calculated.

Input limits are 4096 phase files, 16 MiB combined input bytes, 64 KiB per record
and 65536 combined records. Partial appends wait for a newline. Truncated,
malformed, unavailable, oversized and unfinished evidence is recorded as null
with fixed reasons. A broken input disables only its own tail; limits can make
later tails unavailable. Producer-labelled values are projected through fixed
allowlists. Arbitrary fields, exception messages, paths, file names, source text
and environment values never enter exported observer diagnostics. Callers can
keep a separately authorized private manifest with commit/binary/corpus hashes,
SDK/machine, configuration/TFM/RID, restore/cache state and parallelism. This
observer neither creates that manifest nor copies arbitrary manifest contents.

## Synthetic checks

After resource authorization, run:

```powershell
pwsh -NoProfile -File scripts/Test-ProcessObservation.ps1
pwsh -NoProfile -File scripts/Test-ProcessIoObservation.ps1
```

These checks use injected counters/time and temporary byte fixtures. The I/O
checks invoke no OS provider and cover missing/partial counters, reused identity,
unsupported platforms, exact UInt64 deltas, regression, gaps, unequal collection delays, backward UTC changes and privacy. They cover
C0 caller boundaries, CPU baselines/regression/missing samples, lifetime versus sampled peaks, negative
size changes, absent WAL, identity/exit, fixed lifecycle projection and privacy,
partial/malformed/truncated/bounded and late-created input, required role guards,
both overlap directions, injected reparse ancestors, mocked physical aliases, volume-root containment,
unknown canonical identity, output isolation, cancellation, duration
and collection failure. They launch no product process and perform no benchmark.

## Acceptance limits

CIM/attach acceptance is limited to a short functional smoke with an explicitly
selected installed PowerShell 7 host and an owned .NET target. Other host/runtime
layouts require their own acceptance. The observer fails closed when any required
protected root or ancestor is inaccessible. Do not omit that root, replace it with
a broader root, change permissions or use another path to force launch.

Natural exit, Ctrl+C under the actual host, long watch/resync, resource caps and
observer overhead still require separate acceptance runs.


Official contracts: [Win32_Process read-only counters](https://learn.microsoft.com/windows/win32/cimwin32prov/win32-process),
[IO_COUNTERS scope](https://learn.microsoft.com/windows/win32/api/winnt/ns-winnt-io_counters)
and [GetProcessIoCounters access requirements](https://learn.microsoft.com/windows/win32/api/winbase/nf-winbase-getprocessiocounters).
The observer uses the local CIM projection, not a native call or privileged trace.