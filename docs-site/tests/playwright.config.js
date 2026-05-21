const { defineConfig } = require('@playwright/test');

module.exports = defineConfig({
  testDir: '.',
  timeout: 30000,
  retries: 1,
  use: {
    baseURL: process.env.SITE_URL || 'http://localhost:8765',
    headless: true,
    viewport: { width: 1280, height: 900 },
    actionTimeout: 5000,
  },
});
