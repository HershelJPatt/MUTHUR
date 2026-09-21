import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { execFileSync } from 'node:child_process';

const script = fileURLToPath(new URL('./measure-throughput.mjs', import.meta.url));
const start = '2026-09-20T23:41:26.362Z';
const answer = '2026-09-20T23:41:40.942Z';
const end = '2026-09-20T23:41:59.094Z';
const owner = 'orchestrator-t-3';
const event = (type, at, payload = {}, taskId = 'T-3') => ({ type, at, payload, taskId });
const registration = at => event('agent.reregistered', at, { agent: owner, conductorStaffed: true }, null);
const blocked = () => [
  event('task.added', start), event('task.claimed', '2026-09-20T23:41:40.792Z', { agent: owner }),
  event('task.blocked', '2026-09-20T23:41:40.821Z'),
  event('conductor.orchestrator_exited', '2026-09-20T23:41:40.865Z', { agent: owner })
];
const release = () => event('task.released', '2026-09-20T23:41:55.702Z', { agent: owner, reason: 'Owning conductor session exited; resume preserved work.' });
const claim = () => event('task.claimed', '2026-09-20T23:41:57.947Z', { agent: owner });
function measure(events, until = end) {
  const directory = fs.mkdtempSync(path.join(os.tmpdir(), 'muthur-throughput-measure-'));
  try {
    const file = path.join(directory, 'events.json');
    fs.writeFileSync(file, JSON.stringify(events.map((e, i) => ({ ...e, seq: i + 1 }))));
    return JSON.parse(execFileSync(process.execPath, [script, '--events', file, '--since', start, '--until', until], { encoding: 'utf8' })).conductor;
  } finally {
    assert.equal(path.dirname(path.resolve(directory)), path.resolve(os.tmpdir()));
    assert.ok(path.basename(directory).startsWith('muthur-throughput-measure-'));
    fs.rmSync(directory, { recursive: true });
  }
}

test('actual validator recovery sequence counts its 17.005-second sample across conductor registration', () => {
  const result = measure([...blocked(), event('task.unblocked', answer), release(),
    event('conductor.staffing', '2026-09-20T23:41:56.650Z', { role: '#orchestrator' }),
    registration('2026-09-20T23:41:57.849Z'), claim()]);
  assert.deepEqual(result.answeredExitedOwnerToNextClaimMinutes, { n: 1, mean: .283, median: .283, p90: .283 });
  assert.equal(result.answeredExitedOwnersStillWaiting, 0);
});
test('registration before an answer invalidates old child-exit evidence', () => {
  const result = measure([...blocked(), registration('2026-09-20T23:41:40.900Z'), event('task.unblocked', answer), release(), claim()]);
  assert.equal(result.answeredExitedOwnerToNextClaimMinutes.n, 0);
  assert.equal(result.answeredExitedOwnersStillWaiting, 0);
});
test('registration after the answer but before ownership is released invalidates the pending sample', () => {
  const result = measure([...blocked(), event('task.unblocked', answer), registration('2026-09-20T23:41:41.000Z'), release(), claim()]);
  assert.equal(result.answeredExitedOwnerToNextClaimMinutes.n, 0);
  assert.equal(result.answeredExitedOwnersStillWaiting, 0);
});
test('queued recovery remains censored while the newly registered session has not claimed', () => {
  const result = measure([...blocked(), event('task.unblocked', answer), release(), registration('2026-09-20T23:41:57.849Z')]);
  assert.equal(result.answeredExitedOwnerToNextClaimMinutes.n, 0);
  assert.equal(result.answeredExitedOwnersStillWaiting, 1);
});
test('a later claim or cancellation cannot double count or leave a false pending recovery', () => {
  const result = measure([...blocked(), event('task.unblocked', answer), release(), registration('2026-09-20T23:41:57.849Z'), claim(),
    event('task.released', '2026-09-20T23:41:58.000Z'), event('task.claimed', '2026-09-20T23:41:58.500Z')]);
  assert.equal(result.answeredExitedOwnerToNextClaimMinutes.n, 1);
  const cancelled = measure([...blocked(), event('task.unblocked', answer), release(), event('task.cancelled', '2026-09-20T23:41:58.000Z')]);
  assert.equal(cancelled.answeredExitedOwnersStillWaiting, 0);
});
