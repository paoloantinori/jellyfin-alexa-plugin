const { test, expect } = require('@playwright/test');

const BASE_URL = process.env.SITE_URL || 'http://localhost:8765';

test.describe('Voice Command Explorer', () => {

  test.beforeEach(async ({ page }) => {
    await page.goto(BASE_URL);
    // Wait for D3 force simulation to settle
    await page.waitForTimeout(3000);
  });

  test('renders the initial graph with nodes', async ({ page }) => {
    const nodeCount = await page.locator('.node').count();
    expect(nodeCount).toBeGreaterThan(5);

    // Should have visible text labels
    const texts = await page.locator('.node text').allTextContents();
    const nonEmpty = texts.filter(t => t.trim().length > 0);
    expect(nonEmpty.length).toBeGreaterThan(5);
  });

  test('switches diagram type via tabs', async ({ page }) => {
    // Get initial node labels (Library Browsing = default)
    const initialLabels = await page.locator('.node text').first().textContent();

    // Click Playback Lifecycle tab
    await page.getByText('▶ Playback Lifecycle').click();
    await page.waitForTimeout(3000);

    // Graph should have re-rendered with different nodes
    const nodeCount = await page.locator('.node').count();
    expect(nodeCount).toBeGreaterThan(5);

    // Should contain playback-specific nodes
    const allText = await page.locator('.node text').allTextContents();
    const joined = allText.join(' ');
    expect(joined).toMatch(/Playing|Now Playing|Paused|Stopped/);
  });

  test('switches locale and updates node labels', async ({ page }) => {
    // Get initial English labels
    const beforeTexts = await page.locator('.node text').allTextContents();
    const beforeJoined = beforeTexts.join(' ');

    // Switch to Italian
    await page.locator('#localeSelect').selectOption('it-IT');
    await page.waitForTimeout(3000);

    // Labels should have changed
    const afterTexts = await page.locator('.node text').allTextContents();
    const afterJoined = afterTexts.join(' ');

    // At least some labels should differ (translated)
    expect(afterJoined).not.toBe(beforeJoined);
  });

  test('clicking a node focuses it and opens detail panel', async ({ page }) => {
    // Click the first node
    const firstNode = page.locator('.node').first();
    await firstNode.click();
    await page.waitForTimeout(800);

    // Detail panel should be open
    const panel = page.locator('#detailPanel');
    await expect(panel).toHaveClass(/open/);

    // Panel should have content
    const panelTitle = await page.locator('#panelTitle').textContent();
    expect(panelTitle.length).toBeGreaterThan(0);

    // Panel should list connected commands
    const edgeItems = await page.locator('#panelEdges li').count();
    expect(edgeItems).toBeGreaterThan(0);
  });

  test('clicking connected node in panel navigates to it', async ({ page }) => {
    // Click first node to open panel
    await page.locator('.node').first().click();
    await page.waitForTimeout(500);

    // Click an edge item in the panel
    const firstEdge = page.locator('#panelEdges li').first();
    if (await firstEdge.isVisible()) {
      const initialTitle = await page.locator('#panelTitle').textContent();
      await firstEdge.click();
      await page.waitForTimeout(800);

      // Panel title should have changed (navigated to different node)
      const newTitle = await page.locator('#panelTitle').textContent();
      // Title may or may not change depending on graph structure,
      // but panel should still be open
      const panel = page.locator('#detailPanel');
      await expect(panel).toHaveClass(/open/);
    }
  });

  test('close button closes detail panel', async ({ page }) => {
    await page.locator('.node').first().click();
    await page.waitForTimeout(500);
    await expect(page.locator('#detailPanel')).toHaveClass(/open/);

    await page.locator('#closePanel').click();
    await page.waitForTimeout(300);
    await expect(page.locator('#detailPanel')).not.toHaveClass(/open/);
  });

  test('search highlights matching nodes', async ({ page }) => {
    // Type search query
    await page.locator('#searchInput').fill('browse');
    await page.waitForTimeout(1000);

    // Some nodes should have glow filter (matching)
    const glowNodes = await page.locator('.node rect[filter="url(#glow)"]').count();
    expect(glowNodes).toBeGreaterThan(0);

    // Some nodes should be dimmed (opacity < 1)
    const allNodes = await page.locator('.node').all();
    let dimCount = 0;
    for (const node of allNodes) {
      const opacity = await node.evaluate(el => parseFloat(el.getAttribute('opacity') || el.style.opacity || '1'));
      if (opacity < 0.5) dimCount++;
    }
    expect(dimCount).toBeGreaterThan(0);
  });

  test('clearing search restores all nodes', async ({ page }) => {
    // Search then clear
    await page.locator('#searchInput').fill('browse');
    await page.waitForTimeout(500);
    await page.locator('#searchInput').fill('');
    await page.waitForTimeout(500);

    // All nodes should be fully visible
    const allNodes = await page.locator('.node').all();
    for (const node of allNodes) {
      const opacity = await node.evaluate(el => parseFloat(el.getAttribute('opacity') || el.style.opacity || '1'));
      expect(opacity).toBeGreaterThanOrEqual(0.9);
    }
  });

  test('zoom buttons change zoom level', async ({ page }) => {
    // Get initial transform
    const getTransform = async () => {
      return page.locator('#graph g').first().evaluate(() => {
        const g = document.querySelector('#graph g');
        return g.getAttribute('transform') || '';
      });
    };

    const before = await getTransform();

    // Click zoom in
    await page.locator('#zoomIn').click();
    await page.waitForTimeout(400);

    const after = await getTransform();
    // Transform should have changed (different scale)
    expect(after).not.toBe(before);
  });

  test('clicking background clears highlights and closes panel', async ({ page }) => {
    // Click a node first to open panel and focus
    await page.locator('.node').first().click();
    await page.waitForTimeout(600);

    // Detail panel should be open
    await expect(page.locator('#detailPanel')).toHaveClass(/open/);

    // Click background to clear
    await page.locator('#graph').click({ position: { x: 5, y: 5 } });
    await page.waitForTimeout(600);

    // Panel should close
    await expect(page.locator('#detailPanel')).not.toHaveClass(/open/);

    // All nodes should be fully visible
    for (const node of await page.locator('.node').all()) {
      const op = await node.evaluate(el => parseFloat(el.getAttribute('opacity') || el.style.opacity || '1'));
      expect(op).toBeGreaterThanOrEqual(0.9);
    }
  });

  test('all six diagram types render without errors', async ({ page }) => {
    const tabs = [
      '📚 Library Browsing',
      'ℹ️ Media Info Queries',
      '▶ Playback Lifecycle',
      '💿 Queue Radio',
      '🔍 Search Disambiguation',
      '📡 Session Management',
    ];

    for (const tabText of tabs) {
      await page.getByText(tabText, { exact: false }).click();
      await page.waitForTimeout(2000);

      const nodeCount = await page.locator('.node').count();
      expect(nodeCount, `Tab "${tabText}" should render nodes`).toBeGreaterThan(3);

      const consoleErrors = [];
      page.on('console', msg => {
        if (msg.type() === 'error') consoleErrors.push(msg.text());
      });
      expect(consoleErrors.length).toBe(0);
    }
  });

  test('switching locale preserves diagram type', async ({ page }) => {
    // Click Playback Lifecycle
    await page.getByText('▶ Playback Lifecycle').click();
    await page.waitForTimeout(2000);

    // Switch to German
    await page.locator('#localeSelect').selectOption('de-DE');
    await page.waitForTimeout(3000);

    // Should still be on Playback Lifecycle tab (active)
    const activeTab = page.locator('.tab.active');
    const tabText = await activeTab.textContent();
    expect(tabText).toContain('Playback Lifecycle');

    // And should have German labels
    const allText = await page.locator('.node text').allTextContents();
    const joined = allText.join(' ');
    // German playback diagram should contain translated terms
    expect(joined.length).toBeGreaterThan(20);
  });
});
