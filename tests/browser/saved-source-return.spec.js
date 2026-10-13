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

test('saved Inspector opens an off-graph returned relation and reconstructs it on source return', async ({
  page
}) => {
  await page.setViewportSize({ width: 900, height: 400 });
  await page.goto('/tests/webview/fixtures/shell-fixture.html');
  const method = { ...doc, name: 'Compute', kind: 'Member', sourceLocationAvailable: false };
  const caller = { ...method, id: entries[1].id, name: 'Value' };
  const relation = {
    id: relationId,
    sourceId: caller.id,
    targetId: method.id,
    sourceName: caller.name,
    targetName: method.name,
    kind: 'calls',
    certainty: 'Resolved',
    evidenceCount: 1,
    variantId: 'variant'
  };
  const projection = async (nodes) => {
    const req = await request(page, 'getProjection');
    await result(page, req, 'projection', {
      projection: {
        scope: req.scope,
        granularity: 'type',
        nodes,
        edges: [],
        totalNodeCount: nodes.length,
        totalEdgeCount: 0,
        truncated: false,
        queryMetadata: metadata
      }
    });
  };
  const evidence = async () => {
    const req = await request(page, 'getEvidencePage');
    await result(page, req, 'evidencePage', {
      relationId,
      relation,
      total: 1,
      items: [
        {
          id: evidenceId,
          kind: 'calls',
          certainty: 'Resolved',
          documentPath: 'Input.cs',
          location: {
            sourceId: 'fixture',
            contentHash: 'a'.repeat(64),
            rawSpan: { start: 4, length: 2, end: 6 }
          }
        }
      ]
    });
    return req;
  };
  await post(page, {
    type: 'analysisComplete',
    analysisId,
    queryMetadata: metadata,
    target: { name: 'Saved', relativePath: 'Input.csproj' }
  });
  await page.locator('#sd-view-table').click();
  const tree = await request(
    page,
    'searchEntities',
    (m) => m.query === '' && m.granularity === 'project'
  );
  await result(page, tree, 'searchResults', { items: [], total: 0 });
  await projection([]);
  await page.locator('.sd-search').fill('Compute');
  const search = await request(page, 'searchEntities', (m) => m.query === 'Compute');
  await result(page, search, 'searchResults', { items: [method], total: 1 });
  await page.locator(`.sd-nav-body button[data-entity-id="${method.id}"]`).click();
  const details = await request(page, 'getEntityDetails');
  await result(page, details, 'details', {
    entity: method,
    dependencies: [],
    dependents: [caller],
    relations: { outgoing: [], incoming: [relation] }
  });
  await page.getByRole('button', { name: 'Explore dependents', exact: true }).click();
  await projection([method]);
  await page.locator(`.sd-inspector button[data-relation-id="${relationId}"]`).click();
  const firstEvidence = await evidence();
  await expect(page.locator('.sd-inspector-title')).toHaveText('Value → Compute');
  const button = page.locator(`[data-source-return="evidence"][data-source-id="${evidenceId}"]`);
  await expect(button).toBeVisible();
  await page.evaluate(() => {
    window.sharpdepsApp.elements.inspectorBody.scrollTop = 80;
  });
  await button.click();
  const open = await request(page, 'openEvidence');
  expect(open.evidenceId).toBe(evidenceId);
  const log = await messages(page),
    position = log.findIndex((m) => m.requestId === open.requestId);
  const captured = log[position - 1].viewState;
  expect(captured.selection).toEqual({ relationId });
  expect(captured.history.length).toBeGreaterThan(0);
  expect(open.returnUi.scroll.inspector).toBeGreaterThan(0);
  await post(page, {
    type: 'sourceReturn',
    requestId: open.requestId,
    analysisId,
    state: captured,
    ui: open.returnUi
  });
  const returnedTree = await request(
    page,
    'searchEntities',
    (m) => m.query === '' && m.granularity === 'project'
  );
  await result(page, returnedTree, 'searchResults', { items: [], total: 0 });
  await projection([method]);
  const returnedSearch = await request(page, 'searchEntities', (m) => m.query === 'Compute');
  await result(page, returnedSearch, 'searchResults', { items: [method], total: 1 });
  const secondEvidence = await evidence();
  expect(secondEvidence.requestId).not.toBe(firstEvidence.requestId);
  await expect(button).toBeFocused();
  await expect(page.locator('.sd-inspector-title')).toHaveText('Value → Compute');
  const restored = await page.evaluate(() => ({
    state: window.sharpdepsApp.getState(),
    scroll: window.sharpdepsApp.elements.inspectorBody.scrollTop
  }));
  expect(restored.state.selection).toEqual(captured.selection);
  expect(restored.state.history).toEqual(captured.history);
  expect(restored.state.scope).toEqual(captured.scope);
  expect(restored.state.viewKind).toBe('table');
  expect(restored.state.projection.edges).toEqual([]);
  expect(restored.scroll).toBe(open.returnUi.scroll.inspector);
  // Replayed data from an older pin must not replace the freshly reconstructed relation.
  await post(page, {
    type: 'evidencePage',
    requestId: firstEvidence.requestId,
    analysisId,
    relationId,
    relation: { ...relation, sourceName: 'Old caller' },
    total: 0,
    items: [],
    queryMetadata: { ...metadata, generation: metadata.generation - 1 }
  });
  await expect(page.locator('.sd-inspector-title')).toHaveText('Value → Compute');
});

