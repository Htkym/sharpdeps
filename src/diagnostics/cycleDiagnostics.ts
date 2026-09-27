import * as path from 'node:path';
import * as vscode from 'vscode';
import type { CodeMapReport } from '../analyzer/types';
import type { ReportStore } from '../analyzer/reportStore';
import { computeCycleDiagnostics } from './cycleAnchoring';

export {
  computeCycleDiagnostics,
  type AnchoredCycleDiagnostics,
  type CycleDiagnosticsResult
} from './cycleAnchoring';

/** Publishes circular-dependency findings to the Problems panel. */
export class CycleDiagnostics {
  private readonly collection: vscode.DiagnosticCollection;
  private analysisId: string | undefined;

  constructor(private readonly output: vscode.OutputChannel) {
    this.collection = vscode.languages.createDiagnosticCollection('sharpdeps');
  }

  clear(): void {
    this.analysisId = undefined;
    this.collection.clear();
  }

  update(report: CodeMapReport, analysisId: string, mode: 'quick' | 'semantic'): void {
    this.collection.clear();
    this.analysisId = analysisId;
    const { anchored, unanchored } = computeCycleDiagnostics(report);

    for (const { file, messages } of anchored) {
      this.collection.set(
        vscode.Uri.file(file),
        messages.map((message) => toDiagnostic(message, mode))
      );
    }

    if (unanchored.length > 0) {
      this.output.appendLine('Namespace cycles without a source anchor:');
      for (const line of unanchored) {
        this.output.appendLine(`  ${line}`);
      }
    }
  }

  /**
   * Publishes the semantic cycles at the position of a real evidence record (SD-020).
   * Only findings with a concrete reference are published; a group without a verified
   * cycle is reported in the output channel instead of being placed at a guessed line.
   */
  async updateFromStore(
    store: ReportStore,
    analysisId: string,
    rootDirectory: string | undefined
  ): Promise<void> {
    this.collection.clear();
    this.analysisId = analysisId;
    if (!rootDirectory) {
      return;
    }

    const report = store.getReport(analysisId);
    if (report.cycleGroups.length === 0) {
      return;
    }

    const byFile = new Map<string, vscode.Diagnostic[]>();
    for (const group of report.cycleGroups) {
      const relationIds = group.witness?.relationIds ?? [];
      if (relationIds.length === 0) {
        this.output.appendLine(
          `Cycle group ${group.id} has ${group.memberIds.length} member(s) but no verified cycle; no Problems entry was placed.`
        );
        continue;
      }

      for (const relationId of relationIds) {
        let page;
        try {
          page = await store.getEvidencePage(analysisId, relationId, { limit: 1 });
        } catch {
          continue;
        }

        const item = page.items[0];
        if (!item?.physicalSpan) {
          continue;
        }

        const document = report.sourceManifest.find((entry) => entry.id === item.documentId);
        // Generated documents have no file in the repository; a problem there would
        // point at a read-only document.
        if (!document || document.origin !== 'userSource') {
          continue;
        }

        const filePath = path.join(rootDirectory, document.relativePath);
        const span = item.physicalSpan;
        const diagnostic = new vscode.Diagnostic(
          new vscode.Range(span.startLine, span.startCharacter, span.endLine, span.endCharacter),
          `循環: ${nameOf(store, analysisId, item.sourceTypeId)} → ${nameOf(
            store,
            analysisId,
            item.targetTypeId
          )}（根拠 ${page.total} 件、${group.memberIds.length} 型が相互参照）`,
          vscode.DiagnosticSeverity.Warning
        );
        diagnostic.source = 'SharpDeps';
        const list = byFile.get(filePath) ?? [];
        list.push(diagnostic);
        byFile.set(filePath, list);
      }
    }

    for (const [file, diagnostics] of byFile) {
      if (this.analysisId !== analysisId) return;
      this.collection.set(vscode.Uri.file(file), diagnostics);
    }
  }

  dispose(): void {
    this.collection.dispose();
  }
}

function nameOf(
  store: ReportStore,
  analysisId: string,
  entityId: string | null | undefined
): string {
  if (!entityId) {
    return '?';
  }

  try {
    return store.getEntityDetails(analysisId, entityId)?.entity.name ?? entityId;
  } catch {
    return entityId;
  }
}

function toDiagnostic(message: string, mode: 'quick' | 'semantic'): vscode.Diagnostic {
  const diagnostic = new vscode.Diagnostic(
    new vscode.Range(0, 0, 0, 0),
    mode === 'quick' ? `${message}（Quick解析: 推定診断）` : message,
    vscode.DiagnosticSeverity.Warning
  );
  diagnostic.source = 'SharpDeps';
  return diagnostic;
}
