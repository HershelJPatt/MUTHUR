'use strict';

const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { run } = require('../scripts/browser-capability.cjs');
const { validateSmoke, smoke } = require('../scripts/browser-smoke.cjs');

function fixture(failure) {
  const calls = [];
  const page = {
    goto: async url => calls.push(['goto', url]),
    getByRole: () => ({ click: async () => {
      calls.push('click');
      if (failure === 'click') throw new Error('click failed');
    }, textContent: async () => failure === 'text' ? 'ready' : 'clicked' })
  };
  const context = { setDefaultTimeout() {}, newPage: async () => page, close: async () => {
    calls.push('context-close');
    if (failure === 'close') throw new Error('context close failed');
  } };
  const chromium = { executablePath: () => path.resolve('installed browser/chrome.exe'), launch: async options => {
    calls.push(['launch', options]);
    if (failure === 'launch') throw new Error('permission denied');
    if (failure === 'timeout') throw Object.assign(new Error('deadline'), { name: 'TimeoutError' });
    return { newContext: async () => context, close: async () => calls.push('browser-close') };
  } };
  return { calls, deps: { resolve: name => name, load: () => ({ chromium }),
    isDirectory: () => true, isFile: () => true, env: {} } };
}

test('available proves a click and changed text with a fresh context and sandbox, then closes both', async () => {
  const { calls, deps } = fixture();
  const outcome = await run({}, deps);
  assert.equal(outcome.exitCode, 0);
  assert.equal(outcome.probe.connector.status, 'unknown');
  assert.equal(outcome.probe.http.status, 'render-only');
  assert.equal(outcome.probe.headless.status, 'available');
  assert.ok(calls.includes('click'));
  const options = calls.find(c => c[0] === 'launch')[1];
  assert.equal(options.chromiumSandbox, true);
  assert.equal(options.headless, true);
  assert.equal(options.args, undefined);
  assert.deepEqual(calls.slice(-2), ['context-close', 'browser-close']);
});

test('module discovery tries playwright-core only after playwright is missing', async () => {
  const { deps } = fixture();
  const names = [];
  deps.resolve = name => { names.push(name); if (name === 'playwright') throw new Error('missing'); return name; };
  assert.equal((await run({}, deps)).exitCode, 0);
  assert.deepEqual(names, ['playwright', 'playwright-core']);
});

test('missing modules and executables have distinct actionable reasons', async () => {
  const { deps } = fixture();
  assert.match((await run({}, { ...deps, resolve() { throw new Error('missing'); } })).probe.headless.reason, /^module-missing:/);
  assert.match((await run({}, { ...deps, isFile: () => false })).probe.headless.reason, /^executable-missing:/);
});

test('invalid explicit overrides never fall back, including empty and relative paths', async () => {
  for (const value of ['', 'relative', path.resolve('absent path')]) {
    const { deps, calls } = fixture();
    deps.isFile = deps.isDirectory = () => false;
    assert.match((await run({ playwrightPath: value }, deps)).probe.headless.reason, /^module-missing:/);
    assert.match((await run({ browserPath: value }, deps)).probe.headless.reason, /^executable-missing:/);
    assert.equal(calls.length, 0);
  }
});

test('standard Windows install paths are bounded discovery candidates', async () => {
  const { deps } = fixture();
  deps.env = { PROGRAMFILES: path.resolve('Program Files') };
  const expected = path.join(deps.env.PROGRAMFILES, 'Microsoft/Edge/Application/msedge.exe');
  deps.isFile = candidate => candidate === expected;
  assert.equal((await run({}, deps)).probe.headless.browserPath, expected);
});

for (const [failure, reason] of [['launch', 'launch-denied'], ['timeout', 'launch-timeout'],
  ['click', 'click failed'], ['text', 'interaction-failed'], ['close', 'cleanup-failed']]) {
  test(`${failure} fails and closes every acquired resource`, async () => {
    const { deps, calls } = fixture(failure);
    const outcome = await run({}, deps);
    assert.notEqual(outcome.exitCode, 0);
    assert.ok(outcome.probe.headless.reason.includes(reason));
    if (!['launch', 'timeout'].includes(failure))
      assert.deepEqual(calls.slice(-2), ['context-close', 'browser-close']);
  });
}

