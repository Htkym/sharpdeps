const { test, expect } = require('@playwright/test');

for (const [width, theme] of [
  [1440, 'vscode-dark'],
  [900, 'vscode-light'],
  [1024, 'vscode-high-contrast'],
  [700, 'vscode-high-contrast-light'],
  [360, 'vscode-high-contrast']
]) {
  test(`workbench controls and exports at ${width}px (${theme})`, async ({ page }) => {
    await page.setViewportSize({ width, height: { 360: 740, 700: 800, 1024: 768 }[width] ?? 900 });
    await page.emulateMedia({
      colorScheme: theme.includes('light') ? 'light' : 'dark',
      forcedColors: theme.includes('high-contrast') ? 'active' : 'none'
    });
    const errors = [];
    page.on('pageerror', (error) => errors.push(String(error)));
    await page.goto('/tests/webview/fixtures/shell-fixture.html');
    await page.evaluate((theme) => {
      document.body.className = theme;
      // A user theme may provide a blue button border; neutral actions must remain unframed.
      document.body.style.setProperty('--vscode-button-border', '#007acc');
      if (theme.includes('light')) {
        for (const [name, value] of Object.entries({
          'editor-background': '#ffffff',
          foreground: '#333333',
          'editor-foreground': '#333333',
          descriptionForeground: '#616161',
          'editorWidget-background': '#f3f3f3',
          'panel-border': '#d4d4d4',
          'input-background': '#ffffff',
          'input-foreground': '#333333',
          'input-border': '#cecece',
          'button-secondaryBackground': '#e5e5e5',
          'button-secondaryForeground': '#333333',
          'button-secondaryHoverBackground': '#d8d8d8',
          'inputOption-activeBackground': '#dddddd',
          'inputOption-activeForeground': '#333333',
          'list-hoverBackground': '#eeeeee',
          'list-inactiveSelectionBackground': '#e4e6f1',
          'textCodeBlock-background': '#f2f2f2',
          'editorWarning-foreground': '#8a5a00'
        }))
          document.body.style.setProperty(`--vscode-${name}`, value);
      }
      const app = window.sharpdepsApp;
      app.dispatch({ type: 'targetSelected', name: 'Example.sln', relativePath: 'Example.sln' });
      app.dispatch({
        type: 'analysisComplete',
        analysisId: 'an_0000000000000001',
        mode: 'semantic',
        completeness: 'completeWithinScope',
        coverage: { discovered: 1, loaded: 1, analyzed: 1, failed: 0, skipped: 0 },
        capabilities: {
          typeGraph: true,
          evidence: true,
          generatedDocuments: true,
          cycleWitness: true,
          search: true
        }
      });
      app.dispatch({ type: 'granularityChanged', granularity: 'type' });
      app.dispatch({
        type: 'projectionReceived',
        projection: {
          analysisId: 'an_0000000000000001',
          granularity: 'type',
          scope: { kind: 'root' },
          truncated: false,
          totalNodeCount: 2,
          totalEdgeCount: 1,
          nodes: [
            {
              id: 'ty_0000000000000001',
              name: 'Customer',
              projectName: 'Customer',
              granularity: 'type',
              dependencyCount: 1,
              dependentCount: 0
            },
            {
              id: 'ty_0000000000000002',
              name: 'Order',
              granularity: 'type',
              dependencyCount: 0,
              dependentCount: 1
            }
          ],
          edges: [
            {
              id: 'rel_0000000000000001',
              sourceId: 'ty_0000000000000001',
              targetId: 'ty_0000000000000002',
              basis: 'symbolResolved',
              kinds: ['typeUse'],
              evidenceCount: 2,
              inCycle: false
            }
          ]
        }
      });
      // Export while the worker is still laying out: it must await this projection.
      return app.export('svg');
    }, theme);
    const messages = () =>
      page
        .locator('#app')
        .getAttribute('data-host-messages')
        .then((value) => value.split('\n').map((line) => JSON.parse(line)));
    const svg = (await messages()).find(
      (message) => message.type === 'export' && message.format === 'svg'
    );
    expect(svg.data).toContain('<svg');
    expect(svg.data).toContain('Customer');
    expect(svg.data).toContain('SharpDeps: Example.sln');
    expect(svg.data).toContain('Profile: Debug');
    expect(svg.data).toContain('Legend:');
    expect(svg.granularity).toBe('type');
    if (!theme.includes('high-contrast'))
      await expect(page.locator('#sd-copy')).toHaveCSS('border-top-color', 'rgba(0, 0, 0, 0)');
    expect(await page.locator('g.node').first().locator('.node-sublabel').textContent()).not.toBe(
      'Customer'
    );
    await page.evaluate(() => window.sharpdepsApp.export('png'));
    expect(
      (await messages()).find((message) => message.type === 'export' && message.format === 'png')
        .data
    ).toMatch(/^data:image\/png;base64,/);
    const treeToggle = page.locator('.sd-tree-toggle').first();
    await treeToggle.focus();
    await treeToggle.press('ArrowRight');
    await expect(treeToggle).toHaveAttribute('aria-expanded', 'true');
    await expect(treeToggle).toBeFocused();
    await treeToggle.press('ArrowLeft');
    await expect(treeToggle).toHaveAttribute('aria-expanded', 'false');
    await expect(treeToggle).toHaveCSS('background-color', /rgba\(\d+, \d+, \d+, 0\)/);
    await expect(treeToggle).toHaveCSS('border-top-width', '0px');
    await page.locator('#sd-tab-structure').focus();
    await page.keyboard.press('ArrowRight');
    await expect(page.locator('#sd-tab-cycles')).toHaveAttribute('aria-selected', 'true');
    await page.keyboard.press('Home');
    await expect(page.locator('#sd-tab-structure')).toHaveAttribute('aria-selected', 'true');
    await page.evaluate(() => {
      const app = window.sharpdepsApp;
      app.dispatch({ type: 'entitySelected', entityId: 'ty_0000000000000001' });
      app.dispatch({
        type: 'detailsReceived',
        entityId: 'ty_0000000000000001',
        entity: app.getState().projection.nodes[0],
        dependencies: [app.getState().projection.nodes[1]],
        dependents: []
      });
    });
    await expect(page.locator('.sd-inspector-actions')).toBeVisible();
    await expect(page.locator('.sd-inspector-relations li')).toHaveCount(1);
    await expect(page.getByRole('button', { name: 'Open declaration', exact: true })).toBeVisible();
    await page.screenshot({ path: `.local/browser-results/inspector-node-${width}.png` });
    await page.evaluate(() => {
      const app = window.sharpdepsApp;
      app.dispatch({ type: 'relationSelected', relationId: 'rel_0000000000000001' });
      app.dispatch({
        type: 'evidenceReceived',
        relationId: 'rel_0000000000000001',
        total: 2,
        items: [0, 1].map((n) => ({
          id: `ev_${String(n + 1).padStart(16, '0')}`,
          kind: 'calls',
          confidence: 'resolved',
          origin: 'source',
          documentPath: 'src/Services/CustomerService.cs',
          physicalSpan: { startLine: 12 + n, startCharacter: 8 },
          snippet: 'customer.GetOrder();'
        }))
      });
    });
    await expect(page.locator('.sd-evidence-item')).toHaveCount(2);
    await expect(
      page
        .locator('.sd-evidence-actions')
        .first()
        .getByRole('button', { name: 'Open in editor', exact: true })
    ).toBeVisible();
    expect(
      await page
        .locator('.sd-inspector-body')
        .evaluate((element) => element.scrollWidth <= element.clientWidth)
    ).toBe(true);
    await page.screenshot({ path: `.local/browser-results/inspector-evidence-${width}.png` });
    await expect(page.locator('g.node.selected')).toHaveCount(0);
    await expect(page.locator('g.edge.selected')).toHaveAttribute('aria-selected', 'true');
    // Metadata can change without changing graph IDs or requiring a new layout.
    await page.evaluate(async () => {
      const app = window.sharpdepsApp;
      const projection = structuredClone(app.getState().projection);
      projection.edges[0].evidenceCount = 7;
      app.dispatch({ type: 'projectionReceived', projection });
      await app.export('svg');
    });
    await expect(page.locator('g.edge')).toHaveAttribute('aria-label', /7 evidence record\(s\)/);
    await page.getByRole('button', { name: 'Close', exact: true }).click();
    const edgeTrigger = page.locator('g.edge').first();
    await edgeTrigger.press('Enter');
    await page.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(edgeTrigger).toBeFocused();
    await expect(edgeTrigger).toHaveCSS('outline-style', 'none');
    await expect(edgeTrigger.locator('.edge-line')).toHaveCSS('stroke-width', '3.5px');
    await page.keyboard.press('Tab');
    await page.locator('g.node').first().focus();
    await expect(page.locator('g.node').first()).toHaveCSS('outline-style', 'none');
    await expect(page.locator('g.node').first().locator('rect')).toHaveCSS('stroke-width', '3px');
    const viewport = page.locator('.graph-viewport');
    await viewport.focus();
    await expect(viewport).toHaveCSS('outline-offset', '-2px');
    const viewportBounds = await viewport.boundingBox();
    const footerBounds = await page.locator('.sd-footer').boundingBox();
    const controlsBounds = await page.locator('.sd-graph-controls').boundingBox();
    expect(footerBounds.y - controlsBounds.y - controlsBounds.height).toBeLessThan(20);
    expect(controlsBounds.y - viewportBounds.y - viewportBounds.height).toBeLessThan(20);
    if (width === 1440) {
      await edgeTrigger.press('Enter');
      const splitter = page.getByRole('separator', { name: 'Resize details pane' });
      const bounds = await splitter.boundingBox();
      const start = bounds.x + bounds.width / 2;
      await page.locator('.sd-inspector-metadata summary').click();
      await page.evaluate(() => {
        window.inspectorResizeMutations = [];
        window.inspectorResizeObserver = new MutationObserver((records) =>
          window.inspectorResizeMutations.push(...records)
        );
        window.inspectorResizeObserver.observe(document.querySelector('.sd-inspector-body'), {
          childList: true,
          subtree: true
        });
      });
      await page.mouse.move(start, bounds.y + 50);
      await page.mouse.down();
      for (const delta of [20, 40, 80, 40, 0]) {
        await page.mouse.move(start - delta, bounds.y + 50);
        await expect
          .poll(async () => (await page.locator('.sd-inspector').boundingBox()).width)
          .toBe(320 + delta);
      }
      await page.mouse.up();
      expect(await page.evaluate(() => window.inspectorResizeMutations.length)).toBe(0);
      await page.evaluate(() => window.inspectorResizeObserver.disconnect());
      await expect(page.locator('.sd-inspector-metadata')).toHaveAttribute('open', '');
      await expect(splitter).not.toHaveAttribute('data-dragging', 'true');
      await splitter.focus();
      await splitter.press('ArrowLeft');
      expect((await page.locator('.sd-inspector').boundingBox()).width).toBe(336);
      await splitter.press('ArrowRight');
      expect((await page.locator('.sd-inspector').boundingBox()).width).toBe(320);
      await page.getByRole('button', { name: 'Close', exact: true }).click();
    }
    await page.evaluate(() =>
      window.sharpdepsApp.dispatch({
        type: 'treeReceived',
        parentId: 'root',
        items: [{ id: 'prj_0000000000000001', name: 'Project', granularity: 'project' }],
        total: 1
      })
    );
    const projectTrigger = page.locator('.sd-tree-row .sd-node-item').first();
    await projectTrigger.click();
    await page.evaluate(() =>
      window.sharpdepsApp.dispatch({
        type: 'detailsReceived',
        entityId: 'prj_0000000000000001',
        entity: { id: 'prj_0000000000000001', name: 'Project', granularity: 'project' },
        dependencies: [],
        dependents: []
      })
    );
    await page.getByRole('button', { name: 'Close', exact: true }).click();
    await expect(projectTrigger).toBeFocused();
    const mapBounds = await page.locator('.sd-map-host').boundingBox();
    const toolbarBounds = await page.locator('.sd-map-toolbar').boundingBox();
    const graphBar = page.locator('.sd-graph-controls');
    await expect(graphBar).toBeVisible();
    const barBounds = await graphBar.boundingBox();
    expect(barBounds.y).toBeGreaterThanOrEqual(mapBounds.y + mapBounds.height);
    expect(barBounds.height).toBeLessThan(65);
    await page.getByText('Filters', { exact: true }).click();
    await expectPopover(page, 'filters', mapBounds, toolbarBounds);
    await page.locator('#sd-filters-toggle').press('Escape');
    await page.locator('#sd-spacing-toggle').click();
    await expectPopover(page, 'spacing', mapBounds, toolbarBounds);
    const spacingBounds = await page.locator('#sd-spacing-panel').boundingBox();
    const spacingTriggerBounds = await page.locator('#sd-spacing-toggle').boundingBox();
    expect(spacingBounds.y + spacingBounds.height).toBeCloseTo(spacingTriggerBounds.y - 4, 1);
    expect(
      await page
        .locator('#sd-spacing-panel')
        .evaluate((panel) => panel.scrollHeight - panel.clientHeight)
    ).toBeLessThanOrEqual(1);
    await page.screenshot({ path: `.local/browser-results/spacing-above-${width}.png` });
    await page.keyboard.press('Escape');
    await expect(page.locator('#sd-spacing-panel')).toBeHidden();
    await expect(page.locator('#sd-spacing-toggle')).toBeFocused();
    await page.screenshot({ path: `.local/browser-results/graph-controls-${width}.png` });
    await page.getByRole('combobox', { name: 'Direction', exact: true }).selectOption('DOWN');
    await page.evaluate(() => window.sharpdepsApp.export('svg'));
    expect(await page.evaluate(() => window.sharpdepsApp.getState().layout.direction)).toBe('DOWN');
    const vertical = await graphPositions(page);
    expect(vertical[1].y).toBeGreaterThan(vertical[0].y);
    expect(Math.abs(vertical[1].x - vertical[0].x)).toBeLessThan(1);
    await expect
      .poll(() =>
        page.locator('.graph-viewport').evaluate((viewport) => {
          const graph = viewport.querySelector('.graph-svg').getBoundingClientRect();
          const area = viewport.getBoundingClientRect();
          return Math.abs(graph.x + graph.width / 2 - area.x - viewport.clientWidth / 2);
        })
      )
      .toBeLessThan(1);
    await page.getByRole('combobox', { name: 'Direction', exact: true }).selectOption('RIGHT');
    await page.evaluate(() => window.sharpdepsApp.export('svg'));
    const horizontal = await graphPositions(page);
    expect(horizontal[1].x).toBeGreaterThan(horizontal[0].x);
    expect(Math.abs(horizontal[1].y - horizontal[0].y)).toBeLessThan(1);
    expect(
      await page.locator('.graph-svg').evaluate((svg) => getComputedStyle(svg).marginLeft)
    ).toBe('0px');
    await page.getByRole('button', { name: 'Zoom in', exact: true }).press('Enter');
    await page.locator('#sd-spacing-toggle').click();
    await page.getByLabel('Node spacing', { exact: true }).fill('60');
    await page.getByLabel('Node spacing', { exact: true }).dispatchEvent('change');
    await expect
      .poll(() => page.evaluate(() => window.sharpdepsApp.getState().layout.nodeSpacing))
      .toBe(60);
    await page.getByRole('button', { name: 'Cancel layout', exact: true }).click();
    await expect(page.locator('.sd-map-content')).toContainText('Layout cancelled');
    await expect(page.locator('.sd-table')).toBeVisible();
    await page.getByRole('button', { name: 'Retry layout', exact: true }).click();
    await expect(page.locator('.sd-graph')).toBeVisible();
    await page.locator('#sd-spacing-toggle').press('Escape');
    await page.locator('#sd-view-table').click();
    await expect(page.locator('.sd-menu')).toHaveCount(1);
    await expect(page.locator('.sd-menu')).toBeHidden();
    await expect(page.locator('.sd-table tbody tr')).toHaveCount(2);
    await page.getByText('Filters', { exact: true }).click();
    await page.screenshot({ path: `.local/browser-results/filters-${width}.png` });
    await page.getByLabel('Include tests', { exact: true }).uncheck();
    expect(
      (await messages()).findLast((message) => message.type === 'getProjection').filters
        .includeTests
    ).toBe(false);
    await page.getByText('Entity kinds', { exact: true }).click();
    await page
      .getByRole('group', { name: 'Entity kinds', exact: true })
      .getByRole('checkbox', { name: 'class', exact: true })
      .check();
    await expect(page.locator('.sd-map-content .sd-empty')).toContainText(
      'No match for the current search or filters.'
    );
    await page
      .getByRole('group', { name: 'Entity kinds', exact: true })
      .getByRole('checkbox', { name: 'class', exact: true })
      .uncheck();
    await page.getByText('Filters', { exact: true }).click();
    await expect(page.locator('.sd-table tbody tr')).toHaveCount(2);
    await page.locator('.sd-sort').first().click();
    await expect(page.locator('.sd-table th').first()).toHaveAttribute('aria-sort', 'descending');
    await page.getByRole('button', { name: 'Export ▾', exact: true }).click();
    await expect(page.locator('.sd-menu')).toBeVisible();
    await page.keyboard.press('Escape');
    await expect(page.locator('.sd-menu')).toBeHidden();
    await page.locator('#sd-mode').selectOption('semantic');
    const profileMapBounds = await page.locator('.sd-map-host').boundingBox();
    const profileToolbarBounds = await page.locator('.sd-map-toolbar').boundingBox();
    await page.getByText('Profile', { exact: true }).click();
    await expectPopover(page, 'profile', profileMapBounds, profileToolbarBounds);
    await page.getByLabel('Configuration', { exact: true }).fill('Release');
    await page.getByLabel('Configuration', { exact: true }).press('Tab');
    await page.locator('#sd-analyze').click();
    await expect(page.locator('#sd-profile-panel')).toBeHidden();
    expect(
      (await messages()).findLast((message) => message.type === 'analyze').profile.configuration
    ).toBe('Release');
    expect(
      await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth)
    ).toBe(true);
    await page.screenshot({
      path: `.local/browser-results/workbench-${width}.png`,
      fullPage: true
    });
    const zoomBeforeHiddenExport = await page.evaluate(
      () => window.sharpdepsApp.getState().camera.zoom
    );
    await page.evaluate(async () => {
      const app = window.sharpdepsApp;
      const projection = structuredClone(app.getState().projection);
      projection.nodes[1].name = 'Analyze';
      projection.nodes[0].kind = 'class';
      projection.nodes[1].kind = 'class';
      projection.nodes[1].projectName = 'class';
      app.dispatch({ type: 'projectionReceived', projection });
      await app.export('svg');
    });
    expect(await page.evaluate(() => window.sharpdepsApp.getState().camera.zoom)).toBe(
      zoomBeforeHiddenExport
    );
    const beforeAnalysisRequests = (await messages()).filter(
      (message) => message.type === 'analyze'
    ).length;
    const beforeLanguage = await page.evaluate(() => {
      window.graphBeforeLanguage = document.querySelector('.graph-svg');
      const state = window.sharpdepsApp.getState();
      return {
        analysisId: state.analysisId,
        selection: state.selection,
        camera: state.camera,
        layout: state.layout
      };
    });
    await page.getByRole('button', { name: 'Switch to Japanese', exact: true }).click();
    expect(
      await page.evaluate(() => {
        const state = window.sharpdepsApp.getState();
        return {
          analysisId: state.analysisId,
          selection: state.selection,
          camera: state.camera,
          layout: state.layout
        };
      })
    ).toEqual(beforeLanguage);
    expect(
      await page.evaluate(() => window.graphBeforeLanguage === document.querySelector('.graph-svg'))
    ).toBe(true);
    await expect(page.locator('#app')).toHaveAttribute('lang', 'ja');
    expect((await messages()).filter((message) => message.type === 'analyze')).toHaveLength(
      beforeAnalysisRequests
    );
    await expect(page.locator('#sd-analyze')).toHaveText('解析');
    await expect(page.locator('.sd-table th').first()).toHaveText(/名前/);
    await expect(page.locator('.sd-table-footer')).toContainText('ページ');
    await page.getByRole('button', { name: 'フィルター', exact: true }).click();
    await expect(
      page.getByRole('checkbox', { name: '生成コードを含める', exact: true })
    ).toBeVisible();
    await page.keyboard.press('Escape');
    await page.locator('#sd-view-graph').click();
    await expect(page.locator('g.node .node-label').nth(1)).toHaveText('Analyze');
    await expect(page.locator('g.node .node-sublabel').first()).toHaveText('クラス');
    await expect(page.locator('g.node .node-sublabel').nth(1)).toHaveText('class');
    await page.locator('g.edge').first().press('Enter');
    await expect(page.locator('.sd-inspector-body')).toContainText('参照回数');
    await expect(
      page.getByRole('button', { name: 'エディターで開く', exact: true }).first()
    ).toBeVisible();
    const japaneseExport = await page.evaluate(async () => {
      await window.sharpdepsApp.export('svg');
      return JSON.parse(document.querySelector('#app').dataset.hostMessages.split('\n').at(-1))
        .data;
    });
    expect(japaneseExport).toContain('凡例:');
    expect(japaneseExport).toContain('Customer');
    await page.screenshot({ path: `.local/browser-results/workbench-ja-${width}.png` });
    await page.getByRole('button', { name: '閉じる', exact: true }).click();
    await expect
      .poll(() =>
        page.evaluate(
          () => JSON.parse(localStorage.getItem('sharpdeps.fixture.viewState') ?? '{}').language
        )
      )
      .toBe('ja');
    await page.reload();
    await expect(page.locator('#app')).toHaveAttribute('lang', 'ja');
    await expect(page.locator('#sd-analyze')).toHaveText('解析');
    await page.getByRole('button', { name: '英語に切り替える', exact: true }).click();
    await expect(page.locator('#sd-analyze')).toHaveText('Analyze');
    // The switch itself must not request another analysis (restoring the page may request data).
    expect((await messages()).filter((message) => message.type === 'analyze')).toHaveLength(0);
    expect(beforeLanguage.analysisId).toBe('an_0000000000000001');
    expect(errors).toEqual([]);
  });
}

