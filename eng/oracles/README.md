# Controlled SDK oracle validation

The ordinary `SharpDeps.Cli.Tests` project uses xUnit and the standard VSTest
adapter. Its default `ordinary.runsettings` explicitly excludes
`ControlledSdkOracle=true`. Exclusion is a selection decision, not a passing
or skipped oracle result. The portable package-set contract tests remain in the
ordinary suite. Windows-specific deterministic tests retain their platform gates.

The controlled trait marks the SDK403 singleton/Git oracle, the N-package edit
oracle, the N-package mutation oracle, and the atomic fixture-root filesystem
check. Their input and ownership requirements differ; select each separately. These depend on reviewed local Windows
prerequisites and must be selected intentionally. Do not remove their assertions
or add environment-dependent skips to obtain an ordinary-suite pass.

The controlled tests have different launch contracts:

- `Sdk403PackageAndOrdinaryGitStayClosedAcrossEditsFallbackAndPublication` uses
  its existing singleton/Git fixture and exact SDK gate. It does not use the
  mutation oracle's owner-input contract.
- `ActualSdkPackageSetMatchesIndependentFullAcrossEditsConfigurationDependencyAndFallback`
  uses the existing N-package edit fixture and legacy child helper. Its SDK gate
  is not an owned-stop binding.
- `ActualSecondPackageMutationsRejectReuseAndPublicationWithoutLosingPreviousGeneration`
  requires the explicit fixture and dynamic owned-stop inputs below.
- `CreatesFreshRootAndRejectsExistingEntriesWithoutChangingBytes` is a separate
  Windows filesystem check, not an SDK indexing oracle.

`StandardJobOwner` is a separate package-free .NET 10 Windows tool, outside the
analyzer solution and production dependency graph. Build it using its original
project. `--self-check` is process-free; `--plan <plan.json>` validates a launch
plan without starting a child. `--execute <plan.json>` starts the specified
standard VSTest command inside its own Windows Job and writes owner receipts.
The tool does not decide test success: a genuine TRX from the selected test does.

The package-set mutation oracle requires all three explicit fixture inputs:
`SHARPDEPS_FACT2_FIXTURE_ROOT`, `SHARPDEPS_FACT2_DOTNET_HOST`, and
`SHARPDEPS_FACT2_DOTNET_ROOT`. Its separate owner injects the dynamic
`SHARPDEPS_OWNED_STOP_PATH` and `SHARPDEPS_OWNED_STOP_NONCE` binding. Missing,
partial, or conflicting inputs fail before fixture creation. The fixture root
must be a previously absent named direct child of `D:\gitroot`, separate from
the owner root. The admitted host is `C:\Program Files\dotnet\dotnet.exe`.
Review `LaunchPlan.cs` before preparing a run; a normal test command is not an
ownership or resource-window approval.

For an approved mutation-oracle run, select its exact fully qualified name using standard
`dotnet vstest <SharpDeps.Cli.Tests.dll> --TestCaseFilter:<exact filter>` under the
reviewed owner. Review the distinct legacy launch contracts before selecting
either of the other two real SDK oracles. `dotnet vstest` acts on the test DLL and does not load the project
runsettings default. Never broaden an owned run to the whole controlled trait
implicitly. The atomic fixture-root test creates and retains small evidence
entries directly under `D:\gitroot`; select it separately when needed.

The existing VSIX allowlist excludes this directory; the package-content gate
also rejects `eng/oracles/` entries explicitly. Keep private plans, receipts,
logs, and runsettings probes outside packaged runtime paths.

Ordinary CI continues to run the repository's existing solution, TypeScript,
browser, VSIX, and end-to-end checks. A green ordinary suite does not establish
controlled SDK-oracle acceptance, performance, or real-repository applicability.
