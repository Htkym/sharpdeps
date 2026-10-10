// SharpDeps map panel (SD-017: the v2 shell and the report bridge).
//
// The panel owns the webview and the message plumbing; everything that needs the
// analysis result is answered by the report bridge, and everything that needs extension
// work (starting or stopping an analysis) is delegated to the host callbacks. A message
// type the bridge does not implement is answered with an explicit error, never ignored.

import * as vscode from 'vscode';
import type { ReportBridge } from '../analyzer/reportBridge';
import type { ReportStore } from '../analyzer/reportStore';
import { openDeclarationLocation, openEvidenceLocation } from '../commands/openLocation';
import { buildContextExport, buildExportJson, buildMermaid } from '../export/contextExport';
import type { ContextEvidence, ContextExportInput } from '../export/contextExport';
import { getWebviewHtml } from './html';
import { PROTOCOL_VERSION, validateWebviewMessage } from './protocolV2';
import type { ExportFormat, HostToWebviewMessage, Scope } from './protocolV2';
import type { Granularity } from '../analyzer/reportV2';
import type { AnalysisProgressEvent } from '../analyzer/analysisController';
import type { ProfileRequest, Filters } from './protocolV2';
import { restoreViewState, serializeViewState } from '../../media/app/serializer';
import { INITIAL_STATE } from '../../media/app/state';

