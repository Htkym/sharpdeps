const { test, expect } = require('@playwright/test');

const analysisId = 'an_0123456789abcdef';
const metadata = {
  provider: 'savedIndex',
  workspaceId: 'workspace',
  snapshotId: 'snapshot',
  generation: 5,
  variantIds: ['variant'],
  coverage: 'Partial',
  freshness: 'unverified',
  truncated: false,
  truncationReasons: [],
  diagnostics: [],
  returnedCount: 0,
  totalKind: 'returned',
  candidateCount: 0,
  unresolvedCount: 0,
  resultFilterVersion: 1
};
const doc = {
  id: 'ty_1111111111111111',
  name: '調査😀',
  kind: 'Document',
  granularity: 'type',
  certainty: 'Resolved',
  resultKind: 'docs',
  path: 'docs/調査.md',
  reason: 'exact name/signature',
  sourceLocationAvailable: true
};
const entries = [
  doc,
  ...Array.from({ length: 22 }, (_, i) => ({
    ...doc,
    id: 'ty_' + (i + 100).toString(16).padStart(16, '0'),
    name: '調査の関連項目' + i,
    kind: 'SymbolMention',
    sourceLocationAvailable: false
  }))
];
const relationId = 'rel_1111111111111111',
  evidenceId = 'ev_1111111111111111';
const messages = (page) =>
  page
    .locator('#app')
    .getAttribute('data-host-messages')
    .then((value) => (value || '').split('\n').filter(Boolean).map(JSON.parse));
async function request(page, type, predicate = () => true) {
  await expect
    .poll(async () => (await messages(page)).filter((m) => m.type === type && predicate(m)).length)
    .toBeGreaterThan(0);
  return (await messages(page)).filter((m) => m.type === type && predicate(m)).at(-1);
}
async function post(page, data) {
  await page.evaluate((data) => window.dispatchEvent(new MessageEvent('message', { data })), data);
}
async function result(page, req, type, fields) {
  await post(page, {
    type,
    requestId: req.requestId,
    analysisId,
    queryMetadata: metadata,
    ...fields
  });
}
async function completeViews(page, { evidence = false, pages = false } = {}) {
  const tree = await request(
    page,
    'searchEntities',
    (m) => m.query === '' && m.granularity === 'project'
  );
  await result(page, tree, 'searchResults', { items: [], total: 0 });
  const projection = await request(page, 'getProjection');
  await result(page, projection, 'projection', {
    projection: {
      scope: projection.scope,
      granularity: 'type',
      nodes: [doc, entries[1]],
      edges: [
        {
          id: relationId,
          sourceId: doc.id,
          targetId: entries[1].id,
          basis: 'symbolResolved',
          kinds: ['contains'],
          evidenceCount: 1,
          inCycle: false,
          certainty: 'Resolved',
          variantId: 'variant'
        }
      ],
      queryMetadata: metadata
    }
  });
  const search = await request(page, 'searchEntities', (m) => m.query === '調査' && !m.cursor);
  await result(page, search, 'searchResults', {
    items: pages ? entries.slice(0, 20) : entries,
    total: pages ? 20 : 23,
    ...(pages ? { nextCursor: 'cur_1111111111111111' } : {})
  });
  if (pages) {
    const next = await request(page, 'searchEntities', (m) => m.cursor === 'cur_1111111111111111');
    await result(page, next, 'searchResults', { items: entries.slice(20), total: 3 });
  }
  if (evidence) {
    const req = await request(page, 'getEvidencePage', (m) => m.relationId === relationId);
    await result(page, req, 'evidencePage', {
      relationId,
      total: 1,
      items: [
        {
          id: evidenceId,
          certainty: 'Resolved',
          kind: 'contains',
          producer: 'Markdown',
          documentPath: doc.path,
          location: {
            sourceId: 'source',
            contentHash: 'hash',
            rawSpan: { start: 0, length: 10, end: 10 }
          }
        }
      ]
    });
  } else {
    const details = await request(page, 'getEntityDetails', (m) => m.entityId === doc.id);
    await result(page, details, 'details', {
      entity: doc,
      dependencies: entries.slice(1),
      dependents: []
    });
  }
}

