import { chromium } from '../.tools/ui/node_modules/playwright/index.mjs';
import assert from 'node:assert/strict';
import { mkdir, readFile } from 'node:fs/promises';
import path from 'node:path';

const base = process.env.SIWARE_TEST_URL || 'http://127.0.0.1:5088';
const screenshots = path.resolve('docs/screenshots');
await mkdir(screenshots, { recursive: true });
const browser = await chromium.launch({ channel: process.env.SIWARE_BROWSER_CHANNEL || 'chrome', headless: true });
const page = await browser.newPage({ viewport: { width: 1440, height: 1100 }, reducedMotion: 'reduce', acceptDownloads: true });
const errors = [];
page.on('pageerror', error => errors.push(error.message));
const ready = async id => page.waitForFunction(id => !document.getElementById(id).disabled, id);
async function clickRequest(id, endpoint) {
  await ready(id);
  const responsePromise = page.waitForResponse(response => response.url().includes(endpoint) && response.request().method() === 'POST');
  await page.locator(`#${id}`).click();
  const response = await responsePromise;
  assert.equal(response.status(), 200, await response.text());
  await ready('connect');
  return response.json().catch(() => null);
}
// Full-page captures clip the fixed navigation rail, so grow the viewport to the page height instead.
async function shoot(name, width) {
  await page.setViewportSize({ width, height: 900 });
  const height = await page.evaluate(() => document.documentElement.scrollHeight);
  await page.setViewportSize({ width, height });
  await page.screenshot({ path: path.join(screenshots, name) });
}
try {
  for(let attempt = 0; ; attempt++) {
    try { const response = await page.request.get(`${base}/api/status`); if(response.ok()) break; } catch {}
    if(attempt >= 59) throw new Error('Workbench did not start within 60 seconds.');
    await new Promise(resolve => setTimeout(resolve, 1000));
  }
  await page.goto(base);
  await page.evaluate(() => localStorage.setItem('siware-language', 'en'));
  await page.reload();
  await ready('connect');
  assert.equal(await page.locator('#emptyChart').isVisible(), true);
  const status = await (await page.request.get(`${base}/api/status`)).json();
  if(status.connected) await clickRequest('connect', '/api/disconnect');
  await clickRequest('connect', '/api/connect');
  assert.match(await page.locator('#modeBadge').textContent(), /SIMULATED DATA/);
  await clickRequest('captureDark', '/api/acquire');
  await clickRequest('captureReference', '/api/acquire');
  const sample = await clickRequest('acquire', '/api/acquire');
  assert.equal(sample.count, 257);
  assert.ok((await page.locator('#trace').getAttribute('d')).length > 100);
  await page.locator('#chart').focus();
  await page.keyboard.press('ArrowRight');
  assert.notEqual(await page.locator('#cursorX').textContent(), '— nm');
  await clickRequest('absorbance', '/api/process/');
  // Without an LLM configured the insight button must stay disabled and explain why.
  if (!(await (await page.request.get(`${base}/api/status`)).json()).insightAvailable) {
    assert.equal(await page.locator('#insightButton').isDisabled(), true);
    assert.match(await page.locator('#insightButton').getAttribute('title'), /HF_TOKEN/);
  }
  await shoot('workbench-desktop.png', 1440);
  await clickRequest('snv', '/api/process/');
  assert.match(await page.locator('#yLabel').textContent(), /SNV/);
  await clickRequest('smooth', '/api/process/');
  assert.ok(await page.locator('.history-row').count() >= 6);
  await page.locator('.history-row').last().click();
  await ready('connect');
  for(const format of ['Csv', 'Json']) {
    const downloadPromise = page.waitForEvent('download');
    await page.locator(`#export${format}`).click();
    const download = await downloadPromise;
    const content = await readFile(await download.path(), 'utf8');
    assert.ok(content.length > 100);
    if(format === 'Json') assert.ok(JSON.parse(content));
  }
  await page.locator('#integration').fill('10000');
  const canceledResponse = page.waitForResponse(response => response.url().endsWith('/api/acquire') && response.request().method() === 'POST');
  await page.locator('#acquire').click();
  await page.locator('#cancel').waitFor({state:'visible'});
  await page.waitForFunction(async () => (await (await fetch('/api/status')).json()).busy);
  await page.locator('#cancel').click();
  assert.equal((await canceledResponse).status(),409);
  assert.equal((await (await page.request.get(`${base}/api/status`)).json()).busy,false,'Cancelled acquisition was resent');
  await ready('connect');
  // Continuous run mode: a short run completes and the chart follows each new spectrum.
  await page.locator('#integration').fill('10');
  const historyBefore = (await (await page.request.get(`${base}/api/history`)).json()).length;
  await page.locator('label:has(input[value="continuous"])').click();
  assert.equal(await page.locator('#seriesFields').isVisible(), true);
  await page.locator('#measurements').fill('3');
  await page.locator('#interval').fill('0.3');
  await page.locator('#acquire').click();
  await page.waitForFunction(() => /complete/.test(document.getElementById('seriesText').textContent), null, { timeout: 30000 });
  assert.equal((await (await page.request.get(`${base}/api/history`)).json()).length, historyBefore + 3);
  assert.equal(await page.locator('#seriesBar').getAttribute('value'), '3');
  await ready('connect');
  // A long run can be stopped; spectra captured so far stay in the history.
  await page.locator('#measurements').fill('50');
  await page.locator('#interval').fill('1');
  await page.locator('#acquire').click();
  await page.waitForFunction(() => /Measurement [1-9]/.test(document.getElementById('seriesText').textContent), null, { timeout: 30000 });
  await shoot('workbench-continuous.png', 1440);
  await page.locator('#cancel').click();
  await page.waitForFunction(() => /stopped/.test(document.getElementById('seriesText').textContent), null, { timeout: 30000 });
  assert.equal((await (await page.request.get(`${base}/api/status`)).json()).busy, false);
  await ready('connect');
  await page.locator('label:has(input[value="single"])').click();
  assert.equal(await page.locator('#seriesFields').isVisible(), false);
  await page.locator('#language').click();
  assert.equal(await page.locator('html').getAttribute('lang'), 'id');
  assert.match(await page.locator('#modeBadge').textContent(), /DATA SIMULASI/);
  await page.locator('#integration').fill('100');
  await clickRequest('acquire','/api/acquire');
  await shoot('workbench-desktop-id.png', 1440);
  await shoot('workbench-mobile-id.png', 390);
  assert.equal(await page.evaluate(()=>document.documentElement.scrollWidth <= window.innerWidth),true,'Mobile content overflows viewport');
  await page.locator('#language').click();
  await clickRequest('connect','/api/disconnect');
  assert.deepEqual(errors,[]);
  console.log('UI smoke passed: connect, calibration, acquisition, absorbance, SNV, smoothing, chart keyboard, history, exports, cancel, EN/ID, mobile layout, continuous run and stop.');
} finally { await browser.close(); }
