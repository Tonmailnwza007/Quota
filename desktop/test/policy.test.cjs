'use strict';
const test = require('node:test');
const assert = require('node:assert/strict');
const path = require('node:path');
const { settingsPatch, usageUrl, resourcePath, trustedSender } = require('../lib/policy.cjs');

test('settings patches preserve real booleans and reject unknown or token-shaped input', () => {
  const patch = { enabled: { codex: false, 'codex-cli': true }, refreshMinutes: 10, paths: { codexCliHome: 'C:\\cli' } };
  assert.deepEqual(settingsPatch(patch), patch);
  assert.notEqual(settingsPatch(patch).enabled, patch.enabled);
  for (const invalid of [null, [], { enabled: { codex: 'false' } }, { enabled: { other: true } },
    { refreshMinutes: 0 }, { refreshMinutes: 1.5 }, { refreshMinutes: 61 }, { paths: { access_token: 'sentinel' } },
    { access_token: 'sentinel' }, { paths: { codexAuthPath: 'bad\0path' } }, JSON.parse('{"__proto__":{"polluted":true}}')]) {
    assert.throws(() => settingsPatch(invalid));
  }
  assert.equal({}.polluted, undefined);
});

test('usage pages come from a provider allowlist', () => {
  assert.equal(usageUrl('codex-cli'), 'https://chatgpt.com/codex/settings/usage');
  assert.equal(usageUrl('claude:work'), 'https://claude.ai/settings/usage');
  for (const invalid of ['https://evil.example', 'javascript:alert(1)', 'file:///C:/', 'other']) assert.throws(() => usageUrl(invalid));
});

test('unified Antigravity source selection rejects removed CLI switches and ambiguous sources', () => {
  const valid = {enabled:{antigravity:true},antigravitySource:'custom',paths:{antigravityAuthPath:'C:\\synthetic\\auth.json',antigravityCredentialTarget:''}};
  assert.deepEqual(settingsPatch(valid),valid);
  for (const invalid of [{enabled:{'antigravity-cli':true}},{antigravitySource:'choose'},{antigravitySource:true},
    {paths:{antigravityCliAuthPath:'old'}},{paths:{antigravityAppPath:'old'}}]) assert.throws(()=>settingsPatch(invalid));
});

test('optional usage details and editable remaining thresholds are constrained', () => {
  const valid = {showUsageDetails:true,notifications:{enabled:true,warningPercent:25,criticalPercent:7}};
  assert.deepEqual(settingsPatch(valid),valid);
  for (const invalid of [{showUsageDetails:'yes'}, {notifications:[]}, {notifications:{warningPercent:101}},
    {notifications:{criticalPercent:-1}}, {notifications:{warningPercent:10.1}},
    {notifications:{warningPercent:5,criticalPercent:5}}, {notifications:{access_token:'sentinel'}}]) {
    assert.throws(() => settingsPatch(invalid));
  }
});

test('resource protocol stays within the packaged renderer', () => {
  const root = path.resolve('renderer');
  assert.equal(resourcePath(root, 'costats://app/'), path.join(root, 'index.html'));
  assert.equal(resourcePath(root, 'costats://app/assets/main.js'), path.join(root, 'assets/main.js'));
  for (const invalid of ['file:///C:/private', 'costats://other/index.html', 'costats://app/assets/%2e%2e%2f%2e%2e/private', 'costats://app/a%5cb', 'costats://app/a%00b']) {
    assert.throws(() => resourcePath(root, invalid));
  }
});

test('IPC accepts only this window main frame on the local app origin', () => {
  const frame = { url: 'costats://app/index.html' };
  const contents = { mainFrame: frame, isDestroyed: () => false };
  assert.equal(trustedSender({ sender: contents, senderFrame: frame }, contents), true);
  assert.equal(trustedSender({ sender: {}, senderFrame: frame }, contents), false);
  assert.equal(trustedSender({ sender: contents, senderFrame: { url: frame.url } }, contents), false);
  frame.url = 'https://example.org';
  assert.equal(trustedSender({ sender: contents, senderFrame: frame }, contents), false);
});
