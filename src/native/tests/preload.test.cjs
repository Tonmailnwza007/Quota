'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const script = fs.readFileSync(path.join(__dirname, '../preload.js'), 'utf8');

function environment(origin = 'https://quota.local', topLevel = true) {
  const sent = []; const listeners = []; const timers = new Map(); let timerSerial = 0;
  const window = { chrome: { webview: { postMessage: value => sent.push(JSON.parse(JSON.stringify(value))), addEventListener: (_type, listener) => listeners.push(listener) } } };
  window.top = topLevel ? window : {};
  vm.runInNewContext(script, {window, location:{origin}, TextEncoder,
    setTimeout: callback => { timers.set(++timerSerial, callback); return timerSerial; }, clearTimeout: id => timers.delete(id)});
  return {window, sent, timers, receive: message => listeners.forEach(listener => listener({data:message}))};
}

test('preload is restricted to the local top-level document', () => {
  assert.equal(environment('https://invalid.example').window.costats, undefined);
  assert.equal(environment('https://quota.local', false).window.costats, undefined);
  const api = environment().window.costats;
  assert.ok(Object.isFrozen(api));
  assert.equal(api.setWindowMode, undefined);
  assert.equal(api.execute, undefined);
  assert.equal(api.readFile, undefined);
});

test('native replies stay correlated and arbitrary host error text is redacted', async () => {
  const env = environment(); const api = env.window.costats;
  const state = api.getState(); const settings = api.getSettings();
  env.receive({id:env.sent[1].id,result:{enabled:{codex:true}}});
  env.receive({id:env.sent[0].id,result:{providers:[]}});
  assert.equal(JSON.stringify(await state), '{"providers":[]}');
  assert.equal(JSON.stringify(await settings), '{"enabled":{"codex":true}}');
  const rejected = api.openUsage('unknown');
  assert.deepEqual(env.sent[2].params, {providerId:'unknown'});
  env.receive({id:env.sent[2].id,error:'SECRET_SENTINEL'});
  await assert.rejects(rejected, error => !error.message.includes('SECRET_SENTINEL'));
  assert.equal(env.timers.size, 0);
});

test('state subscriptions unsubscribe and timed-out requests do not remain pending', async () => {
  const env = environment(); let updates = 0;
  const off = env.window.costats.onState(() => updates++);
  env.receive({event:'state',data:{providers:[]}}); off();
  env.receive({event:'state',data:{providers:[]}});
  assert.equal(updates, 1);
  const pending = env.window.costats.refresh();
  const rejection = assert.rejects(pending, /did not respond/);
  [...env.timers.values()][0]();
  await rejection;
});

test('notification test is a fixed no-argument host operation', async () => {
  const env = environment();
  const result = env.window.costats.testNotification();
  assert.equal(env.sent[0].method, 'testNotification');
  assert.equal(env.sent[0].params, undefined);
  env.receive({id:env.sent[0].id,result:{accepted:true}});
  assert.equal((await result).accepted, true);
});

test('startup exposes only a boolean operation without a path or registry API', async () => {
  const env = environment();
  const result = env.window.costats.setStartWithWindows(true);
  assert.equal(env.sent[0].method, 'setStartWithWindows');
  assert.deepEqual(env.sent[0].params, {enabled:true});
  env.receive({id:env.sent[0].id,result:{enabled:true}});
  assert.equal((await result).enabled, true);
  await assert.rejects(env.window.costats.setStartWithWindows('true'), /boolean/);
  assert.equal(env.sent.length, 1);
  assert.equal(env.window.costats.writeRegistry, undefined);
});

test('native layout requests invariant 520 DIPs on initialization without exposing public resize API', () => {
  const sent = [];
  const window = { chrome: { webview: { postMessage: value => sent.push(JSON.parse(JSON.stringify(value))), addEventListener: () => {} } } };
  window.top = window;
  const document = { readyState: 'complete', documentElement: {}, querySelector: () => null };
  vm.runInNewContext(script, {
    window,
    document,
    location: { origin: 'https://quota.local' },
    TextEncoder,
    setTimeout: () => 1,
    clearTimeout: () => {},
  });

  assert.equal(sent.length, 1);
  assert.equal(sent[0].method, '__layout');
  assert.deepEqual(sent[0].params, { height: 520 });

  // Assert no public sizing/resize APIs are exposed to React
  assert.equal(window.costats.resize, undefined);
  assert.equal(window.costats.setHeight, undefined);
  assert.equal(window.costats.__layout, undefined);
});

test('native layout invariant holds when DOM loads asynchronously', () => {
  const sent = [];
  let domContentLoadedHandler = null;
  const window = { chrome: { webview: { postMessage: value => sent.push(JSON.parse(JSON.stringify(value))), addEventListener: () => {} } } };
  window.top = window;
  const document = {
    readyState: 'loading',
    documentElement: {},
    addEventListener: (event, handler) => {
      if (event === 'DOMContentLoaded') domContentLoadedHandler = handler;
    },
    querySelector: () => null,
  };
  vm.runInNewContext(script, {
    window,
    document,
    location: { origin: 'https://quota.local' },
    TextEncoder,
    setTimeout: () => 1,
    clearTimeout: () => {},
  });

  assert.equal(sent.length, 0);
  assert.ok(typeof domContentLoadedHandler === 'function');
  domContentLoadedHandler();
  assert.equal(sent.length, 1);
  assert.equal(sent[0].method, '__layout');
  assert.deepEqual(sent[0].params, { height: 520 });
});
