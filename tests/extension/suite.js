// End-to-end suite for the extension host (SD-027).
//
// VS Code's built-in test runner loads this module and calls run(). Assertions are plain
// checks: a failure throws with every failing check listed, and the run also writes a
// JSON report for the evidence folder.

const fs = require('node:fs');
const path = require('node:path');

async function run() {
  const vscode = require('vscode');
  const results = [];
  const record = (name, ok, detail) => {
    results.push({ name, ok, detail });
    console.log(`${ok ? 'ok' : 'FAIL'} - ${name}${detail ? ` (${detail})` : ''}`);
  };

  const extension = vscode.extensions.getExtension('htkym.sharpdeps');
  record('extension installed', Boolean(extension), 'htkym.sharpdeps');
  if (!extension) {
    return finish(vscode, results);
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
  record('workspace is trusted', vscode.workspace.isTrusted === true);

  const workspaceRoot = vscode.workspace.workspaceFolders?.[0]?.uri;
  record('workspace folder is open', Boolean(workspaceRoot), workspaceRoot?.fsPath ?? 'none');
  if (!workspaceRoot) {
    return finish(vscode, results);
  }

  // Run the real Quick analysis through the command path.
  await vscode.commands.executeCommand('sharpdeps.showDependencyMap', workspaceRoot);
  // The command awaits the analysis, but the panel's registration happens right after:
  // give it a moment before asserting.
  await new Promise((resolve) => setTimeout(resolve, 3000));

  const api = extension.exports ?? {};
  const analysisIds = typeof api.getAnalysisIds === 'function' ? api.getAnalysisIds() : [];
  record(
    'analysis result is registered',
    analysisIds.length >= 1,
    analysisIds.join(', ') || diagnosticsFor(extension, vscode)
  );
  record(
    'current analysis is set',
    typeof api.getCurrentAnalysisId === 'function' && api.getCurrentAnalysisId() !== undefined,
    api.getCurrentAnalysisId?.() ?? 'undefined'
  );

  const diagnostics = vscode.languages.getDiagnostics();
  record(
    'problems collection is queryable',
    Array.isArray(diagnostics),
    `${diagnostics.length} file(s)`
  );

  return finish(vscode, results);
}

async function finish(vscode, results) {
  const failed = results.filter((entry) => !entry.ok);
  const output = process.env.SHARPDEPTS_E2E_REPORT;
  if (output) {
    fs.mkdirSync(path.dirname(output), { recursive: true });
    fs.writeFileSync(
      output,
      `${JSON.stringify(
        {
          checkedAt: new Date().toISOString(),
          vscodeVersion: vscode.version,
          results
        },
        null,
        2
      )}\n`,
      'utf8'
    );
  }

  console.log(`\n${results.length - failed.length}/${results.length} checks passed.`);
  if (failed.length > 0) {
    throw new Error(
      `SharpDeps end-to-end checks failed: ${failed.map((entry) => entry.name).join('; ')}`
    );
  }
}

module.exports = { run };

/** Extra context when the analysis did not register: why it could not run. */
function diagnosticsFor(extension, vscode) {
  const fs = require('node:fs');
  const path = require('node:path');
  const { spawnSync } = require('node:child_process');
  const analyzer = path.join(extension.extensionPath, 'analyzer', 'bin', 'quick', 'code-map.dll');
  const dotnet = spawnSync('dotnet', ['--version'], { encoding: 'utf8' });
  const mode = vscode.workspace.getConfiguration('sharpdeps').get('analysisMode', 'quick');
  return [
    `analyzer=${fs.existsSync(analyzer) ? 'found' : 'missing'}`,
    `dotnet=${dotnet.status === 0 ? dotnet.stdout.trim() : `unavailable (${dotnet.error?.code ?? dotnet.status})`}`,
    `mode=${mode}`
  ].join(', ');
}
