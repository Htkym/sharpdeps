// Fixed Medium fixture and raw measurements for the v0.1 acceptance budgets.
const fs = require('node:fs');
const path = require('node:path');
const os = require('node:os');
const { spawnSync } = require('node:child_process');
const { performance } = require('node:perf_hooks');
const { createHash } = require('node:crypto');
const repo = path.resolve(__dirname, '..');
const root = path.join(repo, '.local', 'perf-v010');
const fixture = path.join(root, 'medium');
const host = path.join(repo, 'analyzer/bin/semantic/sharpdeps-semantic-host.dll');
const evidencePath = path.join(repo, 'docs/implementation/v0.1.0/evidence/sd-028-acceptance.json');
const projects = 30,
  filesPerProject = 100;
fs.mkdirSync(fixture, { recursive: true });
const projectName = (p) => `P${String(p).padStart(2, '0')}`;
for (let p = 0; p < projects; p++) {
  const name = projectName(p),
    dir = path.join(fixture, name);
  fs.mkdirSync(dir, { recursive: true });
  fs.writeFileSync(
    path.join(dir, `${name}.csproj`),
    `<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework><EnableNETAnalyzers>false</EnableNETAnalyzers></PropertyGroup>${p ? `<ItemGroup><ProjectReference Include="../${projectName(p - 1)}/${projectName(p - 1)}.csproj" /></ItemGroup>` : ''}</Project>`
  );
  for (let t = 0; t < filesPerProject; t++) {
    const lines = [`namespace ${name};`, `public class C${t} {`];
    for (let n = 1; n <= 24; n++)
      lines.push(`public ${projectName(Math.max(0, p - 1))}.C${(t + n) % 100} F${n};`);
    for (let n = 1; n <= 3; n++)
      lines.push(`public C${(t + n * 7) % 100} M${n}(C${(t + n * 7) % 100} value) => value;`);
    if (p === 0 && t === 0) for (let n = 0; n < 100; n++) lines.push(`public C1 Repeated${n};`);
    lines.push(
      `public System.Collections.Generic.List<C${(t + 37) % 100}> Items;`,
      `public object Make() => new C${(t + 53) % 100}();`,
      '}'
    );
    fs.writeFileSync(path.join(dir, `C${t}.cs`), lines.join('\n'));
  }
}
const solution = path.join(fixture, 'Medium.slnx');
fs.writeFileSync(
  solution,
  `<Solution>${Array.from({ length: projects }, (_, p) => `<Project Path="${projectName(p)}/${projectName(p)}.csproj"/>`).join('')}</Solution>`
);
const manifest = {
  projects,
  csharpFiles: projects * filesPerProject,
  writtenReferencesPerFile: 34,
  extraRepeatedReferences: 100,
  expectedTypesAtLeast: 3000,
  structure:
    'Project chain; 24 cross-project fields, 3 local signature methods, generic List and constructor per file. Intra-project SCCs and external types included.'
};
fs.writeFileSync(path.join(root, 'manifest.json'), JSON.stringify(manifest, null, 2));
const restore = spawnSync('dotnet', ['restore', solution], { encoding: 'utf8', timeout: 180000 });
fs.writeFileSync(path.join(root, 'restore.log'), restore.stdout + restore.stderr);
if (restore.status !== 0) throw new Error('Fixture restore failed.');
const report = {
  checkedAt: new Date().toISOString(),
  hostSha256: createHash('sha256').update(fs.readFileSync(host)).digest('hex'),
  assemblySha256: Object.fromEntries(
    [
      'SharpDeps.Analysis.Roslyn.dll',
      'SharpDeps.Analysis.Core.dll',
      'SharpDeps.Analysis.Contracts.dll'
    ].map((name) => [
      name,
      createHash('sha256')
        .update(fs.readFileSync(path.join(path.dirname(host), name)))
        .digest('hex')
    ])
  ),
  environment: {
    platform: process.platform,
    arch: process.arch,
    cpu: os.cpus()[0].model,
    logicalProcessors: os.cpus().length,
    memoryBytes: os.totalmem(),
    sdk: spawnSync('dotnet', ['--version'], { encoding: 'utf8' }).stdout.trim(),
    power: spawnSync('powercfg', ['/getactivescheme'], { encoding: 'utf8' }).stdout.trim()
  },
  fixture: manifest,
  methodology:
    'Three new analyzer processes, first-load then file-cache-warm runs. No compilation cache. Restore excluded. Monotonic wall clock includes sampling overhead; memory samples include discovered owned children. Cold OS caches are not asserted.',
  runs: [],
  store: {}
};
const save = () => fs.writeFileSync(evidencePath, JSON.stringify(report, null, 2) + '\n');
(async () => {
  for (let attempt = 0; attempt < 3; attempt++) {
    const output = path.join(root, `run-${attempt}`, 'report.json');
    fs.mkdirSync(path.dirname(output), { recursive: true });
    const spec = path.join(root, `run-${attempt}.json`);
    fs.writeFileSync(
      spec,
      JSON.stringify({
        command: 'dotnet',
        arguments: [
          host,
          '--solution',
          solution,
          '--output',
          output,
          '--configuration',
          'Release',
          '--timeout',
          '240'
        ],
        cwd: repo,
        output
      })
    );
    const run = spawnSync(
      'pwsh',
      [
        '-NoProfile',
        '-File',
        path.join(repo, 'scripts/measure-analyzer.ps1'),
        '-Specification',
        spec
      ],
      { encoding: 'utf8', timeout: 300000, windowsHide: true }
    );
    if (!fs.existsSync(output + '.measurement.json')) {
      report.runs.push({ attempt, exitCode: run.status, error: run.stderr });
      save();
      throw new Error('Measurement failed.');
    }
    const measured = JSON.parse(
      fs.readFileSync(output + '.measurement.json', 'utf8').replace(/^\uFEFF/, '')
    );
    const snapshot = JSON.parse(
      fs.readFileSync(path.join(path.dirname(output), 'report-v2.json'), 'utf8')
    );
    const correctness = {
      projects: snapshot.projects.filter((p) => p.targetFramework !== 'external').length,
      types: snapshot.types.filter((t) => !t.isExternal).length,
      relations: snapshot.relations.length,
      evidence: snapshot.evidenceIndex.relations.reduce((n, r) => n + r.count, 0),
      completeness: snapshot.completeness
    };
    report.runs.push({
      attempt,
      ...measured,
      correctness,
      raw: path.relative(repo, output + '.measurement.json'),
      timeBudgetPass: measured.wallMs <= 120000,
      memoryBudgetPass: measured.sampledTreePeakBytes <= 2 * 1024 ** 3
    });
    save();
    if (
      measured.exitCode !== 0 ||
      correctness.projects !== 30 ||
      correctness.types !== 3000 ||
      correctness.evidence < 90000 ||
      correctness.completeness !== 'completeWithinScope'
    )
      throw new Error('Medium correctness check failed.');
    console.log(
      `Medium ${attempt + 1}: ${Math.round(measured.wallMs)} ms, ${Math.round(measured.sampledTreePeakBytes / 1024 ** 2)} MiB, ${correctness.evidence} evidence`
    );
  }
  require('esbuild').buildSync({
    stdin: {
      contents: "export { ReportStore } from './src/analyzer/reportStore';",
      resolveDir: repo
    },
    bundle: true,
    platform: 'node',
    format: 'cjs',
    outfile: path.join(root, 'store.cjs')
  });
  const { ReportStore } = require(path.join(root, 'store.cjs'));
  const store = new ReportStore();
  const snapshot = await store.register({
    directory: path.join(root, 'run-2'),
    reportFileName: 'report-v2.json'
  });
  const summarize = (values) => ({
    samplesMs: values,
    p95Ms: [...values].sort((a, b) => a - b)[Math.ceil(values.length * 0.95) - 1]
  });
  for (const [name, fn, budget] of [
    ['search', () => store.search(snapshot.analysisId, 'C50', { limit: 100 }), 250],
    ['details', () => store.getEntityDetails(snapshot.analysisId, snapshot.types[1500].id), 100],
    [
      'projection',
      () =>
        store.getProjection(snapshot.analysisId, {
          granularity: 'type',
          scope: { kind: 'dependencies', id: snapshot.types[1500].id, depth: 1 },
          maxNodes: 100,
          maxEdges: 200
        }),
      2000
    ],
    [
      'evidencePage',
      () =>
        store.getEvidencePage(
          snapshot.analysisId,
          snapshot.evidenceIndex.relations.find((entry) => entry.count >= 100).relationId,
          { limit: 100 }
        ),
      300
    ]
  ]) {
    await fn();
    const values = [];
    for (let n = 0; n < 25; n++) {
      const start = performance.now();
      const result = await fn();
      values.push(performance.now() - start);
      if (name === 'evidencePage' && result.items.length !== 100)
        throw new Error('Evidence page must contain 100 records.');
    }
    report.store[name] = {
      ...summarize(values),
      budgetMs: budget,
      hostRssBytes: process.memoryUsage().rss
    };
    save();
  }
  console.log(JSON.stringify(report.store, null, 2));
})().catch((error) => {
  report.error = String(error);
  save();
  console.error(error);
  process.exitCode = 1;
});