test('smoke rejects unsafe URLs and absent names before launch', async () => {
  for (const url of [undefined, 'https://127.0.0.1:7494', 'http://example.com:7494', 'http://127.0.0.1',
    'http://localhost:80', 'http://127.0.0.1:7420', 'http://user:secret@localhost:7494',
    'http://localhost:7494/console', 'http://localhost:7494?next=elsewhere']) {
    assert.throws(() => validateSmoke(url, 'fixture'), /unsafe-smoke-url/);
    const { deps, calls } = fixture();
    assert.equal((await run({ url, agentName: 'fixture' }, deps)).exitCode, 2);
    assert.equal(calls.length, 0);
  }
  assert.throws(() => validateSmoke('http://localhost:7494', ''), /AgentName/);
  for (const host of ['127.0.0.1', 'localhost', '[::1]'])
    assert.equal(validateSmoke(`http://${host}:7494`, 'fixture'), `http://${host}:7494`);
});

test('smoke waits for Blazor, fills exact fields, checks UI and API without exposing tokens', async () => {
  const { EventEmitter } = require('node:events');
  const page = new EventEmitter();
  const filled = [];
  let routeHandler;
  let clicked = false;
  page.goto = async () => {
    const socket = new EventEmitter();
    socket.url = () => 'ws://127.0.0.1:7494/_blazor?id=test';
    page.emit('websocket', socket);
    socket.emit('framereceived', { payload: Buffer.from('JS.RenderBatch') });
  };
  page.locator = selector => ({ fill: async text => filled.push([selector, text]) });
  page.getByRole = (role, options) => ({ click: async () => { assert.equal(options.name, 'Register'); clicked = true; } });
  page.getByText = text => ({ waitFor: async () => assert.equal(text, 'Registered sample (fixture/fixture, mastermind).') });
  page.evaluate = async () => ({ agents: [{ name: 'sample', harness: 'fixture', model: 'fixture', tier: 'mastermind' }] });
  const context = { route: async (_, handler) => { routeHandler = handler; }, newPage: async () => page };
  const outcome = await smoke(context, 'http://127.0.0.1:7494', 'sample');
  assert.equal(outcome.smoke, 'passed');
  assert.equal(outcome.transport, 'websocket');
  assert.equal(clicked, true);
  assert.deepEqual(filled, [['#agent-name', 'sample'], ['#agent-harness', 'fixture'], ['#agent-model', 'fixture'], ['#agent-tier', 'mastermind']]);
  let aborted = false;
  await routeHandler({ request: () => ({ url: () => 'http://localhost:7420/console' }), abort: async () => { aborted = true; } });
  assert.equal(aborted, true);
  page.goto = async () => {};
  await assert.rejects(smoke(context, 'http://127.0.0.1:7494', 'sample', 1), /Blazor connection timed out/);
});

