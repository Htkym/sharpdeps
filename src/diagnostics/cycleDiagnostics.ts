import * as vscode from 'vscode';
import type { CodeMapReport } from '../analyzer/types';
import { computeCycleDiagnostics } from './cycleAnchoring';

export {
  computeCycleDiagnostics,
  type AnchoredCycleDiagnostics,
  type CycleDiagnosticsResult
} from './cycleAnchoring';

/** Publishes circular-dependency findings to the Problems panel. */
export class CycleDiagnostics {
  private readonly collection: vscode.DiagnosticCollection;

  constructor(private readonly output: vscode.OutputChannel) {
    this.collection = vscode.languages.createDiagnosticCollection('sharpdeps');
  }

  update(report: CodeMapReport): void {
    this.collection.clear();
    const { anchored, unanchored } = computeCycleDiagnostics(report);

    for (const { file, messages } of anchored) {
      this.collection.set(vscode.Uri.file(file), messages.map(toDiagnostic));
    }

    if (unanchored.length > 0) {
      this.output.appendLine('Namespace cycles without a source anchor:');
      for (const line of unanchored) {
        this.output.appendLine(`  ${line}`);
      }
    }
  }

  dispose(): void {
    this.collection.dispose();
  }
}

function toDiagnostic(message: string): vscode.Diagnostic {
  const diagnostic = new vscode.Diagnostic(
    new vscode.Range(0, 0, 0, 0),
    message,
    vscode.DiagnosticSeverity.Warning
  );
  diagnostic.source = 'SharpDeps';
  return diagnostic;
}
