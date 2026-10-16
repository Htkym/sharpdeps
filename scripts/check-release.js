// Candidate identity checks are local reads. --tag additionally gates stable publication.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

const root = path.resolve(__dirname, '..');
const stable = /^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)$/;
const hash = (bytes) => crypto.createHash('sha256').update(bytes).digest('hex');
const read = (name) => fs.readFileSync(path.join(root, name), 'utf8');
const xmlValue = (xml, name) => {
  const matches = [...xml.matchAll(new RegExp(`<${name}>([^<]+)</${name}>`, 'g'))];
  assert.equal(matches.length, 1, `Expected one ${name}`);
  return matches[0][1];
};

function checkDefinitions(tag) {
  const manifest = JSON.parse(read('package.json'));
  const lock = JSON.parse(read('package-lock.json'));
  const cli = read('analyzer/src/SharpDeps.Cli/SharpDeps.Cli.csproj');
  assert.match(manifest.version, stable, 'VSIX version must be a stable-shaped candidate base');
  assert.equal(lock.version, manifest.version, 'Lock version');
  assert.equal(lock.packages[''].version, manifest.version, 'Lock root version');
  assert.equal(xmlValue(cli, 'PackageId'), 'SharpDeps.Cli');
  assert.equal(xmlValue(cli, 'Version'), manifest.version, 'CLI/VSIX candidate base');
  assert.equal(xmlValue(cli, 'ToolCommandName'), 'sharpdeps');
  const pin = JSON.parse(read('docs/contracts/markdown-runtime-pin.json'));
  const markdown = read(
    'analyzer/src/SharpDeps.Analysis.Markdown/SharpDeps.Analysis.Markdown.csproj'
  );
  assert.ok(markdown.includes(`Include="Syntamark" VersionOverride="[${pin.componentVersion}]"`));
  for (const [kind, id] of [
    ['runtime', 'Syntamark'],
    ['source', 'Syntamark.Source']
  ]) {
    const artifact = pin.artifacts[kind];
    assert.equal(artifact.packageId, id);
    assert.equal(artifact.componentVersion, pin.componentVersion, 'Selected pair versions');
    assert.equal(artifact.fileName, `${id}.${pin.componentVersion}.nupkg`);
    const bytes = fs.readFileSync(path.join(root, 'eng/markdown/feed', artifact.fileName));
    assert.equal(bytes.length, artifact.byteLength, `${kind} bytes`);
    assert.equal(hash(bytes), artifact.sha256, `${kind} SHA256`);
  }
  if (tag !== undefined) {
    assert.equal(tag, `v${manifest.version}`, 'Tag must match the candidate base');
    assert.match(
      pin.componentVersion,
      stable,
      'Stable publication requires a stable Syntamark runtime/source pair'
    );
  }
  return {
    version: manifest.version,
    extensionId: `${manifest.publisher}.${manifest.name}`,
    releaseTag: `v${manifest.version}`,
    vsixFileName: `${manifest.name}-${manifest.version}.vsix`,
    cliPackageId: 'SharpDeps.Cli',
    pairVersion: pin.componentVersion,
    stablePublicationAllowed: stable.test(pin.componentVersion),
    manifest,
    pin
  };
}

async function readArchive(file, wanted) {
  return new Promise((resolve, reject) => {
    require('yauzl').open(file, { lazyEntries: true }, (error, zip) => {
      if (error) return reject(error);
      const entries = {};
      zip.on('error', reject);
      zip.on('entry', (entry) => {
        if (!wanted(entry.fileName)) return zip.readEntry();
        if (Object.hasOwn(entries, entry.fileName) || entry.uncompressedSize > 8 * 1024 * 1024) {
          zip.close();
          return reject(new Error(`Duplicate or oversized metadata/payload: ${entry.fileName}`));
        }
        zip.openReadStream(entry, (streamError, stream) => {
          if (streamError) return reject(streamError);
          const chunks = [];
          stream.on('error', reject);
          stream.on('data', (chunk) => chunks.push(chunk));
          stream.on('end', () => {
            entries[entry.fileName] = Buffer.concat(chunks);
            zip.readEntry();
          });
        });
      });
      zip.on('end', () => resolve(entries));
      zip.readEntry();
    });
  });
}

