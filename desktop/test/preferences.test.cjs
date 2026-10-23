'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const os = require('node:os');
const path = require('node:path');
const { carryForwardPreferences } = require('../lib/preferences.cjs');

test('rename preserves selected providers without modifying the previous profile or an existing new profile', () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'quota-preferences-'));
  try {
    const previous = path.join(temp, 'previous.json');
    const next = path.join(temp, 'new', 'settings.json');
    const original = JSON.stringify({ enabled: { codex: true, 'codex-cli': true, antigravity: true, claude: false }, refreshMinutes: 2, paths: {} });
    fs.writeFileSync(previous, original);
    assert.equal(carryForwardPreferences(previous, next), true);
    assert.equal(fs.readFileSync(previous, 'utf8'), original);
    assert.equal(JSON.parse(fs.readFileSync(next, 'utf8')).enabled.claude, false);
    fs.writeFileSync(next, '{"enabled":{"codex":false}}');
    assert.equal(carryForwardPreferences(previous, next), false);
    assert.equal(JSON.parse(fs.readFileSync(next, 'utf8')).enabled.codex, false);
  } finally { fs.rmSync(temp, { recursive: true, force: true }); }
});

test('rename skips malformed or credential-shaped preferences without creating a new file', () => {
  const temp = fs.mkdtempSync(path.join(os.tmpdir(), 'quota-preferences-'));
  try {
    const previous = path.join(temp, 'previous.json'); const next = path.join(temp, 'new.json');
    for (const value of ['bad json', '{"access_token":"sentinel"}', '{"enabled":{"other":true}}']) {
      fs.writeFileSync(previous, value);
      assert.equal(carryForwardPreferences(previous, next), false);
      assert.equal(fs.existsSync(next), false);
    }
  } finally { fs.rmSync(temp, { recursive: true, force: true }); }
});
