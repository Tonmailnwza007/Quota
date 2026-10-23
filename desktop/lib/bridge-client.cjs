'use strict';
const { spawn } = require('node:child_process');
const { EventEmitter } = require('node:events');
const { StringDecoder } = require('node:string_decoder');

class BridgeClient extends EventEmitter {
  constructor(command, args = [], options = {}) {
    super();
    this.command = command;
    this.args = args;
    this.timeout = options.timeout ?? 15000;
    this.spawn = options.spawn ?? spawn;
    this.pending = new Map();
    this.serial = 0;
    this.connected = false;
    this.closing = false;
  }

  start() {
    if (this.child) throw new Error('Account engine already started.');
    this.child = this.spawn(this.command, this.args, { stdio: ['pipe', 'pipe', 'pipe'], windowsHide: true });
    const decoder = new StringDecoder('utf8');
    let buffer = '';
    this.child.stdout.on('data', chunk => {
      buffer += decoder.write(chunk);
      if (Buffer.byteLength(buffer, 'utf8') > 1024 * 1024) return this.fail('The account engine sent an oversized response.');
      let end;
      while ((end = buffer.indexOf('\n')) >= 0) {
        const line = buffer.slice(0, end).trim();
        buffer = buffer.slice(end + 1);
        if (line) this.receive(line);
      }
    });
    // Drain diagnostics without exposing arbitrary backend text to renderer/log files.
    this.child.stderr.on('data', () => {});
    this.child.stdin.on('error', () => this.fail('The account engine connection closed.'));
    this.child.on('error', () => this.fail('Could not start the account engine.'));
    this.child.on('exit', () => this.fail(this.closing ? 'Account engine stopped.' : 'The account engine stopped. Restart costats to reconnect.'));
    return this;
  }

  receive(line) {
    if (this.failed) return;
    let message;
    try { message = JSON.parse(line); } catch { return this.fail('The account engine returned an invalid response.'); }
    if (!message || message.jsonrpc !== '2.0' || typeof message !== 'object') return this.fail('The account engine returned an invalid response.');
    if (message.method === 'ready' && message.params?.protocolVersion === 1) {
      this.connected = true;
      this.emit('ready');
      return;
    }
    if (message.method === 'stateChanged' && message.params?.protocolVersion === 1 && Array.isArray(message.params.providers)) {
      this.emit('state', message.params);
      return;
    }
    if (typeof message.id !== 'string') return;
    const pending = this.pending.get(message.id);
    if (!pending) return;
    this.pending.delete(message.id);
    clearTimeout(pending.timer);
    if (message.error) pending.reject(new Error(message.error.code === -32602 ? 'Invalid settings. Check the source fields and try again.' : 'The account engine could not complete this request.'));
    else if (Object.hasOwn(message, 'result')) pending.resolve(message.result);
    else pending.reject(new Error('The account engine returned an invalid response.'));
  }

  request(method, params) {
    if (!this.child || this.closing || this.child.killed || !this.child.stdin.writable) return Promise.reject(new Error('Account engine is disconnected.'));
    if (this.pending.size >= 64) return Promise.reject(new Error('Too many pending requests. Please wait.'));
    const id = String(++this.serial);
    const payload = JSON.stringify({ jsonrpc: '2.0', id, method, ...(params === undefined ? {} : { params }) }) + '\n';
    if (Buffer.byteLength(payload, 'utf8') > 65536) return Promise.reject(new Error('Request is too large.'));
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { this.pending.delete(id); reject(new Error('The account engine did not respond in time.')); }, this.timeout);
      this.pending.set(id, { resolve, reject, timer });
      this.child.stdin.write(payload, error => { if (error) this.fail('The account engine connection closed.'); });
    });
  }

  fail(message) {
    const wasConnected = this.connected;
    this.connected = false;
    for (const { reject, timer } of this.pending.values()) { clearTimeout(timer); reject(new Error(message)); }
    this.pending.clear();
    if (!this.closing && !this.failed) {
      this.failed = true;
      this.emit('disconnect', { connected: false, message });
      this.child?.kill();
    } else if (wasConnected) this.emit('disconnect', { connected: false, message: null });
  }

  async stop() {
    if (!this.child || this.closing) return;
    const shutdown = this.request('shutdown');
    this.closing = true;
    let deadline;
    try { await Promise.race([shutdown, new Promise(resolve => { deadline = setTimeout(resolve, 2000); })]); } catch {}
    finally { clearTimeout(deadline); }
    this.fail('Account engine stopped.');
    this.child.stdin.end();
    const child = this.child;
    if (child.exitCode === null) { const timer = setTimeout(() => child.kill(), 1000); timer.unref(); }
  }
}

module.exports = { BridgeClient };
