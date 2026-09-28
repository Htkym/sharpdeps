// Keyboard map (SD-024): the resolver decides what a key means without touching the DOM.

import { describe, expect, it } from 'vitest';
import { SHORTCUT_HELP, resolveShortcut } from '../../media/app/shortcuts';

const idle = { typing: false, inspectorOpen: false, graphFocused: false };
const typing = { ...idle, typing: true };
const graph = { ...idle, graphFocused: true };

describe('resolveShortcut', () => {
  it('routes search, view switching, and selection without a mouse', () => {
    expect(resolveShortcut({ key: '/' }, idle)).toEqual({ type: 'focusSearch' });
    expect(resolveShortcut({ key: 'g' }, idle)).toEqual({ type: 'viewKind', viewKind: 'graph' });
    expect(resolveShortcut({ key: 't' }, idle)).toEqual({ type: 'viewKind', viewKind: 'table' });
    expect(resolveShortcut({ key: 'Escape' }, idle)).toEqual({ type: 'clearSelection' });
  });

  it('closes the inspector first and leaves typing alone', () => {
    expect(resolveShortcut({ key: 'Escape' }, { ...idle, inspectorOpen: true })).toEqual({
      type: 'closeInspector'
    });
    // While typing, letters and Escape must reach the input.
    expect(resolveShortcut({ key: 'g' }, typing)).toBeUndefined();
    expect(resolveShortcut({ key: 'Escape' }, typing)).toBeUndefined();
  });

  it('only zooms while the graph has focus and never steals host combinations', () => {
    expect(resolveShortcut({ key: '+' }, graph)).toEqual({ type: 'zoom', direction: 'in' });
    expect(resolveShortcut({ key: '-' }, graph)).toEqual({ type: 'zoom', direction: 'out' });
    expect(resolveShortcut({ key: '0' }, graph)).toEqual({ type: 'zoom', direction: 'fit' });
    expect(resolveShortcut({ key: '+' }, idle)).toBeUndefined();
    expect(resolveShortcut({ key: 'g', ctrlKey: true }, idle)).toBeUndefined();
  });

  it('documents every shortcut it resolves', () => {
    expect(SHORTCUT_HELP.map((entry) => entry.keys)).toEqual([
      '/',
      'g / t',
      'Tab / Shift+Tab',
      'Enter / Space',
      'Esc',
      '+ / - / 0'
    ]);
  });
});
