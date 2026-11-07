const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');

const execute = promisify(execFile);

exports.run = async function () {
  const vscode = require('vscode');
  const { chromium, expect } = require('@playwright/test');
  const repo = process.env.SHARPDEPTS_REPO;
  const root = vscode.workspace.workspaceFolders?.[0]?.uri.fsPath;
  const results = [];
  let browser;
  let extension;
  let frame;
  const record = (name, detail) => results.push({ name, ok: true, detail });
  try {
    assert.equal(process.env.SHARPDEPTS_E2E_MODE, 'vsix');
    assert(root);
    const relative = path.relative(path.join(repo, '.local'), root);
    assert(
      relative &&
        relative !== '..' &&
        !relative.startsWith('..' + path.sep) &&
        !path.isAbsolute(relative)
    );
    const cli = process.env.SHARPDEPTS_E2E_CLI;
    assert(cli && path.isAbsolute(cli) && fs.existsSync(cli));
    const dotnet = process.env.SHARPDEPTS_E2E_DOTNET;
    assert(dotnet && path.isAbsolute(dotnet) && fs.existsSync(dotnet));
    const invoke = async (args) => {
      await execute(dotnet, args, {
        cwd: root,
        encoding: 'utf8',
        timeout: 120000,
        windowsHide: true
      });
    };
    fs.writeFileSync(
      path.join(root, 'Input.csproj'),
      '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>'
    );
    fs.writeFileSync(
      path.join(root, 'global.json'),
      JSON.stringify({ sdk: { version: '10.0.100', rollForward: 'latestFeature' } })
    );
    const formats = [
      ['Utf8', (text) => Buffer.from(text)],
      ['Utf8Bom', (text) => Buffer.from('\ufeff' + text)],
      ['Utf16Le', (text) => Buffer.from('\ufeff' + text, 'utf16le')],
      ['Utf16Be', (text) => Buffer.from('\ufeff' + text, 'utf16le').swap16()]
    ];
    for (const [name, encode] of formats) {
      const text = `// 日本語 🐈\r\npublic sealed class ${name} { public string Value => "猫"; }\r\n`;
      fs.writeFileSync(path.join(root, name + '.cs'), encode(text));
    }
    await invoke(['restore', 'Input.csproj', '--verbosity', 'quiet']);
    await invoke([
      cli,
      'index',
      '--root',
      root,
      '--target',
      'Input.csproj',
      '--trusted',
      '--quiet'
    ]);

    extension = vscode.extensions.getExtension('htkym.sharpdeps');
    assert(extension);
    assert(extension.extensionPath.includes(`${path.sep}extensions${path.sep}htkym.sharpdeps-`));
    await extension.activate();
    record('loads the installed VSIX', extension.extensionPath);
    assert(vscode.workspace.isTrusted);
    const config = vscode.workspace.getConfiguration('sharpdeps');
    await config.update('dotnetPath', dotnet, vscode.ConfigurationTarget.Global);
    await config.update('resultProvider', 'savedIndex', vscode.ConfigurationTarget.Global);
    await vscode.commands.executeCommand('sharpdeps.showDependencyMap', vscode.Uri.file(root));
    browser = await chromium.connectOverCDP(
      `http://127.0.0.1:${process.env.SHARPDEPTS_E2E_DEBUG_PORT}`
    );
    const mapFrame = async () => {
      await expect
        .poll(
          async () => {
            for (const context of browser.contexts())
              for (const page of context.pages())
                for (const candidate of page.frames())
                  if (
                    await candidate
                      .locator('#app.sd-shell')
                      .count()
                      .catch(() => 0)
                  ) {
                    frame = candidate;
                    return true;
                  }
            return false;
          },
          { timeout: 30000 }
        )
        .toBe(true);
      return frame;
    };
    await mapFrame();
    await expect
      .poll(() => extension.exports.getOutputTail().join('\n'), { timeout: 30000 })
      .toContain('Read-only saved index:');
    await expect
      .poll(
        async () =>
          (await frame.evaluate(() => window.sharpdepsApp.getState())).projection?.nodes.length ??
          0,
        { timeout: 30000 }
      )
      .toBeGreaterThan(0);
    const initialState = await frame.evaluate(() => window.sharpdepsApp.getState());
    assert.equal(initialState.granularity, 'type');
    await frame.locator('#sd-view-table').click();
    for (const [name] of formats) {
      const row = frame
        .locator('.sd-table tbody tr[data-entity-id]')
        .filter({ has: frame.getByRole('cell', { name, exact: true }) });
      await expect(row).toHaveCount(1);
      await row.click();
      const open = frame.locator('button[data-source-return="declaration"]');
      await expect(open).toHaveCount(1);
      await open.click();
      await expect
        .poll(() => vscode.window.activeTextEditor?.document.uri.fsPath, { timeout: 15000 })
        .toBe(path.join(root, name + '.cs'));
      await expect
        .poll(
          () => {
            const selected = vscode.window.activeTextEditor;
            return (
              selected?.document.uri.fsPath === path.join(root, name + '.cs') &&
              selected.selection.start.line === 1 &&
              selected.selection.start.character === 0 &&
              selected.document.getText(selected.selection).includes('class ' + name)
            );
          },
          { timeout: 15000 }
        )
        .toBe(true);
      const editor = vscode.window.activeTextEditor;
      const text = editor.document.getText();
      assert.equal(text.charCodeAt(0), '/'.charCodeAt(0));
      assert.equal(editor.selection.start.line, 1);
      assert.equal(editor.selection.start.character, 0);
      assert(editor.document.getText(editor.selection).includes(`class ${name}`));
      record(`opens the verified ${name} declaration`, {
        start: editor.selection.start,
        end: editor.selection.end
      });
      await vscode.commands.executeCommand('workbench.action.previousEditor');
      await mapFrame();
    }
  } catch (error) {
    results.push({
      name: 'packaged saved-source navigation',
      ok: false,
      detail: String(error.stack || error),
      output: extension?.exports?.getOutputTail?.(),
      state: await frame?.evaluate(() => window.sharpdepsApp.getState()).catch(() => undefined)
    });
  } finally {
    fs.mkdirSync(path.dirname(process.env.SHARPDEPTS_E2E_REPORT), { recursive: true });
    fs.writeFileSync(
      process.env.SHARPDEPTS_E2E_REPORT,
      JSON.stringify(
        { checkedAt: new Date().toISOString(), vscodeVersion: vscode.version, results },
        null,
        2
      )
    );
    if (browser) {
      await browser.close();
    }
  }
  if (results.some((entry) => !entry.ok)) {
    throw new Error('Packaged saved-source navigation failed.');
  }
};
