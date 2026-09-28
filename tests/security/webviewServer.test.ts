import { spawn, type ChildProcess } from 'node:child_process';
import { once } from 'node:events';
import * as fs from 'node:fs/promises';
import * as os from 'node:os';
import * as path from 'node:path';
import { createServer } from 'node:net';
import { afterAll, beforeAll, describe, expect, it } from 'vitest';

let directory: string;
let server: ChildProcess;
let baseUrl: string;

beforeAll(async () => {
  directory = await fs.mkdtemp(path.join(os.tmpdir(), 'sharpdeps-server-'));
  const root = path.join(directory, 'repository');
  const sibling = path.join(directory, 'repository-sibling');
  const evidence = path.join(root, 'evidence');
  await fs.mkdir(path.join(root, 'scripts'), { recursive: true });
  await fs.mkdir(sibling);
  await fs.mkdir(evidence);
  await fs.writeFile(path.join(root, 'inside.txt'), 'inside');
  await fs.writeFile(path.join(sibling, 'secret.txt'), 'outside');
  await fs.writeFile(path.join(directory, 'parent.txt'), 'parent');
  await fs.copyFile('scripts/serve-webview.mjs', path.join(root, 'scripts/serve-webview.mjs'));
  await fs.symlink(
    sibling,
    path.join(root, 'link'),
    process.platform === 'win32' ? 'junction' : 'dir'
  );
  await fs.symlink(
    sibling,
    path.join(evidence, 'link'),
    process.platform === 'win32' ? 'junction' : 'dir'
  );
  const portHolder = createServer();
  portHolder.listen(0, '127.0.0.1');
  await once(portHolder, 'listening');
  const port = (portHolder.address() as { port: number }).port;
  await new Promise<void>((resolve) => portHolder.close(() => resolve()));
  baseUrl = `http://127.0.0.1:${port}`;
  server = spawn(
    process.execPath,
    [path.join(root, 'scripts/serve-webview.mjs'), String(port), '--evidence', evidence],
    {
      stdio: ['ignore', 'pipe', 'pipe'],
      windowsHide: true
    }
  );
  await once(server.stdout!, 'data');
});

afterAll(async () => {
  if (server && server.exitCode === null) {
    const exited = once(server, 'exit');
    server.kill();
    await exited;
  }
  if (directory) await fs.rm(directory, { recursive: true, force: true });
});

describe('webview fixture server boundaries', () => {
  it('serves repository files', async () => {
    const response = await fetch(`${baseUrl}/inside.txt`);
    expect(response.status).toBe(200);
    expect(await response.text()).toBe('inside');
  });
  it.each([
    '/%2e%2e%2frepository-sibling%2fsecret.txt',
    '/%2e%2e%2fparent.txt',
    '/%2e%2e%2f',
    '/link/secret.txt'
  ])('rejects an outside path: %s', async (url) => {
    const response = await fetch(baseUrl + url);
    expect(response.status).toBe(403);
    expect(await response.text()).not.toContain('outside');
  });
  it('keeps missing files and directories unavailable', async () => {
    expect((await fetch(`${baseUrl}/missing.txt`)).status).toBe(404);
    expect((await fetch(`${baseUrl}/`)).status).toBe(404);
  });
  it('writes explicitly enabled evidence inside its directory', async () => {
    expect(
      (await fetch(`${baseUrl}/__evidence/result.json`, { method: 'POST', body: '{}' })).status
    ).toBe(204);
    expect(await fs.readFile(path.join(directory, 'repository/evidence/result.json'), 'utf8')).toBe(
      '{}'
    );
  });
  it('rejects symlink destinations and parent names for evidence', async () => {
    expect(
      (await fetch(`${baseUrl}/__evidence/link`, { method: 'POST', body: 'changed' })).status
    ).toBe(403);
    expect(
      (await fetch(`${baseUrl}/__evidence/%2e%2e%2f`, { method: 'POST', body: 'changed' })).status
    ).toBe(400);
    expect(await fs.readFile(path.join(directory, 'repository-sibling/secret.txt'), 'utf8')).toBe(
      'outside'
    );
  });
});