test('source return keeps navigation, loaded search pages, focus and scroll across recreation and rejects an older return', async ({
  page
}) => {
  await page.setViewportSize({ width: 900, height: 650 });
  await page.goto('/tests/webview/fixtures/shell-fixture.html');
  await post(page, {
    type: 'analysisComplete',
    analysisId,
    queryMetadata: metadata,
    target: { name: 'Saved', relativePath: 'Input.csproj' }
  });
  await page.evaluate(
    ({ doc }) => {
      const app = window.sharpdepsApp;
      app.dispatch({ type: 'viewKindChanged', viewKind: 'table' });
      app.dispatch({ type: 'filtersChanged', filters: { resultKinds: ['docs'] } });
      app.dispatch({ type: 'scopeChanged', scope: { kind: 'dependencies', id: doc.id, depth: 2 } });
      app.dispatch({ type: 'entitySelected', entityId: doc.id });
      app.dispatch({
        type: 'cameraChanged',
        camera: { zoom: 1.5, scrollLeft: 55, scrollTop: 250 }
      });
    },
    { doc }
  );
  await page.locator('.sd-search').fill('調査');
  await completeViews(page);
  await page.evaluate(() => {
    const app = window.sharpdepsApp;
    app.elements.navPaneBody.scrollTop = 170;
    app.elements.inspectorBody.scrollTop = 120;
    const button = app.elements.inspectorBody.querySelector('[data-source-return="declaration"]');
    button.focus({ preventScroll: true });
    button.click();
  });
  const open = await request(page, 'openDeclaration');
  const log = await messages(page),
    position = log.findIndex((m) => m.requestId === open.requestId);
  expect(log[position - 1].type).toBe('persistViewState');
  const captured = log[position - 1].viewState;
  expect(captured.scope.kind).toBe('dependencies');
  expect(captured.history.length).toBeGreaterThan(0);
  expect(open.returnUi.searchCount).toBe(23);
  expect(open.returnUi.scroll.navigation).toBeGreaterThan(0);
  await page.reload();
  await post(page, {
    type: 'analysisComplete',
    analysisId,
    queryMetadata: metadata,
    target: { name: 'Saved', relativePath: 'Input.csproj' }
  });
  await post(page, {
    type: 'sourceReturn',
    requestId: open.requestId,
    analysisId,
    state: captured,
    ui: open.returnUi
  });
  await completeViews(page, { pages: true });
  await expect
    .poll(() => page.evaluate(() => document.activeElement?.dataset.sourceReturn))
    .toBe('declaration');
  let restored = await page.evaluate(() => window.sharpdepsApp.getState());
  expect(restored.scope).toEqual(captured.scope);
  expect(restored.history).toEqual(captured.history);
  expect(restored.selection).toEqual(captured.selection);
  expect(restored.camera).toEqual(captured.camera);
  expect(restored.searchResults.items).toHaveLength(23);
  expect(await page.evaluate(() => window.sharpdepsApp.elements.navPaneBody.scrollTop)).toBe(
    open.returnUi.scroll.navigation
  );
  expect(await page.evaluate(() => window.sharpdepsApp.elements.inspectorBody.scrollTop)).toBe(
    open.returnUi.scroll.inspector
  );
  await page.evaluate(
    (relationId) => window.sharpdepsApp.dispatch({ type: 'relationSelected', relationId }),
    relationId
  );
  const ev = await request(page, 'getEvidencePage');
  await result(page, ev, 'evidencePage', {
    relationId,
    total: 1,
    items: [
      {
        id: evidenceId,
        certainty: 'Resolved',
        location: {
          sourceId: 'source',
          contentHash: 'hash',
          rawSpan: { start: 0, length: 10, end: 10 }
        }
      }
    ]
  });
  await page.locator('[data-source-return="evidence"]').click();
  const openEvidence = await request(page, 'openEvidence');
  const beforeOld = await page.evaluate(() => window.sharpdepsApp.getState());
  await post(page, {
    type: 'sourceReturn',
    requestId: open.requestId,
    analysisId,
    state: captured,
    ui: open.returnUi
  });
  expect(await page.evaluate(() => window.sharpdepsApp.getState().selection)).toEqual(
    beforeOld.selection
  );
  const evLog = await messages(page),
    evPosition = evLog.findIndex((m) => m.requestId === openEvidence.requestId);
  const evState = evLog[evPosition - 1].viewState;
  await post(page, {
    type: 'sourceReturn',
    requestId: openEvidence.requestId,
    analysisId,
    state: evState,
    ui: openEvidence.returnUi
  });
  await completeViews(page, { evidence: true, pages: true });
  await expect
    .poll(() => page.evaluate(() => document.activeElement?.dataset.sourceReturn))
    .toBe('evidence');
  restored = await page.evaluate(() => window.sharpdepsApp.getState());
  expect(restored.selection).toEqual({ relationId });
  expect(restored.scope).toEqual(evState.scope);
  expect(restored.history).toEqual(evState.history);

  // A new source request is dismissed by keyboard navigation. Its marker must
  // disappear from local state before the frame is recreated.
  await page.locator('[data-source-return="evidence"]').click();
  const dismissed = await request(
    page,
    'openEvidence',
    (m) => m.requestId !== openEvidence.requestId
  );
  await page.keyboard.press('Escape');
  await request(page, 'sourceReturnDismissed');
  await page.reload();
  await post(page, {
    type: 'analysisComplete',
    analysisId,
    queryMetadata: metadata,
    target: { name: 'Saved', relativePath: 'Input.csproj' }
  });
  const afterDismissal = await page.evaluate(() => window.sharpdepsApp.getState());
  await post(page, {
    type: 'sourceReturn',
    requestId: dismissed.requestId,
    analysisId,
    state: captured,
    ui: dismissed.returnUi
  });
  const afterLateReturn = await page.evaluate(() => window.sharpdepsApp.getState());
  expect(afterLateReturn.selection).toEqual(afterDismissal.selection);
  expect(afterLateReturn.scope).toEqual(afterDismissal.scope);
  expect(afterLateReturn.history).toEqual(afterDismissal.history);
  expect(await page.evaluate(() => document.activeElement?.dataset.sourceReturn)).toBeUndefined();
});

