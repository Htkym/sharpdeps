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
  // A dedicated profile keeps the user's own VS Code untouched and holds the runtime
  // extension the product depends on.
  const profile = process.env.SHARPDEPTS_E2E_PROFILE ?? path.join(repoRoot, '.local', 'e2e-vscode');
  const launchArgs = [workspace, '--disable-workspace-trust', `--user-data-dir=${profile}`];
  process.env.SHARPDEPTS_E2E_REPORT = reportPath;

  async function installDependency() {
    if (typeof executable !== 'string') {
      return;
    }

    const { spawn } = require('node:child_process');
    const exitCode = await new Promise((resolve) => {
      const child = spawn(
        executable,
        [
          `--user-data-dir=${profile}`,
          '--install-extension',
          'ms-dotnettools.vscode-dotnet-runtime',
          '--force'
        ],
        { stdio: 'inherit' }
      );
      child.on('exit', (code) => resolve(code ?? 1));
      child.on('error', () => resolve(1));
    });
    if (exitCode !== 0) {
      console.error(
        'The .NET Install Tool extension could not be installed into the test profile; ' +
          'the end-to-end run needs it because the extension depends on it.'
      );
      process.exit(1);
    }
  }

  if (testElectron?.runTests) {
    await testElectron.runTests({
      vscodeExecutablePath: executable,
      extensionDevelopmentPath: repoRoot,
      extensionTestsPath: testsPath,
      launchArgs
    });
  } else if (executable) {
    // No @vscode/test-electron: launch the installed VS Code directly. The test runner
    // is built into VS Code, so --extensionTestsPath works without extra dependencies.
    await installDependency();
    const { spawn, execFile } = require('node:child_process');
    const args = [
      `--extensionDevelopmentPath=${repoRoot}`,
      `--extensionTestsPath=${testsPath}`,
      '--disable-gpu',
      '--no-sandbox',
      // A fresh profile must not wait on onboarding or updates before running the tests.
      '--skip-welcome',
      '--skip-release-notes',
      '--disable-telemetry',
      '--disable-updates',
      ...launchArgs
    ];
    const exitCode = await new Promise((resolve) => {
      const child = spawn(executable, args, { stdio: 'inherit', env: { ...process.env } });
      // A stuck window must not hang the whole run: kill the whole test instance after a
      // generous timeout (VS Code ignores a plain SIGTERM and keeps helper processes).
      const watchdog = setTimeout(() => {
        console.error('VS Code did not finish the end-to-end run in time; stopping it.');
        stopTree(child.pid);
        resolve(124);
      }, 240_000);
      child.on('exit', (code) => {
        clearTimeout(watchdog);
        resolve(code ?? 1);
      });
      child.on('error', (error) => {
        clearTimeout(watchdog);
        console.error(`VS Code could not be started: ${error.message}`);
        resolve(1);
      });
    });
    if (exitCode !== 0) {
      console.error(`VS Code exited with code ${exitCode}.`);
    }

    function stopTree(pid) {
      if (!pid) {
        return;
      }

      if (process.platform === 'win32') {
        execFile('taskkill', ['/PID', String(pid), '/T', '/F'], () => undefined);
      } else {
        process.kill(pid, 'SIGKILL');
      }
    }
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
