// Captures real SiWare.Cli output and renders it as docs/screenshots/cli.png: node scripts/cli-screenshot.mjs
import { chromium } from '../.tools/ui/node_modules/playwright/index.mjs';
import { execFileSync } from 'node:child_process';
import path from 'node:path';

const commands = [['help'], ['info'], ['demo', '--output', 'artifacts/demo']];
const cli = path.resolve('samples/SiWare.Cli/bin/Debug/net10.0/SiWare.Cli.dll');
execFileSync('dotnet', ['build', 'samples/SiWare.Cli', '--nologo', '-v', 'q'], { stdio: 'ignore' });
const escape = text => text.replace(/[&<>]/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;' })[c]);
// Absolute paths are machine-specific, so show them relative to the repository.
const relative = text => text.split(path.resolve('.') + path.sep).join('').replaceAll(path.resolve('.'), '.');
const transcript = commands.map(args => {
  const output = execFileSync('dotnet', [cli, ...args], { encoding: 'utf8' });
  return `<span class="prompt">PS SiWare.Net&gt;</span> <span class="cmd">siware ${escape(args.join(' '))}</span>\n${escape(relative(output))}`;
}).join('\n');

const html = `<!doctype html><html><head><meta charset="utf-8"><style>
body{margin:0;padding:32px;background:#e9eef5;font-family:Consolas,monospace}
.window{width:900px;border-radius:12px;overflow:hidden;box-shadow:0 18px 40px #0f1d3340;background:#0f1d33}
.bar{height:38px;display:flex;align-items:center;gap:8px;padding:0 16px;background:#182c47;color:#aebcd1;font-size:12px}
.bar i{width:11px;height:11px;border-radius:50%;display:inline-block}.bar span{margin-left:12px}
pre{margin:0;padding:22px 24px;color:#dbe5f3;font-size:13.5px;line-height:1.55;white-space:pre-wrap}
.prompt{color:#22b8a6}.cmd{color:#ffffff;font-weight:700}
</style></head><body><div class="window"><div class="bar"><i style="background:#f26d6d"></i><i style="background:#f5b13d"></i><i style="background:#3fc77a"></i><span>SiWare.Cli — Gravicode Studios</span></div><pre>${transcript}</pre></div></body></html>`;

const browser = await chromium.launch({ channel: process.env.SIWARE_BROWSER_CHANNEL || 'chrome', headless: true });
try {
  const page = await browser.newPage({ viewport: { width: 964, height: 400 } });
  await page.setContent(html);
  await page.locator('.window').screenshot({ path: path.resolve('docs/screenshots/cli.png') });
  console.log('Rendered docs/screenshots/cli.png');
} finally { await browser.close(); }
