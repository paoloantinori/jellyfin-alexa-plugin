import { expect, type Page } from "@playwright/test";

export const ADMIN_USER = process.env.JELLYFIN_USER || "paolo";
export const ADMIN_PASS = process.env.JELLYFIN_PASS || "";
export const BASE_URL = process.env.JELLYFIN_URL || "http://minix:8096";
export const PLUGIN_ID = "c5df7de0-8777-4b3c-a70d-5c3dae359c9e";
export const CONFIG_URL = `${BASE_URL}/web/ConfigurationPage?name=AlexaSkill`;

export async function getAccessToken(page: Page): Promise<string> {
  const resp = await page.request.post(`${BASE_URL}/Users/AuthenticateByName`, {
    headers: {
      "X-Emby-Authorization":
        `Emby Client="Playwright", Device="Test", DeviceId="pw-test", Version="1.0"`,
      "Content-Type": "application/json",
    },
    data: { Username: ADMIN_USER, Pw: ADMIN_PASS },
  });
  expect(resp.ok(), `Login failed: ${resp.status()}`).toBeTruthy();
  const body = await resp.json();
  return body.AccessToken;
}

export async function cleanupAllUsersViaApi(page: Page, token: string) {
  const resp = await page.request.fetch(`${BASE_URL}/Plugins/${PLUGIN_ID}/Configuration`, {
    headers: { "X-Emby-Token": token },
  });
  const config = await resp.json();
  for (const user of config.Users || []) {
    await page.request.fetch(`${BASE_URL}/alexaskill/api/user-skills/${user.Id}`, {
      method: "DELETE",
      headers: { "X-Emby-Token": token },
    });
  }
}

export function setupPageInit(page: Page, token: string) {
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
    window.__promptValue = "${ADMIN_USER}";
    window.prompt = function() { return window.__promptValue; };
    window.alert = function(msg) { window.__lastAlert = msg; };
  `;

  page.route(CONFIG_URL, async (route) => {
    const response = await route.fetch();
    let body = await response.text();
    body = body.replace("<script", `<script>${injectScript}</script><script`);
    await route.fulfill({ response, body });
  });

  page.route(`**/Plugins/**`, async (route) => {
    await proxyRoute(route, token, page);
  });
  page.route(`**/alexaskill/**`, async (route) => {
    await proxyRoute(route, token, page);
  });
}

async function proxyRoute(route: import("@playwright/test").Route, token: string, _page: Page) {
  const request = route.request();
  const url = request.url();
  const method = request.method();

  const headers: Record<string, string> = {
    "X-Emby-Token": token,
  };
  const contentType = request.headers()["content-type"];
  if (contentType) headers["Content-Type"] = contentType;

  const postData = request.postData();

  try {
    const resp = await _page.request.fetch(url, {
      method,
      headers,
      data: postData || undefined,
    });
    await route.fulfill({ response: resp });
  } catch {
    try { await route.abort(); } catch { /* already aborted */ }
  }
}
