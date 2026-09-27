const fs = require('node:fs');
const path = require('node:path');
const { performance } = require('node:perf_hooks');
exports.run = async function () {
  const vscode = require('vscode');
  const { chromium, expect } = require('@playwright/test');
  const scenario = process.env.SHARPDEPTS_E2E_SCENARIO;
  const repo = process.env.SHARPDEPTS_REPO;
  const results = [];
  const record = (name, ok, detail) => {
    results.push({ name, ok, detail });
    console.log(`${ok ? 'ok' : 'FAIL'} - ${name}`);
  };
  const extension = vscode.extensions.getExtension('htkym.sharpdeps');
  let browser;
  try {
    await extension.activate();
    const api = extension.exports;
    const config = vscode.workspace.getConfiguration('sharpdeps');
    const root = vscode.workspace.workspaceFolders[0].uri;
    if (scenario === 'trust') {
      record('actual VS Code workspace is untrusted', !vscode.workspace.isTrusted);
      for (const mode of ['quick', 'semantic']) {
        await config.update('analysisMode', mode, vscode.ConfigurationTarget.Global);
        await vscode.commands.executeCommand('sharpdeps.showDependencyMap', root);
      }
      record(
        'both analyses refused before execution',
        api.getAnalysisIds().length === 0 &&
          api.getOutputTail().filter((line) => line.includes('not trusted')).length >= 2,
        api.getOutputTail()
      );
      record(
        'custom executable and build target did not run',
        !fs.existsSync(path.join(root.fsPath, 'executed.txt'))
      );
    } else {
      browser = await chromium.connectOverCDP(
        `http://127.0.0.1:${process.env.SHARPDEPTS_E2E_DEBUG_PORT}`
      );
      for (const context of browser.contexts())
        for (const page of context.pages()) page.setDefaultTimeout(15000);
      const mapFrame = async () => {
        let found;
        await expect
          .poll(
            async () => {
              for (const context of browser.contexts())
                for (const page of context.pages())
                  for (const frame of page.frames())
                    if (
                      await frame
                        .locator('#app.sd-shell')
                        .count()
                        .catch(() => 0)
                    ) {
                      found = frame;
                      return true;
                    }
              return false;
            },
            { timeout: 20000 }
          )
          .toBe(true);
        return found;
      };
      await config.update('analysisMode', 'quick', vscode.ConfigurationTarget.Global);
      if (scenario === 'runtime')
        await config.update(
          'dotnetPath',
          path.join(repo, '.local/sd-030/runtime-only/dotnet.exe'),
          vscode.ConfigurationTarget.Global
        );
      await vscode.commands.executeCommand('sharpdeps.showDependencyMap', root);
      let frame = await mapFrame();
      const state = () => frame.evaluate(() => window.sharpdepsApp.getState());
      await expect
        .poll(async () => (await state()).projection?.nodes.length ?? 0, { timeout: 20000 })
        .toBeGreaterThan(0);
      record('real Quick UI has a result', api.getCurrentReport()?.mode === 'quick');
      if (scenario === 'runtime') {
        const id = api.getCurrentAnalysisId();
        await frame.locator('#sd-mode').selectOption('semantic');
        await frame.locator('#sd-analyze').press('Enter');
        await expect
          .poll(() => api.getOutputTail().join('\n'), { timeout: 20000 })
          .toContain('SDK');
        record(
          'missing SDK preserves the Quick result',
          api.getCurrentAnalysisId() === id,
          api.getOutputTail()
        );
        await frame.locator('#sd-mode').selectOption('quick');
        await frame.locator('#sd-analyze').press('Enter');
        await expect.poll(() => api.getCurrentAnalysisId(), { timeout: 20000 }).not.toBe(id);
        record(
          'Quick remains available after SDK refusal',
          api.getCurrentReport()?.mode === 'quick'
        );
      } else if (scenario === 'incomplete') {
        await config.update('analysisMode', 'semantic', vscode.ConfigurationTarget.Global);
        await vscode.commands.executeCommand(
          'sharpdeps.showDependencyMap',
          vscode.Uri.file(path.join(repo, '.local/sd-030/mixed/Mixed.slnx'))
        );
        frame = await mapFrame();
        await expect.poll(async () => (await state()).status, { timeout: 30000 }).toBe('partial');
        await frame.locator('#sd-view-table').press('Enter');
        for (const [name, status] of [
          ['Good', 'complete'],
          ['Broken', 'partial'],
          ['Unavailable', 'failed']
        ]) {
          await frame.locator('.sd-table tbody tr').filter({ hasText: name }).click();
          await expect(frame.locator('.sd-inspector-body')).toContainText(status);
          record(`${name} is shown as ${status}`, true);
        }
        await expect(frame.locator('.sd-inspector-body')).toContainText('SharpDeps.Missing.Sdk');
        record(
          'mixed SDK and generator failure never claims no dependencies or complete analysis',
          (await state()).status === 'partial'
        );
      } else if (scenario === 'exploration') {
        await config.update('analysisMode', 'semantic', vscode.ConfigurationTarget.Global);
        await config.update('maxVisibleTypes', 10, vscode.ConfigurationTarget.Global);
        await vscode.commands.executeCommand(
          'sharpdeps.showDependencyMap',
          vscode.Uri.file(path.join(repo, '.local/perf-v010/medium/Medium.slnx'))
        );
        frame = await mapFrame();
        await expect
          .poll(async () => (await state()).resultMode, { timeout: 60000 })
          .toBe('semantic');
        await frame.locator('#sd-view-table').press('Enter');
        await frame.locator('#sd-granularity').selectOption('type');
        await expect.poll(async () => (await state()).projection?.nodes.length).toBe(10);
        const analysisId = api.getCurrentAnalysisId();
        await expect.poll(async () => (await state()).tree.root?.items.length).toBe(31);
        const parent = (await state()).tree.root.items.find((item) => item.name === 'P29');
        await frame.getByRole('button', { name: 'Expand P29', exact: true }).press('Enter');
        await expect
          .poll(async () => (await state()).tree[parent.id]?.items.length ?? 0)
          .toBeGreaterThan(0);
        const ns = (await state()).tree[parent.id].items[0];
        await frame.getByRole('button', { name: `Expand ${ns.name}`, exact: true }).press('Enter');
        await expect.poll(async () => (await state()).tree[ns.id]?.items.length).toBe(100);
        record('lazy hierarchy includes types outside the ten-node projection', true);
        await frame.getByRole('searchbox', { name: 'Search analyzed entities' }).fill('C99');
        await expect(
          frame
            .locator('.sd-search-results')
            .getByRole('button', { name: 'Show', exact: true })
            .first()
        ).toBeVisible();
        await frame
          .locator('.sd-search-results')
          .getByRole('button', { name: 'Show', exact: true })
          .first()
          .press('Enter');
        await expect
          .poll(async () => (await state()).temporaryDisplayIds.length)
          .toBeGreaterThan(0);
        record('global search reveals a type outside the original display budget', true);
        await frame.getByRole('searchbox', { name: 'Search analyzed entities' }).fill('');
        await frame.locator('#sd-tab-cycles').press('Enter');
        await frame
          .getByRole('button', { name: 'この循環を表示', exact: true })
          .first()
          .press('Enter');
        await expect.poll(async () => (await state()).scope.kind).toBe('cycle');
        await expect.poll(async () => (await state()).projection.nodes.length).toBe(100);
        const view = await state();
        const group = view.cycles.find((c) => c.id === view.scope.id);
        record(
          'cycle scope retains every member beyond the display budget',
          view.projection.nodes.length === group.memberIds.length,
          { nodes: view.projection.nodes.length, edges: view.projection.edges.length }
        );
        await frame.locator('.sd-cycle-item').first().locator('ol button').first().press('Enter');
        await expect(frame.locator('.sd-evidence-item').first()).toBeVisible();
        record(
          'cycle witness opens evidence without reanalysis',
          api.getCurrentAnalysisId() === analysisId
        );
      } else {
        // Real VS Code theme variables and zoom, rather than body-class simulation.
        await vscode.commands.executeCommand('workbench.action.closeSidebar');
        await vscode.commands.executeCommand('workbench.action.closeAuxiliaryBar');
        for (const theme of ['Default High Contrast', 'Default High Contrast Light']) {
          await vscode.workspace
            .getConfiguration('workbench')
            .update('colorTheme', theme, vscode.ConfigurationTarget.Global);
          await vscode.workspace
            .getConfiguration('window')
            .update('zoomLevel', Math.log(2) / Math.log(1.2), vscode.ConfigurationTarget.Global);
          await expect
            .poll(() => frame.locator('body').getAttribute('data-vscode-theme-kind'))
            .toBe(theme.endsWith('Light') ? 'vscode-high-contrast-light' : 'vscode-high-contrast');
          await expect(frame.locator('#sd-analyze')).toBeVisible();
          await expect(frame.locator('#sd-analyze')).toBeInViewport();
          await frame.locator('#sd-view-table').press('Enter');
          await expect(frame.locator('.sd-table')).toBeVisible();
          await frame.page().screenshot({
            fullPage: true,
            path: path.join(repo, '.local/sd-030', `${theme.replaceAll(' ', '-')}-200.png`)
          });
          record(
            `${theme} at 200% supports keyboard controls`,
            await frame.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth),
            await frame.evaluate(() => ({
              width: innerWidth,
              height: innerHeight,
              documentWidth: document.documentElement.scrollWidth,
              devicePixelRatio,
              analyze: document.querySelector('#sd-analyze').getBoundingClientRect().toJSON()
            }))
          );
        }
        await vscode.workspace
          .getConfiguration('window')
          .update('zoomLevel', 0, vscode.ConfigurationTarget.Global);
        await config.update('analysisMode', 'semantic', vscode.ConfigurationTarget.Global);
        await vscode.commands.executeCommand(
          'sharpdeps.showDependencyMap',
          vscode.Uri.file(path.join(repo, 'tests/fixtures/semantic-baseline/SemanticBaseline.sln'))
        );
        frame = await mapFrame();
        await expect
          .poll(async () => (await state()).resultMode, { timeout: 20000 })
          .toBe('semantic');
        await frame.locator('#sd-granularity').selectOption('type');
        await frame.locator('#sd-view-graph').press('Enter');
        const relation = api
          .getCurrentReport()
          .relations.find(
            (r) =>
              r.basis === 'symbolResolved' &&
              r.generatedEvidenceCount > 0 &&
              r.sourceEntityId !== r.targetEntityId
          );
        await frame.locator(`g.edge[data-id="${relation.id}"]`).press('Enter', { timeout: 20000 });
        await frame
          .getByRole('button', { name: 'エディターで開く', exact: true })
          .first()
          .press('Enter');
        await expect
          .poll(() => vscode.window.activeTextEditor?.document.uri.scheme)
          .toBe('sharpdeps-generated');
        record('keyboard graph → evidence → editor', true);
        await vscode.commands.executeCommand('workbench.action.previousEditor');
        frame = await mapFrame();
        const samples = [];
        for (let i = 0; i < 20; i++) {
          await frame.locator('#sd-analyze').press('Enter');
          await expect(frame.locator('#sd-stop')).toBeEnabled();
          // Measure the DOM event through receipt of the host cancellation message.
          samples.push(
            await frame.evaluate(async () => {
              const start = performance.now();
              document.querySelector('#sd-stop').click();
              while (window.sharpdepsApp.getState().status !== 'cancelled') {
                await new Promise(requestAnimationFrame);
                if (performance.now() - start > 5000) throw new Error('Stop timeout');
              }
              return performance.now() - start;
            })
          );
          await vscode.window.showTextDocument(
            vscode.Uri.file(path.join(repo, 'tests/fixtures/quick-baseline/src/Core/Order.cs'))
          );
          await vscode.commands.executeCommand('workbench.action.previousEditor');
          frame = await mapFrame();
        }
        record('20 open/analyze/stop cycles respond within 250 ms', Math.max(...samples) <= 250, {
          samplesMs: samples
        });
      }
    }
  } catch (error) {
    record(scenario, false, String(error.stack ?? error));
  } finally {
    fs.writeFileSync(
      process.env.SHARPDEPTS_E2E_REPORT,
      JSON.stringify(
        { checkedAt: new Date().toISOString(), vscodeVersion: vscode.version, scenario, results },
        null,
        2
      )
    );
    if (browser) await browser.close();
  }
  if (results.some((r) => !r.ok)) throw new Error(`Acceptance ${scenario} failed`);
};