async function graphPositions(page) {
  return page.locator('g.node').evaluateAll((nodes) =>
    nodes.map((node) => {
      const matrix = node.transform.baseVal.getItem(0).matrix;
      return { x: matrix.e, y: matrix.f };
    })
  );
}

async function expectPopover(page, id, mapBounds, toolbarBounds) {
  const panel = page.locator(`#sd-${id}-panel`);
  await expect(panel).toBeVisible();
  await expect(page.locator(`#sd-${id}-toggle`)).toHaveAttribute('aria-expanded', 'true');
  expect(await page.locator('.sd-map-host').boundingBox()).toEqual(mapBounds);
  expect(await page.locator('.sd-map-toolbar').boundingBox()).toEqual(toolbarBounds);
  const bounds = await panel.boundingBox();
  const viewport = page.viewportSize();
  expect(bounds.x).toBeGreaterThanOrEqual(0);
  expect(bounds.y).toBeGreaterThanOrEqual(0);
  expect(bounds.x + bounds.width).toBeLessThanOrEqual(viewport.width);
  expect(bounds.y + bounds.height).toBeLessThanOrEqual(viewport.height);
  expect(
    await panel.evaluate((element) => {
      const rect = element.getBoundingClientRect();
      return element.contains(document.elementFromPoint(rect.x + 12, rect.y + 12));
    })
  ).toBe(true);
}
