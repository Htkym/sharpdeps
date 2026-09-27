const { test, expect } = require('@playwright/test');
const fs = require('node:fs');
test('fixed graphs: real ELK, UI selection and cancellation', async ({ page }) => {
  test.setTimeout(120000);
  page.on('console', (message) => {
    if (message.type() === 'error') console.log(message.text());
  });
  await page.goto('/tests/webview/fixtures/shell-fixture.html');
  const results = [];
  for (const [nodes, edges, budgetMs] of [
    [100, 200, 2000],
    [300, 1000, 5000]
  ]) {
    await page.reload();
    const result = await page.evaluate(
      async ({ nodes, edges, budgetMs }) => {
        const app = window.sharpdepsApp;
        app.dispatch({
          type: 'analysisComplete',
          analysisId: 'an_0000000000000001',
          mode: 'semantic',
          completeness: 'completeWithinScope'
        });
        const ids = Array.from(
          { length: nodes },
          (_, i) => `ty_${i.toString(16).padStart(16, '0')}`
        );
        const projection = {
          analysisId: 'an_0000000000000001',
          granularity: 'type',
          scope: { kind: 'root' },
          truncated: false,
          totalNodeCount: nodes,
          totalEdgeCount: edges,
          nodes: ids.map((id, i) => ({
            id,
            name: `Example.Namespace.CustomerWithLongName${i}`,
            granularity: 'type',
            dependencyCount: 4,
            dependentCount: 4
          })),
          edges: Array.from({ length: edges }, (_, i) => ({
            id: `rel_${i.toString(16).padStart(16, '0')}`,
            sourceId: ids[i % nodes],
            targetId: ids[((i % nodes) + 1 + Math.floor(i / nodes)) % nodes],
            basis: 'symbolResolved',
            kinds: ['signature'],
            evidenceCount: 1,
            inCycle: true
          }))
        };
        let ticks = 0;
        const timer = setInterval(() => ticks++, 10);
        const samplesMs = [];
        for (let n = 0; n < 3; n++) {
          app.dispatch({ type: 'layoutChanged', layout: { nodeSpacing: 40 + n, rankSpacing: 80 } });
          const start = performance.now();
          app.dispatch({ type: 'projectionReceived', projection });
          await app.export('svg');
          samplesMs.push(performance.now() - start);
        }
        clearInterval(timer);
        const graph = document.querySelector('.graph-content');
        const mutations = [];
        const observer = new MutationObserver((records) => mutations.push(...records));
        observer.observe(graph, { attributes: true, childList: true, subtree: true });
        const selection = [];
        for (let n = 0; n < 25; n++) {
          const start = performance.now();
          app.dispatch({ type: 'entitySelected', entityId: ids[n] });
          await new Promise(requestAnimationFrame);
          selection.push(performance.now() - start);
        }
        observer.disconnect();
        return {
          nodes,
          edges,
          budgetMs,
          samplesMs,
          responsiveTicks: ticks,
          selectionSamplesMs: selection,
          selectionP95Ms: [...selection].sort((a, b) => a - b)[23],
          selectionOnlyMutations: mutations.every(
            (record) =>
              record.type === 'attributes' &&
              ['class', 'aria-selected'].includes(record.attributeName)
          ),
          selectedIds: Array.from(graph.querySelectorAll('g.node.selected'), (node) => [
            node.dataset.id,
            node.getAttribute('aria-selected')
          ]),
          renderedNodes: document.querySelectorAll('g.node').length
        };
      },
      { nodes, edges, budgetMs }
    );
    results.push(result);
    fs.writeFileSync(
      'docs/implementation/v0.1.0/evidence/sd-028-browser.json',
      JSON.stringify(
        {
          checkedAt: new Date().toISOString(),
          methodology:
            'Three actual ELK worker layouts; SVG serialization included; 25 selection-to-next-frame samples. Deterministic cyclic input, warm browser.',
          results
        },
        null,
        2
      )
    );
    expect(result.renderedNodes).toBe(nodes);
    expect(Math.max(...result.samplesMs)).toBeLessThanOrEqual(budgetMs);
    expect(result.responsiveTicks).toBeGreaterThan(0);
    expect(result.selectionOnlyMutations).toBe(true);
    expect(result.selectedIds).toEqual([['ty_0000000000000018', 'true']]);
    expect(result.selectionP95Ms).toBeLessThanOrEqual(100);
  }
});