async function request(page, type, predicate = () => true) {
  await expect
    .poll(async () => (await messages(page)).filter((m) => m.type === type && predicate(m)).length)
    .toBeGreaterThan(0);
  return (await messages(page)).filter((m) => m.type === type && predicate(m)).at(-1);
}
async function post(page, data) {
  await page.evaluate((data) => window.dispatchEvent(new MessageEvent('message', { data })), data);
}
async function result(page, req, type, fields, quick = false) {
  await post(page, {
    type,
    requestId: req.requestId,
    analysisId,
    ...(quick ? {} : { queryMetadata: metadata }),
    ...fields
  });
}
async function completeViews(page, { evidence = false, pages = false, quick = false } = {}) {
  const items = quick
    ? entries.map((entry, index) => ({
        id: entry.id,
        name: index === 0 ? '調査' : `調査関連${index}`,
        kind: 'class',
        granularity: 'type',
        fullName: `N.調査${index}`,
        isExternal: false,
        projectId: 'prj_1111111111111111',
        namespaceId: 'ns_1111111111111111'
      }))
    : entries;
  const tree = await request(
    page,
    'searchEntities',
    (m) => m.query === '' && m.granularity === 'project'
  );
  await result(page, tree, 'searchResults', { items: [], total: 0 }, quick);
  const projection = await request(page, 'getProjection');
  await result(
    page,
    projection,
    'projection',
    {
      projection: {
        scope: projection.scope,
        granularity: 'type',
        nodes: [items[0], items[1]],
        edges: [
          {
            id: relationId,
            sourceId: doc.id,
            targetId: entries[1].id,
            basis: quick ? 'typeReferenceInferred' : 'symbolResolved',
            kinds: [quick ? 'typeReferenceInferred' : 'contains'],
            evidenceCount: 1,
            inCycle: false,
            ...(quick ? {} : { certainty: 'Resolved', variantId: 'variant' })
          }
        ],
        ...(quick ? {} : { queryMetadata: metadata })
      }
    },
    quick
  );
  const search = await request(page, 'searchEntities', (m) => m.query === '調査' && !m.cursor);
  await result(
    page,
    search,
    'searchResults',
    {
      items: pages ? items.slice(0, 20) : items,
      total: quick ? 23 : pages ? 20 : 23,
      ...(pages ? { nextCursor: 'cur_1111111111111111' } : {})
    },
    quick
  );
  if (pages) {
    const next = await request(page, 'searchEntities', (m) => m.cursor === 'cur_1111111111111111');
    await result(
      page,
      next,
      'searchResults',
      { items: items.slice(20), total: quick ? 23 : 3 },
      quick
    );
  }
  if (evidence) {
    const req = await request(page, 'getEvidencePage', (m) => m.relationId === relationId);
    await result(
      page,
      req,
      'evidencePage',
      {
        relationId,
        total: 1,
        items: quick
          ? [quickEvidence()]
          : [
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
      },
      quick
    );
  } else {
    const details = await request(page, 'getEntityDetails', (m) => m.entityId === doc.id);
    await result(
      page,
      details,
      'details',
      {
        entity: items[0],
        dependencies: items.slice(1),
        dependents: []
      },
      quick
    );
  }
}

function quickEvidence() {
  return {
    id: evidenceId,
    relationId,
    sourceEntityId: doc.id,
    targetEntityId: entries[1].id,
    sourceTypeId: doc.id,
    targetTypeId: entries[1].id,
    kind: 'typeReferenceInferred',
    origin: 'userSource',
    documentId: 'doc_1111111111111111',
    physicalSpan: {
      start: 0,
      length: 4,
      startLine: 0,
      startCharacter: 0,
      endLine: 0,
      endCharacter: 4
    },
    sourceContentHash: 'a'.repeat(64),
    confidence: 'inferred',
    publicSurface: false
  };
}

for (const quick of [false, true]) {
  test(`${quick ? 'Quick report' : 'Saved'} source return keeps navigation, loaded search pages, focus and scroll across recreation and rejects an older return`, async ({
    page
  }) => {
    await page.setViewportSize({ width: 900, height: 650 });
    await page.goto('/tests/webview/fixtures/shell-fixture.html');
    const analysis = quick
      ? {
          mode: 'quick',
          completeness: 'partial',
          capabilities: {
            typeGraph: true,
            evidence: true,
            generatedDocuments: false,
            cycleWitness: false,
            search: true
          }
        }
      : { queryMetadata: metadata };
    await post(page, {
      type: 'analysisComplete',
      analysisId,
      ...analysis,
      target: { name: quick ? 'Quick' : 'Saved', relativePath: 'Input.csproj' }
    });
    await page.evaluate(
      ({ doc, quick }) => {
        const app = window.sharpdepsApp;
        if (quick) app.dispatch({ type: 'granularityChanged', granularity: 'type' });
        app.dispatch({ type: 'viewKindChanged', viewKind: 'table' });
        app.dispatch({
          type: 'filtersChanged',
          filters: quick ? { basis: ['typeReferenceInferred'] } : { resultKinds: ['docs'] }
        });
        app.dispatch({
          type: 'scopeChanged',
          scope: { kind: 'dependencies', id: doc.id, depth: 2 }
        });
        app.dispatch({ type: 'entitySelected', entityId: doc.id });
        app.dispatch({
          type: 'cameraChanged',
          camera: { zoom: 1.5, scrollLeft: 55, scrollTop: 250 }
        });
      },
      { doc, quick }
    );
    await page.locator('.sd-search').fill('調査');
    await completeViews(page, { quick });
    if (quick) {
      await expect(
        page.getByRole('button', { name: 'Open declaration', exact: true })
      ).toBeVisible();
      await expect(page.locator('[data-source-return="declaration"]')).toHaveAttribute(
        'data-source-id',
        doc.id
      );
    }
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
      ...analysis,
      target: { name: quick ? 'Quick' : 'Saved', relativePath: 'Input.csproj' }
    });
    await post(page, {
      type: 'sourceReturn',
      requestId: open.requestId,
      analysisId,
      state: captured,
      ui: open.returnUi
    });
    await completeViews(page, { pages: true, quick });
    await expect
      .poll(() => page.evaluate(() => document.activeElement?.dataset.sourceReturn))
      .toBe('declaration');
    let restored = await page.evaluate(() => window.sharpdepsApp.getState());
    expect(restored.scope).toEqual(captured.scope);
    expect(restored.history).toEqual(captured.history);
    expect(restored.selection).toEqual(captured.selection);
    expect(restored.camera).toEqual(captured.camera);
    expect(restored.searchResults.items).toHaveLength(23);
    expect(restored.granularity).toBe('type');
    if (quick) expect(restored.queryMetadata).toBeUndefined();
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
    await result(
      page,
      ev,
      'evidencePage',
      {
        relationId,
        total: 1,
        items: quick
          ? [quickEvidence()]
          : [
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
      },
      quick
    );
    if (quick) await expect(page.locator('.sd-evidence-item .sd-badge-inferred')).toBeVisible();
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
    await completeViews(page, { evidence: true, pages: true, quick });
    await expect
      .poll(() => page.evaluate(() => document.activeElement?.dataset.sourceReturn))
      .toBe('evidence');
    restored = await page.evaluate(() => window.sharpdepsApp.getState());
    expect(restored.selection).toEqual({ relationId });
    expect(restored.scope).toEqual(evState.scope);
    expect(restored.history).toEqual(evState.history);
    if (quick) {
      expect(restored.granularity).toBe('type');
      await expect(page.locator('[data-source-return="evidence"]')).toHaveAttribute(
        'data-source-id',
        evidenceId
      );
    }

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
      ...analysis,
      target: { name: quick ? 'Quick' : 'Saved', relativePath: 'Input.csproj' }
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
}

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