export interface CodeMapPanelHost {
  store: ReportStore;
  bridge: ReportBridge;
  output: vscode.OutputChannel;
  /** Directory the analysed paths are relative to, for opening locations. */
  rootDirectory: (analysisId?: string) => string | undefined;
  /** Target name for export file names. */
  targetName: () => string;
  /** Persists the small view state (SD-021). Never starts an analysis. */
  saveViewState: (state: Record<string, unknown>) => void;
  /** Reads the last small view state, if any (SD-021). */
  loadViewState: () => Record<string, unknown> | undefined;
  /** Starts an analysis for the current target in the requested mode. */
  onAnalyze: (mode: 'quick' | 'semantic', profile?: ProfileRequest) => void;
  /** Stops the running analysis. */
  onCancel: () => void;
  onDispose?: () => void;
  projectionLimits?: (granularity: Granularity) => { maxNodes: number; maxEdges: number };
  currentAnalysisId?: () => string | undefined;
  savedAnalysisState?: (
    analysisId: string
  ) => Extract<HostToWebviewMessage, { type: 'analysisComplete' }> | undefined;
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
      if (!CodeMapPanel.current.panel.visible) CodeMapPanel.current.ready = false;
      CodeMapPanel.current.panel.reveal(column);
      return CodeMapPanel.current;
    }

    const panel = vscode.window.createWebviewPanel(CodeMapPanel.viewType, 'SharpDeps', column, {
      enableScripts: true,
      // No command URIs and no form submission from the map (SD-023); the script only
      // talks through the validated message channel.
      enableCommandUris: false,
      enableForms: false,
      // Restore is verified (serializer + persistence + browser fixture), so the webview
      // is recreated when it is hidden instead of being kept alive (SD-021).
      retainContextWhenHidden: false,
      localResourceRoots: [vscode.Uri.joinPath(extensionUri, 'media')]
    });

    CodeMapPanel.current = new CodeMapPanel(panel, extensionUri, host);
    return CodeMapPanel.current;
  }

  static restore(
    panel: vscode.WebviewPanel,
    extensionUri: vscode.Uri,
    host: CodeMapPanelHost
  ): CodeMapPanel {
    panel.webview.options = {
      enableScripts: true,
      enableCommandUris: false,
      enableForms: false,
      localResourceRoots: [vscode.Uri.joinPath(extensionUri, 'media')]
    };
    CodeMapPanel.current = new CodeMapPanel(panel, extensionUri, host);
    return CodeMapPanel.current;
  }

  private readonly panel: vscode.WebviewPanel;
  private readonly disposables: vscode.Disposable[] = [];
  private ready = false;
  private pendingAnalysisId: string | undefined;
  private runningMessage: Extract<HostToWebviewMessage, { type: 'analysisStarted' }> | undefined;
  private viewState: Record<string, unknown> | undefined;
  private pendingReveal:
    { analysisId: string; entityId: string; scope: Scope; granularity?: Granularity } | undefined;

  private constructor(
    panel: vscode.WebviewPanel,
    extensionUri: vscode.Uri,
    private readonly host: CodeMapPanelHost
  ) {
    this.panel = panel;
    this.viewState = host.loadViewState();
    this.panel.webview.html = getWebviewHtml(this.panel.webview, extensionUri);

    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    this.panel.webview.onDidReceiveMessage(
      (message: unknown) => this.onMessage(message),
      null,
      this.disposables
    );
    this.panel.onDidChangeViewState(
      (event) => {
        if (!event.webviewPanel.visible) this.ready = false;
        setPanelActiveContext(event.webviewPanel.active);
      },
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
    this.runningMessage = undefined;
    this.pendingAnalysisId = analysisId;
    this.postAnalysisState(analysisId);
  }

  notifyStarted(
    analysisId: string,
    mode: 'quick' | 'semantic',
    target: { name: string; relativePath: string }
  ): void {
    this.runningMessage = { type: 'analysisStarted', analysisId, mode, target };
    if (this.ready) this.post(this.runningMessage);
  }
  notifyProgress(event: AnalysisProgressEvent): void {
    if (this.ready) this.post({ ...event, type: 'analysisProgress' });
  }
  notifyFailure(analysisId: string, message: string, cancelled = false): void {
    this.runningMessage = undefined;
    this.post({ type: 'analysisFailed', analysisId, message, cancelled });
  }
  requestExport(format: ExportFormat, copy?: boolean): void {
    this.post({ type: 'requestExport', format, copy });
  }

  /**
   * Asks the webview to select an entity (SD-019). The scope is sent with it, so the
   * view requests the projection it needs; nothing is pushed unrequested.
   */
  revealEntity(entityId: string, scope: Scope, granularity?: Granularity): void {
    const analysisId =
      this.pendingAnalysisId ??
      this.host.currentAnalysisId?.() ??
      this.host.store.currentAnalysisId;
    if (!analysisId) {
      void vscode.window.showInformationMessage(
        'SharpDeps: 解析結果がありません。先に解析してください。'
      );
      return;
    }

    this.pendingReveal = { analysisId, entityId, scope, granularity };
    this.flushReveal();
  }

  private flushReveal(): void {
    if (!this.ready || !this.pendingReveal) {
      return;
    }

    const reveal = this.pendingReveal;
    this.pendingReveal = undefined;
    this.post({
      type: 'reveal',
      analysisId: reveal.analysisId,
      entityId: reveal.entityId,
      scope: reveal.scope,
      granularity: reveal.granularity
    });
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
    // Saved data never falls through to legacy source/export handlers or their store.
    if (
      'analysisId' in request &&
      this.host.savedAnalysisState?.(request.analysisId) &&
      ['openEvidence', 'openDeclaration', 'copyContext', 'export'].includes(request.type)
    ) {
      void this.host.bridge.handle(request).then((response) => this.post(response));
      return;
    }
    switch (request.type) {
      case 'ready':
        this.ready = true;
        this.postViewState();
        void this.host.bridge
          .handle({ type: 'ready', protocolVersion: PROTOCOL_VERSION })
          .then((response) => this.post(response));
        if (
          this.pendingAnalysisId ??
          this.host.currentAnalysisId?.() ??
          this.host.store.currentAnalysisId
        ) {
          this.postAnalysisState(
            (this.pendingAnalysisId ??
              this.host.currentAnalysisId?.() ??
              this.host.store.currentAnalysisId)!
          );
        }
        if (this.runningMessage) this.post(this.runningMessage);
        this.flushReveal();
        return;
      case 'analyze':
        this.host.onAnalyze(request.mode, request.profile);
        return;
      case 'persistViewState': {
        const restored = restoreViewState(request.viewState);
        this.viewState = serializeViewState({
          ...INITIAL_STATE,
          ...restored.state
        }) as unknown as Record<string, unknown>;
        this.host.saveViewState(this.viewState);
        return;
      }
      case 'cancelAnalysis':
        this.host.onCancel();
        return;
      case 'openEvidence':
        // Resolved host-side from the opaque id: the webview never sends a path.
        void openEvidenceLocation(this.locationOptions, request.analysisId, request.evidenceId);
        return;
      case 'openDeclaration':
        void openDeclarationLocation(
          this.locationOptions,
          request.analysisId,
          request.entityId,
          request.declarationIndex
        );
        return;
      case 'copyContext':
        // Built and copied locally; nothing is sent anywhere.
        void this.copyContext(
          request.analysisId,
          request.scope,
          request.includeSnippets === true,
          request
        );
        return;
      case 'export':
        void this.exportSelection(
          request.analysisId,
          request.format,
          request.scope,
          request.data,
          request
        );
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

  /** Notifies the webview that the registered result is out of date (SD-021). */
  notifyStale(
    analysisId: string,
    reason: 'unsavedChange' | 'savedChange' | 'profileChange' | 'unknown',
    relativePaths?: string[]
  ): void {
    this.post({ type: 'stale', analysisId, reason, relativePaths });
  }

  /** Builds the export input for the webview's current selection (SD-022). */
  private async selectionExport(
    analysisId: string,
    scope: Scope,
    includeSnippets: boolean,
    selection: {
      granularity?: Granularity;
      filters?: Filters;
      search?: string;
      includeIds?: string[];
    } = {}
  ): Promise<ContextExportInput> {
    const store = this.host.store;
    const report = store.getReport(analysisId);
    const granularity = selection.granularity ?? (report.mode === 'quick' ? 'project' : 'type');
    const projection = store.getProjection(analysisId, {
      scope,
      ...selection,
      granularity,
      ...(this.host.projectionLimits?.(granularity) ?? { maxNodes: 300, maxEdges: 1000 })
    });

    const evidenceByRelation: Record<string, ContextEvidence[]> = {};
    for (const edge of projection.edges) {
      try {
        const page = await store.getEvidencePage(analysisId, edge.id, { limit: 5 });
        evidenceByRelation[edge.id] = page.items.map((item) => ({
          kind: item.kind,
          origin: item.origin,
          confidence: item.confidence,
          documentPath: report.sourceManifest.find((document) => document.id === item.documentId)
            ?.relativePath,
          line: item.physicalSpan ? item.physicalSpan.startLine + 1 : undefined,
          character: item.physicalSpan ? item.physicalSpan.startCharacter + 1 : undefined,
          snippet: includeSnippets ? (item.snippet ?? undefined) : undefined
        }));
      } catch {
        evidenceByRelation[edge.id] = [];
      }
    }

    return {
      analysisId,
      mode: report.mode === 'semantic' ? 'semantic' : 'quick',
      target: { name: this.host.targetName(), relativePath: report.target.relativePath },
      completeness: report.completeness,
      configuration: report.profile.configuration,
      platform: report.profile.platform,
      projectVariants: report.projects
        .filter((project) => project.targetFramework !== 'external')
        .map((project) => ({
          projectPath: project.relativePath,
          targetFramework: project.targetFramework
        })),
      filters: selection.filters as Record<string, unknown> | undefined,
      search: selection.search,
      limitations: report.limitations.map((limitation) => ({
        code: limitation.code,
        message: limitation.message
      })),
      granularity,
      scopeLabel: describeScope(scope),
      nodes: projection.nodes.map((node) => ({
        id: node.id,
        name: node.name,
        granularity: node.granularity,
        kind: node.kind,
        projectName: node.projectName,
        inCycle: node.inCycle,
        isExternal: node.isExternal
      })),
      edges: projection.edges.map((edge) => ({
        id: edge.id,
        sourceId: edge.sourceId,
        targetId: edge.targetId,
        basis: edge.basis,
        kinds: edge.kinds,
        evidenceCount: edge.evidenceCount,
        inCycle: edge.inCycle,
        generatedEvidenceCount: edge.generatedEvidenceCount,
        publicSurfaceEvidenceCount: edge.publicSurfaceEvidenceCount,
        underlyingRelationIds: edge.underlyingRelationIds,
        underlyingRelations: edge.underlyingRelations
      })),
      totalNodeCount: projection.totalNodeCount,
      totalEdgeCount: projection.totalEdgeCount,
      truncated: projection.truncated,
      cycles: report.cycleGroups.map((group) => ({
        id: group.id,
        scope: group.scope,
        basis: group.basis,
        memberIds: group.memberIds,
        internalRelationIds: group.internalRelationIds,
        witness: group.witness
          ? { memberIds: group.witness.memberIds, relationIds: group.witness.relationIds }
          : null
      })),
      evidenceByRelation,
      includeSnippets
    };
  }

  /** Copies Mermaid for the current selection (SD-022); keeps the existing command. */
  copyMermaid(): void {
    this.requestExport('mermaid', true);
  }

  private async copyContext(
    analysisId: string,
    scope: Scope,
    includeSnippets: boolean,
    selection: {
      granularity?: Granularity;
      filters?: Filters;
      search?: string;
      includeIds?: string[];
    } = {}
  ): Promise<void> {
    try {
      const input = await this.selectionExport(analysisId, scope, includeSnippets, selection);
      const text = buildContextExport(input);
      await vscode.env.clipboard.writeText(text);
      void vscode.window.showInformationMessage(
        `SharpDeps: 解析コンテキストをコピーしました（${input.nodes.length} ノード / ${input.edges.length} 関係、根拠付き）。送信は行いません。`
      );
    } catch (error) {
      void vscode.window.showErrorMessage(
        `SharpDeps: コンテキストを作成できませんでした。${
          error instanceof Error ? error.message : String(error)
        }`
      );
    }
  }

  private async exportSelection(
    analysisId: string,
    format: ExportFormat,
    scope: Scope,
    data: string | undefined,
    selection: {
      granularity?: Granularity;
      filters?: Filters;
      search?: string;
      includeIds?: string[];
      copy?: boolean;
    } = {}
  ): Promise<void> {
    try {
      if ((format === 'svg' || format === 'png') && data) {
        // The webview rendered the same selection, so image and text agree. The SVG is
        // standalone (no scripts, no external references) and the PNG is embedded base64.
        const uri = await vscode.window.showSaveDialog({
          defaultUri: vscode.Uri.file(
            `${this.host.targetName() || 'sharpdeps'}-selection.${format}`
          ),
          filters: format === 'svg' ? { 'SVG image': ['svg'] } : { 'PNG image': ['png'] }
        });
        if (!uri) {
          return;
        }

        const bytes =
          format === 'svg'
            ? Buffer.from(data, 'utf8')
            : Buffer.from(data.replace(/^data:image\/png;base64,/, ''), 'base64');
        await vscode.workspace.fs.writeFile(uri, bytes);
        void vscode.window.showInformationMessage(
          `SharpDeps: ${format.toUpperCase()} を保存しました。外部ビューアで開けます。`
        );
        return;
      }

      if (format === 'svg' || format === 'png')
        throw new Error('The graph is not ready to export. Switch to Graph and wait for layout.');
      const input = await this.selectionExport(analysisId, scope, false, selection);
      const text = format === 'json' ? buildExportJson(input) : buildMermaid(input);
      if (selection.copy) {
        await vscode.env.clipboard.writeText(text);
        void vscode.window.showInformationMessage(
          `SharpDeps: ${format.toUpperCase()} をコピーしました（${input.nodes.length} ノード / ${input.edges.length} 関係）。`
        );
        return;
      }
      const extension = format === 'json' ? 'json' : 'mmd';
      const uri = await vscode.window.showSaveDialog({
        defaultUri: vscode.Uri.file(
          `${this.host.targetName() || 'sharpdeps'}-${input.granularity}.${extension}`
        ),
        filters:
          format === 'json'
            ? { 'SharpDeps selection': ['json'] }
            : { 'Mermaid diagram': ['mmd', 'mermaid'] }
      });
      if (!uri) {
        return;
      }

      await vscode.workspace.fs.writeFile(uri, Buffer.from(text, 'utf8'));
      void vscode.window.showInformationMessage(
        `SharpDeps: ${format.toUpperCase()} を保存しました（${input.nodes.length} ノード / ${input.edges.length} 関係）。`
      );
    } catch (error) {
      void vscode.window.showErrorMessage(
        `SharpDeps: エクスポートに失敗しました。${
          error instanceof Error ? error.message : String(error)
        }`
      );
    }
  }

  /** Restores the last small state when the webview asks (SD-021). */
  private postViewState(): void {
    const state = this.host.loadViewState();
    if (state) {
      this.post({ type: 'viewState', state });
    }
  }

  private postAnalysisState(analysisId: string): void {
    if (!this.ready) {
      return;
    }

    try {
      const saved = this.host.savedAnalysisState?.(analysisId);
      if (saved) {
        this.post(saved);
        return;
      }
      const report = this.host.store.getReport(analysisId);
      this.post({
        type: 'analysisComplete',
        analysisId,
        completeness: report.completeness,
        coverage: report.coverage,
        mode: report.mode === 'semantic' ? 'semantic' : 'quick',
        limitations: report.limitations,
        capabilities: report.capabilities,
        target: { name: this.host.targetName(), relativePath: report.target.relativePath },
        profile: { configuration: report.profile.configuration, platform: report.profile.platform },
        variantOptions: report.profile.projectVariants
          .filter((variant) => variant.targetFramework !== 'external')
          .map((variant) => ({
            projectLogicalId: variant.projectLogicalId,
            targetFramework: variant.targetFramework,
            projectPath:
              report.projects.find((project) => project.variantId === variant.variantId)
                ?.relativePath ?? variant.projectLogicalId
          }))
      });
      if (this.host.store.isStale(analysisId)) this.notifyStale(analysisId, 'unknown');
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

  /** Stable options object for opening locations (SD-019). */
  private readonly locationOptions = {
    store: this.host.store,
    output: this.host.output,
    rootDirectory: (analysisId?: string) => this.host.rootDirectory(analysisId)
  };

  private post(message: HostToWebviewMessage): void {
    void this.panel.webview.postMessage(message);
  }

  dispose(): void {
    this.host.onDispose?.();
    CodeMapPanel.current = undefined;
    setPanelActiveContext(false);
    this.panel.dispose();
    while (this.disposables.length) {
      this.disposables.pop()?.dispose();
    }
  }
}

function describeScope(scope: Scope): string {
  if (!scope || scope.kind === 'root') {
    return 'whole analysis';
  }

  const depth =
    scope.kind === 'dependencies' || scope.kind === 'dependents'
      ? ` (depth ${scope.depth ?? 1})`
      : '';
  return `${scope.kind}: ${scope.id ?? '?'}${depth}`;
}

function setPanelActiveContext(active: boolean): void {
  void vscode.commands.executeCommand('setContext', 'sharpdeps.panelActive', active);
}
