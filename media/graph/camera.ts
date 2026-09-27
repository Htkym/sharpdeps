// Camera (zoom and pan) for the SVG graph (SD-004).
//
// Zoom is expressed as the rendered size of the SVG element relative to its
// viewBox (the content bounds): the viewBox never changes, so one user unit is
// always one layout pixel, and the browser scales the content for us. The
// container scrolls naturally, which keeps panning and `fit` predictable.
//
// The camera is only changed by an explicit user action (wheel, drag, controls).
// Selecting a node, opening the inspector, or switching theme must not re-fit or
// re-layout the graph.

export interface CameraState {
  zoom: number;
  scrollLeft: number;
  scrollTop: number;
}

export const CAMERA_LIMITS = { min: 0.05, max: 8 } as const;

export class Camera {
  private zoomValue = 1;
  private contentWidth = 0;
  private contentHeight = 0;

  constructor(
    private readonly viewport: HTMLElement,
    private readonly svg: SVGSVGElement,
    private readonly onChange?: (state: CameraState) => void
  ) {}

  get zoom(): number {
    return this.zoomValue;
  }

  applyState(state: Partial<CameraState>): void {
    if (typeof state.zoom === 'number' && Number.isFinite(state.zoom)) {
      this.setZoom(state.zoom);
    }

    if (typeof state.scrollLeft === 'number') {
      this.viewport.scrollLeft = state.scrollLeft;
    }

    if (typeof state.scrollTop === 'number') {
      this.viewport.scrollTop = state.scrollTop;
    }

    this.onChange?.(this.state);
  }

  get state(): CameraState {
    return {
      zoom: this.zoomValue,
      scrollLeft: this.viewport.scrollLeft,
      scrollTop: this.viewport.scrollTop
    };
  }

  /** Records the layout bounds. Does not change the zoom. */
  setContentSize(width: number, height: number): void {
    this.contentWidth = Math.max(0, width);
    this.contentHeight = Math.max(0, height);
    this.apply();
  }

  apply(): void {
    if (this.contentWidth <= 0 || this.contentHeight <= 0) {
      return;
    }

    this.svg.setAttribute(
      'viewBox',
      `0 0 ${round(this.contentWidth)} ${round(this.contentHeight)}`
    );
    this.svg.setAttribute('width', String(round(this.contentWidth * this.zoomValue)));
    this.svg.setAttribute('height', String(round(this.contentHeight * this.zoomValue)));
    this.onChange?.(this.state);
  }

  setZoom(zoom: number, focusClientX?: number, focusClientY?: number): void {
    const next = clamp(zoom, CAMERA_LIMITS.min, CAMERA_LIMITS.max);
    if (next === this.zoomValue) {
      return;
    }

    const rect = this.viewport.getBoundingClientRect();
    const focusX = focusClientX === undefined ? rect.width / 2 : focusClientX - rect.left;
    const focusY = focusClientY === undefined ? rect.height / 2 : focusClientY - rect.top;
    const contentX = (this.viewport.scrollLeft + focusX) / this.zoomValue;
    const contentY = (this.viewport.scrollTop + focusY) / this.zoomValue;

    this.zoomValue = next;
    this.apply();

    this.viewport.scrollLeft = contentX * next - focusX;
    this.viewport.scrollTop = contentY * next - focusY;
  }

  zoomBy(factor: number, focusClientX?: number, focusClientY?: number): void {
    this.setZoom(this.zoomValue * factor, focusClientX, focusClientY);
  }

  /** Fits the content into the viewport. Only called on an explicit request. */
  fit(padding = 24): void {
    if (this.contentWidth <= 0 || this.contentHeight <= 0) {
      return;
    }
    const availableWidth = Math.max(1, this.viewport.clientWidth - padding * 2);
    const availableHeight = Math.max(1, this.viewport.clientHeight - padding * 2);
    this.zoomValue = clamp(
      Math.min(availableWidth / this.contentWidth, availableHeight / this.contentHeight),
      CAMERA_LIMITS.min,
      1.5
    );
    this.apply();
    this.viewport.scrollLeft = 0;
    this.viewport.scrollTop = 0;
  }

  restore(state: CameraState): void {
    this.zoomValue = clamp(state.zoom, CAMERA_LIMITS.min, CAMERA_LIMITS.max);
    this.apply();
    this.viewport.scrollLeft = state.scrollLeft;
    this.viewport.scrollTop = state.scrollTop;
  }

  wireWheel(): () => void {
    const handler = (event: WheelEvent): void => {
      if (!event.ctrlKey && !event.metaKey) {
        return;
      }
      event.preventDefault();
      this.zoomBy(event.deltaY < 0 ? 1.1 : 1 / 1.1, event.clientX, event.clientY);
    };
    this.viewport.addEventListener('wheel', handler, { passive: false });
    return () => this.viewport.removeEventListener('wheel', handler);
  }

  wireDrag(): () => void {
    let dragging = false;
    let startX = 0;
    let startY = 0;
    let startLeft = 0;
    let startTop = 0;

    const onPointerDown = (event: PointerEvent): void => {
      if (event.button !== 0 || (event.target as Element).closest('g.node, g.edge')) {
        return;
      }
      dragging = true;
      startX = event.clientX;
      startY = event.clientY;
      startLeft = this.viewport.scrollLeft;
      startTop = this.viewport.scrollTop;
      this.viewport.setPointerCapture(event.pointerId);
      this.viewport.classList.add('panning');
    };

    const onPointerMove = (event: PointerEvent): void => {
      if (!dragging) {
        return;
      }
      this.viewport.scrollLeft = startLeft - (event.clientX - startX);
      this.viewport.scrollTop = startTop - (event.clientY - startY);
    };

    const onPointerUp = (event: PointerEvent): void => {
      if (!dragging) {
        return;
      }
      dragging = false;
      this.viewport.releasePointerCapture(event.pointerId);
      this.viewport.classList.remove('panning');
    };

    this.viewport.addEventListener('pointerdown', onPointerDown);
    this.viewport.addEventListener('pointermove', onPointerMove);
    this.viewport.addEventListener('pointerup', onPointerUp);
    this.viewport.addEventListener('pointercancel', onPointerUp);
    return () => {
      this.viewport.removeEventListener('pointerdown', onPointerDown);
      this.viewport.removeEventListener('pointermove', onPointerMove);
      this.viewport.removeEventListener('pointerup', onPointerUp);
      this.viewport.removeEventListener('pointercancel', onPointerUp);
    };
  }
}

function clamp(value: number, min: number, max: number): number {
  return Math.min(Math.max(value, min), max);
}

function round(value: number): number {
  return Math.round(value * 100) / 100;
}
