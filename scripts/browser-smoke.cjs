'use strict';

function validateSmoke(value, agentName) {
  let url;
  try { url = new URL(value); } catch { throw new Error('unsafe-smoke-url: Supply an explicit loopback http URL and non-default port.'); }
  if (url.protocol !== 'http:' || !['localhost', '127.0.0.1', '[::1]'].includes(url.hostname)
      || !url.port || url.port === '7420' || url.username || url.password
      || url.pathname !== '/' || url.search || url.hash)
    throw new Error('unsafe-smoke-url: Require loopback http origin with non-default port other than 7420, without credentials.');
  if (typeof agentName !== 'string' || !agentName.trim()) throw new Error('invalid-options: Smoke requires AgentName.');
  return url.origin;
}

async function smoke(context, value, agentName, timeout = 30000) {
  const origin = validateSmoke(value, agentName);
  const deadline = performance.now() + timeout;
  const errors = [];
  let signalConnected;
  const connected = new Promise(resolve => { signalConnected = resolve; });
  // Guard before following redirects, including subresources. Never send a request to a second origin.
  await context.route('**/*', async route => {
    try {
      if (new URL(route.request().url()).origin !== origin) {
        errors.push('Cross-origin request or redirect refused.');
        await route.abort();
      } else {
        // Fetch without following redirects; routing alone need not intercept a redirect chain.
        const remaining = Math.ceil(deadline - performance.now());
        if (remaining <= 0) throw new Error('Smoke request deadline exceeded.');
        const response = await route.fetch({ maxRedirects: 0, timeout: remaining });
        try {
          if (response.status() >= 300 && response.status() < 400) {
            errors.push('HTTP redirect refused: smoke does not support redirects.');
            await route.abort();
          } else {
            const render = response.status() >= 200 && response.status() < 300
              && new URL(route.request().url()).pathname === '/_blazor'
              && route.request().method() === 'GET'
              && (await response.body()).includes(Buffer.from('JS.RenderBatch'));
            await route.fulfill({ response });
            if (render) signalConnected('long-polling');
          }
        } finally { await response.dispose(); }
      }
    } catch (error) {
      errors.push(`Request failed: ${error.message}`);
      await route.abort().catch(() => {});
    }
  });
  const page = await context.newPage();
  page.on('pageerror', error => errors.push(error.message));
  page.on('websocket', socket => {
    const socketUrl = new URL(socket.url());
    if (socketUrl.host !== new URL(origin).host || socketUrl.pathname !== '/_blazor') return;
    socket.on('framereceived', ({ payload }) => {
      if (payload.toString().includes('JS.RenderBatch')) signalConnected('websocket');
    });
  });
  try { await page.goto(`${origin}/console`); }
  catch (error) { throw new Error(errors.length ? errors.join(' ') : error.message); }
  if (errors.length) throw new Error(errors.join(' '));
  // The first server render batch proves the interactive circuit has started, unlike prerendered inputs.
  let timer;
  let transport;
  try {
    transport = await Promise.race([connected, new Promise((_, reject) => {
      timer = setTimeout(() => reject(new Error(['Blazor connection timed out.', ...errors].join(' '))), timeout);
    })]);
  } finally { clearTimeout(timer); }
  for (const [field, text] of [['name', agentName], ['harness', 'fixture'], ['model', 'fixture'], ['tier', 'mastermind']])
    await page.locator(`#agent-${field}`).fill(text);
  await page.getByRole('button', { name: 'Register', exact: true }).click();
  const feedback = `Registered ${agentName} (fixture/fixture, mastermind).`;
  await page.getByText(feedback, { exact: true }).waitFor();
  const roster = await page.evaluate(async () => {
    const response = await fetch('/api/agents', { redirect: 'error' });
    if (!response.ok) throw new Error(`Agent API returned ${response.status}`);
    return response.json();
  });
  if (!roster.agents?.some(agent => agent.name === agentName && agent.harness === 'fixture'
      && agent.model === 'fixture' && agent.tier === 'mastermind')) throw new Error('Registration absent from scratch /api/agents.');
  if (errors.length) throw new Error(errors.join(' '));
  return { smoke: 'passed', origin, agentName, transport, evidence: [`Blazor connected via ${transport}`, 'Filled four agent fields',
    'Clicked Register', feedback, 'Verified scratch /api/agents'] };
}

module.exports = { validateSmoke, smoke };
