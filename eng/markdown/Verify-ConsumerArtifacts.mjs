import fs from 'node:fs';
import path from 'node:path';
import { createHash } from 'node:crypto';
import { fileURLToPath } from 'node:url';

// Read-only artifact gate; no restore, copy, packing, publishing or executing source.
const args = process.argv.slice(2);
if (args.length !== 3)
  throw new Error(
    'Usage: node eng/markdown/Verify-ConsumerArtifacts.mjs <pair-manifest.json> <feed> <project.assets.json>'
  );
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const read = (p) => JSON.parse(fs.readFileSync(p, 'utf8'));
const pin = read(path.join(root, 'docs/contracts/markdown-runtime-pin.json'));
const pair = read(args[0]);
const hash = (b) => createHash('sha256').update(b).digest('hex');
for (const key of [
  'componentVersion',
  'sourceCommit',
  'canonicalSourceHash',
  'parserVersion',
  'contractVersion',
  'profileId'
])
  if (pair[key] !== pin[key]) throw new Error('Pair metadata mismatch: ' + key);
if (!same(pair.sourceRepository, pin.sourceRepository))
  throw new Error('Pair source repository identity mismatch');
if (!same(pair.dependencies, pin.dependencies))
  throw new Error('Pair dependency identity mismatch');
function same(a, b) {
  if (a === b) return true;
  if (
    !a ||
    !b ||
    typeof a !== 'object' ||
    typeof b !== 'object' ||
    Array.isArray(a) !== Array.isArray(b)
  )
    return false;
  const keys = Object.keys(a).sort();
  return (
    keys.length === Object.keys(b).length &&
    keys.every((k) => Object.hasOwn(b, k) && same(a[k], b[k]))
  );
}
const checked = [];
for (const kind of ['runtime', 'source']) {
  const expected = pin.artifacts[kind];
  if (!same(pair.artifacts[kind], expected))
    throw new Error('Pair artifact record mismatch: ' + kind);
  if (path.basename(expected.fileName) !== expected.fileName)
    throw new Error('Invalid package filename');
  const bytes = fs.readFileSync(path.join(args[1], expected.fileName));
  if (bytes.length !== expected.byteLength || hash(bytes) !== expected.sha256)
    throw new Error('Immutable package bytes mismatch: ' + kind);
  checked.push({ kind, sha256: hash(bytes), bytes: bytes.length });
}
const assets = read(args[2]);
const runtimeId = pin.artifacts.runtime.packageId;
const runtimeKey = runtimeId + '/' + pin.componentVersion;
const yamlKey = pin.dependencies[0].packageId + '/' + pin.dependencies[0].version;
if (!assets.libraries[runtimeKey] || !assets.libraries[yamlKey])
  throw new Error('Restored runtime/dependency exact versions missing');
const runtimeVersions = Object.keys(assets.libraries).filter((k) => k.startsWith(runtimeId + '/'));
const yamlVersions = Object.keys(assets.libraries).filter((k) =>
  k.startsWith(pin.dependencies[0].packageId + '/')
);
if (runtimeVersions.length !== 1 || yamlVersions.length !== 1)
  throw new Error('Ambiguous restored package version');
const relative = assets.libraries[runtimeKey].path;
if (relative !== 'syntamark/' + pin.componentVersion)
  throw new Error('Unexpected restored package path');
let restored;
for (const folder of Object.keys(assets.packageFolders)) {
  const file = path.join(
    folder,
    relative,
    'syntamark.' + pin.componentVersion + '.nupkg'
  );
  if (!fs.existsSync(file)) continue;
  const bytes = fs.readFileSync(file);
  const assembly = path.join(folder, relative, 'lib/net10.0/Syntamark.dll');
  if (
    hash(bytes) !== pin.artifacts.runtime.sha256 ||
    hash(fs.readFileSync(assembly)) !== pin.artifacts.runtime.assemblySha256
  )
    throw new Error('Restored package/assembly hash mismatch');
  restored = { path: file, sha256: hash(bytes), assemblySha256: hash(fs.readFileSync(assembly)) };
  break;
}
if (!restored) throw new Error('Restored runtime package bytes unavailable');
process.stdout.write(
  JSON.stringify({
    task: 'SD2-03',
    state: 'ARTIFACT_GATE_PASS',
    checked,
    restored,
    yamlVersion: pin.dependencies[0].version
  }) + '\n'
);
