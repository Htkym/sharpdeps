// VSIX content check (SD-029).
//
// Packs the extension and asserts what a user installs: the analyzer hosts and their
// runtime files, the webview entry point and worker, the styles, and the licence notices
// are present, while development-only material (sources, tests, schemas, plans, local
// state) is not. Run with `npm run check:vsix`.

const { spawnSync } = require('node:child_process');
const fs = require('node:fs');
const path = require('node:path');

const repoRoot = path.resolve(__dirname, '..');
const vsce = require.resolve('@vscode/vsce/vsce');

function runVsce(args) {
  const result = spawnSync(process.execPath, [vsce, ...args], {
    cwd: repoRoot,
    encoding: 'utf8',
    windowsHide: true
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
  'analyzer/bin/semantic/sharpdeps-semantic-host.dll',
  'analyzer/bin/semantic/sharpdeps-semantic-host.runtimeconfig.json',
  'analyzer/bin/semantic/BuildHost-netcore/Microsoft.CodeAnalysis.Workspaces.MSBuild.BuildHost.dll',
  'resources/ELK-LICENSE.md',
  'resources/icon.png',
  'LICENSE.txt', // VSCE adds .txt to an extensionless LICENSE in the archive.
  'package.json',
  'readme.md',
  'README.ja.md',
  'docs/guide.md',
  'docs/guide.ja.md',
  ...['overview', 'evidence', 'spacing', 'table'].flatMap((name) =>
    ['en', 'ja'].map((language) => `images/${name}-${language}.png`)
  ),
  'changelog.md',
  'THIRD-PARTY-NOTICES.md'
];

const FORBIDDEN = [
  'src/',
  'tests/',
  'schemas/',
  'docs/implementation/',
  'docs/adr/',
  '.local/',
  'node_modules/',
  'analyzer/src/',
  'analyzer/tests/',
  'media/viewer.js',
  'media/viewer.css',
  'media/graph/prototype.js',
  'out/extension.js.map',
  'media/app/entry.js.map',
  'media/workers/elkLayout.worker.js.map',
  'analyzer/bin/quick/code-map.exe',
  'analyzer/bin/semantic/sharpdeps-semantic-host.exe'
];

async function main() {
  const packed = runVsce(['package', '--out', 'sharpdeps-check.vsix']);
  void packed;
  const vsixPath = path.join(repoRoot, 'sharpdeps-check.vsix');
  // Inspect the ZIP actually produced, including files with no recognised extension.
  const entries = await new Promise((resolve, reject) => {
    require('yauzl').open(vsixPath, { lazyEntries: true }, (error, zip) => {
      if (error) return reject(error);
      const names = [];
      zip.on('error', reject);
      zip.on('entry', (entry) => {
        names.push(entry.fileName.replace(/^extension\//, ''));
        zip.readEntry();
      });
      zip.on('end', () => resolve(names));
      zip.readEntry();
    });
  });
  const missing = REQUIRED.filter((entry) => !entries.includes(entry));
  const leaked = FORBIDDEN.filter((entry) =>
    entries.some((name) => (entry.endsWith('/') ? name.startsWith(entry) : name === entry))
  );

  const report = {
    checkedAt: new Date().toISOString(),
    vsix: path.basename(vsixPath),
    bytes: fs.statSync(vsixPath).size,
    sha256: require('node:crypto')
      .createHash('sha256')
      .update(fs.readFileSync(vsixPath))
      .digest('hex'),
    packagedEntries: entries.length,
    required: REQUIRED.map((entry) => ({ entry, present: entries.includes(entry) })),
    forbidden: FORBIDDEN.map((entry) => ({ entry, present: leaked.includes(entry) })),
    ok: missing.length === 0 && leaked.length === 0
  };

  const evidenceDirectory = path.join(repoRoot, '.local', 'verification');
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

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
