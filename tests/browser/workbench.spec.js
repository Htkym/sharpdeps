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
    const errors = [];
    page.on('pageerror', (error) => errors.push(String(error)));
    await page.goto('/tests/webview/fixtures/shell-fixture.html');
    await page.evaluate((theme) => {
      document.body.className = theme;
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
              basis: 'semantic',
              kinds: ['typeUse'],
              evidenceCount: 1,
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
    await page.evaluate(() => window.sharpdepsApp.export('png'));
    expect(
      (await messages()).find((message) => message.type === 'export' && message.format === 'png')
        .data
    ).toMatch(/^data:image\/png;base64,/);
    await page.getByText('Graph controls', { exact: true }).click();
    await page.getByRole('button', { name: 'Zoom in', exact: true }).press('Enter');
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
    await page.getByText('Graph controls', { exact: true }).click();
    await page.locator('#sd-view-table').click();
    await expect(page.locator('.sd-menu')).toHaveCount(1);
    await expect(page.locator('.sd-menu')).toBeHidden();
    await expect(page.locator('.sd-table tbody tr')).toHaveCount(2);
    await page.getByText('Filters', { exact: true }).click();
    await page.getByLabel('Include tests', { exact: true }).uncheck();
    expect(
      (await messages()).findLast((message) => message.type === 'getProjection').filters
        .includeTests
    ).toBe(false);
    await page.getByRole('listbox', { name: 'Entity kinds', exact: true }).selectOption('class');
    await expect(page.locator('.sd-map-content .sd-empty')).toContainText(
      'No match for the current search or filters.'
    );
    await page.getByRole('listbox', { name: 'Entity kinds', exact: true }).selectOption([]);
    await page.getByText('Filters', { exact: true }).click();
    await expect(page.locator('.sd-table tbody tr')).toHaveCount(2);
    await page.locator('#sd-mode').selectOption('semantic');
    await page.getByText('Profile', { exact: true }).click();
    await page.getByLabel('Configuration', { exact: true }).fill('Release');
    await page.getByLabel('Configuration', { exact: true }).press('Tab');
    await page.getByText('Profile', { exact: true }).click();
    await page.locator('#sd-analyze').click();
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
    expect(errors).toEqual([]);
  });
}
