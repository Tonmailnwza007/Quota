import assert from 'node:assert/strict';
import { spawn } from 'node:child_process';
import { mkdtemp, readFile, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { resolve, join } from 'node:path';
import { createInterface } from 'node:readline';

const worker = resolve(process.argv[2] || 'bridge/publish/costats-bridge.exe');
const temp = await mkdtemp(join(tmpdir(), 'costats-bridge-protocol-'));
const settings = join(temp, 'modern', 'settings.json');
const child = spawn(worker, ['--fixture', '--settings-file', settings, '--fixture-delay-ms', '1500'], {
  stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true,
});
const frames = [];
const waiters = new Set();
let stderr = '';
let fatal;
child.stderr.on('data', data => { stderr += data.toString(); });
child.on('error', error => { fatal = error; });
const exited = new Promise((resolveExit) => child.once('exit', (code, signal) => resolveExit({ code, signal })));
const lines = createInterface({ input: child.stdout });
lines.on('line', line => {
  try {
    const frame = JSON.parse(line);
    assert.equal(frame.jsonrpc, '2.0');
    frames.push(frame);
    for (const wake of waiters) wake();
  } catch (error) { fatal = error; }
});
async function waitFor(predicate, timeout = 6000) {
  const start = Date.now();
  while (Date.now() - start < timeout) {
    if (fatal) throw fatal;
    const found = frames.find(predicate);
    if (found) return found;
    await new Promise(resolveWake => {
      const timer = setTimeout(wake, 20);
      function wake() { clearTimeout(timer); waiters.delete(wake); resolveWake(); }
      waiters.add(wake);
    });
  }
  throw new Error('Protocol response timed out.');
}
let sequence = 0;
async function request(method, params) {
  const id = String(++sequence);
  child.stdin.write(JSON.stringify({ jsonrpc: '2.0', id, method, ...(params === undefined ? {} : { params }) }) + '\n');
  return waitFor(frame => frame.id === id);
}
try {
  await waitFor(frame => frame.method === 'ready' && frame.params.protocolVersion === 1);
  const initial = await waitFor(frame => frame.method === 'stateChanged' && frame.params.providers.length === 0);
  assert.equal(initial.params.isRefreshing, true);
  assert.equal(initial.params.lastRefreshTime, null);
  const before = performance.now();
  assert.ok('accepted' in (await request('refresh')).result);
  const prefs = await request('updateSettings', { enabled: { copilot: true }, refreshMinutes: 2 });
  assert.equal(prefs.result.enabled.copilot, true);
  assert.ok(performance.now() - before < 1000, 'Commands waited on the 1500ms source.');
  const loaded = await waitFor(frame => frame.method === 'stateChanged' && !frame.params.isRefreshing && frame.params.providers.length === 7);
  const providers = loaded.params.providers;
  assert.deepEqual(providers.slice(0, 4).map(p => p.id), ['codex', 'codex-cli', 'antigravity', 'antigravity-cli']);
  assert.notEqual(providers[0].accountId, providers[1].accountId);
  assert.equal(providers[1].sessionProgress, 0);
  assert.equal(providers[1].weeklyProgress, null);
  assert.equal(providers[2].isSharedAccountQuota, true);
  assert.equal(providers[3].status, 'expired');
  assert.equal(providers[4].id, 'claude:work');
  const invalid = await request('updateSettings', { enabled: { codex: false }, access_token: 'SECRET_SENTINEL' });
  assert.equal(invalid.error.code, -32602);
  assert.equal((await request('getSettings')).result.enabled.codex, true);
  child.stdin.write('this is not JSON\n');
  await waitFor(frame => frame.error?.code === -32700);
  await request('updateSettings', { enabled: Object.fromEntries(['codex', 'codex-cli', 'antigravity', 'antigravity-cli', 'claude', 'copilot'].map(id => [id, false])) });
  const final = (await request('getState')).result;
  assert.deepEqual(final.providers, []);
  const saved = JSON.parse(await readFile(settings, 'utf8'));
  assert.ok(Object.values(saved.enabled).every(value => value === false));
  await request('shutdown');
  const exit = await Promise.race([exited, new Promise((_, reject) => setTimeout(() => reject(new Error('Worker did not exit.')), 5000).unref())]);
  assert.equal(exit.code, 0);
  assert.equal(stderr, '');
  assert.ok(!JSON.stringify(frames).includes('SECRET_SENTINEL'));
  console.log('PASS: published worker handshake, loading, responsive commands, six-provider settings, fixtures, validation, persistence and shutdown.');
} finally {
  if (child.exitCode === null) child.kill();
  lines.close();
  await rm(temp, { recursive: true, force: true });
}
