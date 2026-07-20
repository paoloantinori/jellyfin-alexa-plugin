import { test, expect, type Page } from "@playwright/test";
import {
  BASE_URL,
  PLUGIN_ID,
  CONFIG_URL,
  getAccessToken,
  cleanupAllUsersViaApi,
  setupPageInit,
} from "./helpers";

// ── helpers ──────────────────────────────────────────────────────────

async function loadConfigPage(page: Page) {
  await page.goto(CONFIG_URL, { waitUntil: "domcontentloaded" });
  await page.waitForSelector("#ConfigPage", { timeout: 10_000 });
  await page.waitForFunction(
    () => (document.querySelector("#userTableBody") as HTMLTableSectionElement) !== null,
    { timeout: 10_000 },
  );
}

async function getRowData(page: Page, index = 0) {
  return page.evaluate((idx) => {
    const rows = document.querySelector("#userTableBody")!.rows;
    if (idx >= rows.length) return null;
    const r = rows[idx];
    const authBtn = r.cells[7]?.querySelector("button:last-child") as HTMLButtonElement | null;
    return {
      id: r.getAttribute("data-id"),
      status: r.cells[5]?.innerText,
      authorizeBtnDisabled: authBtn?.disabled ?? true,
    };
  }, index);
}

// ── tests ────────────────────────────────────────────────────────────

