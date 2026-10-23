(() => {
  'use strict';
  if (window !== window.top || location.origin !== 'https://quota.local') return;
  const transport = window.chrome?.webview;
  if (!transport) return;
  const pending = new Map();
  const events = new Map([['state', new Set()], ['connection', new Set()]]);
  let serial = 0;
  const invoke = (method, params) => {
    if (pending.size >= 64) return Promise.reject(new Error('Too many requests. Please wait.'));
    const id = String(++serial);
    const message = { id, method, ...(params === undefined ? {} : { params }) };
    if (new TextEncoder().encode(JSON.stringify(message)).length > 65536) return Promise.reject(new Error('Request is too large.'));
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => { pending.delete(id); reject(new Error('The account engine did not respond in time.')); }, 15000);
      pending.set(id, { resolve, reject, timer });
      try { transport.postMessage(message); }
      catch { pending.delete(id); clearTimeout(timer); reject(new Error('The desktop connection is unavailable.')); }
    });
  };
  transport.addEventListener('message', event => {
    const message = event.data;
    if (!message || typeof message !== 'object') return;
    if (typeof message.event === 'string' && events.has(message.event)) {
      for (const listener of events.get(message.event)) listener(message.data);
      return;
    }
    if (typeof message.id !== 'string' || !pending.has(message.id)) return;
    const request = pending.get(message.id);
    pending.delete(message.id); clearTimeout(request.timer);
    if (message.error) request.reject(new Error('The desktop could not complete this request. Check your preferences and try again.'));
    else request.resolve(message.result);
  });
  const subscribe = (event, listener) => {
    if (typeof listener !== 'function') throw new TypeError('Listener must be a function.');
    events.get(event).add(listener);
    return () => events.get(event).delete(listener);
  };
  Object.defineProperty(window, 'costats', {
    configurable: false, writable: false,
    value: Object.freeze({
      getState: () => invoke('getState'),
      getSettings: () => invoke('getSettings'),
      updateSettings: patch => invoke('updateSettings', patch),
      refresh: () => invoke('refresh'),
      onState: listener => subscribe('state', listener),
      onConnection: listener => subscribe('connection', listener),
      getRuntimeInfo: () => invoke('getRuntimeInfo'),
      setStartWithWindows: enabled => typeof enabled === 'boolean'
        ? invoke('setStartWithWindows', { enabled })
        : Promise.reject(new TypeError('Startup preference must be a boolean.')),
      hide: () => invoke('hide'),
      chooseDirectory: () => invoke('chooseDirectory'),
      testNotification: () => invoke('testNotification'),
      openUsage: providerId => invoke('openUsage', { providerId }),
    }),
  });
  // Content fitting is host-owned. No window sizing control is exposed to React.
  // Normal tray panel height is fixed at 520 DIPs across collapsing, unfolding, provider switches, and settings.
  if (typeof document !== 'undefined') {
    let requested = false;
    const requestFixedLayout = () => {
      if (requested) return;
      requested = true;
      void invoke('__layout', { height: 520 }).catch(() => {});
    };
    if (document.readyState === 'loading') {
      document.addEventListener('DOMContentLoaded', requestFixedLayout, { once: true });
    } else {
      requestFixedLayout();
    }
  }
})();
