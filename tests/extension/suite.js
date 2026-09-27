// End-to-end suite for the extension host (SD-027).
//
// VS Code's built-in test runner loads this module and calls run(). Assertions are plain
// checks: a failure throws with every failing check listed, and the run also writes a
// JSON report for the evidence folder.

const fs = require('node:fs');
const path = require('node:path');

async function run() {
  const vscode = require('vscode');
  const results = [];
  const record = (name, ok, detail) => {
    results.push({ name, ok, detail });
    console.log(`${ok ? 'ok' : 'FAIL'} - ${name}${detail ? ` (${detail})` : ''}`);
  };

  const extension = vscode.extensions.getExtension('htkym.sharpdeps');
  record('extension installed', Boolean(extension), 'htkym.sharpdeps');
  if (!extension) {
    return finish(vscode, results);
  }

  await extension.activate();
  if (process.env.SHARPDEPTS_E2E_MODE === 'vsix')
    record(
      'SharpDeps is loaded from the installed VSIX',
      extension.extensionPath.includes(`${path.sep}extensions${path.sep}htkym.sharpdeps-`),
      extension.extensionPath
    );
  const commands = await vscode.commands.getCommands(true);
  const expected = [
    'sharpdeps.showDependencyMap',
    'sharpdeps.refresh',
    'sharpdeps.showTypeDependencies',
    'sharpdeps.showTypeDependents',
    'sharpdeps.copyMermaid',
    'sharpdeps.exportSvg',
    'sharpdeps.exportPng'
  ];
  const missing = expected.filter((command) => !commands.includes(command));
  record('commands are registered', missing.length === 0, missing.join(', '));
  record('workspace is trusted', vscode.workspace.isTrusted === true);

  const workspaceRoot = vscode.workspace.workspaceFolders?.[0]?.uri;
  record('workspace folder is open', Boolean(workspaceRoot), workspaceRoot?.fsPath ?? 'none');
  if (!workspaceRoot) {
    return finish(vscode, results);
  }

  // Run the real Quick analysis through the command path.
  const { chromium, expect } = require('@playwright/test');
  const browser = await chromium.connectOverCDP(
    `http://127.0.0.1:${process.env.SHARPDEPTS_E2E_DEBUG_PORT}`
  );
  await vscode.workspace
    .getConfiguration('sharpdeps')
    .update('analysisMode', 'quick', vscode.ConfigurationTarget.Global);
  await vscode.commands.executeCommand('sharpdeps.showDependencyMap', workspaceRoot);
  // The command awaits the analysis, but the panel's registration happens right after:
  // give it a moment before asserting.
  await new Promise((resolve) => setTimeout(resolve, 3000));

  const api = extension.exports ?? {};
  const analysisIds = typeof api.getAnalysisIds === 'function' ? api.getAnalysisIds() : [];
  const outputTail = typeof api.getOutputTail === 'function' ? api.getOutputTail().join(' | ') : '';
  record(
    'analysis result is registered',
    analysisIds.length >= 1,
    analysisIds.join(', ') || `${diagnosticsFor(extension, vscode)} || ${outputTail.slice(-600)}`
  );
  record(
    'current analysis is set',
    typeof api.getCurrentAnalysisId === 'function' && api.getCurrentAnalysisId() !== undefined,
    api.getCurrentAnalysisId?.() ?? 'undefined'
  );

  const diagnostics = vscode.languages.getDiagnostics();
  record(
    'problems collection is queryable',
    Array.isArray(diagnostics),
    `${diagnostics.length} file(s)`
  );

  // Drive the real webview, not a fixture with hand-dispatched reducer actions.
  try {
    for (const context of browser.contexts())
      for (const page of context.pages()) {
        page.on('pageerror', (error) => console.log('PAGE ERROR', String(error)));
        page.on('console', (message) => {
          if (message.type() === 'error') console.log('CONSOLE ERROR', message.text());
        });
      }
    const mapFrame = async () => {
      let found;
      await expect
        .poll(
          async () => {
            for (const context of browser.contexts())
              for (const page of context.pages())
                for (const frame of page.frames()) {
                  if (
                    await frame
                      .locator('#app.sd-shell')
                      .count()
                      .catch(() => 0)
                  ) {
                    found = frame;
                    return true;
                  }
                }
            return false;
          },
          { timeout: 20000, message: 'The real SharpDeps webview must load.' }
        )
        .toBe(true)
        .catch(async (error) => {
          for (const context of browser.contexts())
            for (const page of context.pages())
              for (const frame of page.frames()) {
                console.log(
                  'FRAME',
                  frame.url(),
                  await frame
                    .evaluate(() => document.documentElement.outerHTML.slice(-15000))
                    .catch(String)
                );
              }
          const session = await browser.newBrowserCDPSession();
          console.log('TARGETS', JSON.stringify(await session.send('Target.getTargets')));
          throw error;
        });
      return found;
    };
    let frame = await mapFrame();
    const state = () => frame.evaluate(() => window.sharpdepsApp.getState());
    await expect
      .poll(async () => (await state()).projection?.nodes.length ?? 0, { timeout: 20000 })
      .toBeGreaterThan(0);
    record('Quick renders a nonempty project view', (await state()).granularity === 'project');
    const quickEdges = (await state()).projection.edges;
    expect(new Set(quickEdges.map((edge) => `${edge.sourceId}:${edge.targetId}`)).size).toBe(
      quickEdges.length
    );
    record('Quick draws one connection per directed project pair', true);
    const repo = process.env.SHARPDEPTS_REPO;
    const mapBounds = await frame.locator('.sd-map-host').boundingBox();
    for (const id of ['filters', 'spacing', 'profile']) {
      const trigger = frame.locator(`#sd-${id}-toggle`);
      const panel = frame.locator(`#sd-${id}-panel`);
      await trigger.click();
      await expect(panel).toBeVisible();
      expect(await frame.locator('.sd-map-host').boundingBox()).toEqual(mapBounds);
      const visibleInFrame = await panel.evaluate((element) => {
        const rect = element.getBoundingClientRect();
        return (
          rect.left >= 0 &&
          rect.top >= 0 &&
          rect.right <= innerWidth &&
          rect.bottom <= innerHeight &&
          element.contains(document.elementFromPoint(rect.left + 12, rect.top + 12))
        );
      });
      expect(visibleInFrame).toBe(true);
      if (id === 'spacing') {
        const bounds = await panel.boundingBox();
        const anchor = await trigger.boundingBox();
        expect(bounds.y + bounds.height).toBeCloseTo(anchor.y - 4, 1);
        expect(
          await panel.evaluate((element) => element.scrollHeight - element.clientHeight)
        ).toBeLessThanOrEqual(1);
      }
      await frame.locator('#app').screenshot({
        path: path.join(repo, '.local', 'sd-030', `toolbar-${id}-vscode.png`)
      });
      await trigger.press('Escape');
      await expect(panel).toBeHidden();
      record(`${id} overlays the map without resizing it`, true);
    }
    const graphPositions = () =>
      frame
        .locator('g.node')
        .evaluateAll((nodes) => nodes.map((node) => node.getAttribute('transform')).join('|'));
    await expect.poll(async () => await frame.locator('g.node').count()).toBeGreaterThan(0);
    const barBounds = await frame.locator('.sd-graph-controls').boundingBox();
    expect(barBounds.y).toBeGreaterThanOrEqual(mapBounds.y + mapBounds.height);
    expect(barBounds.height).toBeLessThan(65);
    record('graph controls stay in a compact bottom bar', true);
    const horizontalPositions = await graphPositions();
    const beforeDirectionChange = api.getCurrentAnalysisId();
    await frame.getByRole('combobox', { name: 'Direction', exact: true }).selectOption('DOWN');
    await expect.poll(graphPositions).not.toBe(horizontalPositions);
    await expect.poll(() => api.getViewState()?.layout?.direction).toBe('DOWN');
    expect(api.getCurrentAnalysisId()).toBe(beforeDirectionChange);
    const verticalPositions = await graphPositions();
    await frame
      .locator('#app')
      .screenshot({ path: path.join(repo, '.local', 'sd-030', 'ui-vertical-vscode.png') });
    record('vertical layout is persisted without rerunning analysis', true);
    await expect
      .poll(() =>
        frame.locator('.graph-viewport').evaluate((viewport) => {
          const svg = viewport.querySelector('.graph-svg').getBoundingClientRect();
          const area = viewport.getBoundingClientRect();
          return Math.abs(svg.x + svg.width / 2 - area.x - viewport.clientWidth / 2);
        })
      )
      .toBeLessThan(1);
    record('vertical graph is centered in the visible viewport', true);
    await frame.getByRole('combobox', { name: 'Direction', exact: true }).selectOption('RIGHT');
    await expect.poll(graphPositions).not.toBe(verticalPositions);
    const duplicateLabels = await frame.locator('g.node').evaluateAll((nodes) =>
      nodes.some((node) => {
        const label = node.querySelector('.node-label')?.textContent;
        return !!label && label === node.querySelector('.node-sublabel')?.textContent;
      })
    );
    expect(duplicateLabels).toBe(false);
    record('node captions contain no duplicate labels', true);
    await vscode.commands.executeCommand('sharpdeps.copyMermaid');
    await expect.poll(() => vscode.env.clipboard.readText()).toContain('flowchart');
    record(
      'Quick exports the current nonempty view',
      (await vscode.env.clipboard.readText()).includes((await state()).projection.nodes[0].name)
    );
    await expect(frame.locator('.sd-error-bar')).toBeHidden();
    await frame.locator('#sd-view-table').click();
    await expect(frame.locator('.sd-table tbody tr')).not.toHaveCount(0);
    record(
      'table displays dependency counts',
      (await frame.locator('.sd-table tbody td:nth-child(4)').allTextContents()).some(
        (text) => Number(text) > 0
      )
    );
    await expect.poll(() => api.getViewState()?.viewKind, { timeout: 5000 }).toBe('table');
    record('view state reaches workspace storage', true);
    const beforeLanguageChange = api.getCurrentAnalysisId();
    await frame.getByRole('button', { name: 'Switch to Japanese', exact: true }).click();
    await expect(frame.locator('#sd-analyze')).toHaveText('解析');
    await expect.poll(() => api.getViewState()?.language).toBe('ja');
    await frame
      .locator('#app')
      .screenshot({ path: path.join(repo, '.local', 'sd-030', 'localization-ja-vscode.png') });

    // Open an actual file, then return to the map: retainContextWhenHidden is false.
    await vscode.window.showTextDocument(
      vscode.Uri.joinPath(workspaceRoot, 'src', 'Core', 'Order.cs')
    );
    await vscode.commands.executeCommand('workbench.action.previousEditor');
    frame = await mapFrame();
    await expect.poll(async () => (await state()).viewKind).toBe('table');
    record('hidden webview restores selection conditions', true);
    await expect(frame.locator('#app')).toHaveAttribute('lang', 'ja');
    expect(api.getCurrentAnalysisId()).toBe(beforeLanguageChange);
    record('Japanese UI is restored without rerunning analysis', true);
    await frame.getByRole('button', { name: '英語に切り替える', exact: true }).click();
    await expect(frame.locator('#sd-analyze')).toHaveText('Analyze');
    await expect.poll(() => api.getViewState()?.language).toBe('en');
    record('top menu switches between English and Japanese', true);

    await vscode.workspace
      .getConfiguration('sharpdeps')
      .update('analysisMode', 'semantic', vscode.ConfigurationTarget.Global);
    await vscode.commands.executeCommand(
      'sharpdeps.showDependencyMap',
      vscode.Uri.file(
        path.join(repo, 'tests', 'fixtures', 'semantic-baseline', 'SemanticBaseline.sln')
      )
    );
    frame = await mapFrame();
    await expect.poll(async () => (await state()).resultMode, { timeout: 20000 }).toBe('semantic');
    record(
      'Semantic runs through the installed extension',
      api.getCurrentReport()?.mode === 'semantic'
    );
    await frame.locator('#sd-granularity').selectOption('type');
    await frame.locator('#sd-view-graph').click();
    const report = api.getCurrentReport();
    const generatedRelation = report.relations.find(
      (relation) =>
        relation.generatedEvidenceCount > 0 && relation.sourceEntityId !== relation.targetEntityId
    );
    await frame
      .locator(`g.edge[data-id="${generatedRelation.id}"]`)
      .press('Enter', { timeout: 20000 });
    await expect(frame.locator('.sd-evidence-item')).not.toHaveCount(0);
    record('real Semantic edge supplies evidence', true);
    await expect(frame.locator(`g.edge[data-id="${generatedRelation.id}"]`)).toHaveCSS(
      'outline-style',
      'none'
    );
    record('focused graph edges use their path instead of a clipped rectangular outline', true);
    const detailsSplitter = frame.getByRole('separator', { name: 'Resize details pane' });
    if (await detailsSplitter.isVisible()) {
      const detailsWidth = (await frame.locator('.sd-inspector').boundingBox()).width;
      await detailsSplitter.press('ArrowLeft');
      await expect
        .poll(async () => (await frame.locator('.sd-inspector').boundingBox()).width)
        .toBe(detailsWidth + 16);
      await detailsSplitter.press('ArrowRight');
      await expect
        .poll(async () => (await frame.locator('.sd-inspector').boundingBox()).width)
        .toBe(detailsWidth);
      record('details splitter changes the right pane width in both directions', true);
    }
    await frame
      .locator('#app')
      .screenshot({ path: path.join(repo, '.local', 'review-20260927', 'workbench-semantic.png') });
    await frame.getByRole('button', { name: 'Open in editor', exact: true }).first().click();
    await expect
      .poll(() => vscode.window.activeTextEditor?.document.uri.scheme, { timeout: 10000 })
      .toBe('sharpdeps-generated');
    record('generated evidence opens the read-only document provider', true);
    await vscode.commands.executeCommand('workbench.action.previousEditor');
    frame = await mapFrame();
    const beforeCancel = api.getCurrentAnalysisId();
    await frame.locator('#sd-analyze').click();
    await expect(frame.locator('#sd-stop')).toBeEnabled();
    await frame.locator('#sd-stop').click();
    await expect.poll(async () => (await state()).status).toBe('cancelled');
    record(
      'stop preserves the previous registered result',
      api.getCurrentAnalysisId() === beforeCancel
    );
    await frame.locator('#sd-tab-analysis').click();
    await expect(frame.getByText('Target frameworks:', { exact: false })).toBeVisible();
    record('profile and per-project TFMs are available', true);

    const source = vscode.Uri.file(
      path.join(repo, 'tests/fixtures/semantic-baseline/src/Application/OrderService.cs')
    );
    const editor = await vscode.window.showTextDocument(source);
    editor.selection = new vscode.Selection(4, 22, 4, 22);
    await vscode.commands.executeCommand('sharpdeps.showTypeDependencies');
    frame = await mapFrame();
    await expect.poll(async () => (await state()).scope.kind).toBe('dependencies');
    await expect(frame.getByRole('button', { name: /^Outgoing:/ }).first()).toBeVisible();
    await frame
      .getByRole('button', { name: /^Outgoing:/ })
      .first()
      .press('Enter');
    await frame.getByRole('button', { name: 'Open in editor', exact: true }).first().press('Enter');
    await expect
      .poll(() => vscode.window.activeTextEditor?.document.uri.fsPath)
      .toBe(source.fsPath);
    record('cursor → dependencies → evidence → physical source using keyboard', true);
    await vscode.commands.executeCommand('sharpdeps.showTypeDependents');
    frame = await mapFrame();
    await expect.poll(async () => (await state()).scope.kind).toBe('dependents');
    record(
      'cursor command also reveals dependents without reanalysis',
      api.getCurrentAnalysisId() === beforeCancel
    );
    const linked = vscode.Uri.file(
      path.join(repo, 'tests/fixtures/semantic-baseline/shared/Shared.cs')
    );
    const linkedEditor = await vscode.window.showTextDocument(linked);
    const declarationLine = linkedEditor.document
      .getText()
      .split(/\r?\n/)
      .findIndex((line) => line.includes('class '));
    linkedEditor.selection = new vscode.Selection(declarationLine, 20, declarationLine, 20);
    await vscode.commands.executeCommand('sharpdeps.showTypeDependencies');
    frame = await mapFrame();
    await expect.poll(async () => (await state()).details?.entity?.name).toContain('Shared');
    record('linked source resolves through the declaration index', true);
    const dirtyEditor = await vscode.window.showTextDocument(source);
    await dirtyEditor.edit((edit) =>
      edit.insert(new vscode.Position(0, 0), '// unsaved acceptance edit\n')
    );
    await frame.page().locator('.tab').filter({ hasText: 'SharpDeps' }).first().click();
    frame = await mapFrame();
    await expect.poll(async () => (await state()).status).toBe('stale');
    record('unsaved source changes mark the existing result stale', true);
    await vscode.window.showTextDocument(source);
    await vscode.commands.executeCommand('workbench.action.files.revert');
  } catch (error) {
    record('Analyzer → Host → Webview → Editor', false, String(error?.stack ?? error));
  }

  return finish(vscode, results);
}

