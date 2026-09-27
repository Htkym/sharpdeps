// VS Code end-to-end runner (SD-027).
//
// Launching VS Code needs a real installation: this script uses an existing one
// (SHARPDEPTS_VSCODE or a PATH installation) or the one @vscode/test-electron would
// download. When neither is available it exits with code 2 and says so, so a missing
// machine never looks like a passing test.

const fs = require('node:fs');
const path = require('node:path');

const repoRoot = path.resolve(__dirname, '..');
const reportPath = path.join(
  repoRoot,
  'docs',
  'implementation',
  'v0.1.0',
  'evidence',
  'sd-027-e2e.json'
);

function findInstalledCode() {
  if (process.env.SHARPDEPTS_VSCODE) {
    return process.env.SHARPDEPTS_VSCODE;
  }

  const candidates = [
    path.join(process.env.LOCALAPPDATA ?? '', 'Programs', 'Microsoft VS Code', 'Code.exe'),
    'C:\\Program Files\\Microsoft VS Code\\Code.exe',
    path.join(process.env.HOME ?? '', '.local', 'share', 'code', 'code'),
    '/usr/share/code/code'
  ];
  return candidates.find((candidate) => candidate && fs.existsSync(candidate));
}

function loadTestElectron() {
  for (const name of ['@vscode/test-electron', 'vscode-test']) {
    try {
      return require(name);
    } catch {
      // Try the next option.
    }
  }

  return undefined;
}

async function main() {
  const executable = findInstalledCode();
  const testElectron = loadTestElectron();

  if (!executable && !testElectron) {
    console.error(
      'VS Code end-to-end run skipped: no VS Code installation was found and ' +
        '@vscode/test-electron is not installed.\n' +
        'Install one of them (npm install --save-dev @vscode/test-electron) and run ' +
        '`npm run test:e2e` again, or set SHARPDEPTS_VSCODE to a Code executable.'
    );
    process.exit(2);
  }

  const workspace = path.join(repoRoot, 'tests', 'fixtures', 'quick-baseline');
  const testsPath = path.join(repoRoot, 'tests', 'extension', 'suite.js');
  const launchArgs = [workspace, '--disable-extensions', '--disable-workspace-trust'];
  process.env.SHARPDEPTS_E2E_REPORT = reportPath;

  if (testElectron?.runTests) {
    await testElectron.runTests({
      vscodeExecutablePath: executable,
      extensionDevelopmentPath: repoRoot,
      extensionTestsPath: testsPath,
      launchArgs
    });
  } else if (testElectron?.runVSCodeCommand) {
    // Older vscode-test API: resolve a version and run the tests the same way.
    const cli = testElectron;
    const vscodePath = executable ?? (await cli.downloadAndUnzipVSCode());
    const { runTests } = await import('@vscode/test-electron');
    await runTests({
      vscodeExecutablePath: vscodePath,
      extensionDevelopmentPath: repoRoot,
      extensionTestsPath: testsPath,
      launchArgs
    });
  }

  if (!fs.existsSync(reportPath)) {
    console.error(
      'VS Code end-to-end run finished without a report; the suite probably did not run.'
    );
    process.exit(1);
  }

  const report = JSON.parse(fs.readFileSync(reportPath, 'utf8'));
  const failed = report.results.filter((entry) => !entry.ok);
  console.log(
    `E2E report: ${reportPath} (${report.results.length - failed.length}/${report.results.length} passed)`
  );
  if (failed.length > 0) {
    process.exit(1);
  }
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
