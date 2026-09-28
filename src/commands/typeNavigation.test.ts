import * as path from 'node:path';
import { describe, expect, it, vi } from 'vitest';
import * as vscode from 'vscode';
import { resolveTypeAtCursor } from './typeNavigation';
import type { DeclarationMatch, ReportStore } from '../analyzer/reportStore';
import { IDS, SPAN, makeSnapshot } from '../../tests/helpers/reportV2Fixtures';

vi.mock('vscode', () => ({ window: { activeTextEditor: undefined } }));

describe('cursor project selection for shared documents', () => {
  it.each([
    ['src/App2/Order.cs', 'src/App/App.csproj', 'src/Other/Other.csproj', IDS.variantB],
    ['src/App2/Order.cs', 'src/App/App.csproj', 'src/App2/App2.csproj', IDS.variantB],
    ['src/App2/Order.cs', 'Root.csproj', 'src/App2/App2.csproj', IDS.variantB],
    ['Order.cs', 'src/App/App.csproj', 'Root.csproj', IDS.variantB],
    ['../Shared/Order.cs', 'Root.csproj', '../Shared/Shared.csproj', IDS.variantB]
  ])('chooses the enclosing project for %s', async (file, projectA, projectB, expected) => {
    const snapshot = makeSnapshot();
    snapshot.declarationIndex = {
      format: 'ndjson',
      fileName: 'declarations.ndjson',
      byteLength: 0,
      types: []
    };
    snapshot.projects[0].relativePath = projectA;
    snapshot.projects[1].relativePath = projectB;
    const variants =
      projectB === 'src/Other/Other.csproj'
        ? [IDS.variantB, IDS.variantA]
        : [IDS.variantA, IDS.variantB];
    const matches: DeclarationMatch[] = variants.map((variant, index) => ({
      typeId: variant === IDS.variantB ? IDS.typeB : IDS.typeA,
      projectVariantId: variant,
      declarationIndex: index,
      span: SPAN,
      documentId: IDS.document,
      relativePath: file,
      isPartial: false
    }));
    const root = path.resolve('cursor-fixture');
    Object.assign(vscode.window, {
      activeTextEditor: {
        document: {
          uri: { scheme: 'file', fsPath: path.resolve(root, file) },
          isDirty: false,
          offsetAt: () => SPAN.start
        },
        selection: { active: { line: 0, character: 0 } }
      }
    });
    const store = {
      currentAnalysisId: snapshot.analysisId,
      getReport: () => snapshot,
      isStale: () => false,
      documentIdForPath: () => IDS.document,
      findTypesAt: async () => matches
    } as unknown as ReportStore;
    const result = await resolveTypeAtCursor(store, root, {
      appendLine: vi.fn()
    } as unknown as vscode.OutputChannel);
    expect(result).toMatchObject({ ok: true, value: { variantId: expected, typeId: IDS.typeB } });
  });
});
