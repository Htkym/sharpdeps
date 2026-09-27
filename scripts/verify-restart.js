// Extension test hosts use ephemeral storage. Verify restart using two ordinary
// VS Code processes, a dedicated profile, and actual keyboard interaction.
const fs = require('node:fs'),
  path = require('node:path'),
  { spawn } = require('node:child_process');
const { chromium, expect } = require('@playwright/test');
const repo = path.resolve(__dirname, '..'),
  profile = path.join(repo, '.local/e2e-restart-final');
const executable =
  process.env.SHARPDEPTS_VSCODE ??
  path.join(process.env.LOCALAPPDATA, 'Programs/Microsoft VS Code/Code.exe');
const report = { checkedAt: new Date().toISOString(), results: [] };
const output = path.join(repo, 'docs/implementation/v0.1.0/evidence/sd-030-restart.json');
const runs = () => {
  const dir = path.join(profile, 'User/globalStorage/htkym.sharpdeps/runs');
  return fs.existsSync(dir)
    ? fs
        .readdirSync(dir)
        .filter((n) => n.startsWith('sharpdeps-run-'))
        .sort()
    : [];
};
let browser, child;
(async () => {
  let before, expected;
  for (let phase = 0; phase < 2; phase++) {
    const port = await new Promise((resolve) => {
      const server = require('node:net').createServer();
      server.listen(0, '127.0.0.1', () => {
        const port = server.address().port;
        server.close(() => resolve(port));
      });
    });
    const log = fs.openSync(path.join(repo, `.local/sd-030/restart-normal-${phase}.log`), 'w');
    child = spawn(
      executable,
      [
        path.join(repo, 'tests/fixtures/quick-baseline'),
        `--user-data-dir=${profile}`,
        `--extensions-dir=${path.join(profile, 'extensions')}`,
        `--extensionDevelopmentPath=${repo}`,
        `--remote-debugging-port=${port}`,
        '--disable-workspace-trust',
        '--skip-welcome',
        '--skip-release-notes',
        '--disable-updates'
      ],
      { windowsHide: true, stdio: ['ignore', log, log] }
    );
    await expect
      .poll(
        async () =>
          fetch(`http://127.0.0.1:${port}/json/version`)
            .then((r) => r.ok)
            .catch(() => false),
        { timeout: 30000 }
      )
      .toBe(true);
    browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
    const page = browser.contexts()[0].pages()[0];
    page.setDefaultTimeout(20000);
    await expect(page.locator('.monaco-workbench')).toBeVisible({ timeout: 30000 });
    const command = async (name) => {
      await page.keyboard.press('Control+Shift+P');
      const input = page.locator('.quick-input-widget input').first();
      await input.fill(`>${name}`);
      await expect(page.locator('.quick-input-list')).toContainText(name, { timeout: 20000 });
      await new Promise((resolve) => setTimeout(resolve, 300));
      await input.press('Enter');
    };
    if (phase === 0) await command('SharpDeps: Show Dependency Map');
    let frame;
    await expect
      .poll(
        async () => {
          for (const context of browser.contexts())
            for (const p of context.pages())
              for (const f of p.frames())
                if (
                  await f
                    .locator('#app.sd-shell')
                    .count()
                    .catch(() => 0)
                ) {
                  frame = f;
                  return true;
                }
          return false;
        },
        { timeout: 30000 }
      )
      .toBe(true);
    if (phase === 0) {
      await expect
        .poll(
          async () =>
            (await frame.evaluate(() => window.sharpdepsApp.getState())).projection?.nodes.length ??
            0,
          { timeout: 30000 }
        )
        .toBeGreaterThan(0);
      await frame.locator('#sd-view-table').press('Enter');
      await frame.getByRole('searchbox', { name: 'Search analyzed entities' }).fill('Core');
      await new Promise((resolve) => setTimeout(resolve, 1000));
      expected = await frame.evaluate(() => ({
        viewKind: window.sharpdepsApp.getState().viewKind,
        search: window.sharpdepsApp.getState().search
      }));
      before = runs();
    } else {
      const state = await frame.evaluate(() => window.sharpdepsApp.getState());
      report.results.push({
        name: 'restored real webview tab and state in second process',
        ok: state.viewKind === expected.viewKind && state.search === expected.search,
        detail: { viewKind: state.viewKind, search: state.search }
      });
      await new Promise((resolve) => setTimeout(resolve, 1500));
      report.results.push({
        name: 'restart starts no analysis',
        ok: JSON.stringify(runs()) === JSON.stringify(before) && !state.analysisId,
        detail: { before, after: runs(), status: state.status }
      });
    }
    const exited = new Promise((resolve) => child.once('exit', resolve));
    await page.keyboard.press('Control+Shift+W');
    await exited;
    await browser.close();
    browser = undefined;
    fs.closeSync(log);
  }
})()
  .catch((error) => {
    report.results.push({
      name: 'normal restart',
      ok: false,
      detail: String(error.stack ?? error)
    });
    process.exitCode = 1;
  })
  .finally(async () => {
    fs.writeFileSync(output, JSON.stringify(report, null, 2));
    if (browser) {
      const page = browser.contexts()[0]?.pages()[0];
      await page
        ?.screenshot({ path: path.join(repo, '.local/sd-030/restart-failure.png') })
        .catch(() => {});
      if (page)
        fs.writeFileSync(
          path.join(repo, '.local/sd-030/restart-page.txt'),
          await page.locator('body').innerText().catch(String)
        );
      await page?.keyboard.press('Control+Shift+W').catch(() => {});
      await browser.close();
    }
    if (report.results.some((r) => !r.ok)) process.exitCode = 1;
    console.log(JSON.stringify(report, null, 2));
  });
