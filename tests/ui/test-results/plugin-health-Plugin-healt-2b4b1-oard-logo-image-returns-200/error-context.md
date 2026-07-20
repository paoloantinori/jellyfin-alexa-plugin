# Test info

- Name: Plugin health checks >> plugin dashboard logo image returns 200
- Location: /home/pantinor/data/repo/personal/jellyfin-alexa-plugin/tests/ui/plugin-health.spec.ts:36:7

# Error details

```
Error: Login failed: 401

expect(received).toBeTruthy()

Received: false
    at getAccessToken (/home/pantinor/data/repo/personal/jellyfin-alexa-plugin/tests/ui/helpers.ts:18:55)
    at /home/pantinor/data/repo/personal/jellyfin-alexa-plugin/tests/ui/plugin-health.spec.ts:37:19
```

# Test source

```ts
   1 | import { expect, type Page } from "@playwright/test";
   2 |
   3 | export const ADMIN_USER = process.env.JELLYFIN_USER || "paolo";
   4 | export const ADMIN_PASS = process.env.JELLYFIN_PASS || "";
   5 | export const BASE_URL = process.env.JELLYFIN_URL || "http://minix:8096";
   6 | export const PLUGIN_ID = "c5df7de0-8777-4b3c-a70d-5c3dae359c9e";
   7 | export const CONFIG_URL = `${BASE_URL}/web/ConfigurationPage?name=AlexaSkill`;
   8 |
   9 | export async function getAccessToken(page: Page): Promise<string> {
   10 |   const resp = await page.request.post(`${BASE_URL}/Users/AuthenticateByName`, {
   11 |     headers: {
   12 |       "X-Emby-Authorization":
   13 |         `Emby Client="Playwright", Device="Test", DeviceId="pw-test", Version="1.0"`,
   14 |       "Content-Type": "application/json",
   15 |     },
   16 |     data: { Username: ADMIN_USER, Pw: ADMIN_PASS },
   17 |   });
>  18 |   expect(resp.ok(), `Login failed: ${resp.status()}`).toBeTruthy();
      |                                                       ^ Error: Login failed: 401
   19 |   const body = await resp.json();
   20 |   return body.AccessToken;
   21 | }
   22 |
   23 | export async function cleanupAllUsersViaApi(page: Page, token: string) {
   24 |   const resp = await page.request.fetch(`${BASE_URL}/Plugins/${PLUGIN_ID}/Configuration`, {
   25 |     headers: { "X-Emby-Token": token },
   26 |   });
   27 |   const config = await resp.json();
   28 |   for (const user of config.Users || []) {
   29 |     await page.request.fetch(`${BASE_URL}/alexaskill/api/user-skills/${user.Id}`, {
   30 |       method: "DELETE",
   31 |       headers: { "X-Emby-Token": token },
   32 |     });
   33 |   }
   34 | }
   35 |
   36 | export function setupPageInit(page: Page, token: string) {
   37 |   const injectScript = `
   38 |     window.ApiClient = {
   39 |       getUrl: function(path) { return "${BASE_URL}/" + path; },
   40 |       ajax: function(options) {
   41 |         var headers = { "X-Emby-Token": "${token}" };
   42 |         if (options.contentType) headers["Content-Type"] = options.contentType;
   43 |         return fetch(options.url, {
   44 |           method: options.type || "GET",
   45 |           headers: headers,
   46 |           body: options.data
   47 |         });
   48 |       },
   49 |       getPluginConfiguration: function(id) {
   50 |         return fetch("${BASE_URL}/Plugins/" + id + "/Configuration", {
   51 |           headers: { "X-Emby-Token": "${token}" }
   52 |         }).then(function(r) { return r.json(); });
   53 |       },
   54 |       updatePluginConfiguration: function(id, config) {
   55 |         return fetch("${BASE_URL}/Plugins/" + id + "/Configuration", {
   56 |           method: "POST",
   57 |           headers: { "X-Emby-Token": "${token}", "Content-Type": "application/json" },
   58 |           body: JSON.stringify(config)
   59 |         });
   60 |       }
   61 |     };
   62 |     window.Dashboard = {
   63 |       showLoadingMsg: function() {},
   64 |       hideLoadingMsg: function() {},
   65 |       alert: function(msg) { window.__lastAlert = msg; },
   66 |       processPluginConfigurationUpdateResult: function() {}
   67 |     };
   68 |     window.__promptValue = "${ADMIN_USER}";
   69 |     window.prompt = function() { return window.__promptValue; };
   70 |     window.alert = function(msg) { window.__lastAlert = msg; };
   71 |   `;
   72 |
   73 |   page.route(CONFIG_URL, async (route) => {
   74 |     const response = await route.fetch();
   75 |     let body = await response.text();
   76 |     body = body.replace("<script", `<script>${injectScript}</script><script`);
   77 |     await route.fulfill({ response, body });
   78 |   });
   79 |
   80 |   page.route(`**/Plugins/**`, async (route) => {
   81 |     await proxyRoute(route, token, page);
   82 |   });
   83 |   page.route(`**/alexaskill/**`, async (route) => {
   84 |     await proxyRoute(route, token, page);
   85 |   });
   86 | }
   87 |
   88 | async function proxyRoute(route: import("@playwright/test").Route, token: string, _page: Page) {
   89 |   const request = route.request();
   90 |   const url = request.url();
   91 |   const method = request.method();
   92 |
   93 |   const headers: Record<string, string> = {
   94 |     "X-Emby-Token": token,
   95 |   };
   96 |   const contentType = request.headers()["content-type"];
   97 |   if (contentType) headers["Content-Type"] = contentType;
   98 |
   99 |   const postData = request.postData();
  100 |
  101 |   try {
  102 |     const resp = await _page.request.fetch(url, {
  103 |       method,
  104 |       headers,
  105 |       data: postData || undefined,
  106 |     });
  107 |     await route.fulfill({ response: resp });
  108 |   } catch {
  109 |     try { await route.abort(); } catch { /* already aborted */ }
  110 |   }
  111 | }
  112 |
```