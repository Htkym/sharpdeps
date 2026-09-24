// Selection state and keyboard interaction for the SVG graph (SD-004).
//
// Selection is plain state plus events: the renderer only receives id sets, and
// the camera is never touched by a selection change. Keyboard users can reach
// every node and edge because both are focusable, and Enter opens the selection.

export interface GraphSelection {
  nodeIds: ReadonlySet<string>;
  edgeIds: ReadonlySet<string>;
}

export const EMPTY_SELECTION: GraphSelection = {
  nodeIds: new Set<string>(),
  edgeIds: new Set<string>()
};

export interface SelectionController {
  get(): GraphSelection;
  selectNode(id: string, additive?: boolean): void;
  selectEdge(id: string, additive?: boolean): void;
  /** Replaces the whole selection in one change event. */
  set(nodeIds: Iterable<string>, edgeIds: Iterable<string>): void;
  clear(): void;
  onChange(listener: (selection: GraphSelection) => void): () => void;
  /** Opens (activates) the current selection; used by Enter and double click. */
  onActivate(listener: (selection: GraphSelection) => void): () => void;
}

export function createSelectionController(
  initial: GraphSelection = EMPTY_SELECTION
): SelectionController {
  let selection: GraphSelection = {
    nodeIds: new Set(initial.nodeIds),
    edgeIds: new Set(initial.edgeIds)
  };
  const listeners = new Set<(selection: GraphSelection) => void>();
  const activateListeners = new Set<(selection: GraphSelection) => void>();

  const emit = (): void => {
    for (const listener of listeners) {
      listener(selection);
    }
  };

  return {
    get: () => selection,
    selectNode(id, additive = false) {
      const nodeIds = additive ? new Set(selection.nodeIds) : new Set<string>();
      if (additive && nodeIds.has(id)) {
        nodeIds.delete(id);
      } else {
        nodeIds.add(id);
      }
      selection = { nodeIds, edgeIds: additive ? new Set(selection.edgeIds) : new Set<string>() };
      emit();
    },
    selectEdge(id, additive = false) {
      const edgeIds = additive ? new Set(selection.edgeIds) : new Set<string>();
      if (additive && edgeIds.has(id)) {
        edgeIds.delete(id);
      } else {
        edgeIds.add(id);
      }
      selection = { nodeIds: additive ? new Set(selection.nodeIds) : new Set<string>(), edgeIds };
      emit();
    },
    set(nodeIds, edgeIds) {
      const next: GraphSelection = {
        nodeIds: new Set(nodeIds),
        edgeIds: new Set(edgeIds)
      };
      if (
        next.nodeIds.size === selection.nodeIds.size &&
        next.edgeIds.size === selection.edgeIds.size &&
        [...next.nodeIds].every((id) => selection.nodeIds.has(id)) &&
        [...next.edgeIds].every((id) => selection.edgeIds.has(id))
      ) {
        return;
      }

      selection = next;
      emit();
    },
    clear() {
      if (selection.nodeIds.size === 0 && selection.edgeIds.size === 0) {
        return;
      }
      selection = EMPTY_SELECTION;
      emit();
    },
    onChange(listener) {
      listeners.add(listener);
      return () => listeners.delete(listener);
    },
    onActivate(listener) {
      activateListeners.add(listener);
      return () => activateListeners.delete(listener);
    }
  };
}

export interface InteractionOptions {
  /** Called when a node or edge is activated (Enter, double click). */
  onActivate?: (selection: GraphSelection) => void;
}

/**
 * Wires pointer and keyboard interaction onto the rendered layers. The handler
 * does not depend on hover, so touch and keyboard users get the same behavior.
 */
export function wireGraphInteraction(
  svg: SVGSVGElement,
  selection: SelectionController,
  options: InteractionOptions = {}
): () => void {
  const activate = (): void => {
    options.onActivate?.(selection.get());
  };

  const findTarget = (
    target: EventTarget | null
  ): { kind: 'node' | 'edge'; id: string } | undefined => {
    if (!(target instanceof Element)) {
      return undefined;
    }
    const element = target.closest('g.node, g.edge');
    if (!element) {
      return undefined;
    }
    const id = element.getAttribute('data-id');
    if (!id) {
      return undefined;
    }
    return { kind: element.classList.contains('node') ? 'node' : 'edge', id };
  };

  const onClick = (event: MouseEvent): void => {
    const hit = findTarget(event.target);
    if (!hit) {
      selection.clear();
      return;
    }
    if (hit.kind === 'node') {
      selection.selectNode(hit.id, event.ctrlKey || event.metaKey);
    } else {
      selection.selectEdge(hit.id, event.ctrlKey || event.metaKey);
    }
  };

  const onDoubleClick = (event: MouseEvent): void => {
    if (findTarget(event.target)) {
      activate();
    }
  };

  const onKeyDown = (event: KeyboardEvent): void => {
    if (event.key === 'Enter' || event.key === ' ') {
      const focused = document.activeElement;
      const hit = findTarget(focused);
      if (!hit) {
        return;
      }
      event.preventDefault();
      if (hit.kind === 'node') {
        selection.selectNode(hit.id);
      } else {
        selection.selectEdge(hit.id);
      }
      activate();
      return;
    }

    if (event.key === 'Escape') {
      selection.clear();
    }
  };

  svg.addEventListener('click', onClick);
  svg.addEventListener('dblclick', onDoubleClick);
  svg.addEventListener('keydown', onKeyDown);
  return () => {
    svg.removeEventListener('click', onClick);
    svg.removeEventListener('dblclick', onDoubleClick);
    svg.removeEventListener('keydown', onKeyDown);
  };
}
