import * as vscode from 'vscode';
import { buildWebviewCsp } from './csp';

/** Build the webview HTML with a strict, nonce-based Content Security Policy. */
export function getWebviewHtml(webview: vscode.Webview, extensionUri: vscode.Uri): string {
  const nonce = createNonce();
  const scriptUri = webview.asWebviewUri(
    vscode.Uri.joinPath(extensionUri, 'media', 'app', 'entry.js')
  );
  const styleUri = webview.asWebviewUri(
    vscode.Uri.joinPath(extensionUri, 'media', 'styles', 'shell.css')
  );
  // The layout worker is fetched by the webview and started from a Blob URL
  // (workers cannot be loaded directly from the extension resource URI).
  const workerUri = webview.asWebviewUri(
    vscode.Uri.joinPath(extensionUri, 'media', 'workers', 'elkLayout.worker.js')
  );
  const graphStyleUri = webview.asWebviewUri(
    vscode.Uri.joinPath(extensionUri, 'media', 'styles', 'graph.css')
  );

  const csp = buildWebviewCsp(webview.cspSource, nonce);

  return `<!DOCTYPE html>
<html lang="en">
<head>
  <meta charset="UTF-8" />
  <meta http-equiv="Content-Security-Policy" content="${csp}" />
  <meta name="viewport" content="width=device-width, initial-scale=1.0" />
  <link href="${styleUri}" rel="stylesheet" />
  <link href="${graphStyleUri}" rel="stylesheet" />
  <title>SharpDeps</title>
</head>
<body>
  <div id="app" data-worker-uri="${workerUri}"></div>
  <script nonce="${nonce}" src="${scriptUri}"></script>
</body>
</html>`;
}

function createNonce(): string {
  const chars = 'ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789';
  let text = '';
  for (let i = 0; i < 32; i++) {
    text += chars.charAt(Math.floor(Math.random() * chars.length));
  }
  return text;
}
