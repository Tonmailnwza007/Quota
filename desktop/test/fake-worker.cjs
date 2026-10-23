'use strict';
const readline = require('node:readline');
const send = value => process.stdout.write(JSON.stringify(value) + '\n');
send({ jsonrpc: '2.0', method: 'ready', params: { protocolVersion: 1 } });
readline.createInterface({ input: process.stdin }).on('line', line => {
  const request = JSON.parse(line);
  const response = result => send({ jsonrpc: '2.0', id: request.id, result });
  switch (request.method) {
    case 'echo': response(request.params); break;
    case 'delayed': setTimeout(() => response(true), 400); break;
    case 'invalid': process.stdout.write('not-json\n'); break;
    case 'secretError': send({ jsonrpc: '2.0', id: request.id, error: { code: -32602, message: 'SECRET_TOKEN_SENTINEL' } }); break;
    case 'unicode': {
      const bytes = Buffer.from(JSON.stringify({ jsonrpc: '2.0', id: request.id, result: 'ผู้ใช้' }) + '\n');
      process.stdout.write(bytes.subarray(0, bytes.length - 4));
      setTimeout(() => process.stdout.write(bytes.subarray(bytes.length - 4)), 10);
      break;
    }
    case 'shutdown': response(true); process.exit(0); break;
  }
});
