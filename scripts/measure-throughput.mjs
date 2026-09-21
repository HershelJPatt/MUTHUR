// Read-only ledger measurement. Node 22+; no tokens, mutations or model calls.
// node scripts/measure-throughput.mjs --url http://127.0.0.1:7420 --since 2026-09-20T15:24:15Z --output artifacts/baseline.json
// Archived replay: --events artifacts/events.json --until <UTC timestamp> --since <UTC timestamp>
import fs from 'node:fs';
import path from 'node:path';

const args = Object.fromEntries(Array.from({ length: (process.argv.length - 2) / 2 }, (_, i) =>
  [process.argv[2 + i * 2].replace(/^--/, ''), process.argv[3 + i * 2]]));
const now = Date.now();
const until = args.until ? Date.parse(args.until) : now;
const since = args.since ? Date.parse(args.since) : until - 24 * 3600000;
if (!Number.isFinite(since) || !Number.isFinite(until) || since >= until || until > now)
  throw new Error('Provide a valid --since earlier than --until (at or before now).');
let events;
if (args.events) events = JSON.parse(fs.readFileSync(args.events, 'utf8').replace(/^\uFEFF/, ''));
else {
  const base = (args.url || process.env.MUTHUR_URL || 'http://127.0.0.1:7420').replace(/\/$/, '');
  async function read(query) {
    const response = await fetch(`${base}/api/v1/events?${query}`, { signal: AbortSignal.timeout(30000) });
    if (!response.ok) throw new Error(`Read failed: HTTP ${response.status}`);
    return response.json();
  }
  const head = (await read('limit=1')).at(-1)?.seq || 0;
  events = [];
  let after = 0;
  while (after < head) {
    const page = (await read(`since=${after}&limit=1000`)).filter(e => e.seq <= head);
    if (!page.length) throw new Error('Ledger changed or pagination stopped before the captured head.');
    events.push(...page);
    after = page.at(-1).seq;
  }
}
events.sort((a, b) => a.seq - b.seq);
for (let i = 1; i < events.length; i++)
  if (events[i].seq !== events[i - 1].seq + 1) throw new Error('Incomplete ledger: sequence gap.');
if (events.length && events[0].seq !== 1) throw new Error('Replay needs the ledger from sequence 1.');
const capturedHighSequence = events.at(-1)?.seq || 0;
const at = e => Date.parse(e.at);
events = events.filter(e => at(e) <= until);
const selected = events.filter(e => at(e) >= since);
const round = x => Math.round(x * 1000) / 1000;
const stats = values => {
  values.sort((a, b) => a - b);
  return { n: values.length, mean: values.length ? round(values.reduce((a, b) => a + b, 0) / values.length) : null,
    median: values.length ? round((values[Math.floor((values.length - 1) / 2)] + values[Math.floor(values.length / 2)]) / 2) : null,
    p90: values.length ? round(values[Math.ceil(values.length * .9) - 1]) : null };
};
const count = type => selected.filter(e => e.type === type).length;
const cloud = selected.filter(e => /^worker\.(finished|failed)$/.test(e.type) && e.payload.tier !== 'utility');
const failures = cloud.filter(e => e.type === 'worker.failed');
const entered = { 'task.added': 'backlog', 'task.claimed': 'in_progress', 'task.released': 'backlog', 'task.claim_expired': 'backlog',
  'task.reopened': 'backlog', 'task.blocked': 'blocked', 'task.unblocked': 'in_progress', 'task.implemented': 'validating',
  'task.validation_failed': 'in_progress', 'task.validation_blocked': 'in_progress', 'task.validated': 'validated',
  'task.land_failed': 'in_progress', 'task.landed': 'done', 'task.pr_opened': 'done', 'task.cancelled': 'cancelled' };
