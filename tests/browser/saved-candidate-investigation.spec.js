// Actual viewer/entry messages with fixture host replies; not QueryHost/native source proof.
const { test, expect } = require('@playwright/test');

const analysisId = 'an_0123456789abcdef';
const originId = 'ci_' + '1'.repeat(32);
const relationId = 'rel_1111111111111111';
const variantId = 'hvar_' + '1'.repeat(64);
const mention = {
  id: 'ty_1111111111111111',
  name: 'Compute mention',
  kind: 'SymbolMention',
  granularity: 'type',
  certainty: 'Resolved',
  resultKind: 'docs',
  path: 'docs/調査.md',
  sourceLocationAvailable: false
};
const candidate = {
  id: 'ty_2222222222222222',
  name: 'Compute',
  kind: 'Member',
  granularity: 'type',
  certainty: 'Candidate',
  resultKind: 'code',
  sourceLocationAvailable: false
};
const metadata = {
  provider: 'savedIndex',
  workspaceId: 'workspace',
  snapshotId: 'snapshot',
  generation: 5,
  variantIds: [variantId],
  coverage: 'Partial',
  freshness: 'unverified',
  truncated: false,
  truncationReasons: [],
  diagnostics: [],
  returnedCount: 2,
  totalKind: 'returned',
  candidateCount: 1,
  unresolvedCount: 0,
  resultFilterVersion: 1
};
const origin = { originId, relationId, targetEntityId: candidate.id, targetName: candidate.name };
const declarations = [
  {
    capabilityId: 'cd_' + '1'.repeat(32),
    occurrenceId: 'hocc_' + '1'.repeat(64),
    variantId,
    path: 'src/Helper.cs'
  },
  {
    capabilityId: 'cd_' + '2'.repeat(32),
    occurrenceId: 'hocc_' + '2'.repeat(64),
    variantId,
    path: 'src/Helper.追加.cs'
  }
];

const messages = (page) =>
  page
    .locator('#app')
    .getAttribute('data-host-messages')
    .then((value) => (value || '').split('\n').filter(Boolean).map(JSON.parse));
async function request(page, type, predicate = () => true) {
  await expect
    .poll(
      async () =>
        (await messages(page)).filter((message) => message.type === type && predicate(message))
          .length
    )
    .toBeGreaterThan(0);
  return (await messages(page))
    .filter((message) => message.type === type && predicate(message))
    .at(-1);
}
async function post(page, data) {
  await page.evaluate((data) => window.dispatchEvent(new MessageEvent('message', { data })), data);
}
async function investigation(page, req, fields = {}) {
  await post(page, {
    type: 'candidateInvestigation',
    requestId: req.requestId,
    analysisId,
    originId,
    relationId,
    targetEntityId: candidate.id,
    certainty: 'Candidate',
    relationKind: 'mentionsSymbol',
    reason: 'name only',
    message: 'Relation remains uncertain; declarations are checked separately.',
    queryMetadata: metadata,
    declarations,
    ...fields
  });
}
async function setup(page) {
  await page.goto('/tests/webview/fixtures/shell-fixture.html');
  await post(page, {
    type: 'analysisComplete',
    analysisId,
    queryMetadata: metadata,
    target: { name: 'Saved candidates', relativePath: 'Input.csproj' }
  });
  const projection = await request(page, 'getProjection');
  await post(page, {
    type: 'projection',
    requestId: projection.requestId,
    analysisId,
    projection: {
      scope: projection.scope,
      granularity: 'type',
      nodes: [mention, candidate],
      edges: [
        {
          id: relationId,
          sourceId: mention.id,
          targetId: candidate.id,
          basis: 'nameOnly',
          kinds: ['mentionsSymbol'],
          certainty: 'Candidate',
          evidenceCount: 1,
          inCycle: false,
          variantId
        }
      ],
      totalNodeCount: 2,
      totalEdgeCount: 1,
      truncated: false,
      queryMetadata: metadata
    }
  });
  await page.locator('#sd-view-table').click();
  await page.locator('.sd-table tbody tr').filter({ hasText: mention.name }).click();
  const details = await request(
    page,
    'getEntityDetails',
    (message) => message.entityId === mention.id
  );
  await post(page, {
    type: 'details',
    requestId: details.requestId,
    analysisId,
    entity: mention,
    dependencies: [candidate],
    dependents: [],
    candidateOrigins: [origin],
    queryMetadata: metadata
  });
  await expect(page.locator(`[data-candidate-origin="${originId}"]`)).toHaveCount(1);
  return projection;
}
async function normalState(page) {
  return page.evaluate(() => {
    const state = window.sharpdepsApp.getState();
    return {
      projection: state.projection,
      details: state.details,
      queryMetadata: state.queryMetadata,
      projectionRequestId: state.projectionRequestId,
      selection: state.selection,
      scope: state.scope,
      filters: state.filters,
      search: state.search,
      temporaryDisplayIds: state.temporaryDisplayIds
    };
  });
}
async function sourceMessages(page) {
  return (await messages(page)).filter((message) =>
    ['openDeclaration', 'openEvidence', 'openCandidateDeclaration'].includes(message.type)
  );
}

