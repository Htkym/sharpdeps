// Precompiles the file-based analyzer (analyzer/code-map.cs) into a framework-dependent
// DLL under analyzer/bin so the extension can run it with a runtime-only .NET install.
//
// Strategy:
//   1. Try `dotnet publish` directly on the .cs file (file-based app, .NET 10+).
//   2. If that does not yield analyzer/bin/code-map.dll, fall back to generating a
//      temporary .csproj wrapper that mirrors the file's #:package / #:property directives
//      and publish that instead.

const { spawnSync } = require('child_process');
const fs = require('fs');
const path = require('path');

const analyzerDir = path.join(__dirname, '..', 'analyzer');
const sourceFile = path.join(analyzerDir, 'code-map.cs');
const outDir = path.join(analyzerDir, 'bin');
const dllPath = path.join(outDir, 'code-map.dll');

function run(command, args, cwd) {
  console.log(`> ${command} ${args.join(' ')}`);
  const result = spawnSync(command, args, { cwd, stdio: 'inherit', shell: false });
  return result.status === 0;
}

function cleanOutput() {
  fs.rmSync(outDir, { recursive: true, force: true });
}

function parseDirectives(source) {
  const packages = [];
  const properties = [];
  for (const line of source.split(/\r?\n/)) {
    const pkg = line.match(/^#:package\s+(.+?)@(.+?)\s*$/);
    if (pkg) {
      packages.push({ name: pkg[1].trim(), version: pkg[2].trim() });
      continue;
    }
    const prop = line.match(/^#:property\s+(.+?)=(.+?)\s*$/);
    if (prop) {
      properties.push({ name: prop[1].trim(), value: prop[2].trim() });
    }
  }
  return { packages, properties };
}

function publishFileBased() {
  return run(
    'dotnet',
    [
      'publish',
      sourceFile,
      '-c',
      'Release',
      '-o',
      outDir,
      '-p:PublishAot=false',
      '--self-contained',
      'false'
    ],
    analyzerDir
  );
}

function publishViaProject() {
  const source = fs.readFileSync(sourceFile, 'utf8');
  const { packages, properties } = parseDirectives(source);

  const wrapperDir = path.join(analyzerDir, '.build');
  fs.rmSync(wrapperDir, { recursive: true, force: true });
  fs.mkdirSync(wrapperDir, { recursive: true });

  // The analyzer is top-level-statement source; copy it beside the temp project.
  const programPath = path.join(wrapperDir, 'Program.cs');
  fs.writeFileSync(programPath, stripDirectives(source), 'utf8');

  const packageRefs = packages
    .map((p) => `    <PackageReference Include="${p.name}" Version="${p.version}" />`)
    .join('\n');
  const extraProps = properties.map((p) => `    <${p.name}>${p.value}</${p.name}>`).join('\n');

  const csproj = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AssemblyName>code-map</AssemblyName>
    <SelfContained>false</SelfContained>
    <PublishAot>false</PublishAot>
${extraProps}
  </PropertyGroup>
  <ItemGroup>
${packageRefs}
  </ItemGroup>
</Project>
`;
  const csprojPath = path.join(wrapperDir, 'code-map.csproj');
  fs.writeFileSync(csprojPath, csproj, 'utf8');

  const ok = run('dotnet', ['publish', csprojPath, '-c', 'Release', '-o', outDir], wrapperDir);
  fs.rmSync(wrapperDir, { recursive: true, force: true });
  return ok;
}

function stripDirectives(source) {
  return source
    .split(/\r?\n/)
    .filter((line) => !/^#:(package|property|sdk)\b/.test(line))
    .join('\n');
}

function main() {
  if (!fs.existsSync(sourceFile)) {
    console.error(`Analyzer source not found: ${sourceFile}`);
    process.exit(1);
  }

  cleanOutput();

  let published = publishFileBased();
  if (!published || !fs.existsSync(dllPath)) {
    console.log(
      'File-based publish did not produce code-map.dll; falling back to a temporary project.'
    );
    cleanOutput();
    published = publishViaProject();
  }

  if (!published || !fs.existsSync(dllPath)) {
    console.error('Failed to build the analyzer DLL.');
    process.exit(1);
  }

  console.log(`Analyzer built: ${dllPath}`);
}

main();
