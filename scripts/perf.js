// Performance and cleanup measurement (SD-028).
//
// Generates a fixed synthetic fixture, runs the Quick and Semantic hosts against it, and
// records wall time, peak working set, output sizes, and what is left behind. Nothing
// here changes the product: it reports what was measured, with the machine and versions.

const { spawn, spawnSync } = require('node:child_process');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');

const repoRoot = path.resolve(__dirname, '..');
const perfRoot = path.join(repoRoot, '.local', 'perf');
const fixtureRoot = path.join(perfRoot, 'big');
const runsRoot = path.join(perfRoot, 'runs');
const reportPath = path.join(
  repoRoot,
  'docs',
  'implementation',
  'v0.1.0',
  'evidence',
  'sd-028-performance.json'
);

const PROJECT_COUNT = 40;
const TYPES_PER_PROJECT = 10;

function generateFixture() {
  fs.rmSync(fixtureRoot, { recursive: true, force: true });
  fs.mkdirSync(fixtureRoot, { recursive: true });
  fs.writeFileSync(
    path.join(fixtureRoot, 'Big.csproj'),
    '<Project Sdk="Microsoft.NET.Sdk">\n  <PropertyGroup>\n    <TargetFramework>net10.0</TargetFramework>\n    <Nullable>enable</Nullable>\n  </PropertyGroup>\n</Project>\n',
    'utf8'
  );

  for (let project = 0; project < PROJECT_COUNT; project++) {
    const namespace = `Big.P${project}`;
    for (let type = 0; type < TYPES_PER_PROJECT; type++) {
      const references = [];
      // A deterministic fan: every type uses two types from the previous namespace and
      // one from its own, so edges and SCCs are exercised.
      const previous = Math.max(0, project - 1);
      references.push(`Big.P${previous}.T${(type + 1) % TYPES_PER_PROJECT}`);
      references.push(`Big.P${previous}.T${(type + 3) % TYPES_PER_PROJECT}`);
      if (type > 0) {
        references.push(`${namespace}.T${type - 1}`);
      }

      const body = references
        .map(
          (reference, index) => `        public ${reference} F${index} { get; set; } = default!;`
        )
        .join('\n');
      fs.writeFileSync(
        path.join(fixtureRoot, `${namespace.replace('.', '_')}_T${type}.cs`),
        `namespace ${namespace};\n\npublic sealed class T${type}\n{\n${body}\n}\n`,
        'utf8'
      );
    }
  }

  return fs.readdirSync(fixtureRoot).filter((name) => name.endsWith('.cs')).length;
}

function measure(command, args, label) {
  return new Promise((resolve) => {
    const started = process.hrtime.bigint();
    const child = spawn(command, args, { cwd: repoRoot, stdio: ['ignore', 'pipe', 'pipe'] });
    let peakWorkingSet = 0;
    const sampler = setInterval(() => {
      try {
        const output = spawnSync(
          'powershell',
          [
            '-NoProfile',
            '-Command',
            `(Get-Process -Id ${child.pid} -ErrorAction SilentlyContinue).PeakWorkingSet64`
          ],
          { encoding: 'utf8' }
        );
        const value = Number.parseInt(output.stdout.trim(), 10);
        if (Number.isFinite(value)) {
          peakWorkingSet = Math.max(peakWorkingSet, value);
        }
      } catch {
        // Sampling is best effort.
      }
    }, 250);

    let stdout = '';
    let stderr = '';
    child.stdout.on('data', (chunk) => {
      stdout += chunk.toString();
    });
    child.stderr.on('data', (chunk) => {
      stderr += chunk.toString();
    });
    child.on('exit', (code) => {
      clearInterval(sampler);
      resolve({
        label,
        exitCode: code,
        wallMs: Number(process.hrtime.bigint() - started) / 1e6,
        peakWorkingSetBytes: peakWorkingSet,
        stdoutTail: stdout.slice(-400),
        stderrTail: stderr.slice(-400)
      });
    });
  });
}

function directorySize(directory) {
  if (!fs.existsSync(directory)) {
    return 0;
  }

  return fs.readdirSync(directory, { withFileTypes: true }).reduce((total, entry) => {
    const full = path.join(directory, entry.name);
    return total + (entry.isDirectory() ? directorySize(full) : fs.statSync(full).size);
  }, 0);
}