const runner = path.resolve(__dirname, '../scripts/browser-capability.ps1');
test('smoke requires a fulfilled successful Blazor GET render batch for long-poll readiness', async () => {
  const { EventEmitter } = require('node:events');
  for (const scenario of ['render', 'non-render', 'negotiate', 'initializers', 'post', 'failed', 'fulfill-error', 'request-error', 'roster']) {
    const page = new EventEmitter();
    const calls = [];
    let handler;
    const context = { route: async (_, callback) => { handler = callback; }, newPage: async () => page };
    page.goto = async () => {
      await handler({
        request: () => ({ url: () => `http://localhost:7494/_blazor${scenario === 'negotiate' ? '/negotiate' : scenario === 'initializers' ? '/initializers' : ''}?id=test`,
          method: () => scenario === 'post' ? 'POST' : 'GET' }),
        fetch: async options => {
          assert.equal(options.maxRedirects, 0);
          assert.ok(options.timeout > 0 && options.timeout <= 100);
          if (scenario === 'request-error') throw new Error('poll request failed');
          return { status: () => scenario === 'failed' ? 500 : 200,
            body: async () => Buffer.concat([Buffer.from([0, 128]), Buffer.from(scenario === 'non-render' ? 'keepalive' : 'JS.RenderBatch'), Buffer.from([255])]),
            dispose: async () => calls.push('dispose') };
        },
        fulfill: async () => {
          if (scenario === 'fulfill-error') throw new Error('poll fulfillment failed');
          calls.push('fulfill');
        },
        abort: async () => calls.push('abort'),
        continue: async () => assert.fail('HTTP requests must never continue unchecked')
      });
    };
    page.locator = selector => ({ fill: async text => calls.push([selector, text]) });
    page.getByRole = (role, options) => ({ click: async () => {
      assert.equal(role, 'button');
      assert.deepEqual(options, { name: 'Register', exact: true });
      calls.push('click');
    } });
    page.getByText = (text, options) => ({ waitFor: async () => {
      assert.equal(text, 'Registered sample (fixture/fixture, mastermind).');
      assert.deepEqual(options, { exact: true });
      calls.push('feedback');
    } });
    page.evaluate = async () => {
      calls.push('api');
      return { agents: scenario === 'roster' ? [] : [{ name: 'sample', harness: 'fixture', model: 'fixture', tier: 'mastermind' }] };
    };
    if (scenario === 'render') {
      const outcome = await smoke(context, 'http://localhost:7494', 'sample', 100);
      assert.equal(outcome.smoke, 'passed');
      assert.equal(outcome.transport, 'long-polling');
      assert.ok(outcome.evidence.includes('Blazor connected via long-polling'));
      assert.deepEqual(calls, ['fulfill', 'dispose', ['#agent-name', 'sample'], ['#agent-harness', 'fixture'],
        ['#agent-model', 'fixture'], ['#agent-tier', 'mastermind'], 'click', 'feedback', 'api']);
    } else {
      await assert.rejects(smoke(context, 'http://localhost:7494', 'sample', 100),
        scenario === 'fulfill-error' ? /poll fulfillment failed/ : scenario === 'request-error' ? /poll request failed/
          : scenario === 'roster' ? /Registration absent/ : /Blazor connection timed out/);
      if (scenario !== 'roster') assert.ok(!calls.includes('click'));
    }
  }
});

test('smoke rejects redirect origins before following and fails on page errors or missing API evidence', async () => {
  const { EventEmitter } = require('node:events');
  for (const failure of ['redirect', 'same-origin-redirect', 'success', 'pageerror', 'roster']) {
    const page = new EventEmitter();
    let handler;
    let disposed = false;
    let aborted = false;
    let fulfilled = false;
    const redirect = failure.includes('redirect');
    const context = { route: async (_, callback) => { handler = callback; }, newPage: async () => page };
    page.goto = async () => {
      const socket = new EventEmitter();
      socket.url = () => 'ws://localhost:7494/_blazor';
      page.emit('websocket', socket);
      socket.emit('framereceived', { payload: 'JS.RenderBatch' });
      if (failure === 'pageerror') page.emit('pageerror', new Error('render exploded'));
      if (redirect || failure === 'success') await handler({
        request: () => ({ url: () => 'http://localhost:7494/console' }),
        fetch: async options => {
          assert.equal(options.maxRedirects, 0);
          assert.ok(options.timeout > 0 && options.timeout <= 30000);
          return { headers: () => ({ location: failure === 'same-origin-redirect' ? '/console' : 'http://localhost:7420/console' }),
            status: () => redirect ? 302 : 200,
            dispose: async () => { disposed = true; } };
        },
        abort: async () => { aborted = true; },
        continue: async () => assert.fail('HTTP requests must never continue unchecked'),
        fulfill: async ({ response }) => { assert.equal(response.status(), 200); fulfilled = true; }
      });
    };
    page.locator = () => ({ fill: async () => {} });
    page.getByRole = () => ({ click: async () => {} });
    page.getByText = () => ({ waitFor: async () => {} });
    page.evaluate = async () => ({ agents: failure === 'roster' ? [] : [
      { name: 'sample', harness: 'fixture', model: 'fixture', tier: 'mastermind' }
    ] });
    if (failure === 'success') {
      assert.equal((await smoke(context, 'http://localhost:7494', 'sample')).smoke, 'passed');
      assert.equal(fulfilled, true);
      assert.equal(disposed, true);
    } else await assert.rejects(smoke(context, 'http://localhost:7494', 'sample'),
      redirect ? /redirect refused/ : failure === 'pageerror' ? /render exploded/ : /Registration absent/);
    if (redirect) { assert.equal(aborted, true); assert.equal(disposed, true); assert.equal(fulfilled, false); }
  }
});

