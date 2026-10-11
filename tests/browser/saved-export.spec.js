const { test, expect } = require('@playwright/test');

test('saved copy and JSON reach the host only for the received projection', async ({ page }) => {
  const errors = [];
  page.on('pageerror', (error) => errors.push(String(error)));
  await page.goto('/tests/webview/fixtures/shell-fixture.html');
  await page.evaluate(() => {
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
            truncated: true,
            truncationReasons: ['PAGE_LIMIT'],
            diagnostics: [],
            returnedCount: 0,
            totalKind: 'returned',
            candidateCount: 0,
            unresolvedCount: 0
          }
        }
      })
    );
  });
  const messages = () =>
    page
      .locator('#app')
      .getAttribute('data-host-messages')
      .then((log) => log.split('\n').map(JSON.parse));
  const outputs = async () =>
    (await messages()).filter((m) => ['copyContext', 'export'].includes(m.type));
  const receiveProjection = () =>
    page.evaluate(() => {
      const requests = document
        .getElementById('app')
        .dataset.hostMessages.split('\n')
        .map(JSON.parse);
      const request = requests.filter((message) => message.type === 'getProjection').at(-1);
      window.dispatchEvent(
        new MessageEvent('message', {
          data: {
            type: 'projection',
            requestId: request.requestId,
            analysisId: request.analysisId,
            projection: {
              granularity: request.granularity,
              scope: request.scope,
              nodes: [],
              edges: [],
              queryMetadata: window.sharpdepsApp.getState().queryMetadata
            }
          }
        })
      );
      return request.requestId;
    });
  await expect(page.locator('#sd-copy')).toBeDisabled();
  await expect(page.locator('#sd-export-toggle')).toBeDisabled();
  await page.evaluate(() => window.sharpdepsApp.export('json'));
  expect(await outputs()).toHaveLength(0);

  const token = await receiveProjection();
  await expect(page.locator('#sd-copy')).toBeEnabled();
  await page.locator('#sd-copy').click();
  await page.locator('#sd-export-toggle').click();
  await expect(page.locator('#sd-export-svg')).toBeHidden();
  await expect(page.locator('#sd-export-png')).toBeHidden();
  await expect(page.locator('#sd-export-mermaid')).toBeHidden();
  await expect(page.locator('#sd-export-json')).toBeVisible();
  await expect(page.locator('label').filter({ hasText: 'Image: legend' })).toBeHidden();
  await page.locator('#sd-export-json').click();
  await page.evaluate(() => window.sharpdepsApp.export('json', true));
  const sent = await outputs();
  expect(sent).toHaveLength(3);
  expect(sent.map((message) => message.type)).toEqual(['copyContext', 'export', 'export']);
  expect(sent[2].copy).toBe(true);
  for (const message of sent) {
    expect(message.projectionRequestId).toBe(token);
    expect(message.analysisId).toBe('an_0123456789abcdef');
    expect(message.data).toBeUndefined();
  }
  for (const format of ['svg', 'png', 'mermaid'])
    await page.evaluate((format) => window.sharpdepsApp.export(format), format);
  expect(await outputs()).toHaveLength(3);

  await page.locator('.sd-search').fill('Changed');
  await expect(page.locator('#sd-copy')).toBeDisabled();
  await expect(page.locator('#sd-export-toggle')).toBeDisabled();
  await page.evaluate(() => window.sharpdepsApp.export('json'));
  expect(await outputs()).toHaveLength(3);
  const newToken = await receiveProjection();
  expect(newToken).not.toBe(token);
  await page.locator('#sd-copy').click();
  expect((await outputs()).at(-1)).toMatchObject({
    projectionRequestId: newToken,
    search: 'Changed'
  });
  expect(errors).toEqual([]);
});
