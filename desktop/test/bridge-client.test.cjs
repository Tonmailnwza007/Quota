'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { once } = require('node:events');
const { BridgeClient } = require('../lib/bridge-client.cjs');

async function setup(t, options) {
  const client = new BridgeClient(process.execPath, [path.join(__dirname, 'fake-worker.cjs')], options);
  t.after(() => client.stop());
  const ready = once(client, 'ready');
  client.start();
  await ready;
  return client;
}

test('concurrent requests remain correlated and split Unicode frames survive', async t => {
  const client = await setup(t);
  const answers = await Promise.all([client.request('echo', { n: 1 }), client.request('echo', { n: 2 }), client.request('unicode')]);
  assert.deepEqual(answers, [{ n: 1 }, { n: 2 }, 'ผู้ใช้']);
});

test('request deadline rejects a slow response without blocking other commands', async t => {
  const client = await setup(t, { timeout: 120 });
  const slow = assert.rejects(client.request('delayed'), /did not respond/);
  assert.equal(await client.request('echo', 42), 42);
  await slow;
});

test('malformed backend output disconnects and rejects pending requests', async t => {
  const client = await setup(t);
  const disconnected = once(client, 'disconnect');
  await assert.rejects(client.request('invalid'), /invalid response/);
  await disconnected;
  assert.equal(client.connected, false);
});

test('raw backend error text cannot leak through the preload boundary', async t => {
  const client = await setup(t);
  await assert.rejects(client.request('secretError'), error => !error.message.includes('SECRET_TOKEN') && /Invalid settings/.test(error.message));
});
