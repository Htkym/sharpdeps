const { defineConfig } = require('@playwright/test');

module.exports = defineConfig({
  testDir: './tests/browser',
  outputDir: '.local/browser-results',
  // Keep timing measurements isolated from other browser fixtures.
  workers: 1,
  use: { baseURL: 'http://127.0.0.1:4173', screenshot: 'only-on-failure' },
  webServer: {
    command: 'node scripts/serve-webview.mjs 4173',
    url: 'http://127.0.0.1:4173/tests/webview/fixtures/shell-fixture.html',
    reuseExistingServer: !process.env.CI
  }
});
