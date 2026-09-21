'use strict';

const fs = require('node:fs');
const path = require('node:path');
const { validateSmoke, smoke } = require('./browser-smoke.cjs');

function result(options = {}) {
  return {
    connector: { status: 'unknown', reason: 'Caller must inspect its connector.' },
    headless: { status: 'unavailable', reason: '', nodePath: process.execPath,
      modulePath: options.playwrightPath || null, browserPath: options.browserPath || null },
    http: { status: 'render-only', reason: 'HTTP is never interactive evidence.' }
  };
}

function discover(options, deps, headless) {
  if (options.playwrightPath !== undefined && (!options.playwrightPath || !path.isAbsolute(options.playwrightPath)))
    throw new Error('module-missing: PlaywrightPath must be an absolute installed module directory.');
  try {
    if (options.playwrightPath !== undefined) {
      if (!deps.isDirectory(options.playwrightPath)) throw new Error('Not a directory');
      headless.modulePath = deps.resolve(options.playwrightPath);
    } else {
      for (const name of ['playwright', 'playwright-core']) {
        try { headless.modulePath = deps.resolve(name); break; } catch { /* Try the other installed module. */ }
      }
    }
    if (!headless.modulePath) throw new Error('Not installed');
    const module = deps.load(headless.modulePath);
    if (!module.chromium) throw new Error('Module has no chromium launcher');
    return module.chromium;
  } catch (error) {
    throw new Error(`module-missing: Cannot load installed Playwright: ${error.message}`);
  }
}

function browserPath(options, chromium, deps) {
  if (options.browserPath !== undefined) {
    if (!options.browserPath || !path.isAbsolute(options.browserPath) || !deps.isFile(options.browserPath))
      throw new Error('executable-missing: BrowserPath must name an absolute installed executable.');
    return options.browserPath;
  }
  const candidates = [];
  for (const root of [deps.env.PROGRAMFILES, deps.env['PROGRAMFILES(X86)'], deps.env.LOCALAPPDATA]) {
    if (root) candidates.push(path.join(root, 'Google/Chrome/Application/chrome.exe'),
      path.join(root, 'Microsoft/Edge/Application/msedge.exe'));
  }
  candidates.push(chromium.executablePath());
  const selected = candidates.find(candidate => candidate && deps.isFile(candidate));
  if (!selected) throw new Error('executable-missing: No installed Chrome, Edge or Playwright chromium executable.');
  return selected;
}

async function run(options = {}, overrides = {}) {
  const deps = {
    resolve: name => require.resolve(name, { paths: [__dirname, path.resolve(__dirname, '..')] }),
    load: name => require(name),
    isFile: name => { try { return fs.statSync(name).isFile(); } catch { return false; } },
    isDirectory: name => { try { return fs.statSync(name).isDirectory(); } catch { return false; } },
    env: process.env, ...overrides
  };
  const probe = result(options);
  let browser;
  let context;
  let evidence;
  let exitCode = 2;
  try {
    const timeout = options.timeoutSeconds ?? 30;
    if (!Number.isInteger(timeout) || timeout < 1 || timeout > 120)
      throw new Error('invalid-options: TimeoutSeconds must be 1..120.');
    if (options.url !== undefined || options.agentName !== undefined) validateSmoke(options.url, options.agentName);
    const chromium = discover(options, deps, probe.headless);
    probe.headless.browserPath = browserPath(options, chromium, deps);
    try {
      // Playwright disables Chromium's sandbox by default; explicitly retain the browser's security boundary.
      browser = await chromium.launch({ executablePath: probe.headless.browserPath,
        headless: true, chromiumSandbox: true, timeout: timeout * 1000 });
    } catch (error) {
      throw new Error(`${error.name === 'TimeoutError' ? 'launch-timeout' : 'launch-denied'}: ${error.message}`);
    }
    context = await browser.newContext();
    context.setDefaultTimeout(timeout * 1000);
    const page = await context.newPage();
    await page.goto('data:text/html,<button onclick="this.textContent=\'clicked\'">ready</button>');
    await page.getByRole('button', { name: 'ready', exact: true }).click();
    if (await page.getByRole('button').textContent() !== 'clicked') throw new Error('interaction-failed: Button text did not change.');
    probe.headless.status = 'available';
    probe.headless.reason = 'Local button clicked and changed text asserted.';
    exitCode = 0;
    if (options.url !== undefined) evidence = await smoke(context, options.url, options.agentName, timeout * 1000);
  } catch (error) {
    if (probe.headless.status === 'available') {
      evidence = { smoke: 'failed', reason: error.message };
      exitCode = 1;
    } else probe.headless.reason = error.message;
  } finally {
    // A failed context close must not prevent closing the browser.
    for (const owned of [context, browser]) {
      try { if (owned) await owned.close(); }
      catch (error) {
        probe.headless.status = 'unavailable';
        probe.headless.reason = `cleanup-failed: ${error.message}`;
        exitCode = 1;
      }
    }
  }
  return { probe, evidence, exitCode };
}

if (require.main === module) {
  // The PowerShell parent owns the total deadline, including discovery and cleanup.
  (async () => {
    const outcome = await run(JSON.parse(process.argv[2] || '{}'));
    console.log(JSON.stringify(outcome.probe));
    if (outcome.evidence) console.log(JSON.stringify(outcome.evidence));
    process.exitCode = outcome.exitCode;
  })().catch(error => {
    const probe = result();
    probe.headless.reason = `unexpected-error: ${error.message}`;
    console.log(JSON.stringify(probe));
    process.exitCode = 1;
  });
}

module.exports = { run, browserPath };
