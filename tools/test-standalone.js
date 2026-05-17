#!/usr/bin/env node
/**
 * test-standalone.js
 * ==================
 * Smoke-test cpn-visualizer.html in a headless Chromium browser.
 *
 * Opens the file via the  file://  protocol (the same way a user would),
 * waits up to 60 seconds for Blazor to render the app shell, and fails if
 * any console errors appear.
 *
 * Usage:
 *   node tools/test-standalone.js dist/cpn-visualizer.html
 *
 * Requirements:
 *   npm install playwright
 *   npx playwright install chromium --with-deps
 */

'use strict';

const { chromium } = require('playwright');
const path = require('path');
const fs   = require('fs');

// Console-error strings that are expected / harmless and should not fail
// the test.
const IGNORED_PATTERNS = [
  /favicon/i,
  /manifest\.json/i,
  /Cannot read properties of null.*style/i,   // occasional Blazor init race
];

async function main() {
  const htmlArg = process.argv[2];
  if (!htmlArg) {
    console.error('Usage: node test-standalone.js <path-to-html>');
    process.exit(1);
  }

  const absPath = path.resolve(htmlArg);
  if (!fs.existsSync(absPath)) {
    console.error('ERROR: file not found:', absPath);
    process.exit(1);
  }

  // On Windows, path.resolve() returns  C:\…  – turn it into  /C:/…
  // so the file:// URL is valid in all environments.
  const urlPath = process.platform === 'win32'
    ? '/' + absPath.replace(/\\/g, '/')
    : absPath;
  const fileUrl = 'file://' + urlPath;

  console.log('Opening:', fileUrl);

  const browser = await chromium.launch({
    // --no-sandbox is required inside GitHub Actions containers.
    // --allow-file-access-from-files is NOT needed because the HTML is
    // self-contained; all assets are served from embedded blob: URLs.
    args: ['--no-sandbox', '--disable-dev-shm-usage'],
  });

  const page = await browser.newPage();

  const errors   = [];
  const warnings = [];

  page.on('console', (msg) => {
    const text = msg.text();
    if (msg.type() === 'error') {
      if (!IGNORED_PATTERNS.some((p) => p.test(text))) {
        errors.push(text);
        console.error('[browser error]', text);
      }
    } else if (/CORS|MONO_WASM|Failed to (fetch|load|start)/i.test(text)) {
      warnings.push(text);
      console.warn('[browser warn ]', text);
    }
  });

  page.on('pageerror', (err) => {
    const text = err.message;
    if (!IGNORED_PATTERNS.some((p) => p.test(text))) {
      errors.push(text);
      console.error('[page  error]', text);
    }
  });

  await page.goto(fileUrl, { waitUntil: 'commit' });

  // Wait for Blazor to render something inside #app.
  // The app is considered "started" when #app contains visible text.
  let blazorOk = false;
  try {
    await page.waitForFunction(
      () => {
        const app = document.getElementById('app');
        return app != null && app.innerText && app.innerText.trim().length > 5;
      },
      { timeout: 60_000 }
    );
    blazorOk = true;
    console.log('✓ Blazor rendered app content');
  } catch (_) {
    const appText = await page.evaluate(() => {
      const el = document.getElementById('app');
      return el ? el.innerHTML.substring(0, 500) : '(#app not found)';
    }).catch(() => '(could not evaluate)');
    console.error('✗ Blazor did not render within 60 s');
    console.error('  #app innerHTML snippet:', appText);
    errors.push('Blazor render timeout');
  }

  await browser.close();

  // ── Summary ──────────────────────────────────────────────────────────────
  if (warnings.length) {
    console.warn(`\n⚠  ${warnings.length} warning(s) (non-fatal):`);
    warnings.forEach((w) => console.warn('  •', w));
  }

  if (errors.length || !blazorOk) {
    console.error(`\n✗ TEST FAILED  (${errors.length} error(s))`);
    errors.forEach((e) => console.error('  •', e));
    process.exit(1);
  }

  console.log('\n✓ TEST PASSED — standalone HTML works from file://');
  process.exit(0);
}

main().catch((err) => {
  console.error('Unexpected test script failure:', err);
  process.exit(1);
});
