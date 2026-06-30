// Build script for the SharpDeps extension.
// Produces two bundles:
//   - src/extension.ts  -> out/extension.js      (Node, CommonJS, 'vscode' external)
//   - media/viewer.ts   -> media/viewer.js        (browser, IIFE, mermaid bundled in)
const esbuild = require('esbuild');

const production = process.argv.includes('--production');
const watch = process.argv.includes('--watch');

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

/** @type {import('esbuild').BuildOptions} */
const viewerConfig = {
  ...common,
  entryPoints: ['media/viewer.ts'],
  outfile: 'media/viewer.js',
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
      esbuild.context(viewerConfig)
    ]);
    await Promise.all(contexts.map((c) => c.watch()));
    console.log('[esbuild] watching for changes...');
  } else {
    await Promise.all([esbuild.build(extensionConfig), esbuild.build(viewerConfig)]);
    console.log('[esbuild] build complete');
  }
}

main().catch((err) => {
  console.error(err);
  process.exit(1);
});
