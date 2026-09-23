// Minimal static file server for webview fixtures (SD-004 verification, and the
// basis for the Playwright suite in SD-027).
//
//   node scripts/serve-webview.mjs [port] [--evidence <dir>]
//
// Serves the repository root so tests/webview/fixtures/*.html can load the built
// media bundles and the layout worker over HTTP (file:// cannot fetch workers).
//
// With --evidence, a fixture can POST an artifact (for example an exported SVG)
// to /__evidence/<file-name> and it is written into that directory. The endpoint
// is off by default.

import { createServer } from 'node:http';
import { mkdir, readFile, stat, writeFile } from 'node:fs/promises';
import * as path from 'node:path';
import { fileURLToPath } from 'node:url';

const repositoryRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const argumentsList = process.argv.slice(2);
const evidenceIndex = argumentsList.indexOf('--evidence');
const evidenceDirectory =
  evidenceIndex >= 0 && argumentsList[evidenceIndex + 1]
    ? path.resolve(argumentsList[evidenceIndex + 1])
    : undefined;
const port = Number(argumentsList.find((value) => /^\d+$/.test(value)) ?? 4173);

const CONTENT_TYPES = {
  '.html': 'text/html; charset=utf-8',
  '.js': 'text/javascript; charset=utf-8',
  '.mjs': 'text/javascript; charset=utf-8',
  '.css': 'text/css; charset=utf-8',
  '.json': 'application/json; charset=utf-8',
  '.svg': 'image/svg+xml',
  '.png': 'image/png',
  '.map': 'application/json; charset=utf-8'
};

const server = createServer(async (request, response) => {
  try {
    const url = new URL(request.url ?? '/', `http://${request.headers.host}`);

    if (evidenceDirectory && request.method === 'POST' && url.pathname.startsWith('/__evidence/')) {
      const name = path.basename(decodeURIComponent(url.pathname.slice('/__evidence/'.length)));
      const chunks = [];
      for await (const chunk of request) {
        chunks.push(chunk);
      }
      await mkdir(evidenceDirectory, { recursive: true });
      await writeFile(path.join(evidenceDirectory, name), Buffer.concat(chunks));
      response.writeHead(204).end();
      return;
    }

    const relativePath = decodeURIComponent(url.pathname).replace(/^\/+/, '');
    const target = path.resolve(repositoryRoot, relativePath);
    if (!target.startsWith(repositoryRoot)) {
      response.writeHead(403).end('Forbidden');
      return;
    }

    const info = await stat(target);
    if (info.isDirectory()) {
      response.writeHead(404).end('Not found');
      return;
    }

    const body = await readFile(target);
    response.writeHead(200, {
      'Content-Type':
        CONTENT_TYPES[path.extname(target).toLowerCase()] ?? 'application/octet-stream',
      'Cache-Control': 'no-store'
    });
    response.end(body);
  } catch {
    response.writeHead(404).end('Not found');
  }
});

server.listen(port, '127.0.0.1', () => {
  console.log(`Serving ${repositoryRoot} at http://127.0.0.1:${port}/`);
});
