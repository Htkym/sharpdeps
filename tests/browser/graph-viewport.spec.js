const { test, expect } = require('@playwright/test');

test('long horizontal graphs keep readable text and use the available viewport height', async ({
  page
}) => {
  await page.setViewportSize({ width: 1440, height: 900 });
  await page.goto('/tests/webview/fixtures/shell-fixture.html');
  await page.evaluate(async () => {
    const app = window.sharpdepsApp;
    app.dispatch({
      type: 'analysisComplete',
      analysisId: 'an_0000000000000001',
      mode: 'semantic',
      completeness: 'completeWithinScope'
    });
    const nodes = Array.from({ length: 12 }, (_, i) => ({
      id: `ty_${i.toString(16).padStart(16, '0')}`,
      name: `CustomerService${i}`,
      granularity: 'type'
    }));
    const edges = nodes.slice(1).map((node, i) => ({
      id: `rel_${i.toString(16).padStart(16, '0')}`,
      sourceId: nodes[i].id,
      targetId: node.id,
      basis: 'symbolResolved',
      kinds: ['typeUse'],
      evidenceCount: 1,
      inCycle: false
    }));
    app.dispatch({
      type: 'projectionReceived',
      projection: {
        analysisId: 'an_0000000000000001',
        granularity: 'type',
        scope: { kind: 'root' },
        nodes,
        edges,
        totalNodeCount: nodes.length,
        totalEdgeCount: edges.length,
        truncated: false
      }
    });
    await app.export('svg');
  });
  const geometry = () =>
    page.evaluate(() => {
      const viewport = document.querySelector('.graph-viewport');
      const svg = document.querySelector('.graph-svg');
      return {
        zoom: Number(svg.getAttribute('width')) / svg.viewBox.baseVal.width,
        viewportHeight: viewport.clientHeight,
        svgHeight: Number(svg.getAttribute('height')),
        scrollWidth: viewport.scrollWidth,
        viewportWidth: viewport.clientWidth
      };
    });
  const initial = await geometry();
  expect(initial.zoom).toBeGreaterThanOrEqual(1);
  expect(initial.scrollWidth).toBeGreaterThan(initial.viewportWidth);
  expect(initial.svgHeight).toBeLessThan(initial.viewportHeight);
  // Scrolling to the last node still leaves its shape-based focus indicator visible.
  await page.locator('g.node').last().focus();
  await expect(page.locator('g.node').last()).toBeInViewport();
  await expect(page.locator('g.node').last()).toHaveCSS('outline-style', 'none');
  await page.screenshot({ path: '.local/browser-results/horizontal-readable.png' });
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  await expect
    .poll(async () => (await geometry()).scrollWidth <= (await geometry()).viewportWidth)
    .toBe(true);
  await page.getByRole('button', { name: 'Zoom in', exact: true }).click();
  const manual = (await geometry()).zoom;
  await page.setViewportSize({ width: 1300, height: 800 });
  await page.evaluate(() => new Promise(requestAnimationFrame));
  expect((await geometry()).zoom).toBeCloseTo(manual, 2);
  await page.getByRole('combobox', { name: 'Direction', exact: true }).selectOption('DOWN');
  await page.evaluate(() => window.sharpdepsApp.export('svg'));
  const vertical = await geometry();
  expect(vertical.svgHeight).toBeLessThanOrEqual(vertical.viewportHeight);
  expect(vertical.zoom).toBeLessThan(1);
  const centerOffset = () =>
    page.locator('.graph-viewport').evaluate((viewport) => {
      const svg = viewport.querySelector('.graph-svg').getBoundingClientRect();
      const area = viewport.getBoundingClientRect();
      return svg.x + svg.width / 2 - area.x - viewport.clientWidth / 2;
    });
  expect(Math.abs(await centerOffset())).toBeLessThan(1);
  await page.screenshot({ path: '.local/browser-results/vertical-centered.png' });
  await page.setViewportSize({ width: 1440, height: 900 });
  expect(Math.abs(await centerOffset())).toBeLessThan(1);
  await page.setViewportSize({ width: 1200, height: 900 });
  // Cross from centered content to a scrolling canvas while retaining the center anchor.
  await page.locator('#sd-zoom-percent').fill('600');
  await page.locator('#sd-zoom-percent').dispatchEvent('change');
  const enlarged = await geometry();
  expect(enlarged.scrollWidth).toBeGreaterThan(enlarged.viewportWidth);
  expect(Math.abs(await centerOffset())).toBeLessThan(10);
  await page.locator('.graph-viewport').evaluate((viewport) => {
    viewport.scrollLeft = 0;
    viewport.scrollTop = 0;
  });
  await expect(page.locator('g.node').first()).toBeInViewport();
  await page.getByRole('button', { name: 'Fit', exact: true }).click();
  expect(Math.abs(await centerOffset())).toBeLessThan(1);
  const representativeId = await page.evaluate(async () => {
    const app = window.sharpdepsApp;
    const projection = app.getState().projection;
    const first = projection.edges[0];
    const alias = 'rel_ffffffffffffffff';
    app.dispatch({
      type: 'projectionReceived',
      projection: {
        ...projection,
        edges: [
          {
            ...first,
            basis: 'projectDeclared',
            underlyingRelationIds: [first.id, alias],
            underlyingRelations: [
              {
                id: first.id,
                basis: 'projectDeclared',
                kinds: ['projectReference'],
                evidenceCount: 1
              },
              { id: alias, basis: 'usingInferred', kinds: ['typeUse'], evidenceCount: 1 }
            ]
          },
          ...projection.edges.slice(1)
        ]
      }
    });
    app.dispatch({ type: 'relationSelected', relationId: alias });
    app.dispatch({ type: 'evidenceReceived', relationId: alias, total: 0, items: [] });
    await app.export('svg');
    return first.id;
  });
  const mergedEdge = page.locator(`g.edge[data-id="${representativeId}"]`);
  await expect(mergedEdge).toHaveAttribute('aria-selected', 'true');
  await expect(page.locator('.sd-inspector-body')).toContainText('Quick (inferred)');
  await expect(page.locator('.sd-inspector-body')).not.toContainText(
    'pages the representative relation'
  );
  expect(await page.evaluate(() => window.sharpdepsApp.getState().selection.relationId)).toBe(
    'rel_ffffffffffffffff'
  );
  // Selecting the drawn connection still returns to its representative evidence.
  await mergedEdge.press('Enter');
  expect(await page.evaluate(() => window.sharpdepsApp.getState().selection.relationId)).toBe(
    representativeId
  );
});
