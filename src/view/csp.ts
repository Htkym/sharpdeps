// Webview Content Security Policy (SD-023).
//
// Kept free of the vscode import so the policy is unit-testable. Minimal by
// construction: no eval, no remote origins, scripts only with the nonce, workers only
// from Blob URLs (the ELK script is fetched from the extension's own resources), and
// objects, frames, and the base URI locked down.

export function buildWebviewCsp(cspSource: string, nonce: string): string {
  return [
    `default-src 'none'`,
    `img-src ${cspSource} data: blob:`,
    `style-src ${cspSource} 'unsafe-inline'`,
    `font-src ${cspSource}`,
    `script-src 'nonce-${nonce}'`,
    `worker-src blob:`,
    // Only the webview's own resources may be fetched (the worker script).
    `connect-src ${cspSource}`,
    `object-src 'none'`,
    `frame-src 'none'`,
    `base-uri 'none'`,
    `form-action 'none'`
  ].join('; ');
}
