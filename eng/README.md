# Repository maintenance

These instructions are for contributors maintaining this source tree. End-user installation and operation start in [README](../README.md) and the [user guide](../docs/guide.md); this directory is not part of the VSIX documentation.

## Build

Use Node.js 20 or later and .NET SDK 10:

```bash
npm ci
npm run package
```

Packaging builds the Quick, Semantic and saved-index Query hosts and webview, then creates `sharpdeps-<package.json version>.vsix`. For an extension development window, run `npm run build:analyzer` and `npm run build`, then press F5 in VS Code. A local package does not establish publication.

## References

- [Release maintenance](releasing.md): channel selection, candidate checks and existing publication workflow.
- [Extraction oracle](../analyzer/tests/SharpDeps.Cli.Tests/FULL-ORACLE.md) and [owned-process support](oracles/README.md): active test procedures.
- [Technical contracts](../docs/contracts/cli-skill-v1.ja.md): CLI, index, source and protocol compatibility. Machine-readable fixtures and artifact pins remain beside their related contracts.
- [Current implementation notes](../docs/development/explore-discovery.md), [phase diagnostics](../docs/development/phase-metrics.md), [semantic sessions](../docs/development/semantic-session.md) and [Markdown preparation](../docs/development/markdown-session-cache.md): maintainer references, not claims about released capabilities.
- [Comparison tooling](../comparison/README.md): explicit comparison preparation; its smoke checks and raw tool results are not AI answer or general performance results.

Historical development-start and acceptance logs are available through Git history. Runtime fixtures, conformance data, licenses and current compatibility references remain in the tree.
