// Build script for the SharpDeps extension.
// Bundles the extension host, webview shell, and ELK layout worker.
const esbuild = require('esbuild');
const fs = require('fs');

const production = process.argv.includes('--production');
const watch = process.argv.includes('--watch');

/**
 * elkjs's worker script decides at load time whether to register itself as a real
 * worker (`self.onmessage = ...`) or to export a synchronous "fake worker". Inside
 * our own worker it would take the real-worker branch, so the layout engine cannot
 * be imported. Shadowing `document` inside that one module selects the documented
 * non-worker path (the same one Node uses): the engine then runs synchronously in
 * the current thread and is exported as `Worker`. `self` stays intact because the
 * GWT bootstrap needs the global namespace object.
 *
 * @type {import('esbuild').Plugin}
 */
const elkFakeWorkerPlugin = {
  name: 'elk-fake-worker',
  setup(build) {
    build.onLoad({ filter: /elkjs[\\/]lib[\\/]elk-worker\.min\.js$/ }, async (args) => {
      const source = await fs.promises.readFile(args.path, 'utf8');
      // A truthy `document` selects the non-worker branch; the value is never used
      // by the layout engine.
      return { contents: `var document = {};\n${source}`, loader: 'js' };
    });
  }
};

/** @type {import('esbuild').BuildOptions} */
const common = {
  bundle: true,
  sourcemap: !production,
  minify: production,
  logLevel: 'info'
};

/** @type {import('esbuild').BuildOptions} */
const extensionConfig = {
  ...common,
  entryPoints: ['src/extension.ts'],
  outfile: 'out/extension.js',
  platform: 'node',
  format: 'cjs',
  target: 'node18',
  external: ['vscode']
};

// The layout worker is bundled on its own because the webview starts it from a
// Blob URL (a worker script cannot be loaded directly from the resource URI).
/** @type {import('esbuild').BuildOptions} */
const layoutWorkerConfig = {
  ...common,
  entryPoints: ['media/workers/elkLayout.worker.ts'],
  outfile: 'media/workers/elkLayout.worker.js',
  platform: 'browser',
  format: 'iife',
  target: 'es2020',
  plugins: [elkFakeWorkerPlugin]
};

// Webview shell.
/** @type {import('esbuild').BuildOptions} */
const shellConfig = {
  ...common,
  entryPoints: ['media/app/entry.ts'],
  outfile: 'media/app/entry.js',
  platform: 'browser',
  format: 'iife',
  target: 'es2020',
  define: {
    'process.env.NODE_ENV': production ? '"production"' : '"development"'
  }
};

async function main() {
  if (watch) {
    const contexts = await Promise.all([
      esbuild.context(extensionConfig),
      esbuild.context(layoutWorkerConfig),
      esbuild.context(shellConfig)
    ]);
    await Promise.all(contexts.map((c) => c.watch()));
    console.log('[esbuild] watching for changes...');
  } else {
    await Promise.all([
      esbuild.build(extensionConfig),
      esbuild.build(layoutWorkerConfig),
      esbuild.build(shellConfig)
    ]);
    console.log('[esbuild] build complete');
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
