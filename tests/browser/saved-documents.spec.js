// Prepared only: copy into tests/browser after applying the reviewed document-source repair.
// Uses actual viewer messages and keyboard events with fixture host replies.
// Does not prove QueryHost, native source opening, trust, or installed VSIX acceptance.
const { test, expect } = require('@playwright/test');
const doc = {
  id: 'ty_1111111111111111',
  name: '注文の仕様',
  kind: 'Document',
  granularity: 'type',
  certainty: 'Resolved'
};
const member = {
  id: 'ty_2222222222222222',
  name: 'ConfirmOrder',
  kind: 'Member',
  granularity: 'type',
  certainty: 'Resolved'
};

async function messages(page) {
  return (await page.locator('#app').getAttribute('data-host-messages'))
    .split('\n')
    .map(JSON.parse);
}
async function request(page, type, predicate = () => true) {
  await expect
    .poll(async () => (await messages(page)).filter((m) => m.type === type && predicate(m)).length)
    .toBeGreaterThan(0);
  return (await messages(page)).filter((m) => m.type === type && predicate(m)).at(-1);
}
async function reply(page, request, type, fields) {
  await page.evaluate(
    ({ request, type, fields }) => {
      window.dispatchEvent(
        new MessageEvent('message', {
          data: {
            type,
            requestId: request.requestId,
            analysisId: request.analysisId,
            ...fields,
            queryMetadata: window.sharpdepsApp.getState().queryMetadata
          }
        })
      );
    },
    { request, type, fields }
  );
}
async function project(page, request) {
  await page.evaluate(
    ({ request, doc, member }) => {
      window.dispatchEvent(
        new MessageEvent('message', {
          data: {
            type: 'projection',
            requestId: request.requestId,
            analysisId: request.analysisId,
            projection: {
              granularity: request.granularity,
              scope: request.scope,
              nodes: [doc, member],
              edges: [],
              queryMetadata: window.sharpdepsApp.getState().queryMetadata
            }
          }
        })
      );
    },
    { request, doc, member }
  );
}
async function activateWithKeyboard(page, target) {
  await expect(target).toHaveCount(1);
  for (let step = 0; step < 60; step++) {
    if (await target.evaluate((element) => element === document.activeElement)) {
      await page.keyboard.press('Enter');
      return;
    }
    await page.keyboard.press('Tab');
  }
  throw new Error('Target not reachable through a bounded Tab sequence');
}

test('saved Markdown search opens its source, follows related code and returns with keyboard', async ({
  page
}) => {
  const errors = [];
  page.on('pageerror', (error) => errors.push(String(error)));
  await page.setViewportSize({ width: 900, height: 900 });
  await page.goto('/tests/webview/fixtures/shell-fixture.html');
  await page.evaluate(() => {
    window.dispatchEvent(
      new MessageEvent('message', {
        data: {
          type: 'analysisComplete',
          analysisId: 'an_0123456789abcdef',
          target: { name: 'Saved documents', relativePath: 'Input.csproj' },
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
            returnedCount: 2,
            totalKind: 'returned',
            candidateCount: 0,
            unresolvedCount: 0
          }
        }
      })
    );
  });
  await project(page, await request(page, 'getProjection'));
  await page.keyboard.press('/');
  await expect(page.locator('.sd-search')).toBeFocused();
  await page.keyboard.type('注文');
  const search = await request(page, 'searchEntities', (m) => m.query === '注文');
  await reply(page, search, 'searchResults', { items: [doc, member], total: 2 });
  await activateWithKeyboard(
    page,
    page.locator('.sd-search-results button.sd-node-item').filter({ hasText: doc.name })
  );
  const docDetails = await request(page, 'getEntityDetails', (m) => m.entityId === doc.id);
  await reply(page, docDetails, 'details', { entity: doc, dependencies: [member], dependents: [] });
  await activateWithKeyboard(
    page,
    page.getByRole('button', { name: 'Open document', exact: true })
  );
  await expect
    .poll(
      async () =>
        (await messages(page)).filter((m) => m.type === 'openDeclaration' && m.entityId === doc.id)
          .length
    )
    .toBe(1);
  await activateWithKeyboard(
    page,
    page.getByRole('button', { name: 'Explore dependencies', exact: true })
  );
  await project(page, await request(page, 'getProjection', (m) => m.scope.kind === 'dependencies'));
  await activateWithKeyboard(
    page,
    page.locator('.sd-inspector button.sd-node-item').filter({ hasText: member.name })
  );
  const codeDetails = await request(page, 'getEntityDetails', (m) => m.entityId === member.id);
  await reply(page, codeDetails, 'details', {
    entity: member,
    dependencies: [],
    dependents: [doc]
  });
  await activateWithKeyboard(
    page,
    page.getByRole('button', { name: 'Open declaration', exact: true })
  );
  await expect
    .poll(
      async () =>
        (await messages(page)).filter(
          (m) => m.type === 'openDeclaration' && m.entityId === member.id
        ).length
    )
    .toBe(1);
  const priorProjectionIds = new Set(
    (await messages(page)).filter((m) => m.type === 'getProjection').map((m) => m.requestId)
  );
  await activateWithKeyboard(page, page.locator('#sd-back'));
  const backProjection = await request(
    page,
    'getProjection',
    (m) => m.scope.kind === 'root' && !priorProjectionIds.has(m.requestId)
  );
  await project(page, backProjection);
  const backDetails = await request(
    page,
    'getEntityDetails',
    (m) => m.entityId === doc.id && m.requestId !== docDetails.requestId
  );
  await reply(page, backDetails, 'details', {
    entity: doc,
    dependencies: [member],
    dependents: []
  });
  await expect(page.locator('.sd-search')).toHaveValue('注文');
  await expect(page.locator('.sd-inspector-title')).toHaveText(doc.name);
  await expect(page.locator('.sd-inspector')).not.toContainText('Loading details...');
  expect(await page.evaluate(() => window.sharpdepsApp.getState().selection.entityId)).toBe(doc.id);
  expect(errors).toEqual([]);
});