function artifactIdentity(file) {
  const bytes = fs.readFileSync(file);
  return { file: path.basename(file), bytes: bytes.length, sha256: hash(bytes) };
}

async function checkVsix(file, definition = checkDefinitions()) {
  const entries = await readArchive(file, (name) =>
    ['extension/package.json', 'extension.vsixmanifest'].includes(name)
  );
  const manifest = JSON.parse(entries['extension/package.json'].toString('utf8'));
  for (const key of ['name', 'publisher', 'version'])
    assert.equal(manifest[key], definition.manifest[key], `VSIX ${key}`);
  const identity = entries['extension.vsixmanifest'].toString('utf8').match(/<Identity\s+[^>]*\/>/);
  assert.ok(identity, 'VSIX Identity');
  for (const [key, value] of Object.entries({
    Id: manifest.name,
    Publisher: manifest.publisher,
    Version: manifest.version
  })) {
    assert.ok(identity[0].includes(`${key}="${value}"`), `VSIX Identity ${key}`);
  }
  return { ...artifactIdentity(file), version: manifest.version };
}

async function checkCli(file, version, definition = checkDefinitions()) {
  version ??= definition.version;
  assert.ok(
    version === definition.version ||
      version.startsWith(`${definition.version}-ci.`) ||
      version.startsWith(`${definition.version}-rc.`),
    'CLI candidate version must use the declared base'
  );
  const prefix = 'tools/net10.0/any/';
  const required = [
    'DotnetToolSettings.xml',
    'SharpDeps.Cli.dll',
    'SharpDeps.Cli.runtimeconfig.json',
    'SharpDeps.Cli.deps.json',
    'Syntamark.dll'
  ];
  const entries = await readArchive(
    file,
    (name) => /^[^/]+\.nuspec$/.test(name) || required.some((item) => name === prefix + item)
  );
  const specs = Object.keys(entries).filter((name) => name.endsWith('.nuspec'));
  assert.equal(specs.length, 1, 'CLI nuspec');
  const spec = entries[specs[0]].toString('utf8');
  assert.equal(xmlValue(spec, 'id'), definition.cliPackageId);
  assert.equal(xmlValue(spec, 'version'), version);
  assert.equal(path.basename(file), `${definition.cliPackageId}.${version}.nupkg`);
  for (const name of required) assert.ok(entries[prefix + name], `CLI ${name}`);
  assert.match(
    entries[prefix + 'DotnetToolSettings.xml'].toString('utf8'),
    /<Command Name="sharpdeps" EntryPoint="SharpDeps.Cli.dll" Runner="dotnet"\s*\/>/
  );
  const deps = JSON.parse(entries[prefix + 'SharpDeps.Cli.deps.json'].toString('utf8'));
  assert.ok(
    deps.libraries[`Syntamark/${definition.pairVersion}`],
    'CLI selected runtime dependency'
  );
  assert.equal(
    hash(entries[prefix + 'Syntamark.dll']),
    definition.pin.artifacts.runtime.assemblySha256,
    'CLI fixed runtime bytes'
  );
  return { ...artifactIdentity(file), version };
}

async function main() {
  const options = {};
  const names = ['--tag', '--vsix', '--cli-package', '--cli-version'];
  for (let i = 2; i < process.argv.length; i += 2) {
    const name = process.argv[i];
    assert.ok(
      names.includes(name) && process.argv[i + 1] && !Object.hasOwn(options, name),
      `Invalid option ${name}`
    );
    options[name] = process.argv[i + 1];
  }
  assert.ok(
    !options['--cli-version'] || options['--cli-package'],
    '--cli-version requires --cli-package'
  );
  const definition = checkDefinitions(options['--tag']);
  const { manifest, pin, ...report } = definition;
  void manifest;
  void pin;
  if (options['--vsix']) report.vsix = await checkVsix(options['--vsix'], definition);
  if (options['--cli-package'])
    report.cli = await checkCli(options['--cli-package'], options['--cli-version'], definition);
  console.log(JSON.stringify(report, null, 2));
}

module.exports = { checkDefinitions, checkVsix, checkCli };
if (require.main === module)
  main().catch((error) => {
    console.error(error.message);
    process.exitCode = 1;
  });
