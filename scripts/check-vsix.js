// VSIX content check (SD-029).
//
// Packs the extension and asserts what a user installs: the analyzer hosts and their
// runtime files, the webview entry point and worker, the styles, and the licence notices
// are present, while development-only material (sources, tests, schemas, docs, local
// state) is not. Run with `npm run check:vsix`.

const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const repoRoot = path.resolve(__dirname, '..');
const vsce = path.join(
  repoRoot,
  'node_modules',
  '.bin',
  process.platform === 'win32' ? 'vsce.cmd' : 'vsce'
);

function runVsce(args) {
  const result = spawnSync(vsce, args, {
    cwd: repoRoot,
    encoding: 'utf8',
    shell: process.platform === 'win32'
  });
  if (result.status !== 0) {
    console.error(result.stdout);
    console.error(result.stderr);
    throw new Error(`vsce ${args.join(' ')} failed with ${result.status}`);
  }

  return result.stdout;
}

const REQUIRED = [
  'out/extension.js',
  'media/app/entry.js',
  'media/workers/elkLayout.worker.js',
  'media/styles/shell.css',
  'media/styles/graph.css',
  'analyzer/bin/quick/code-map.dll',
  'analyzer/bin/quick/code-map.runtimeconfig.json',
  'analyzer/bin/quick/SharpDeps.Analysis.Quick.dll',
  'package.json',
  'README.md',
  'CHANGELOG.md',
  'THIRD-PARTY-NOTICES.md'
];

const FORBIDDEN = [
  'src/',
  'tests/',
  'schemas/',
  'docs/',
  '.local/',
  'node_modules/',
  'analyzer/src/',
  'analyzer/tests/',
  // The extension does not run the semantic host in this version: it must not ship.
  'analyzer/bin/semantic/',
  'media/viewer.js.map'
];

function main() {
  const packed = runVsce(['package', '--out', 'sharpdeps-check.vsix']);
  void packed;
  const vsixPath = path.join(repoRoot, 'sharpdeps-check.vsix');
  const entries = runVsce(['ls'])
    .split('\n')
    .map((line) => line.trim())
    .filter(
      (line) =>
        line.endsWith('.js') ||
        line.endsWith('.json') ||
        line.endsWith('.md') ||
        line.endsWith('.css') ||
        line.endsWith('.dll') ||
        line.endsWith('.png') ||
        line.endsWith('.svg')
    )
    .map((line) => line.replace(/\\/g, '/'));

  const haystack = `${entries.join('\n')}\n`;
  const missing = REQUIRED.filter((entry) => !haystack.includes(entry));
  const leaked = FORBIDDEN.filter(
    (entry) => haystack.includes(`\n${entry}`) || haystack.startsWith(entry)
  );

  const report = {
    checkedAt: new Date().toISOString(),
    vsix: path.basename(vsixPath),
    bytes: fs.statSync(vsixPath).size,
    packagedEntries: entries.length,
    required: REQUIRED.map((entry) => ({ entry, present: haystack.includes(entry) })),
    forbidden: FORBIDDEN.map((entry) => ({ entry, present: leaked.includes(entry) })),
    ok: missing.length === 0 && leaked.length === 0
  };

  const evidenceDirectory = path.join(repoRoot, 'docs', 'implementation', 'v0.1.0', 'evidence');
  fs.mkdirSync(evidenceDirectory, { recursive: true });
  fs.writeFileSync(
    path.join(evidenceDirectory, 'sd-029-vsix.json'),
    `${JSON.stringify(report, null, 2)}\n`,
    'utf8'
  );

  console.log(`VSIX ${report.vsix}: ${report.bytes} bytes, ${report.packagedEntries} entries`);
  if (missing.length > 0) {
    console.error(`Missing from the VSIX: ${missing.join(', ')}`);
  }

  if (leaked.length > 0) {
    console.error(`Should not be packaged: ${leaked.join(', ')}`);
  }

  if (!report.ok) {
    process.exit(1);
  }
}

main();
