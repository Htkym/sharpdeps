const fs = require('node:fs'),
  path = require('node:path'),
  { spawn, execFileSync } = require('node:child_process'),
  { performance } = require('node:perf_hooks');
const repo = path.resolve(__dirname, '..'),
  root = path.join(repo, '.local/sd-030/lifecycle');
fs.mkdirSync(root, { recursive: true });
require('esbuild').buildSync({
  stdin: {
    contents: "export {AnalysisController} from './src/analyzer/analysisController';",
    resolveDir: repo
  },
  bundle: true,
  platform: 'node',
  format: 'cjs',
  outfile: path.join(root, 'controller.cjs')
});
const { AnalysisController } = require(path.join(root, 'controller.cjs'));
const output = path.join(repo, 'docs/implementation/v0.1.0/evidence/sd-028-lifecycle.json');
const report = {
  checkedAt: new Date().toISOString(),
  methodology:
    'Two completed Quick runs, then 20 real Semantic launches stopped after one second. Descendant PID snapshot before Stop; process query after termination includes its overhead. Dedicated run directory.',
  runs: []
};
const query = (script) =>
  JSON.parse(
    execFileSync('pwsh', ['-NoProfile', '-Command', script], {
      encoding: 'utf8',
      windowsHide: true
    }).trim() || '[]'
  );
let child;
const controller = new AnalysisController({
  workRoot: root,
  spawnImpl: (...args) => {
    child = spawn(...args);
    return child;
  },
  processFactory: (request, directory) => ({
    command: 'dotnet',
    args: [
      path.join(
        repo,
        request.mode === 'quick'
          ? 'analyzer/bin/quick/code-map.dll'
          : 'analyzer/bin/semantic/sharpdeps-semantic-host.dll'
      ),
      '--solution',
      request.targetPath,
      '--output',
      path.join(directory, 'report.json'),
      '--watch-stdin',
      '--analysis-id',
      request.analysisId
    ],
    cwd: repo
  })
});
(async () => {
  for (let i = 0; i < 2; i++) {
    const result = await controller.start({
      targetPath: path.join(repo, 'tests/fixtures/quick-baseline/Baseline.sln'),
      mode: 'quick'
    });
    if (result.status !== 'completed') throw new Error(JSON.stringify(result));
  }
  for (let i = 0; i < 20; i++) {
    child = undefined;
    const run = controller.start({
      targetPath: path.join(repo, '.local/perf-v010/medium/Medium.slnx'),
      mode: 'semantic'
    });
    await new Promise((resolve) => setTimeout(resolve, 1000));
    if (!child?.pid) throw new Error('Analyzer was not spawned');
    const owned = query(
      `$owned=[System.Collections.Generic.HashSet[int]]::new(); $null=$owned.Add(${child.pid}); $all=@(Get-CimInstance Win32_Process); do {$added=0; foreach($p in $all){if($owned.Contains([int]$p.ParentProcessId) -and $owned.Add([int]$p.ProcessId)){$added++}}}while($added -gt 0); ConvertTo-Json -InputObject @($owned)`
    );
    const start = performance.now();
    controller.cancel();
    const outcome = await run;
    const remaining = query(
      `ConvertTo-Json -InputObject @(Get-Process -Id ${owned.join(',')} -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)`
    );
    report.runs.push({
      attempt: i,
      status: outcome.status,
      stopThroughInspectionMs: performance.now() - start,
      ownedPids: owned,
      remainingOwnedPids: remaining,
      directories: fs.readdirSync(root).filter((name) => name.startsWith('sharpdeps-run-')).length
    });
    fs.writeFileSync(output, JSON.stringify(report, null, 2));
    const last = report.runs.at(-1);
    if (
      last.status !== 'cancelled' ||
      remaining.length ||
      last.stopThroughInspectionMs > 5000 ||
      last.directories > 4
    )
      throw new Error(JSON.stringify(last));
  }
  await controller.dispose();
  console.log('20 lifecycle cycles passed');
})().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