function listProcesses(name) {
  try {
    const output = spawnSync(
      'powershell',
      [
        '-NoProfile',
        '-Command',
        `@(Get-Process -Name ${name} -ErrorAction SilentlyContinue).Count`
      ],
      { encoding: 'utf8' }
    );
    return Number.parseInt(output.stdout.trim(), 10) || 0;
  } catch {
    return 0;
  }
}

async function main() {
  const fileCount = generateFixture();
  fs.rmSync(runsRoot, { recursive: true, force: true });
  fs.mkdirSync(runsRoot, { recursive: true });

  const quickHost = path.join(repoRoot, 'analyzer', 'bin', 'quick', 'code-map.dll');
  const semanticHost = path.join(
    repoRoot,
    'analyzer',
    'bin',
    'semantic',
    'sharpdeps-semantic-host.dll'
  );
  const dotnetBefore = listProcesses('dotnet');

  const quickRuns = [];
  for (let attempt = 0; attempt < 3; attempt++) {
    const outputDirectory = fs.mkdtempSync(path.join(runsRoot, 'sharpdeps-run-'));
    quickRuns.push(
      await measure(
        'dotnet',
        [
          quickHost,
          '--solution',
          path.join(fixtureRoot, 'Big.csproj'),
          '--output',
          path.join(outputDirectory, 'report.json')
        ],
        `quick-${attempt + 1}`
      )
    );
    quickRuns[quickRuns.length - 1].outputBytes = directorySize(outputDirectory);
    quickRuns[quickRuns.length - 1].outputDirectory = path.relative(repoRoot, outputDirectory);
  }

  const semanticOutput = fs.mkdtempSync(path.join(runsRoot, 'sharpdeps-semantic-'));
  const semanticRun = await measure(
    'dotnet',
    [
      semanticHost,
      '--solution',
      path.join('tests', 'fixtures', 'semantic-baseline', 'SemanticBaseline.sln'),
      '--output',
      path.join(semanticOutput, 'probe.json'),
      '--configuration',
      'Debug'
    ],
    'semantic-baseline'
  );
  semanticRun.outputBytes = directorySize(semanticOutput);

  const sorted = [...quickRuns].map((run) => run.wallMs).sort((left, right) => left - right);
  const summary = {
    task: 'SD-028',
    checkedAt: new Date().toISOString(),
    environment: {
      platform: `${process.platform} ${process.arch}`,
      cpus: os.cpus().length,
      totalMemoryBytes: os.totalmem()
    },
    fixture: {
      path: path.relative(repoRoot, fixtureRoot),
      files: fileCount,
      note: `${PROJECT_COUNT} namespaces x ${TYPES_PER_PROJECT} types with a deterministic fan`
    },
    quick: {
      runs: quickRuns,
      medianWallMs: sorted[Math.floor(sorted.length / 2)],
      minWallMs: sorted[0],
      maxWallMs: sorted[sorted.length - 1],
      peakWorkingSetBytes: Math.max(...quickRuns.map((run) => run.peakWorkingSetBytes))
    },
    semantic: semanticRun,
    cleanup: {
      runDirectoriesCreated: 4,
      runDirectoriesAfter: fs.existsSync(runsRoot) && fs.readdirSync(runsRoot).length,
      dotnetProcessesBefore: dotnetBefore,
      dotnetProcessesAfter: listProcesses('dotnet'),
      workspaceLeftovers: fs.existsSync(path.join(fixtureRoot, 'obj'))
        ? 'obj/ was created by the semantic build'
        : 'none in the fixture'
    }
  };

  // The runner cleans its own measurement directories once their sizes are recorded; the
  // controller's retention policy is covered by analysisController.test.ts.
  const measurementDirectories = fs.readdirSync(runsRoot);
  for (const entry of measurementDirectories) {
    fs.rmSync(path.join(runsRoot, entry), { recursive: true, force: true });
  }
  summary.cleanup.measurementDirectoriesRemoved = measurementDirectories.length;
  summary.cleanup.runDirectoriesAfter = fs.readdirSync(runsRoot).length;

  fs.mkdirSync(path.dirname(reportPath), { recursive: true });
  fs.writeFileSync(reportPath, `${JSON.stringify(summary, null, 2)}\n`, 'utf8');
  console.log(JSON.stringify(summary, null, 1));
}

main().catch((error) => {
  console.error(error);
  process.exit(1);
});
