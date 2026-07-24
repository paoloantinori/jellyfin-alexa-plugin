import { test, expect } from "@playwright/test";
import { BASE_URL, PLUGIN_ID, getAccessToken } from "./helpers";

test.describe("Plugin health checks", () => {
  test("icon endpoint serves small icon as PNG", async ({ page }) => {
    const resp = await page.request.get(`${BASE_URL}/alexaskill/api/icon-small`);
    expect(resp.ok(), `icon-small returned ${resp.status()}`).toBeTruthy();
    expect(resp.headers()["content-type"]).toContain("image/png");
    const body = await resp.body();
    expect(body.length, "icon-small should not be empty").toBeGreaterThan(100);
  });

  test("icon endpoint serves large icon as PNG", async ({ page }) => {
    const resp = await page.request.get(`${BASE_URL}/alexaskill/api/icon-large`);
    expect(resp.ok(), `icon-large returned ${resp.status()}`).toBeTruthy();
    expect(resp.headers()["content-type"]).toContain("image/png");
    const body = await resp.body();
    expect(body.length, "icon-large should not be empty").toBeGreaterThan(100);
  });

  test("icon endpoints work without authentication", async ({ page }) => {
    const small = await page.request.get(`${BASE_URL}/alexaskill/api/icon-small`);
    const large = await page.request.get(`${BASE_URL}/alexaskill/api/icon-large`);
    expect(small.ok()).toBeTruthy();
    expect(large.ok()).toBeTruthy();
  });

  test("small icon is smaller than large icon", async ({ page }) => {
    const small = await page.request.get(`${BASE_URL}/alexaskill/api/icon-small`);
    const large = await page.request.get(`${BASE_URL}/alexaskill/api/icon-large`);
    const smallBody = await small.body();
    const largeBody = await large.body();
    expect(smallBody.length, "small icon should be smaller than large").toBeLessThan(largeBody.length);
  });

  test("plugin dashboard logo image returns 200", async ({ page }) => {
    const token = await getAccessToken(page);
    const pluginsResp = await page.request.get(`${BASE_URL}/Plugins`, {
      headers: { "X-Emby-Token": token },
    });
    expect(pluginsResp.ok()).toBeTruthy();
    const plugins = await pluginsResp.json();
    const alexaPlugin = plugins.find((p: any) => p.Name === "Alexa Skill");
    expect(alexaPlugin).toBeDefined();

    const version = alexaPlugin.Version;
    const imageResp = await page.request.get(
      `${BASE_URL}/Plugins/${PLUGIN_ID}/${version}/Image`,
    );
    expect(imageResp.ok(), `Plugin image returned ${imageResp.status()}`).toBeTruthy();
    expect(imageResp.headers()["content-type"]).toContain("image/png");
    const body = await imageResp.body();
    expect(body.length, "plugin image should not be empty").toBeGreaterThan(100);
  });

  test("plugin appears in installed plugins list", async ({ page }) => {
    const token = await getAccessToken(page);
    const resp = await page.request.get(`${BASE_URL}/Plugins`, {
      headers: { "X-Emby-Token": token },
    });
    expect(resp.ok()).toBeTruthy();
    const plugins = await resp.json();
    const alexaPlugin = plugins.find((p: any) => p.Name === "Alexa Skill");
    expect(alexaPlugin, "Alexa Skill plugin should be installed").toBeDefined();
    expect(alexaPlugin.Version).toMatch(/^\d+\.\d+\.\d+/);
  });

  test("plugin configuration API returns valid config", async ({ page }) => {
    const token = await getAccessToken(page);
    const resp = await page.request.get(`${BASE_URL}/Plugins/${PLUGIN_ID}/Configuration`, {
      headers: { "X-Emby-Token": token },
    });
    expect(resp.ok()).toBeTruthy();
    const config = await resp.json();
    expect(config).toHaveProperty("ServerAddress");
    expect(config).toHaveProperty("Users");
    expect(Array.isArray(config.Users)).toBeTruthy();
  });

  test("config page HTML loads and contains expected elements", async ({ page }) => {
    const token = await getAccessToken(page);

    const injectScript = `
      window.ApiClient = {
        getUrl: function(path) { return "${BASE_URL}/" + path; },
        ajax: function(options) {
          var headers = { "X-Emby-Token": "${token}" };
          if (options.contentType) headers["Content-Type"] = options.contentType;
          return fetch(options.url, {
            method: options.type || "GET",
            headers: headers,
            body: options.data
          });
        },
        getPluginConfiguration: function(id) {
          return fetch("${BASE_URL}/Plugins/" + id + "/Configuration", {
            headers: { "X-Emby-Token": "${token}" }
          }).then(function(r) { return r.json(); });
        },
        updatePluginConfiguration: function(id, config) {
          return fetch("${BASE_URL}/Plugins/" + id + "/Configuration", {
            method: "POST",
            headers: { "X-Emby-Token": "${token}", "Content-Type": "application/json" },
            body: JSON.stringify(config)
          });
        }
      };
      window.Dashboard = {
        showLoadingMsg: function() {},
        hideLoadingMsg: function() {},
        alert: function(msg) { window.__lastAlert = msg; },
        processPluginConfigurationUpdateResult: function() {}
      };
      window.prompt = function() { return ""; };
      window.alert = function(msg) { window.__lastAlert = msg; };
    `;

    const configUrl = `${BASE_URL}/web/ConfigurationPage?name=AlexaSkill`;
    page.route(configUrl, async (route) => {
      const response = await route.fetch();
      let body = await response.text();
      body = body.replace("<script", `<script>${injectScript}</script><script`);
      await route.fulfill({ response, body });
    });

    page.route(`**/Plugins/**`, async (route) => {
      const request = route.request();
      const headers: Record<string, string> = { "X-Emby-Token": token };
      const ct = request.headers()["content-type"];
      if (ct) headers["Content-Type"] = ct;
      try {
        const resp = await page.request.fetch(request.url(), {
          method: request.method(),
          headers,
          data: request.postData() || undefined,
        });
        await route.fulfill({ response: resp });
      } catch {
        try { await route.abort(); } catch { /* already aborted */ }
      }
    });

    page.route(`**/alexaskill/**`, async (route) => {
      const request = route.request();
      const headers: Record<string, string> = { "X-Emby-Token": token };
      const ct = request.headers()["content-type"];
      if (ct) headers["Content-Type"] = ct;
      try {
        const resp = await page.request.fetch(request.url(), {
          method: request.method(),
          headers,
          data: request.postData() || undefined,
        });
        await route.fulfill({ response: resp });
      } catch {
        try { await route.abort(); } catch { /* already aborted */ }
      }
    });

    await page.goto(configUrl, { waitUntil: "domcontentloaded" });
    await page.waitForSelector("#ConfigPage", { timeout: 10_000 });

    await expect(page.locator("#ServerAddress")).toBeAttached();
    await expect(page.locator("#LwaClientId")).toBeAttached();
    await expect(page.locator("#LwaClientSecret")).toBeAttached();
    await expect(page.locator("#saveConfigButton")).toBeAttached();
    await expect(page.locator("#userTableBody")).toBeAttached();
  });
});
