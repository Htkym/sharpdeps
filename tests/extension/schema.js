const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');

exports.run = async function () {
  const vscode = require('vscode');
  const { chromium, expect } = require('@playwright/test');
  const results = [];
  const record = (name, ok, detail) => results.push({ name, ok, detail });
  const extension = vscode.extensions.getExtension('htkym.sharpdeps');
  let browser;
  try {
    record('extension is installed', Boolean(extension));
    if (!extension) throw new Error('SharpDeps was not installed.');
    record(
      'candidate version',
      extension.packageJSON.version === '0.4.0',
      extension.packageJSON.version
    );
    record(
      'loaded from installed VSIX',
      extension.extensionPath.includes(`${path.sep}extensions${path.sep}htkym.sharpdeps-`),
      extension.extensionPath
    );
    await extension.activate();
    const root = vscode.workspace.workspaceFolders?.[0]?.uri;
    if (!root) throw new Error('Open the compatibility fixture as one workspace folder.');
    const host = path.join(extension.extensionPath, 'analyzer/bin/query/sharpdeps-query-host.dll');
    record('bundled query host exists', fs.existsSync(host));
    const hostHash = crypto.createHash('sha256').update(fs.readFileSync(host)).digest('hex');
    const config = vscode.workspace.getConfiguration('sharpdeps');
    await config.update(
      'dotnetPath',
      process.env.DOTNET_HOST_PATH,
      vscode.ConfigurationTarget.Global
    );
    await config.update('resultProvider', 'savedIndex', vscode.ConfigurationTarget.Global);
    await config.update('savedIndexAutoRefresh', false, vscode.ConfigurationTarget.Global);
    await config.update('savedIndexVariant', '', vscode.ConfigurationTarget.Global);
    // Reads must succeed even when no usable external mutation/status CLI is configured.
    await config.update(
      'savedIndexCliPath',
      path.join(root.fsPath, 'not-installed/SharpDeps.Cli.dll'),
      vscode.ConfigurationTarget.Global
    );
    browser = await chromium.connectOverCDP(
      `http://127.0.0.1:${process.env.SHARPDEPTS_E2E_DEBUG_PORT}`
    );
    await vscode.commands.executeCommand('sharpdeps.showDependencyMap', root);
    await expect
      .poll(() => extension.exports.getOutputTail().join('\n'), { timeout: 30000 })
      .toContain('Read-only saved index:');
    let frame;
    await expect
      .poll(
        async () => {
          for (const context of browser.contexts())
            for (const page of context.pages())
              for (const candidate of page.frames()) {
                if (
                  await candidate
                    .locator('#app.sd-shell')
                    .count()
                    .catch(() => 0)
                ) {
                  frame = candidate;
                  return true;
                }
              }
          return false;
        },
        { timeout: 20000 }
      )
      .toBe(true);
    await expect
      .poll(
        async () =>
          (await frame.evaluate(() => window.sharpdepsApp.getState())).projection?.nodes.length ??
          0,
        { timeout: 20000 }
      )
      .toBeGreaterThan(0);
    const state = await frame.evaluate(() => window.sharpdepsApp.getState());
    record('saved index projection rendered', state.projection.nodes.length > 0, {
      nodes: state.projection.nodes.length,
      storageSchema: process.env.SHARPDEPTS_E2E_STORAGE_SCHEMA,
      hostHash
    });
    record('no legacy analysis ran', extension.exports.getAnalysisIds().length === 0);
    record('external CLI is absent', !fs.existsSync(config.get('savedIndexCliPath')));
    record(
      'bundled saved reader succeeded',
      extension.exports.getOutputTail().some((line) => line.includes('Read-only saved index:'))
    );
  } catch (error) {
    record('schema connection completed', false, String(error.stack ?? error));
  } finally {
    if (browser) await browser.close().catch(() => {});
    const report = {
      checkedAt: new Date().toISOString(),
      scenario: 'schema',
      storageSchema: process.env.SHARPDEPTS_E2E_STORAGE_SCHEMA,
      results
    };
    fs.mkdirSync(path.dirname(process.env.SHARPDEPTS_E2E_REPORT), { recursive: true });
    fs.writeFileSync(process.env.SHARPDEPTS_E2E_REPORT, JSON.stringify(report, null, 2));
  }
  const failed = results.filter((result) => !result.ok);
  if (failed.length)
    throw new Error(
      failed.map((result) => `${result.name}: ${JSON.stringify(result.detail)}`).join('\n')
    );
};