function powershell(args) {
  const child = spawnSync('pwsh', ['-NoProfile', '-File', runner, ...args], { encoding: 'utf8', timeout: 25000, windowsHide: true });
  assert.ifError(child.error);
  return { ...child, probe: JSON.parse(child.stdout.trim()) };
}

test('PowerShell reports unsupported platforms', { skip: process.platform === 'win32' }, () => {
  const outcome = powershell([]);
  assert.equal(outcome.status, 2);
  assert.match(outcome.probe.headless.reason, /^unsupported-platform:/);
});

test('PowerShell reports missing Node and module without a browser', { skip: process.platform !== 'win32' }, () => {
  const missingNode = powershell(['-NodePath', 'a-definitely-missing-node']);
  assert.equal(missingNode.status, 2);
  assert.match(missingNode.probe.headless.reason, /^node-absent:/);
  const missingModule = powershell(['-NodePath', process.execPath, '-PlaywrightPath', path.resolve('missing-T94-playwright')]);
  assert.equal(missingModule.status, 2);
  assert.match(missingModule.probe.headless.reason, /^module-missing:/);
});

test('PowerShell preserves spaces and kills its owned tree at the total deadline', { skip: process.platform !== 'win32' }, () => {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'browser capability '));
  const pidFile = path.join(directory, 'child.pid');
  try {
    const modulePath = path.join(directory, 'fake module');
    fs.mkdirSync(modulePath);
    const browser = path.join(directory, 'installed browser.exe');
    fs.writeFileSync(browser, 'fixture');
    fs.writeFileSync(path.join(modulePath, 'index.js'), `module.exports.chromium = {
      launch: async options => {
        if (!options.executablePath.includes('installed browser.exe')) throw Error('lost path');
        return { newContext: async () => ({ setDefaultTimeout() {},
          newPage: async () => ({ goto: async () => {}, getByRole: () => ({ click: async () => {}, textContent: async () => 'clicked' }) }),
          close: async () => {} }), close: async () => {} };
      }
    };`);
    const args = ['-NodePath', process.execPath, '-PlaywrightPath', modulePath, '-BrowserPath', browser];
    assert.equal(powershell(args).status, 0);
    for (const rootExits of [false, true]) {
      fs.writeFileSync(path.join(modulePath, 'index.js'), `const fs = require('fs');
        module.exports.chromium = { launch: async () => {
          const child = require('child_process').spawn(process.execPath, ['-e', 'setInterval(() => {}, 1000)'],
            { detached: true, windowsHide: true, stdio: ['ignore', 'inherit', 'inherit'] });
          fs.writeFileSync(${JSON.stringify(pidFile)}, String(child.pid));
          ${rootExits ? 'process.exit(0);' : ''}
          await new Promise(() => {});
        } };`);
      const started = performance.now();
      const timeout = powershell([...args, '-TimeoutSeconds', '8']);
      assert.ok(performance.now() - started < 20000, 'Probe exceeded bounded outer watchdog');
      assert.equal(timeout.status, 2);
      assert.match(timeout.probe.headless.reason, /^launch-timeout:/);
      const pid = Number(fs.readFileSync(pidFile, 'utf8'));
      assert.throws(() => process.kill(pid, 0), { code: 'ESRCH' });
      fs.unlinkSync(pidFile);
      }
  } finally {
    if (fs.existsSync(pidFile)) {
      const pid = Number(fs.readFileSync(pidFile, 'utf8'));
      try { process.kill(pid); } catch (error) { if (error.code !== 'ESRCH') throw error; }
    }
    assert.equal(path.dirname(directory), fs.realpathSync(os.tmpdir()));
    fs.rmSync(directory, { recursive: true });
  }
});
