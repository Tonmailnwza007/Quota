'use strict';
const path = require('node:path');

const PROVIDERS = new Set(['codex', 'codex-cli', 'antigravity', 'claude', 'copilot']);
const PATH_KEYS = new Set(['codexAuthPath', 'codexLogDir', 'codexCliHome', 'codexCliAuthPath', 'codexCliLogDir',
  'codexDesktopZeronAccountId', 'codexCliZeronAccountId', 'antigravityAuthPath', 'antigravityCredentialTarget']);
const plainObject = value => value !== null && typeof value === 'object' && !Array.isArray(value)
  && (Object.getPrototypeOf(value) === Object.prototype || Object.getPrototypeOf(value) === null);

function settingsPatch(value) {
  if (!plainObject(value)) throw new Error('Invalid settings.');
  const result = {};
  for (const key of Object.keys(value)) {
    if (key === 'enabled') {
      if (!plainObject(value.enabled)) throw new Error('Invalid provider selection.');
      result.enabled = {};
      for (const [id, enabled] of Object.entries(value.enabled)) {
        if (!PROVIDERS.has(id) || typeof enabled !== 'boolean') throw new Error('Invalid provider selection.');
        result.enabled[id] = enabled;
      }
    } else if (key === 'refreshMinutes') {
      if (!Number.isInteger(value[key]) || value[key] < 1 || value[key] > 60) throw new Error('Refresh interval must be between 1 and 60 minutes.');
      result.refreshMinutes = value[key];
    } else if (key === 'showUsageDetails') {
      if (typeof value[key] !== 'boolean') throw new Error('Invalid display setting.');
      result.showUsageDetails = value[key];
    } else if (key === 'notifications') {
      if (!plainObject(value.notifications)) throw new Error('Invalid alert settings.');
      result.notifications = {};
      for (const [field, setting] of Object.entries(value.notifications)) {
        if (field === 'enabled' && typeof setting === 'boolean') result.notifications[field] = setting;
        else if ((field === 'warningPercent' || field === 'criticalPercent') && Number.isInteger(setting) && setting >= 0 && setting <= 100) result.notifications[field] = setting;
        else throw new Error('Invalid alert settings.');
      }
      if (result.notifications.warningPercent !== undefined && result.notifications.criticalPercent !== undefined && result.notifications.criticalPercent >= result.notifications.warningPercent) throw new Error('Warning must exceed critical.');
    } else if (key === 'antigravitySource') {
      if (value[key] !== 'saved' && value[key] !== 'custom') throw new Error('Choose an Antigravity account source.');
      result.antigravitySource = value[key];
    } else if (key === 'paths') {
      if (!plainObject(value.paths)) throw new Error('Invalid source settings.');
      result.paths = {};
      for (const [name, text] of Object.entries(value.paths)) {
        if (!PATH_KEYS.has(name) || typeof text !== 'string' || text.length > 4096 || text.includes('\0')) throw new Error('Invalid source settings.');
        result.paths[name] = text;
      }
    } else throw new Error('Unknown settings field.');
  }
  return result;
}

function usageUrl(id) {
  if (typeof id !== 'string' || id.length > 256) throw new Error('Unknown provider.');
  if (id === 'codex' || id === 'codex-cli') return 'https://chatgpt.com/codex/settings/usage';
  if (id === 'claude' || /^claude:[a-zA-Z0-9_. @-]{1,200}$/.test(id)) return 'https://claude.ai/settings/usage';
  if (id === 'copilot') return 'https://github.com/settings/copilot/features';
  throw new Error('No usage page is available for this provider.');
}

function resourcePath(root, rawUrl) {
  let url;
  try { url = new URL(rawUrl); } catch { throw new Error('Invalid resource.'); }
  if (url.protocol !== 'costats:' || url.host !== 'app' || url.username || url.password || url.port) throw new Error('Invalid resource.');
  let relative;
  try { relative = decodeURIComponent(url.pathname); } catch { throw new Error('Invalid resource.'); }
  if (relative.includes('\\') || relative.includes('\0') || relative.split('/').includes('..')) throw new Error('Invalid resource.');
  const resolvedRoot = path.resolve(root);
  const file = path.resolve(resolvedRoot, '.' + (relative === '/' ? '/index.html' : relative));
  if (!file.startsWith(resolvedRoot + path.sep)) throw new Error('Invalid resource.');
  return file;
}

function trustedSender(event, contents) {
  if (!contents || contents.isDestroyed() || event.sender !== contents || event.senderFrame !== contents.mainFrame) return false;
  try { const url = new URL(event.senderFrame.url); return url.protocol === 'costats:' && url.host === 'app' && !url.username && !url.password; }
  catch { return false; }
}

module.exports = { settingsPatch, usageUrl, resourcePath, trustedSender };
