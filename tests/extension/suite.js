// End-to-end suite for the extension host (SD-027).
//
// Runs inside VS Code (launched by scripts/e2e.js) with the vscode API available. It is
// plain Node rather than mocha: the harness launches this file and reads the JSON report
// it writes, so a failure is visible without a test framework.

const fs = require('node:fs');
const path = require('node:path');

async function main() {
  const vscode = require('vscode');
  const results = [];
  const record = (name, ok, detail) => {
    results.push({ name, ok, detail });
    console.log(`${ok ? 'ok' : 'FAIL'} - ${name}${detail ? ` (${detail})` : ''}`);
  };

  try {
    const extension = vscode.extensions.getExtension('sharpdeps.sharpdeps');
    if (!extension) {
      record('extension installed', false, 'sharpdeps.sharpdeps was not found');
      return finish(results);
    }

    await extension.activate();
    const commands = await vscode.commands.getCommands(true);
    const expected = [
      'sharpdeps.showDependencyMap',
      'sharpdeps.refresh',
      'sharpdeps.showTypeDependencies',
      'sharpdeps.showTypeDependents',
      'sharpdeps.copyMermaid',
      'sharpdeps.exportSvg',
      'sharpdeps.exportPng'
    ];
    const missing = expected.filter((command) => !commands.includes(command));
    record('commands are registered', missing.length === 0, missing.join(', '));

    // The trusted-workspace gate must not block a normal run.
    record('workspace is trusted', vscode.workspace.isTrusted === true);

    // Run the Quick analysis through the real command path.
    const workspaceRoot = vscode.workspace.workspaceFolders?.[0]?.uri;
    if (!workspaceRoot) {
      record('workspace folder is open', false);
      return finish(results);
    }

    await vscode.commands.executeCommand('sharpdeps.showDependencyMap', workspaceRoot);

    // The result store is fed by the controller; the view state is written once the
    // webview reports ready, so give the panel a moment.
    await new Promise((resolve) => setTimeout(resolve, 3000));

    const api = extension.exports ?? {};
    const analysisIds = typeof api.getAnalysisIds === 'function' ? api.getAnalysisIds() : [];
    record('analysis result is registered', analysisIds.length >= 1, analysisIds.join(', '));
    record(
      'current analysis is set',
      typeof api.getCurrentAnalysisId === 'function' && api.getCurrentAnalysisId() !== undefined
    );

    const diagnostics = vscode.languages.getDiagnostics();
    record(
      'problems collection is queryable',
      Array.isArray(diagnostics),
      `${diagnostics.length} file(s)`
    );
  } catch (error) {
    record(
      'suite ran without throwing',
      false,
      error instanceof Error ? error.message : String(error)
    );
  }

  return finish(results);
}

function finish(results) {
  const failed = results.filter((entry) => !entry.ok);
  const output = process.env.SHARPDEPTS_E2E_REPORT;
  if (output) {
    fs.mkdirSync(path.dirname(output), { recursive: true });
    fs.writeFileSync(output, `${JSON.stringify({ results }, null, 2)}\n`, 'utf8');
  }

  console.log(`\n${results.length - failed.length}/${results.length} checks passed.`);
  if (failed.length > 0) {
    // The harness reads the exit code; a non-zero code fails the run.
    process.exitCode = 1;
  }
}

void main();
