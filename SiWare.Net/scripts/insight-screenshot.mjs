// Live AI insight check and screenshot. Start the Workbench with an LLM configured (HF_TOKEN, or SIWARE_LLM_BASE_URL,
// SIWARE_LLM_API_KEY, SIWARE_LLM_MODEL), then: node scripts/insight-screenshot.mjs
import { chromium } from '../.tools/ui/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import path from 'node:path';

const base = process.env.SIWARE_TEST_URL || 'http://127.0.0.1:5091';
const browser = await chromium.launch({ channel: process.env.SIWARE_BROWSER_CHANNEL || 'chrome', headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 900 }, reducedMotion: 'reduce' });
const ready = id => page.waitForFunction(id => !document.getElementById(id).disabled, id, { timeout: 120_000 });
async function click(id, endpoint) {
  await ready(id);
  const response = page.waitForResponse(r => r.url().includes(endpoint) && r.request().method() === 'POST', { timeout: 120_000 });
  await page.locator(`#${id}`).click();
  assert.equal((await response).status(), 200);
  await ready('connect');
}
async function shoot(name) {
  const height = await page.evaluate(() => document.documentElement.scrollHeight);
  await page.setViewportSize({ width: 1440, height });
  await page.screenshot({ path: path.resolve('docs/screenshots', name) });
  await page.setViewportSize({ width: 1440, height: 900 });
}
try {
  await page.goto(base);
  for (const language of ['en', 'id']) {
    await page.evaluate(language => localStorage.setItem('siware-language', language), language);
    await page.reload();
    const status = await (await page.request.get(`${base}/api/status`)).json();
    assert.ok(status.insightAvailable, 'Start the Workbench with an LLM configured.');
    if (!status.connected) await click('connect', '/api/connect');
    await click('captureDark', '/api/acquire');
    await click('captureReference', '/api/acquire');
    await click('acquire', '/api/acquire');
    await click('absorbance', '/api/process/');
    await click('insightButton', '/api/insight/');
    await page.locator('#insight').waitFor({ state: 'visible' });
    assert.ok((await page.locator('#insightText').textContent()).length > 80);
    assert.match(await page.locator('#insightDisclaimer').textContent(), /AI/);
    await shoot(language === 'en' ? 'workbench-insight.png' : 'workbench-insight-id.png');
  }
  console.log('AI insight passed in EN and ID; screenshots saved.');
} finally { await browser.close(); }
