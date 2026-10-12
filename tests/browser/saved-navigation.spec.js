// Browser fixture scope: host messages are supplied; no QueryHost/VSCode/clipboard proof.
const { test, expect } = require('@playwright/test');
const entityId = 'ty_1111111111111111';
const hostile = '<img src=x onerror="window.__sdXss=1">[link](javascript:alert(1))';

async function startSaved(page, name = 'Input') {
  await page.goto('/tests/webview/fixtures/shell-fixture.html');
  await page.evaluate(() => {
    window.__sdXss = 0;
    window.dispatchEvent(
      new MessageEvent('message', {
        data: {
          type: 'analysisComplete',
          analysisId: 'an_0123456789abcdef',
          target: { name: 'Saved', relativePath: 'Input.csproj' },
          queryMetadata: {
            provider: 'savedIndex',
            workspaceId: 'workspace',
            snapshotId: 'snapshot',
            generation: 3,
            variantIds: ['variant'],
            coverage: 'Partial',
            freshness: 'unverified',
            truncated: false,
            truncationReasons: [],
            diagnostics: [],
            returnedCount: 1,
            totalKind: 'returned',
            candidateCount: 0,
            unresolvedCount: 0
          }
        }
      })
    );
  });
  return receive(page, 'getProjection', 'projection', { name });
}

async function messages(page) {
  return (await page.locator('#app').getAttribute('data-host-messages'))
    .split('\n')
    .map(JSON.parse);
}
async function receive(page, requestType, replyType, { name = 'Input' } = {}) {
  await expect
    .poll(async () => (await messages(page)).filter((m) => m.type === requestType).length)
    .toBeGreaterThan(0);
  const request = (await messages(page)).filter((m) => m.type === requestType).at(-1);
  await page.evaluate(
    ({ request, replyType, name, entityId }) => {
      const entity = {
        id: entityId,
        name,
        granularity: 'type',
        kind: 'Type',
        certainty: 'Resolved'
      };
      const queryMetadata = window.sharpdepsApp.getState().queryMetadata;
      const body =
        replyType === 'projection'
          ? {
              projection: {
                granularity: request.granularity,
                scope: request.scope,
                nodes: [entity],
                edges: [],
                queryMetadata
              }
            }
          : { entity, dependencies: [], dependents: [], queryMetadata };
      window.dispatchEvent(
        new MessageEvent('message', {
          data: {
            type: replyType,
            requestId: request.requestId,
            analysisId: request.analysisId,
            ...body
          }
        })
      );
    },
    { request, replyType, name, entityId }
  );
  return request;
}

test('saved Back keyboard path waits for a fresh projection token before Copy', async ({
  page
}) => {
  const initial = await startSaved(page);
  await page.locator('#sd-view-table').click();
  await page.locator('.sd-table tbody tr').first().click();
  await receive(page, 'getEntityDetails', 'details');
  await expect(page.locator('.sd-inspector')).not.toContainText('Loading details...');
  const loadingStates = [];
  await page.getByRole('button', { name: 'Explore dependencies', exact: true }).click();
  await expect(page.locator('#sd-copy')).toBeDisabled();
  const scoped = await receive(page, 'getProjection', 'projection');
  loadingStates.push(
    (await page.locator('.sd-inspector').textContent()).includes('Loading details...')
  );
  expect(scoped.scope).toMatchObject({ kind: 'dependencies', id: entityId });
  await expect(page.locator('#sd-copy')).toBeEnabled();
  await page.locator('#sd-back').focus();
  await page.locator('#sd-back').press('Space');
  await expect(page.locator('#sd-copy')).toBeDisabled();
  await expect
    .poll(
      async () =>
        (await messages(page)).filter((m) => m.type === 'getProjection').at(-1)?.scope.kind
    )
    .toBe('root');
  const returned = await receive(page, 'getProjection', 'projection');
  loadingStates.push(
    (await page.locator('.sd-inspector').textContent()).includes('Loading details...')
  );
  expect(returned.requestId).not.toBe(initial.requestId);
  expect(returned.requestId).not.toBe(scoped.requestId);
  await expect(page.locator('#sd-copy')).toBeEnabled();
  await page.locator('#sd-copy').click();
  const copy = (await messages(page)).filter((m) => m.type === 'copyContext').at(-1);
  expect(copy).toMatchObject({ projectionRequestId: returned.requestId, scope: { kind: 'root' } });
  expect((await messages(page)).filter((m) => m.type === 'getEntityDetails')).toHaveLength(1);
  expect(loadingStates).toEqual([false, false]);
});

test('saved hostile display text stays literal at narrow width', async ({ page }) => {
  await page.setViewportSize({ width: 360, height: 740 });
  await startSaved(page, hostile);
  await page.locator('#sd-view-table').click();
  await expect(page.locator('.sd-table')).toContainText(hostile);
  await page.locator('.sd-table tbody tr').first().click();
  await receive(page, 'getEntityDetails', 'details', { name: hostile });
  await expect(page.locator('.sd-inspector')).toContainText(hostile);
  await expect(
    page.locator('#app img[src="x"], #app [onerror], #app [onload], #app a[href^="javascript:"]')
  ).toHaveCount(0);
  expect(await page.evaluate(() => window.__sdXss)).toBe(0);
  expect(
    await page
      .locator('.sd-inspector-body')
      .evaluate((element) => element.scrollWidth <= element.clientWidth)
  ).toBe(true);
});