test.describe("Alexa Skill config page", () => {
  let originalConfig: any = null;

  test.afterEach(async ({ page }) => {
    if (!originalConfig) return;
    const token = await getAccessToken(page).catch(() => null);
    if (token) {
      await page.request.fetch(`${BASE_URL}/Plugins/${PLUGIN_ID}/Configuration`, {
        method: "POST",
        headers: { "X-Emby-Token": token, "Content-Type": "application/json" },
        data: JSON.stringify(originalConfig),
      });
    }
    originalConfig = null;
  });

  test("save creates user, second save preserves it, authorize works", async ({ page }) => {
    const token = await getAccessToken(page);

    const snapshotResp = await page.request.fetch(
      `${BASE_URL}/Plugins/${PLUGIN_ID}/Configuration`,
      { headers: { "X-Emby-Token": token } },
    );
    originalConfig = await snapshotResp.json();

    await cleanupAllUsersViaApi(page, token);

    setupPageInit(page, token);
    await loadConfigPage(page);

    // ── Step 1: Add a new user skill row (unsaved) ───────────────────
    await page.evaluate((username) => {
      const user = {
        Id: "undefined",
        Username: username,
        UserSkill: null,
        SkillStatus: "unsaved",
        AllowedLibraryIds: [],
        InvocationName: "jellyfin player",
        FuzzyMatchBehavior: "Confirm",
        FuzzyMatchThreshold: 80,
        FuzzySuggestionThreshold: 40,
      };
      const config = {
        ServerAddress: "http://minix:8096",
        LwaClientId: "test-client-id",
        LwaClientSecret: "test-client-secret",
      };
      const newRow = (window as any).createUserRow(user, config);
      document.querySelector("#userTableBody")!.appendChild(newRow);
    }, "paolo");

    let data = await getRowData(page, 0);
    expect(data, "row should exist after adding user").not.toBeNull();
    expect(data!.id).toBe("undefined");
    expect(data!.authorizeBtnDisabled).toBe(true);

    // ── Step 2: First Save (creates user via POST) ──────────────────
    await page.evaluate(() => {
      (document.querySelector("#ServerAddress") as HTMLInputElement).value = "http://minix:8096";
      (document.querySelector("#LwaClientId") as HTMLInputElement).value = "test-client-id";
      (document.querySelector("#LwaClientSecret") as HTMLInputElement).value = "test-client-secret";
    });

    await page.evaluate(() => document.getElementById("saveConfigButton")!.click());

    await page.waitForFunction(
      () => {
        const id = document.querySelector("#userTableBody tr")?.getAttribute("data-id");
        return id != null && id !== "undefined";
      },
      { timeout: 10_000 },
    );

    data = await getRowData(page, 0);
    expect(data!.id, "user should have a non-null GUID").toBeTruthy();
    expect(data!.status).toBe("LwaAuthPending");
    expect(data!.authorizeBtnDisabled, "authorize should be enabled after save").toBe(false);

    const userId = data!.id!;

    // ── Step 3: Second Save (PATCH existing — the bug scenario) ─────
    await page.evaluate(() => document.getElementById("saveConfigButton")!.click());
    await page.waitForTimeout(3_000);

    data = await getRowData(page, 0);
    expect(data!.id, "user GUID must survive second save").toBe(userId);
    expect(data!.status).toBe("LwaAuthPending");
    expect(data!.authorizeBtnDisabled).toBe(false);

    // ── Step 4: Authorize ───────────────────────────────────────────
    await page.evaluate(() => {
      const row = document.querySelector("#userTableBody tr")!;
      const btns = row.cells[7].querySelectorAll("button");
      for (const b of btns) {
        if (b.textContent?.includes("Authorize")) { b.click(); break; }
      }
    });

    await page.waitForFunction(
      () => (window as any).__lastAlert !== undefined,
      { timeout: 10_000 },
    );

    const alertMsg = await page.evaluate(() => (window as any).__lastAlert);
    expect(alertMsg).toContain("/alexaskill/lwa/");
    expect(alertMsg).not.toContain("Could not find user");

    // ── Step 5: Verify persistence after page reload ────────────────
    const serverCheck = await page.request.fetch(
      `${BASE_URL}/Plugins/${PLUGIN_ID}/Configuration`,
      { headers: { "X-Emby-Token": token } },
    );
    const serverConfig = await serverCheck.json();
    const serverUsers = serverConfig.Users || [];
    expect(serverUsers.length, "user must exist on server after save").toBe(1);
    expect(serverUsers[0].Id, "server user ID must match").toBe(userId);

    await page.unrouteAll({ behavior: "ignoreErrors" });
    setupPageInit(page, token);
    await loadConfigPage(page);
    await page.evaluate(() => {
      document.querySelector("#ConfigPage")!.dispatchEvent(new Event("pageshow"));
    });

    await page.waitForFunction(
      () => document.querySelector("#userTableBody")!.rows.length > 0,
      { timeout: 10_000 },
    );

    data = await getRowData(page, 0);
    expect(data, "row should exist after reload").not.toBeNull();
    expect(data!.id, "user must persist across page reload").toBe(userId);
    expect(data!.status).toBe("LwaAuthPending");
  });

  test("no duplicate rows after multiple pageshow events", async ({ page }) => {
    const token = await getAccessToken(page);

    const snapshotResp = await page.request.fetch(
      `${BASE_URL}/Plugins/${PLUGIN_ID}/Configuration`,
      { headers: { "X-Emby-Token": token } },
    );
    originalConfig = await snapshotResp.json();

    await cleanupAllUsersViaApi(page, token);
    setupPageInit(page, token);
    await loadConfigPage(page);

    await page.evaluate((username) => {
      const user = {
        Id: "test-dedup-" + username,
        Username: username,
        UserSkill: null,
        SkillStatus: "unsaved",
        AllowedLibraryIds: [],
        InvocationName: "jellyfin player",
        FuzzyMatchBehavior: "Confirm",
        FuzzyMatchThreshold: 80,
        FuzzySuggestionThreshold: 40,
      };
      const config = {
        ServerAddress: "http://minix:8096",
        LwaClientId: "test-client-id",
        LwaClientSecret: "test-client-secret",
      };
      const newRow = (window as any).createUserRow(user, config);
      document.querySelector("#userTableBody")!.appendChild(newRow);
    }, "paolo");

    await page.evaluate(() => {
      const el = document.querySelector("#ConfigPage")!;
      el.dispatchEvent(new Event("pageshow"));
    });
    await page.waitForTimeout(500);
    await page.evaluate(() => {
      const el = document.querySelector("#ConfigPage")!;
      el.dispatchEvent(new Event("pageshow"));
    });
    await page.waitForTimeout(500);

    const rowCount = await page.evaluate(() => {
      return document.querySelector("#userTableBody")!.rows.length;
    });
    expect(rowCount, "should not duplicate rows on repeated pageshow").toBe(1);
  });
});
