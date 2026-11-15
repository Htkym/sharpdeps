import assert from 'node:assert/strict';
import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import test from 'node:test';

const verifier = fileURLToPath(new URL('./Verify-ConsumerArtifacts.mjs', import.meta.url));
const hash = (bytes) => createHash('sha256').update(bytes).digest('hex');

function fixture(t, dependencies) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'sharpdeps-artifact-gate-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const version = '2.0.0';
  const runtime = Buffer.from('runtime package');
  const source = Buffer.from('source package');
  const assembly = Buffer.from('runtime assembly');
  const artifacts = {};
  for (const [kind, packageId, bytes] of [
    ['runtime', 'Syntamark', runtime],
    ['source', 'Syntamark.Source', source]
  ]) {
    const fileName = packageId + '.' + version + '.nupkg';
    artifacts[kind] = {
      packageId,
      componentVersion: version,
      fileName,
      sha256: hash(bytes),
      byteLength: bytes.length
    };
    fs.writeFileSync(path.join(root, fileName), bytes);
    const restored = path.join(root, packageId.toLowerCase(), version);
    fs.mkdirSync(restored, { recursive: true });
    fs.writeFileSync(path.join(restored, fileName.toLowerCase()), bytes);
  }
  artifacts.runtime.assemblySha256 = hash(assembly);
  const lib = path.join(root, 'syntamark', version, 'lib/net10.0');
  fs.mkdirSync(lib, { recursive: true });
  fs.writeFileSync(path.join(lib, 'Syntamark.dll'), assembly);
  const pin = {
    componentVersion: version,
    sourceCommit: 'fixture',
    canonicalSourceHash: 'fixture',
    parserVersion: '1/fixture',
    contractVersion: '2.0',
    profileId: 'lithosharp-markdown/1',
    sourceRepository: { kind: 'fixture' },
    dependencies,
    artifacts
  };
  const runtimeKey = 'Syntamark/' + version;
  const assets = {
    libraries: {
      [runtimeKey]: { path: 'syntamark/' + version },
      ['Syntamark.Source/' + version]: { path: 'syntamark.source/' + version }
    },
    targets: {
      net10: {
        [runtimeKey]: {
          dependencies: Object.fromEntries(dependencies.map((d) => [d.packageId, d.version]))
        }
      }
    },
    packageFolders: { [root]: {} }
  };
  for (const dependency of dependencies)
    assets.libraries[dependency.packageId + '/' + dependency.version] = {};
  for (const name of ['pin', 'pair'])
    fs.writeFileSync(path.join(root, name + '.json'), JSON.stringify(pin));
  return {
    assets,
    run() {
      fs.writeFileSync(path.join(root, 'assets.json'), JSON.stringify(assets));
      return spawnSync(
        process.execPath,
        [
          verifier,
          path.join(root, 'pair.json'),
          root,
          path.join(root, 'assets.json'),
          path.join(root, 'pin.json')
        ],
        { encoding: 'utf8', windowsHide: true }
      );
    }
  };
}

test('dependency-free native YAML formal runtime passes without a phantom YAML package', (t) => {
  const result = fixture(t, []).run();
  assert.equal(result.status, 0, result.stderr);
  assert.deepEqual(JSON.parse(result.stdout).dependencyVersions, {});
  assert.equal(JSON.parse(result.stdout).yamlVersion, null);
});

test('stable YAML dependency still requires its exact restored version', (t) => {
  const f = fixture(t, [{ packageId: 'YamlDotNet', version: '18.1.0' }]);
  assert.equal(f.run().status, 0);
  delete f.assets.libraries['YamlDotNet/18.1.0'];
  assert.notEqual(f.run().status, 0);
});

test('unexpected dependency on a supposedly dependency-free runtime is rejected', (t) => {
  const f = fixture(t, []);
  f.assets.targets.net10['Syntamark/2.0.0'].dependencies = { YamlDotNet: '18.1.0' };
  const result = f.run();
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /dependency set differs/);
});

test('missing runtime target cannot pass an otherwise matching package inventory', (t) => {
  const f = fixture(t, []);
  f.assets.targets = {};
  assert.notEqual(f.run().status, 0);
});

test('formal runtime requires the exact restored Source package too', (t) => {
  const f = fixture(t, []);
  delete f.assets.libraries['Syntamark.Source/2.0.0'];
  const result = f.run();
  assert.notEqual(result.status, 0);
  assert.match(result.stderr, /Restored source exact version missing/);
});