test('drops a pending source-return page target when the saved session changes', async ({
  page
}) => {
  await page.goto('/tests/webview/fixtures/shell-fixture.html');
  await post(page, {
    type: 'analysisComplete',
    analysisId,
    queryMetadata: metadata,
    target: { name: 'Saved', relativePath: 'Input.csproj' }
  });
  await page.evaluate((doc) => {
    window.sharpdepsApp.dispatch({ type: 'viewKindChanged', viewKind: 'table' });
    window.sharpdepsApp.dispatch({ type: 'entitySelected', entityId: doc.id });
  }, doc);
  await page.locator('.sd-search').fill('調査');
  await completeViews(page);
  await page.evaluate(() => {
    window.sharpdepsApp.elements.inspectorBody
      .querySelector('[data-source-return="declaration"]')
      .click();
  });
  const open = await request(page, 'openDeclaration');
  const log = await messages(page);
  const captured = log[log.findIndex((m) => m.requestId === open.requestId) - 1].viewState;
  const oldSearch = await request(page, 'searchEntities', (m) => m.query === '調査' && !m.cursor);
  await post(page, {
    type: 'sourceReturn',
    requestId: open.requestId,
    analysisId,
    state: captured,
    ui: open.returnUi
  });
  const reread = await request(
    page,
    'searchEntities',
    (m) => m.query === '調査' && !m.cursor && m.requestId !== oldSearch.requestId
  );
  await result(page, reread, 'searchResults', {
    items: entries.slice(0, 20),
    total: 20,
    nextCursor: 'cur_1111111111111111'
  });
  await request(page, 'searchEntities', (m) => m.cursor === 'cur_1111111111111111');
  expect(await page.evaluate(() => window.sharpdepsApp.sourceReturnSearchTarget())).toBe(23);

  const newAnalysisId = 'an_2222222222222222';
  const newMetadata = { ...metadata, generation: 6, snapshotId: 'new-snapshot' };
  await post(page, {
    type: 'analysisComplete',
    analysisId: newAnalysisId,
    queryMetadata: newMetadata,
    target: { name: 'Saved', relativePath: 'Input.csproj' }
  });
  expect(await page.evaluate(() => window.sharpdepsApp.sourceReturnSearchTarget())).toBeUndefined();

  // A controlled input event avoids pointer/key dismissal masking the session guard.
  // This checks the defensive boundary, not a claim of normal user-flow reproduction.
  await page.evaluate(() => {
    const input = window.sharpdepsApp.elements.searchInput;
    input.value = '調査';
    input.dispatchEvent(new Event('input', { bubbles: true }));
  });
  const newSearch = await request(
    page,
    'searchEntities',
    (m) => m.analysisId === newAnalysisId && m.query === '調査' && !m.cursor
  );
  await post(page, {
    type: 'searchResults',
    requestId: newSearch.requestId,
    analysisId: newAnalysisId,
    queryMetadata: newMetadata,
    items: [doc],
    total: 1,
    nextCursor: 'cur_2222222222222222'
  });
  const after = await messages(page);
  expect(
    after.some((m) => m.analysisId === newAnalysisId && m.cursor === 'cur_2222222222222222')
  ).toBe(false);
  expect(
    await page.evaluate(() => window.sharpdepsApp.getState().searchResults.items)
  ).toHaveLength(1);
});

