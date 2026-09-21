// Deterministic installed-product fixture. Used only by verify-throughput.ps1, never a real model.
import fs from 'node:fs';
import path from 'node:path';
import { execFileSync } from 'node:child_process';
const fixture = process.env.MUTHUR_THROUGHPUT_FIXTURE;
if (!fixture) throw new Error('This fixture requires an explicit scratch configuration.');
const config = JSON.parse(fs.readFileSync(fixture, 'utf8'));
if (process.env.MUTHUR_URL !== config.url || !config.url.startsWith('http://127.0.0.1:')) throw new Error('Scratch URL mismatch.');
const prompt = fs.readFileSync(0, 'utf8');
const output = process.argv[process.argv.indexOf('--output-last-message') + 1];
const record = { output, agent: process.env.MUTHUR_AGENT || null, prompt, at: new Date().toISOString() };
fs.appendFileSync(path.join(config.evidence, 'attempts.jsonl'), JSON.stringify(record) + '\n');
async function api(method, route, body) {
  const response = await fetch(`${config.url}/api/v1/${route}`, { method,
    headers: { 'Content-Type': 'application/json', Authorization: `Bearer ${process.env.MUTHUR_TOKEN}` },
    body: body === undefined ? undefined : JSON.stringify(body) });
  if (!response.ok) throw new Error(`${method} ${route}: ${response.status} ${await response.text()}`);
  return response.status === 204 ? null : response.json();
}
if (process.env.MUTHUR_AGENT) {
  const task = config.resumeTask;
  if (process.env.MUTHUR_AGENT !== `orchestrator-${task.toLowerCase()}`) throw new Error('Unexpected session identity.');
  await api('POST', `tasks/${task}/claim`, {});
  const detail = await api('GET', `tasks/${task}`);
  if (!detail.events.some(e => e.type === 'request.answered')) {
    await api('POST', `tasks/${task}/spec`, { path: `specs/${task}.md` });
    const first = await api('POST', 'requests', { task, question: 'First scope decision?', kind: 'human' });
    const second = await api('POST', 'requests', { task, question: 'Second prerequisite decision?', kind: 'human' });
    fs.writeFileSync(path.join(config.evidence, 'requests.json'), JSON.stringify([first.id, second.id]));
  } else {
    if (!prompt.includes('T95 approved scope') || !prompt.includes('T95 preserve prerequisite')) throw new Error('Missing latest decisions.');
    await api('POST', `tasks/${task}/dependencies`, { tasks: [config.prerequisite], reason: 'Approved prerequisite preserved.' });
    fs.writeFileSync(path.join(config.evidence, 'resumed.json'), JSON.stringify({ at: new Date().toISOString(), task }));
  }
  fs.writeFileSync(output, 'STATUS: done\nNOTES: fixture session completed');
} else {
  if (process.env.MUTHUR_TOKEN) throw new Error('Worker inherited hub credentials.');
  for (const field of ['Named local base branch:', 'Full base commit SHA at dispatch:', 'Named default branch:', 'Frozen spec blob SHA:'])
    if (!prompt.includes(field)) throw new Error(`Missing ${field}`);
  execFileSync('git', ['status', '--porcelain'], { cwd: process.cwd(), env: { ...process.env, GIT_TEST_ASSUME_DIFFERENT_OWNER: '1' } });
  if (process.env.MUTHUR_THROUGHPUT_FAIL) {
    fs.writeFileSync(output, 'STATUS: done\nNOTES: final text cannot override process failure');
    process.stderr.write('T95 intentional process failure\n');
    process.exitCode = 7;
  } else {
    fs.writeFileSync(path.join(process.cwd(), 'worker-result.txt'), 'Deterministic fixture output.\n');
    fs.writeFileSync(output, 'STATUS: done\nCOMMITS: pending (launcher)\nNOTES: fixture verified assignment and process-local Git trust');
  }
}
