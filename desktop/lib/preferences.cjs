'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { settingsPatch } = require('./policy.cjs');

function carryForwardPreferences(previousFile, nextFile) {
  if (fs.existsSync(nextFile) || !fs.existsSync(previousFile)) return false;
  try {
    const previous = fs.readFileSync(previousFile, 'utf8');
    if (Buffer.byteLength(previous, 'utf8') > 65536) return false;
    const preferences = settingsPatch(JSON.parse(previous));
    fs.mkdirSync(path.dirname(nextFile), { recursive: true });
    fs.writeFileSync(nextFile, JSON.stringify(preferences, null, 2), { encoding: 'utf8', flag: 'wx' });
    return true;
  } catch { return false; }
}

module.exports = { carryForwardPreferences };