test('keeps a manual search change while old source and search replies arrive', async ({
  page
}) => {
  await page.goto('/tests/webview/fixtures/shell-fixture.html');
  await post(page, {
    type: 'analysisComplete',
    analysisId,
    queryMetadata: metadata,
    target: { name: 'Saved', relativePath: 'Input.csproj' }
  });
  await page.evaluate((doc) => {
    window.sharpdepsApp.dispatch({ type: 'viewKindChanged', viewKind: 'table' });
    window.sharpdepsApp.dispatch({ type: 'entitySelected', entityId: doc.id });
  }, doc);
  await page.locator('.sd-search').fill('調査');
  await completeViews(page);
  await page.evaluate(() => {
    window.sharpdepsApp.elements.inspectorBody
      .querySelector('[data-source-return="declaration"]')
      .click();
  });
  const open = await request(page, 'openDeclaration');
  const log = await messages(page);
  const captured = log[log.findIndex((m) => m.requestId === open.requestId) - 1].viewState;
  const previous = await request(page, 'searchEntities', (m) => m.query === '調査' && !m.cursor);
  await post(page, {
    type: 'sourceReturn',
    requestId: open.requestId,
    analysisId,
    state: captured,
    ui: open.returnUi
  });
  const reread = await request(
    page,
    'searchEntities',
    (m) => m.query === '調査' && !m.cursor && m.requestId !== previous.requestId
  );
  expect(await page.evaluate(() => window.sharpdepsApp.sourceReturnSearchTarget())).toBe(23);
  const searchInput = page.locator('.sd-search');
  await searchInput.click();
  await searchInput.fill('新しい検索');
  const newSearch = await request(page, 'searchEntities', (m) => m.query === '新しい検索');
  const fresh = {
    ...doc,
    id: 'ty_3333333333333333',
    name: '新しい検索',
    sourceLocationAvailable: false
  };
  await result(page, newSearch, 'searchResults', { items: [fresh], total: 1 });
  await result(page, reread, 'searchResults', {
    items: entries.slice(0, 20),
    total: 20,
    nextCursor: 'cur_3333333333333333'
  });
  await post(page, {
    type: 'sourceReturn',
    requestId: open.requestId,
    analysisId,
    state: captured,
    ui: open.returnUi
  });
  const state = await page.evaluate(() => window.sharpdepsApp.getState());
  expect(state.search).toBe('新しい検索');
  expect(state.searchResults.query).toBe('新しい検索');
  expect(state.searchResults.items.map((item) => item.id)).toEqual([fresh.id]);
  expect(await page.evaluate(() => window.sharpdepsApp.sourceReturnSearchTarget())).toBeUndefined();
  expect((await messages(page)).some((m) => m.cursor === 'cur_3333333333333333')).toBe(false);
  expect(
    await page.evaluate(() => document.activeElement === window.sharpdepsApp.elements.searchInput)
  ).toBe(true);
});
