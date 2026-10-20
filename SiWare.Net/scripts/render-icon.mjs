// Renders the NuGet package icon from its SVG source: node scripts/render-icon.mjs
import { chromium } from '../.tools/ui/node_modules/playwright/index.mjs';
import { readFile } from 'node:fs/promises';
import path from 'node:path';

const source = path.resolve('docs/assets/siware-net-icon.svg');
const svg = await readFile(source, 'utf8');
const browser = await chromium.launch({ channel: process.env.SIWARE_BROWSER_CHANNEL || 'chrome', headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 256, height: 256 } });
  await page.setContent(`<!doctype html><html><body style="margin:0;background:transparent">${svg}</body></html>`);
  for (const output of ['src/SiWare.Sdk/icon.png', 'docs/assets/siware-net-icon.png'])
    await page.locator('svg').screenshot({ path: path.resolve(output), omitBackground: true });
  console.log('Rendered icon.png');
} finally { await browser.close(); }