const tasks = new Map();
const stateSeconds = {};
const validationQueue = [], validationWork = [], landingWait = [], answeredRecovery = [];
function addState(t, end) {
  if (!t.state) return;
  stateSeconds[t.state] = (stateSeconds[t.state] || 0) + Math.max(0, Math.min(until, end) - Math.max(since, t.from)) / 1000;
}
for (const e of events) {
  if (e.type === 'agent.reregistered') {
    for (const t of tasks.values()) if (t.exited === e.payload.agent) {
      t.exited = null;
      // A replacement registered while ownership remains invalidates stale exit proof. Once the task
      // has actually returned to backlog, registration is part of its next session: keep the wait.
      if (!t.recoveryQueued) t.recovery = null;
    }
  }
  if (!e.taskId) continue;
  if (!tasks.has(e.taskId)) tasks.set(e.taskId, { claims: new Map() });
  const t = tasks.get(e.taskId), time = at(e);
  let next = entered[e.type];
  if (e.type === 'task.dependencies_set' && t.state === 'in_progress' && e.payload.tasks?.length) next = 'backlog';
  if (next && next !== t.state) { addState(t, time); t.state = next; t.from = time; }
  if (next === 'backlog' && t.recovery != null) t.recoveryQueued = true;
  if (['validating', 'validated', 'done', 'cancelled'].includes(next)) {
    t.exited = null; t.recovery = null; t.recoveryQueued = false;
  }
  if (e.type === 'conductor.orchestrator_exited') t.exited = e.payload.agent;
  if (e.type === 'task.unblocked' && t.exited) { t.recovery = time; t.recoveryQueued = false; }
  if (e.type === 'task.claimed') {
    if (t.recovery !== undefined && t.recovery !== null && time >= since) answeredRecovery.push((time - t.recovery) / 60_000);
    t.exited = null; t.recovery = null; t.recoveryQueued = false;
  }
  if (e.type === 'task.implemented') { t.submitted = time; t.claims.clear(); }
  if (e.type === 'validation.claimed') {
    if (t.submitted !== undefined && time >= since) validationQueue.push((time - t.submitted) / 60_000);
    t.claims.set(e.payload.validator, time);
  }
  if (/^validation\.(passed|failed|blocked)$/.test(e.type)) {
    if (t.claims.has(e.payload.validator) && time >= since) validationWork.push((time - t.claims.get(e.payload.validator)) / 60_000);
    t.claims.delete(e.payload.validator);
  }
  if (e.type === 'task.validated') t.validated = time;
  if (['task.validation_failed', 'task.validation_blocked', 'task.land_failed', 'task.reopened', 'task.implemented'].includes(e.type)) t.validated = null;
  if (e.type === 'task.landed' && t.validated != null && time >= since) landingWait.push((time - t.validated) / 60_000);
}
for (const t of tasks.values()) addState(t, until);
const hours = (until - since) / 3600000;
const report = {
  since: new Date(since).toISOString(), until: new Date(until).toISOString(), hours: round(hours), capturedHighSequence,
  replayedThroughSequence: events.at(-1)?.seq || 0, landed: count('task.landed'), landedPerHour: round(count('task.landed') / hours),
  prOpened: count('task.pr_opened'), reopened: count('task.reopened'),
  validation: { passed: count('validation.passed'), failed: count('validation.failed'), blocked: count('validation.blocked'),
    queueMinutes: stats(validationQueue), claimToVerdictMinutes: stats(validationWork), approvedToLandMinutes: stats(landingWait) },
  workers: { runs: cloud.length, successful: cloud.length - failures.length, failed: failures.length,
    minutes: round(cloud.reduce((s, e) => s + (e.payload.seconds || 0), 0) / 60),
    failedMinutes: round(failures.reduce((s, e) => s + (e.payload.seconds || 0), 0) / 60),
    failureKinds: Object.fromEntries([...new Set(failures.map(e => e.payload.failureKind || 'unclassified_legacy'))]
      .map(kind => [kind, failures.filter(e => (e.payload.failureKind || 'unclassified_legacy') === kind).length])) },
  conductor: { staffings: count('conductor.staffing'), sessionFailures: count('conductor.session_failed'), noVerdict: count('conductor.no_verdict'),
    claimExpiries: count('task.claim_expired'), answeredExitedOwnerToNextClaimMinutes: stats(answeredRecovery),
    answeredExitedOwnersStillWaiting: [...tasks.values()].filter(t => t.recovery != null).length },
  taskStateHours: Object.fromEntries(Object.entries(stateSeconds).filter(([s]) => !['done', 'cancelled'].includes(s)).map(([s, seconds]) => [s, round(seconds / 3600)])),
  interpretation: 'Landings are ledger events, not weighted business outcomes. Worker time covers reported non-utility runs only. Completed latency samples exclude still-waiting work; recovery requires recorded child-exit proof. Compare equal windows and task mix; a short sample does not establish 10x improvement.'
};
const json = JSON.stringify(report, null, 2) + '\n';
if (args.output) { fs.mkdirSync(path.dirname(path.resolve(args.output)), { recursive: true }); fs.writeFileSync(args.output, json); }
process.stdout.write(json);
