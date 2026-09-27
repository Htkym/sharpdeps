// View state persistence (SD-021): round trip, version guard, and clamped restore.

import { describe, expect, it } from 'vitest';
import { INITIAL_STATE } from '../../media/app/state';
import {
  VIEW_STATE_VERSION,
  deserializeViewState,
  restoreViewState,
  serializeViewState
} from '../../media/app/serializer';

describe('view state serialization', () => {
  it('round-trips the small state and the camera', () => {
    const state = {
      ...INITIAL_STATE,
      target: { name: 'Sample.sln', relativePath: 'src/Sample.sln' },
      selection: { entityId: 'ty_1111111111111111' },
      filters: { includeGenerated: false, kinds: ['class'] },
      granularity: 'namespace' as const,
      viewKind: 'table' as const,
      scope: { kind: 'dependencies' as const, id: 'ty_1111111111111111', depth: 2 },
      search: 'Order',
      paneWidths: { navigation: 260, inspector: 400 },
      tablePage: 2,
      layout: { direction: 'DOWN' as const, nodeSpacing: 40, rankSpacing: 80 }
    };

    const stored = serializeViewState(state, { zoom: 1.4, scrollLeft: 30, scrollTop: 12 });
    const restored = deserializeViewState(stored);

    expect(restored.versionMismatch).toBe(false);
    expect(restored.state).toMatchObject({
      target: { name: 'Sample.sln', relativePath: 'src/Sample.sln' },
      selection: { entityId: 'ty_1111111111111111' },
      filters: { includeGenerated: false, kinds: ['class'] },
      granularity: 'namespace',
      viewKind: 'table',
      scope: { kind: 'dependencies', id: 'ty_1111111111111111', depth: 2 },
      search: 'Order',
      paneWidths: { navigation: 260, inspector: 400 },
      tablePage: 2,
      layout: { direction: 'DOWN', nodeSpacing: 40, rankSpacing: 80 }
    });

    const withCamera = restoreViewState(stored);
    expect(withCamera.state.camera).toEqual({ zoom: 1.4, scrollLeft: 30, scrollTop: 12 });
  });

  it('restores old or invalid direction settings as horizontal', () => {
    for (const direction of [undefined, 'UP', 3]) {
      const restored = deserializeViewState({
        version: VIEW_STATE_VERSION,
        layout: { direction, nodeSpacing: 40, rankSpacing: 80 }
      });
      expect(restored.state.layout?.direction).toBe('RIGHT');
    }
  });

  it('ignores a different schema version instead of guessing', () => {
    const oldSchema = { ...serializeViewState(INITIAL_STATE), version: VIEW_STATE_VERSION - 1 };
    expect(deserializeViewState(oldSchema)).toEqual({ state: {}, versionMismatch: true });
    expect(deserializeViewState('not an object')).toEqual({ state: {}, versionMismatch: false });
    expect(deserializeViewState(null)).toEqual({ state: {}, versionMismatch: false });
  });

  it('clamps pane widths so a wide-screen state cannot break a narrow window', () => {
    const state = serializeViewState({
      ...INITIAL_STATE,
      paneWidths: { navigation: 4000, inspector: 5 }
    });
    const restored = deserializeViewState(state);

    expect(restored.state.paneWidths).toEqual({ navigation: 480, inspector: 200 });
  });

  it('repairs a malformed scope and camera instead of restoring them', () => {
    const restored = restoreViewState({
      version: VIEW_STATE_VERSION,
      scope: { kind: 'dependencies' },
      camera: { zoom: 'wide', scrollLeft: 4 },
      filters: { kinds: 'class' },
      tablePage: -3
    });

    expect(restored.state.scope).toEqual({ kind: 'root', id: null, depth: null });
    expect(restored.state.camera).toBeUndefined();
    expect(restored.state.filters).toEqual({});
    expect(restored.state.tablePage).toBeUndefined();
  });
});