test('Candidate investigation requires an explicit declaration-row choice and keeps normal results uncertain', async ({
  page
}) => {
  const errors = [];
  page.on('pageerror', (error) => errors.push(String(error)));
  await setup(page);
  const ordinaryCandidate = page
    .locator('.sd-inspector button.sd-node-item')
    .filter({ hasText: '[Member · Candidate] Compute' });
  await expect(ordinaryCandidate).toBeDisabled();
  const before = await normalState(page);
  await page.locator(`[data-candidate-origin="${originId}"]`).click();
  const req = await request(page, 'investigateCandidate');
  expect(req).toMatchObject({ analysisId, originId });
  expect(Object.keys(req).sort()).toEqual(['analysisId', 'originId', 'requestId', 'type']);
  expect(await sourceMessages(page)).toHaveLength(0);
  await investigation(page, req);
  const panel = page.locator('[data-candidate-investigation]');
  await expect(panel.locator('[data-candidate-declaration]')).toHaveCount(2);
  await expect(panel).toContainText(
    'The relationship is unconfirmed; check declaration existence separately.'
  );
  await expect(panel).toContainText('name only');
  await expect(panel).toContainText(declarations[1].path);
  expect(await sourceMessages(page)).toHaveLength(0);
  expect(await normalState(page)).toEqual(before);
  await expect(ordinaryCandidate).toBeDisabled();
  await page.locator('#sd-copy').click();
  const copy = await request(page, 'copyContext');
  expect(copy.projectionRequestId).toBe(before.projectionRequestId);
  expect(copy.includeIds).toEqual(before.temporaryDisplayIds);
  expect(copy).not.toHaveProperty('candidateInvestigation');
  expect(copy).not.toHaveProperty('originId');
  expect(copy).not.toHaveProperty('declarations');
  await page.locator(`[data-candidate-declaration="${declarations[1].capabilityId}"]`).click();
  const open = await request(page, 'openCandidateDeclaration');
  expect(open).toMatchObject({
    analysisId,
    originId,
    capabilityId: declarations[1].capabilityId,
    returnUi: { id: mention.id, focus: 'declaration' }
  });
  expect(Object.keys(open).sort()).toEqual([
    'analysisId',
    'capabilityId',
    'originId',
    'requestId',
    'returnUi',
    'type'
  ]);
  expect(JSON.stringify(open)).not.toContain('src/Helper');
  expect(open).not.toHaveProperty('occurrenceId');
  expect(await sourceMessages(page)).toHaveLength(1);
  const log = await messages(page),
    index = log.findIndex((message) => message.requestId === open.requestId);
  expect(log[index - 1].type).toBe('persistViewState');
  expect(log[index - 1].viewState.selection.entityId).toBe(mention.id);
  expect(log[index - 1].viewState).not.toHaveProperty('candidateInvestigation');
  expect(errors).toEqual([]);
});

test('closing or replacing a Candidate investigation rejects its late reply and leaves unavailable declarations unopened', async ({
  page
}) => {
  await setup(page);
  const originButton = page.locator(`[data-candidate-origin="${originId}"]`);
  await originButton.click();
  const old = await request(page, 'investigateCandidate');
  await page.locator('[data-candidate-dismiss]').click();
  const dismissed = await request(page, 'dismissCandidateInvestigation');
  expect(dismissed).toMatchObject({ analysisId, originId });
  await investigation(page, old);
  await expect(page.locator('[data-candidate-dismiss]')).toHaveCount(0);
  await expect(page.locator('[data-candidate-declaration]')).toHaveCount(0);
  expect(
    await page.evaluate(() => window.sharpdepsApp.getState().candidateInvestigation)
  ).toBeUndefined();
  await originButton.click();
  const current = await request(
    page,
    'investigateCandidate',
    (message) => message.requestId !== old.requestId
  );
  await investigation(page, old);
  await expect(page.locator('[data-candidate-declaration]')).toHaveCount(0);
  await investigation(page, current, {
    declarations: [],
    unavailableReason: 'No declaration exists within the selected variant.'
  });
  await expect(page.locator('[data-candidate-investigation]')).toContainText(
    'No declaration exists within the selected variant.'
  );
  await expect(page.locator('[data-candidate-declaration]')).toHaveCount(0);
  expect(await sourceMessages(page)).toHaveLength(0);
});
