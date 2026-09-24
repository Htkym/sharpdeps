// SharpDeps map panel (SD-017: the v2 shell and the report bridge).
//
// The panel owns the webview and the message plumbing; everything that needs the
// analysis result is answered by the report bridge, and everything that needs extension
// work (starting or stopping an analysis) is delegated to the host callbacks. A message
// type the bridge does not implement is answered with an explicit error, never ignored.

import * as vscode from 'vscode';
import type { ReportBridge } from '../analyzer/reportBridge';
import type { ReportStore } from '../analyzer/reportStore';
import { getWebviewHtml } from './html';
import { PROTOCOL_VERSION, validateWebviewMessage } from './protocolV2';
import type { Capabilities, HostToWebviewMessage } from './protocolV2';

export interface CodeMapPanelHost {
  store: ReportStore;
  bridge: ReportBridge;
  output: vscode.OutputChannel;
  /** Starts an analysis for the current target in the requested mode. */
  onAnalyze: (mode: 'quick' | 'semantic') => void;
  /** Stops the running analysis. */
  onCancel: () => void;
}

/** Singleton webview panel (an editor tab) that renders the dependency map. */
export class CodeMapPanel {
  public static readonly viewType = 'sharpdeps.codeMap';
  private static current: CodeMapPanel | undefined;

  static get currentPanel(): CodeMapPanel | undefined {
    return CodeMapPanel.current;
  }

  static show(extensionUri: vscode.Uri, host: CodeMapPanelHost): CodeMapPanel {
    const column = vscode.window.activeTextEditor?.viewColumn ?? vscode.ViewColumn.One;
    if (CodeMapPanel.current) {
      CodeMapPanel.current.panel.reveal(column);
      return CodeMapPanel.current;
    }

    const panel = vscode.window.createWebviewPanel(CodeMapPanel.viewType, 'SharpDeps', column, {
      enableScripts: true,
      retainContextWhenHidden: true,
      localResourceRoots: [vscode.Uri.joinPath(extensionUri, 'media')]
    });

    CodeMapPanel.current = new CodeMapPanel(panel, extensionUri, host);
    return CodeMapPanel.current;
  }

  private readonly panel: vscode.WebviewPanel;
  private readonly disposables: vscode.Disposable[] = [];
  private ready = false;
  private pendingAnalysisId: string | undefined;

  private constructor(
    panel: vscode.WebviewPanel,
    extensionUri: vscode.Uri,
    private readonly host: CodeMapPanelHost
  ) {
    this.panel = panel;
    this.panel.webview.html = getWebviewHtml(this.panel.webview, extensionUri);

    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    this.panel.webview.onDidReceiveMessage(
      (message: unknown) => this.onMessage(message),
      null,
      this.disposables
    );
    this.panel.onDidChangeViewState(
      (event) => setPanelActiveContext(event.webviewPanel.active),
      null,
      this.disposables
    );

    setPanelActiveContext(this.panel.active);
  }

  /**
   * Tells the panel which analysis is current. The webview asks for its own projection,
   * so nothing is pushed that the view did not request.
   */
  notifyAnalysis(analysisId: string): void {
    this.pendingAnalysisId = analysisId;
    this.postAnalysisState(analysisId);
  }

  private onMessage(message: unknown): void {
    const validation = validateWebviewMessage(message);
    if (!validation.ok) {
      this.post({
        type: 'error',
        code:
          validation.code === 'unknownType' ? 'protocol.unknownType' : 'protocol.invalidMessage',
        message: validation.errors.join('; ')
      });
      return;
    }

    const request = validation.value;
    switch (request.type) {
      case 'ready':
        this.ready = true;
        void this.host.bridge
          .handle({ type: 'ready', protocolVersion: PROTOCOL_VERSION })
          .then((response) => this.post(response));
        if (this.pendingAnalysisId) {
          this.postAnalysisState(this.pendingAnalysisId);
        }
        return;
      case 'analyze':
        this.host.onAnalyze(request.mode);
        return;
      case 'cancelAnalysis':
        this.host.onCancel();
        return;
      default:
        // Everything else is answered from the store by the bridge; unimplemented host
        // work comes back as an explicit error message.
        void this.host.bridge.handle(request).then(
          (response) => this.post(response),
          (error: unknown) =>
            this.post({
              type: 'error',
              code: 'bridge.failed',
              message: error instanceof Error ? error.message : String(error)
            })
        );
    }
  }

  private postAnalysisState(analysisId: string): void {
    if (!this.ready) {
      return;
    }

    try {
      const report = this.host.store.getReport(analysisId);
      this.post({
        type: 'analysisComplete',
        analysisId,
        completeness:
          report.completeness === 'completeWithinScope' ? 'completeWithinScope' : 'partial',
        coverage: report.coverage,
        mode: report.mode === 'semantic' ? 'semantic' : 'quick',
        limitations: report.limitations
      });
    } catch (error) {
      this.host.output.appendLine(
        `The result of ${analysisId} is no longer available: ${
          error instanceof Error ? error.message : String(error)
        }`
      );
      this.post({
        type: 'stale',
        analysisId,
        reason: 'unknown'
      });
    }
  }

  private capabilities(): Capabilities {
    const analysisId = this.pendingAnalysisId ?? this.host.store.currentAnalysisId;
    if (analysisId) {
      try {
        const report = this.host.store.getReport(analysisId);
        return {
          typeGraph: report.capabilities.typeGraph,
          evidence: report.capabilities.evidence,
          generatedDocuments: report.capabilities.generatedDocuments,
          cycleWitness: report.capabilities.cycleWitness,
          search: report.capabilities.search
        };
      } catch {
        // Falls through to the conservative defaults below.
      }
    }

    return {
      typeGraph: true,
      evidence: true,
      generatedDocuments: false,
      cycleWitness: true,
      search: true
    };
  }

  private post(message: HostToWebviewMessage): void {
    void this.panel.webview.postMessage(message);
  }

  dispose(): void {
    CodeMapPanel.current = undefined;
    setPanelActiveContext(false);
    this.panel.dispose();
    while (this.disposables.length) {
      this.disposables.pop()?.dispose();
    }
  }
}

function setPanelActiveContext(active: boolean): void {
  void vscode.commands.executeCommand('setContext', 'sharpdeps.panelActive', active);
}
