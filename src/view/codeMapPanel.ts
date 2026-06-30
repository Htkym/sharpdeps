import * as vscode from 'vscode';
import { getWebviewHtml } from './html';
import { saveExport } from '../export/exportGraph';
import type {
  CodeMapViewModel,
  ExportFormat,
  Granularity,
  HostToWebviewMessage,
  ThemeKind,
  WebviewToHostMessage
} from './protocol';

/** Singleton webview panel (an editor tab) that renders the dependency map. */
export class CodeMapPanel {
  public static readonly viewType = 'sharpdeps.codeMap';
  private static current: CodeMapPanel | undefined;

  static get currentPanel(): CodeMapPanel | undefined {
    return CodeMapPanel.current;
  }

  private readonly panel: vscode.WebviewPanel;
  private readonly disposables: vscode.Disposable[] = [];
  private model: CodeMapViewModel | undefined;
  private ready = false;

  static show(extensionUri: vscode.Uri, output: vscode.OutputChannel): CodeMapPanel {
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

    CodeMapPanel.current = new CodeMapPanel(panel, extensionUri, output);
    return CodeMapPanel.current;
  }

  private constructor(
    panel: vscode.WebviewPanel,
    extensionUri: vscode.Uri,
    private readonly output: vscode.OutputChannel
  ) {
    this.panel = panel;
    this.panel.webview.html = getWebviewHtml(this.panel.webview, extensionUri);

    this.panel.onDidDispose(() => this.dispose(), null, this.disposables);
    this.panel.webview.onDidReceiveMessage(
      (message: WebviewToHostMessage) => this.onMessage(message),
      null,
      this.disposables
    );
    this.panel.onDidChangeViewState(
      (event) => setPanelActiveContext(event.webviewPanel.active),
      null,
      this.disposables
    );
    vscode.window.onDidChangeActiveColorTheme(
      () => this.post({ type: 'theme', theme: currentTheme() }),
      null,
      this.disposables
    );

    setPanelActiveContext(this.panel.active);
  }

  setModel(model: CodeMapViewModel): void {
    this.model = model;
    this.panel.title = model.solutionName ? `SharpDeps — ${model.solutionName}` : 'SharpDeps';
    if (this.ready) {
      this.post({ type: 'render', model, theme: currentTheme() });
    }
  }

  copyMermaid(): void {
    this.post({ type: 'doCopyMermaid' });
  }

  export(format: ExportFormat): void {
    this.post({ type: 'doExport', format });
  }

  setGranularity(granularity: Granularity): void {
    this.post({ type: 'setGranularity', granularity });
  }

  private onMessage(message: WebviewToHostMessage): void {
    switch (message.type) {
      case 'ready':
        this.ready = true;
        if (this.model) {
          this.post({ type: 'render', model: this.model, theme: currentTheme() });
        }
        break;
      case 'copyMermaid':
        void vscode.env.clipboard
          .writeText(message.text)
          .then(() =>
            vscode.window.showInformationMessage('SharpDeps: Mermaid source copied to clipboard.')
          );
        break;
      case 'export':
        void saveExport(
          message.format,
          message.data,
          message.granularity,
          this.model?.solutionName ?? ''
        );
        break;
      case 'exportError':
        void vscode.window.showErrorMessage(`SharpDeps: Export failed. ${message.message}`);
        break;
      case 'log':
        this.output.appendLine(`[viewer:${message.level}] ${message.message}`);
        break;
    }
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

function currentTheme(): ThemeKind {
  const kind = vscode.window.activeColorTheme.kind;
  return kind === vscode.ColorThemeKind.Light || kind === vscode.ColorThemeKind.HighContrastLight
    ? 'light'
    : 'dark';
}
