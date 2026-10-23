// Tommy's headless-browser fetcher. Renders one JS-interactive page with
// Playwright Chromium and prints its readable text as JSON on stdout:
//   { "url": <final url>, "title": <page title>, "content": <main text> }
// On failure it writes a message to stderr and exits non-zero. The C# side
// (NodeHeadlessBrowser) spawns this, points PLAYWRIGHT_BROWSERS_PATH at the
// bundled Chromium, and parses the JSON. Output is untrusted data — the agent
// never acts on instructions inside a fetched page.
//
// Usage: node fetch.js <url>
'use strict';

const { chromium } = require('playwright');

const NAV_TIMEOUT_MS = 30000;
const SETTLE_TIMEOUT_MS = 8000;

async function main() {
  const url = process.argv[2];
  if (!url) {
    process.stderr.write('usage: fetch.js <url>');
    process.exit(2);
  }

  const browser = await chromium.launch({ headless: true });
  try {
    const context = await browser.newContext({
      userAgent:
        'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 ' +
        '(KHTML, like Gecko) Chrome/125.0 Safari/537.36',
    });
    const page = await context.newPage();
    await page.goto(url, { waitUntil: 'domcontentloaded', timeout: NAV_TIMEOUT_MS });
    // Give client-side rendering a moment to settle; ignore if it never idles.
    await page.waitForLoadState('networkidle', { timeout: SETTLE_TIMEOUT_MS }).catch(() => {});

    const title = await page.title();
    const content = await page.evaluate(extractReadableText);

    process.stdout.write(JSON.stringify({ url: page.url(), title: title || page.url(), content: content || '' }));
  } finally {
    await browser.close();
  }
}

// Runs in the page: prefer the main article region, strip chrome, fall back to
// the whole body. Returns plain visible text.
function extractReadableText() {
  const drop = ['script', 'style', 'noscript', 'template', 'nav', 'header', 'footer', 'aside', 'form'];
  const pick = document.querySelector('article') || document.querySelector('main') || document.body;
  if (!pick) return '';

  const clone = pick.cloneNode(true);
  for (const sel of drop) clone.querySelectorAll(sel).forEach((n) => n.remove());

  const text = clone.innerText || clone.textContent || '';
  return text.replace(/\n{3,}/g, '\n\n').trim();
}

main().catch((err) => {
  process.stderr.write(String(err && err.message ? err.message : err));
  process.exit(1);
});
