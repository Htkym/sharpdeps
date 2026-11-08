const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { pipeline } = require('node:stream/promises');
const { checkMcp } = require('./check-release');

async function extractTool(file, destination) {
  await new Promise((resolve, reject) => {
    require('yauzl').open(file, { lazyEntries: true }, (error, zip) => {
      if (error) {
        return reject(error);
      }
      let total = 0;
      const seen = new Set();
      const fail = (failure) => {
        zip.close();
        reject(failure);
      };
      zip.on('error', fail);
      zip.on('end', resolve);
      zip.on('entry', (entry) => {
        const name = entry.fileName;
        const parts = name.split('/');
        if (
          name.includes('\\') ||
          name.includes('\0') ||
          path.posix.isAbsolute(name) ||
          parts.some((part) => part === '..' || part.includes(':')) ||
          ((entry.externalFileAttributes >>> 16) & 0xf000) === 0xa000
        ) {
          return fail(new Error(`Unsafe package entry: ${name}`));
        }
        const prefix = 'tools/net10.0/any/';
        if (!name.startsWith(prefix) || name.endsWith('/')) {
          return zip.readEntry();
        }
        total += entry.uncompressedSize;
        const target = path.resolve(destination, name.slice(prefix.length));
        if (
          seen.has(target.toLowerCase()) ||
          !target.startsWith(destination + path.sep) ||
          entry.uncompressedSize > 64 * 1024 * 1024 ||
          total > 512 * 1024 * 1024
        ) {
          return fail(new Error(`Duplicate, escaped or oversized payload: ${name}`));
        }
        seen.add(target.toLowerCase());
        zip.openReadStream(entry, (streamError, stream) => {
          if (streamError) {
            return fail(streamError);
          }
          try {
            fs.mkdirSync(path.dirname(target), { recursive: true });
          } catch (error) {
            stream.destroy();
            return fail(error);
          }
          pipeline(stream, fs.createWriteStream(target, { flags: 'wx' })).then(
            () => zip.readEntry(),
            fail
          );
        });
      });
      zip.readEntry();
    });
  });
}

function runHelper(assembly, arguments_, eof = false) {
  return new Promise((resolve, reject) => {
    const child = spawn(
      process.env.DOTNET_HOST_PATH || 'dotnet',
      [assembly, '--cli-mutation-helper', ...arguments_],
      {
        stdio: ['pipe', 'pipe', 'pipe'],
        windowsHide: true
      }
    );
    const chunks = [[], []];
    const lengths = [0, 0];
    let failure;
    let kill;
    const cancel = (error) => {
      if (failure) {
        return;
      }
      failure = error;
      child.stdin.end();
      kill = setTimeout(() => {
        child.kill();
        child.stdout.destroy();
        child.stderr.destroy();
      }, 5000);
    };
    const timeout = setTimeout(
      () => cancel(new Error('Mutation helper exceeded 15 seconds')),
      15000
    );
    child.stdin.on('error', (error) => {
      if (error.code !== 'EPIPE') {
        cancel(error);
      }
    });
    for (const [index, stream] of [child.stdout, child.stderr].entries()) {
      stream.on('error', cancel);
      stream.on('data', (chunk) => {
        lengths[index] += chunk.length;
        if (lengths[index] > 256 * 1024) {
          cancel(new Error('Mutation helper output exceeded limit'));
        } else {
          chunks[index].push(chunk);
        }
      });
    }
    child.on('error', (error) => {
      clearTimeout(timeout);
      clearTimeout(kill);
      reject(error);
    });
    child.on('close', (exit) => {
      clearTimeout(timeout);
      clearTimeout(kill);
      if (failure) {
        return reject(failure);
      }
      try {
        const output = Buffer.concat(chunks[0]).toString('utf8').trim().split('\n');
        assert.equal(output.length, 1, 'One CLI JSON reply');
        const reply = JSON.parse(output[0]);
        assert.equal(reply.apiVersion, '1');
        assert.equal(reply.command, arguments_[0]);
        assert.equal(reply.errors.length, 1);
        resolve({
          exit,
          code: reply.errors[0].code,
          stdout: output[0],
          stderr: Buffer.concat(chunks[1]).toString('utf8')
        });
      } catch (error) {
        reject(error);
      }
    });
    if (eof) {
      child.stdin.end();
    }
  });
}

async function main() {
  const [, , file, version] = process.argv;
  assert.ok(
    file && process.argv.length <= 4,
    'Usage: node scripts/check-mcp-mutation.js package.nupkg [version]'
  );
  const identity = await checkMcp(file, version);
  const scratch = fs.mkdtempSync(path.join(os.tmpdir(), 'sharpdeps-mcp-mutation-'));
  try {
    const tool = path.join(scratch, 'tool');
    await extractTool(file, tool);
    const root = path.join(scratch, 'workspace');
    fs.mkdirSync(root);
    fs.writeFileSync(path.join(root, 'App.csproj'), '<Project Sdk="Microsoft.NET.Sdk" />');
    const assembly = path.join(tool, 'SharpDeps.Mcp.dll');
    const results = [];
    for (const command of ['index', 'update']) {
      for (const seconds of ['-1', '3601']) {
        const result = await runHelper(assembly, [
          command,
          '--root',
          root,
          '--target',
          'App.csproj',
          '--trusted',
          '--timeout-seconds',
          seconds
        ]);
        assert.deepEqual(
          { exit: result.exit, code: result.code },
          { exit: 2, code: 'ARGUMENT_INVALID' }
        );
        results.push({ command, seconds, ...result });
      }
    }
    for (const seconds of ['1800', '3600']) {
      const result = await runHelper(assembly, [
        'update',
        '--root',
        root,
        '--target',
        'App.csproj',
        '--trusted',
        '--timeout-seconds',
        seconds
      ]);
      assert.deepEqual(
        { exit: result.exit, code: result.code },
        { exit: 3, code: 'INDEX_MISSING' }
      );
      results.push({ command: 'update', seconds, ...result });
    }
    const cancelled = await runHelper(
      assembly,
      ['index', '--root', root, '--target', 'App.csproj', '--trusted', '--timeout-seconds', '3600'],
      true
    );
    assert.deepEqual(
      { exit: cancelled.exit, code: cancelled.code },
      { exit: 130, code: 'CANCELLED' }
    );
    console.log(JSON.stringify({ ...identity, results, eofCancellation: cancelled }, null, 2));
  } finally {
    assert.equal(path.dirname(path.resolve(scratch)), path.resolve(os.tmpdir()));
    fs.rmSync(scratch, { recursive: true, force: true });
  }
}

main().catch((error) => {
  console.error(error);
  process.exitCode = 1;
});
