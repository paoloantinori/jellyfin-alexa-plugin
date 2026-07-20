import { defineConfig } from "@playwright/test";

export default defineConfig({
  testDir: ".",
  timeout: 60_000,
  retries: 0,
  use: {
    baseURL: process.env.JELLYFIN_URL || "http://minix:8096",
    actionTimeout: 10_000,
    launchOptions: {
      executablePath: process.env.CHROME_PATH || "/usr/bin/chromium-browser",
      args: ["--no-sandbox"],
    },
    screenshot: "only-on-failure",
  },
});
