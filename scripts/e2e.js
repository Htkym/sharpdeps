// VS Code end-to-end runner (SD-027).
//
// Launching VS Code needs a real installation: this script uses an existing one
// (SHARPDEPTS_VSCODE or a PATH installation) or the one @vscode/test-electron would
// download. When neither is available it exits with code 2 and says so, so a missing
// machine never looks like a passing test.

const fs = require('node:fs');
const path = require('node:path');

const repoRoot = path.resolve(__dirname, '..');
const scenario = process.env.SHARPDEPTS_E2E_SCENARIO;
const reportPath = path.join(
  repoRoot,
  'docs',
  'implementation',
  'v0.1.0',
  'evidence',
  scenario
    ? `sd-030-${scenario}.json`
    : process.env.SHARPDEPTS_E2E_MODE === 'vsix'
      ? 'sd-027-e2e-vsix.json'
      : 'sd-027-e2e.json'
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
  const startedAt = Date.now();
  fs.rmSync(reportPath, { force: true });
  const testElectron = loadTestElectron();
  if (!testElectron) throw new Error('Run npm ci to install the pinned VS Code test runner.');
  const executable = findInstalledCode() ?? (await testElectron.downloadAndUnzipVSCode());
  const vsixMode = process.env.SHARPDEPTS_E2E_MODE === 'vsix';
  const profile =
    process.env.SHARPDEPTS_E2E_PROFILE ??
    path.join(repoRoot, '.local', vsixMode ? 'e2e-vsix-review' : 'e2e-review');
  const extensions = path.join(profile, 'extensions');
  fs.mkdirSync(extensions, { recursive: true });
  const profileArgs = [`--user-data-dir=${profile}`, `--extensions-dir=${extensions}`];
  // Run the CLI entry point, not Code.exe's graphical entry point.
  const [cli, ...cliArgs] = testElectron.resolveCliArgsFromVSCodeExecutablePath(executable, {
    reuseMachineInstall: true
  });
  const { spawnSync } = require('node:child_process');
  const install = (extension) => {
    const cliScript =
      process.platform === 'win32'
        ? fs.readFileSync(cli, 'utf8').match(/"%~dp0([^"]+cli\.js)"/i)?.[1]
        : undefined;
    if (process.platform === 'win32' && !cliScript)
      throw new Error('The VS Code CLI entry point was not found.');
    const result = spawnSync(
      cliScript ? executable : cli,
      [
        ...(cliScript ? [path.resolve(path.dirname(cli), cliScript)] : cliArgs),
        ...profileArgs,
        '--install-extension',
        extension,
        '--force'
      ],
      {
        encoding: 'utf8',
        timeout: 120000,
        windowsHide: true,
        env: { ...process.env, ...(cliScript ? { ELECTRON_RUN_AS_NODE: '1', VSCODE_DEV: '' } : {}) }
      }
    );
    if (result.status !== 0)
      throw new Error(`Extension install failed: ${extension}\n${result.stderr ?? result.error}`);
  };
  install('ms-dotnettools.vscode-dotnet-runtime');
  if (vsixMode) install(path.join(repoRoot, 'sharpdeps-check.vsix'));
  // VS Code starts extension tests only in a development host. Use a separate,
  // empty harness so SharpDeps itself is still loaded from the installed VSIX.
  const harness = path.join(profile, 'test-harness');
  if (vsixMode) {
    fs.mkdirSync(harness, { recursive: true });
    fs.writeFileSync(
      path.join(harness, 'package.json'),
      JSON.stringify({
        name: 'sharpdeps-test-harness',
        publisher: 'sharpdeps-tests',
        version: '0.0.0',
        engines: { vscode: '^1.90.0' }
      })
    );
  }
  const port = await new Promise((resolve) => {
    const server = require('node:net').createServer();
    server.listen(0, '127.0.0.1', () => {
      const port = server.address().port;
      server.close(() => resolve(port));
    });
  });
  const options = {
    vscodeExecutablePath: executable,
    extensionDevelopmentPath: vsixMode ? harness : repoRoot,
    extensionTestsPath: path.join(
      repoRoot,
      'tests',
      'extension',
      scenario ? 'acceptance.js' : 'suite.js'
    ),
    extensionTestsEnv: {
      SHARPDEPTS_E2E_REPORT: reportPath,
      SHARPDEPTS_E2E_DEBUG_PORT: String(port),
      SHARPDEPTS_REPO: repoRoot
    },
    launchArgs: [
      process.env.SHARPDEPTS_E2E_WORKSPACE ??
        path.join(repoRoot, 'tests', 'fixtures', 'quick-baseline'),
      ...profileArgs,
      `--remote-debugging-port=${port}`,
      '--disable-gpu',
      '--disable-telemetry',
      '--disable-updates'
    ],
    reuseMachineInstall: true
  };
  if (scenario === 'trust') {
    fs.mkdirSync(path.join(profile, 'User'), { recursive: true });
    fs.writeFileSync(
      path.join(profile, 'User', 'settings.json'),
      JSON.stringify({
        'security.workspace.trust.enabled': true,
        'security.workspace.trust.startupPrompt': 'never',
        'security.workspace.trust.emptyWindow': false
      })
    );
    // test-electron always adds --disable-workspace-trust. This case deliberately
    // launches the same extension test host without that switch.
    await new Promise((resolve, reject) => {
      const child = require('node:child_process').spawn(
        executable,
        [
          ...options.launchArgs,
          '--no-sandbox',
          '--skip-welcome',
          '--skip-release-notes',
          `--extensionDevelopmentPath=${options.extensionDevelopmentPath}`,
          `--extensionTestsPath=${options.extensionTestsPath}`
        ],
        {
          windowsHide: true,
          env: { ...process.env, ...options.extensionTestsEnv },
          stdio: 'inherit'
        }
      );
      child.on('error', reject);
      child.on('exit', (code) =>
        code === 0 ? resolve() : reject(new Error(`Trust host exit ${code}`))
      );
    });
  } else await testElectron.runTests(options);
  if (!fs.existsSync(reportPath)) throw new Error('VS Code finished without a test report.');
  const report = JSON.parse(fs.readFileSync(reportPath, 'utf8'));
  if (
    !Number.isFinite(Date.parse(report.checkedAt)) ||
    Date.parse(report.checkedAt) < startedAt ||
    !report.results?.length
  )
    throw new Error('The end-to-end report is stale or empty.');
  const failed = report.results.filter((entry) => !entry.ok);
  console.log(`E2E: ${report.results.length - failed.length}/${report.results.length} passed`);
  if (failed.length) process.exitCode = 1;
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
