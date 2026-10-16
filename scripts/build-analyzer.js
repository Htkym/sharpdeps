// Quick ships runtime-only; Semantic loads MSBuild from the installed SDK.

const { spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const analyzerDir = path.join(__dirname, '..', 'analyzer');
const quickProject = path.join(
  analyzerDir,
  'src',
  'SharpDeps.QuickHost',
  'SharpDeps.QuickHost.csproj'
);
const semanticProject = path.join(
  analyzerDir,
  'src',
  'SharpDeps.SemanticHost',
  'SharpDeps.SemanticHost.csproj'
);
const quickOutDir = path.join(analyzerDir, 'bin', 'quick');
const semanticOutDir = path.join(analyzerDir, 'bin', 'semantic');
const quickDll = path.join(quickOutDir, 'code-map.dll');
const semanticDll = path.join(semanticOutDir, 'sharpdeps-semantic-host.dll');
const queryProject = path.join(
  analyzerDir,
  'src',
  'SharpDeps.QueryHost',
  'SharpDeps.QueryHost.csproj'
);
const queryOutDir = path.join(analyzerDir, 'bin', 'query');
const queryDll = path.join(queryOutDir, 'sharpdeps-query-host.dll');

function run(command, args, cwd) {
  console.log(`> ${command} ${args.join(' ')}`);
  const result = spawnSync(command, args, { cwd, stdio: 'inherit', shell: false });
  return result.status === 0;
}

function publish(project, outDir) {
  return run(
    'dotnet',
    ['publish', project, '-c', 'Release', '-o', outDir, '--self-contained', 'false'],
    analyzerDir
  );
}

function main() {
  if (!fs.existsSync(quickProject)) {
    console.error(`Analyzer project not found: ${quickProject}`);
    process.exit(1);
  }

  fs.rmSync(quickOutDir, { recursive: true, force: true });
  if (!publish(quickProject, quickOutDir) || !fs.existsSync(quickDll)) {
    console.error('Failed to build the Quick analyzer.');
    process.exit(1);
  }

  console.log(`Quick analyzer built: ${quickDll}`);

  // Semantic loads MSBuild from the installed SDK.
  fs.rmSync(semanticOutDir, { recursive: true, force: true });
  if (!publish(semanticProject, semanticOutDir) || !fs.existsSync(semanticDll)) {
    console.error('Failed to build the semantic analyzer host.');
    process.exit(1);
  }

  console.log(`Semantic analyzer host built: ${semanticDll}`);
  // A separate runtime-only reader host: no Roslyn, CLI PackAsTool, SDK or writer.
  if (path.relative(analyzerDir, queryOutDir) !== path.join('bin', 'query'))
    throw new Error('Query output directory escaped the analyzer directory.');
  fs.rmSync(queryOutDir, { recursive: true, force: true });
  if (!publish(queryProject, queryOutDir) || !fs.existsSync(queryDll)) {
    console.error('Failed to build the saved Query host.');
    process.exit(1);
  }
  console.log(`Saved Query host built: ${queryDll}`);
}

main();
