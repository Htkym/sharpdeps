// Ambient declaration for elkjs's GWT layout engine.
//
// The package ships `elk-worker.d.ts` as `export type Worker = Worker`, which does
// not describe the runtime value. This declaration mirrors the subset of the DOM
// Worker interface that the engine implements when it runs synchronously.
//
// esbuild.js rewrites this module so the engine exports `Worker` instead of
// registering itself as a real worker (see elkFakeWorkerPlugin).

declare module 'elkjs/lib/elk-worker.min.js' {
  export class Worker {
    constructor(url?: string);
    postMessage(message: unknown): void;
    terminate(): void;
    onmessage: ((event: { data: unknown }) => void) | null;
  }
}
