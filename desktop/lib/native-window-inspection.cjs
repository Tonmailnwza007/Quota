'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { execFile } = require('node:child_process');
const { promisify } = require('node:util');

async function inspectNativeFlyout(window) {
  if (process.platform !== 'win32') return null;
  const handle = window.getNativeWindowHandle();
  const hwnd = handle.length === 8 ? handle.readBigUInt64LE(0).toString() : String(handle.readUInt32LE(0));
  const script = fs.readFileSync(path.join(__dirname, 'native-window-inspection.ps1'), 'utf8');
  const powershell = path.join(process.env.SystemRoot || 'C:\\Windows', 'System32/WindowsPowerShell/v1.0/powershell.exe');
  const { stdout } = await promisify(execFile)(powershell, ['-NoProfile', '-NonInteractive', '-EncodedCommand', Buffer.from(script, 'utf16le').toString('base64')], {
    windowsHide: true, timeout: 15000, maxBuffer: 65536,
    env: { ...process.env, QUOTA_INSPECT_HWND: hwnd, QUOTA_INSPECT_PID: String(process.pid) },
  });
  return JSON.parse(stdout.replace(/^\uFEFF/, '').trim());
}

module.exports = { inspectNativeFlyout };
