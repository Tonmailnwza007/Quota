'use strict';
const { contextBridge, ipcRenderer } = require('electron');

const invoke = (name, payload) => ipcRenderer.invoke(`costats:${name}`, payload);
const subscribe = (name, listener) => {
  if (typeof listener !== 'function') throw new TypeError('Listener must be a function.');
  const handler = (_event, data) => listener(data);
  ipcRenderer.on(`costats:${name}`, handler);
  return () => ipcRenderer.removeListener(`costats:${name}`, handler);
};

contextBridge.exposeInMainWorld('costats', Object.freeze({
  getState: () => invoke('getState'),
  refresh: () => invoke('refresh'),
  getSettings: () => invoke('getSettings'),
  updateSettings: patch => invoke('updateSettings', patch),
  onState: listener => subscribe('state', listener),
  onConnection: listener => subscribe('connection', listener),
  getRuntimeInfo: () => invoke('getRuntimeInfo'),
  hide: () => invoke('hide'),
  openUsage: id => invoke('openUsage', id),
  chooseDirectory: () => invoke('chooseDirectory'),
  testNotification: () => invoke('testNotification'),
}));