async function finish(vscode, results) {
  const failed = results.filter((entry) => !entry.ok);
  const output = process.env.SHARPDEPTS_E2E_REPORT;
  if (output) {
    fs.mkdirSync(path.dirname(output), { recursive: true });
    fs.writeFileSync(
      output,
      `${JSON.stringify(
        {
          checkedAt: new Date().toISOString(),
          vscodeVersion: vscode.version,
          results
        },
        null,
        2
      )}\n`,
      'utf8'
    );
  }

  console.log(`\n${results.length - failed.length}/${results.length} checks passed.`);
  if (failed.length > 0) {
    throw new Error(
      `SharpDeps end-to-end checks failed: ${failed.map((entry) => entry.name).join('; ')}`
    );
  }
}

module.exports = { run };

/** Extra context when the analysis did not register: why it could not run. */
function diagnosticsFor(extension, vscode) {
  const fs = require('node:fs');
  const path = require('node:path');
  const { spawnSync } = require('node:child_process');
  const analyzer = path.join(extension.extensionPath, 'analyzer', 'bin', 'quick', 'code-map.dll');
  const dotnet = spawnSync('dotnet', ['--version'], { encoding: 'utf8' });
  const mode = vscode.workspace.getConfiguration('sharpdeps').get('analysisMode', 'quick');
  return [
    `analyzer=${fs.existsSync(analyzer) ? 'found' : 'missing'}`,
    `dotnet=${dotnet.status === 0 ? dotnet.stdout.trim() : `unavailable (${dotnet.error?.code ?? dotnet.status})`}`,
    `mode=${mode}`
  ].join(', ');
}
