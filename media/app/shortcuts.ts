// Keyboard shortcuts (SD-024).
//
// Pure so the key map is testable: the resolver only decides which action a key means
// in the current context (where focus is, whether the inspector is open). Nothing here
// touches the DOM.

export type ShortcutAction =
  | { type: 'focusSearch' }
  | { type: 'viewKind'; viewKind: 'graph' | 'table' }
  | { type: 'clearSelection' }
  | { type: 'closeInspector' }
  | { type: 'zoom'; direction: 'in' | 'out' | 'fit' };

export interface ShortcutContext {
  /** True when focus is inside a text field: shortcuts must not eat typing. */
  typing: boolean;
  inspectorOpen: boolean;
  /** True when an element inside the graph viewport has focus. */
  graphFocused: boolean;
}

export interface ShortcutEventLike {
  key: string;
  ctrlKey?: boolean;
  metaKey?: boolean;
  altKey?: boolean;
}

export function resolveShortcut(
  event: ShortcutEventLike,
  context: ShortcutContext
): ShortcutAction | undefined {
  // Ctrl/Cmd combinations belong to the host (VS Code).
  if (event.ctrlKey || event.metaKey || event.altKey) {
    return undefined;
  }

  if (event.key === 'Escape') {
    if (context.inspectorOpen) {
      return { type: 'closeInspector' };
    }

    return context.typing ? undefined : { type: 'clearSelection' };
  }

  if (context.typing) {
    return undefined;
  }

  switch (event.key) {
    case '/':
      return { type: 'focusSearch' };
    case 'g':
      return { type: 'viewKind', viewKind: 'graph' };
    case 't':
      return { type: 'viewKind', viewKind: 'table' };
    case '+':
    case '=':
      return context.graphFocused ? { type: 'zoom', direction: 'in' } : undefined;
    case '-':
      return context.graphFocused ? { type: 'zoom', direction: 'out' } : undefined;
    case '0':
      return context.graphFocused ? { type: 'zoom', direction: 'fit' } : undefined;
    default:
      return undefined;
  }
}

/** Shortcut list shown to the user (and used by the accessibility checklist). */
export const SHORTCUT_HELP: ReadonlyArray<{ keys: string; description: string }> = [
  { keys: '/', description: '検索へフォーカス' },
  { keys: 'g / t', description: 'グラフ / テーブル表示' },
  { keys: 'Tab / Shift+Tab', description: '操作要素を移動' },
  { keys: 'Enter / Space', description: '選択・活性化（ノード・辺・行）' },
  { keys: 'Esc', description: 'Inspectorを閉じる / 選択解除' },
  { keys: '+ / - / 0', description: 'グラフの拡大・縮小・フィット' }
];
