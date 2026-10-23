'use strict';
const { app, BrowserWindow, Tray, Menu, globalShortcut, screen, ipcMain, protocol, net, session, dialog, shell } = require('electron');
const path = require('node:path');
const fs = require('node:fs');
const os = require('node:os');
const { pathToFileURL } = require('node:url');
const { once } = require('node:events');
const { BridgeClient } = require('./lib/bridge-client.cjs');
const { settingsPatch, usageUrl, resourcePath, trustedSender } = require('./lib/policy.cjs');
const { productName: APP_NAME, name: APP_ID } = require('./package.json');
const { carryForwardPreferences } = require('./lib/preferences.cjs');

const smoke = process.argv.includes('--smoke');
const fixture = smoke || process.argv.includes('--fixture');
const scratch = fixture ? fs.mkdtempSync(path.join(os.tmpdir(), `${APP_ID}-fixture-`)) : null;
if (scratch) app.setPath('userData', path.join(scratch, 'profile'));
else app.setPath('userData', path.join(app.getPath('appData'), APP_ID));
app.setName(APP_NAME);
if (process.platform === 'win32') app.setAppUserModelId(`fmdz.${APP_ID}`);
if (smoke) app.disableHardwareAcceleration();
protocol.registerSchemesAsPrivileged([{ scheme: 'costats', privileges: { standard: true, secure: true, supportFetchAPI: true, corsEnabled: true } }]);

let window;
let tray;
let client;
let quitting = false;
let hotkeyAvailable = false;
let connected = false;
let choosingDirectory = false;
let latestState = { protocolVersion: 1, mode: fixture ? 'demo' : 'live', providers: [], isRefreshing: true, lastRefreshTime: null, globalErrors: [] };
const smokeErrors = [];
const send = (channel, data) => { if (window && !window.isDestroyed()) window.webContents.send(`costats:${channel}`, data); };
const runtimeInfo = () => ({ version: app.getVersion(), connected, hotkeyAvailable, demo: fixture });

function bridgeExecutable() {
  return app.isPackaged
    ? path.join(process.resourcesPath, 'bridge', 'costats-bridge.exe')
    : path.resolve(__dirname, '../bridge/publish/costats-bridge.exe');
}

function startBridge() {
  const localAppData = process.env.LOCALAPPDATA || path.join(os.homedir(), 'AppData', 'Local');
  const settingsFile = fixture ? path.join(scratch, 'settings.json') : path.join(localAppData, APP_ID, 'settings.json');
  if (!fixture) carryForwardPreferences(path.join(localAppData, 'costats-ui', 'settings.json'), settingsFile);
  const args = [...(fixture ? ['--fixture'] : []), '--settings-file', path.resolve(settingsFile)];
  client = new BridgeClient(bridgeExecutable(), args);
  client.on('ready', () => { connected = true; send('connection', { connected: true, message: null }); });
  client.on('state', state => { latestState = state; send('state', state); });
  client.on('disconnect', state => { connected = false; send('connection', state); });
  client.start();
}

function positionCompact() {
  if (!window || window.isDestroyed()) return;
  const anchor = tray?.getBounds();
  const point = anchor && anchor.width ? { x: anchor.x, y: anchor.y } : screen.getCursorScreenPoint();
  const work = screen.getDisplayNearestPoint(point).workArea;
  const width = Math.min(380, work.width);
  const height = Math.min(640, work.height - 16);
  const desiredX = anchor && anchor.width ? anchor.x + anchor.width - width : work.x + work.width - width - 12;
  const desiredY = anchor && anchor.height ? anchor.y - height - 8 : work.y + work.height - height - 12;
  window.setBounds({ x: Math.max(work.x, Math.min(desiredX, work.x + work.width - width)),
    y: Math.max(work.y, Math.min(desiredY, work.y + work.height - height)), width, height });
}

function showWindow() {
  if (!window || window.isDestroyed()) return;
  positionCompact();
  window.show();
  window.focus();
  if (connected) void client.request('refresh').catch(() => {});
}

function registerIpc() {
  const handle = (name, action) => ipcMain.handle(`costats:${name}`, (event, input) => {
    if (!trustedSender(event, window?.webContents)) throw new Error('Untrusted application frame.');
    return action(input);
  });
  handle('getState', () => client.request('getState'));
  handle('getSettings', () => client.request('getSettings'));
  handle('refresh', () => client.request('refresh'));
  handle('updateSettings', patch => client.request('updateSettings', settingsPatch(patch)));
  handle('getRuntimeInfo', runtimeInfo);
  handle('hide', () => window.hide());
  // Low-quota notification delivery belongs to the default native host.
  handle('testNotification', () => ({ accepted: false }));
  handle('openUsage', id => { const url = usageUrl(id); return smoke ? undefined : shell.openExternal(url); });
  handle('chooseDirectory', async () => {
    if (fixture) return null;
    choosingDirectory = true;
    try {
      const selection = await dialog.showOpenDialog(window, { title: 'Select install directory', properties: ['openDirectory'] });
      return selection.canceled ? null : selection.filePaths[0] ?? null;
    } finally { choosingDirectory = false; if (!window.isDestroyed()) { window.show(); window.focus(); } }
  });
}

async function createWindow() {
  window = new BrowserWindow({ width: 380, height: 640,
    title: APP_NAME, show: false, frame: false, type: 'toolbar', thickFrame: false,
    backgroundColor: '#0a0a0b', autoHideMenuBar: true,
    skipTaskbar: true, resizable: false, minimizable: false, maximizable: false,
    fullscreenable: false, movable: false, alwaysOnTop: true,
    icon: path.join(__dirname, 'assets', 'app.ico'),
    webPreferences: { preload: path.join(__dirname, 'preload.cjs'), sandbox: true, contextIsolation: true,
      nodeIntegration: false, webSecurity: true, webviewTag: false, ...(smoke ? { offscreen: true, backgroundThrottling: false } : {}) } });
  window.setMenuBarVisibility(false);
  window.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  window.webContents.on('will-navigate', (event, url) => { if (!url.startsWith('costats://app/')) event.preventDefault(); });
  window.webContents.on('will-attach-webview', event => event.preventDefault());
  window.webContents.on('render-process-gone', () => { if (!smoke) window.reload(); });
  window.webContents.on('console-message', (_event, details) => {
    if (details?.level === 'error' && smoke) smokeErrors.push(details.message);
  });
  window.on('close', event => { if (!quitting && !smoke) { event.preventDefault(); window.hide(); } });
  window.on('blur', () => { if (!smoke && !quitting && !choosingDirectory) window.hide(); });
  if (!smoke) hotkeyAvailable = globalShortcut.register('CommandOrControl+Alt+U', () => window.isVisible() ? window.hide() : showWindow());
  registerIpc();
  await window.loadURL('costats://app/index.html');
}

async function runSmoke() {
  const trayOnly = !window.isVisible() && !window.isResizable() && !window.isMaximizable() && !window.isMinimizable() && !window.isFullScreenable() && window.isAlwaysOnTop();
  if (!trayOnly) throw new Error('Popover must start hidden with fixed dimensions and no full-window controls.');
  const nativeWindow = await require('./lib/native-window-inspection.cjs').inspectNativeFlyout(window);
  if (nativeWindow && (!nativeWindow.toolWindow || nativeWindow.appWindow || nativeWindow.caption || nativeWindow.thickFrame)) throw new Error('Native window must be a captionless tool flyout, not a regular application window.');
  const ready = connected ? Promise.resolve() : once(client, 'ready');
  await Promise.race([ready, new Promise((_, reject) => setTimeout(() => reject(new Error('Account engine startup timed out.')), 20000))]);
  const result = await window.webContents.executeJavaScript(`(async () => {
    const api = window.costats;
    if (!api || typeof api.getSettings !== 'function') throw new Error('Preload bridge unavailable.');
    if ('setWindowMode' in api || 'onWindowMode' in api) throw new Error('Window size APIs must not be exposed.');
    const waitFor = async (predicate, description) => {
      const end = Date.now() + 10000;
      while (!await predicate()) {
        if (Date.now() > end) throw new Error('Timed out: ' + description);
        await new Promise(resolve => setTimeout(resolve, 30));
      }
    };
    const defaults = await api.getSettings();
    await api.updateSettings({enabled:Object.fromEntries(Object.keys(defaults.enabled).map(id => [id,true]))});
    const settings = await api.getSettings();
    await waitFor(async () => !(await api.getState()).isRefreshing, 'initial fixture scan');
    const refresh = await api.refresh();
    await waitFor(async () => !(await api.getState()).isRefreshing, 'refresh completion');
    const state = await api.getState();
    let rejected = false;
    try { await api.updateSettings({ enabled: { arbitrary: true } }); } catch { rejected = true; }
    if (!rejected) throw new Error('Unknown provider was accepted.');
    const disabled = await api.updateSettings({ enabled: Object.fromEntries(Object.keys(settings.enabled).map(k => [k, false])) });
    const empty = await api.getState();
    if (empty.providers.length !== 0) throw new Error('All-disabled state did not clear.');
    await waitFor(() => document.querySelectorAll('article').length === 0, 'empty renderer');
    await api.updateSettings({ enabled: settings.enabled });
    await waitFor(() => document.querySelectorAll('article').length === 6 &&
      document.querySelector('article')?.innerText.includes('alpha@example.test') &&
      document.querySelectorAll('article')[1]?.innerText.includes('beta@example.test'), 'restored account readings');
    const cards = [...document.querySelectorAll('article')];
    const first = cards[0].innerText; const second = cards[1].innerText;
    if (!first.includes('alpha@example.test') || !second.includes('beta@example.test') ||
        !first.includes('66%') || !second.includes('100%') || !second.includes('--')) throw new Error('Independent account identity/quota mapping failed.');
    const bothAccountsFit = cards[1].getBoundingClientRect().bottom < innerHeight;
    if (!bothAccountsFit) throw new Error('Both Plus accounts must fit in the initial viewport.');
    const settingsButton = [...document.querySelectorAll('button')].find(button => /settings/i.test(button.getAttribute('aria-label') || ''));
    if (!settingsButton) throw new Error('Settings control missing.');
    settingsButton.click();
    await waitFor(() => document.querySelector('[role="dialog"]'), 'settings dialog');
    const toggles = [...document.querySelectorAll('[role="switch"]')];
    if (toggles.length !== 5) throw new Error('Expected five provider controls.');
    const cliToggle = document.querySelector('[aria-labelledby="label-codex-cli"]');
    cliToggle.click();
    await waitFor(async () => !(await api.getSettings()).enabled['codex-cli'], 'switch persistence');
    await waitFor(() => !document.querySelector('[aria-labelledby="provider-codex-cli-title"]'), 'immediate switch filtering');
    cliToggle.click();
    await waitFor(async () => (await api.getSettings()).enabled['codex-cli'], 'switch restoration');
    await waitFor(() => document.querySelector('[role="status"]')?.textContent === 'Saved', 'switch disk persistence');
    document.querySelector('[role="dialog"] button[aria-label^="Close"]').click();
    await waitFor(() => document.querySelectorAll('article').length === 6, 'final cards');
    const brandImages = [...document.querySelectorAll('article img')];
    await waitFor(() => brandImages.length >= 6 && brandImages.every(img => img.complete && img.naturalWidth > 0), 'bundled provider logos');
    const sizeControlsRemoved = ![...document.querySelectorAll('button')].some(button => /wide dashboard|compact tray|switch.*size|960px/i.test(button.getAttribute('aria-label') || button.textContent));
    if (!sizeControlsRemoved) throw new Error('Size controls must be removed.');
    return { title: document.title, meaningfulContent: document.body.innerText.length > 30,
      contextIsolation: typeof require === 'undefined' && typeof process === 'undefined',
      switches: Object.keys(settings.enabled).length, refreshAccepted: typeof refresh.accepted === 'boolean',
      mode: state.mode, rejectedUnknownProvider: rejected, allDisabled: Object.values(disabled.enabled).every(v => !v),
      distinctAccountIdentities: true, bothAccountsFit, switchPersistsAndFilters: true, bundledLogos: true, sizeControlsRemoved };
  })()`);
  if (!result.meaningfulContent || !result.contextIsolation || result.switches !== 6 || result.mode !== 'demo' || smokeErrors.length) throw new Error('Desktop smoke verification failed.');
  const metrics = app.getAppMetrics().map(item => ({ type: item.type, workingSetSizeKb: item.memory.workingSetSize }));
  const persisted = JSON.parse(fs.readFileSync(path.join(scratch, 'settings.json'), 'utf8'));
  if (!persisted.enabled['codex-cli']) throw new Error('Settings were not persisted to the isolated profile.');
  const output = process.env.COSTATS_SMOKE_OUTPUT;
  if (output) {
    fs.mkdirSync(output, { recursive: true });
    fs.writeFileSync(path.join(output, 'desktop-smoke.json'), JSON.stringify({ ...result, trayOnly, nativeWindow, consoleErrors: smokeErrors, processes: metrics }, null, 2));
    // Read the offscreen compositor frame without making the native window visible.
    const image = await new Promise((resolve, reject) => {
      const timeout = setTimeout(() => { window.webContents.removeListener('paint', painted); reject(new Error('Offscreen frame timed out.')); }, 5000);
      function painted(_event, _dirty, frame) {
        if (frame.isEmpty()) return;
        clearTimeout(timeout); window.webContents.removeListener('paint', painted); resolve(frame);
      }
      window.webContents.on('paint', painted);
      window.webContents.startPainting();
      window.webContents.invalidate();
    });
    fs.writeFileSync(path.join(output, 'desktop-smoke.png'), image.toPNG());
  }
  process.stdout.write(JSON.stringify({ ...result, trayOnly, nativeWindow, consoleErrors: smokeErrors, processes: metrics }) + '\n');
  app.quit();
}

const primary = smoke || app.requestSingleInstanceLock();
if (!primary) app.quit();
else {
  app.on('second-instance', showWindow);
  app.whenReady().then(async () => {
    session.defaultSession.setPermissionRequestHandler((_contents, _permission, callback) => callback(false));
    session.defaultSession.setPermissionCheckHandler(() => false);
    protocol.handle('costats', async request => {
      try {
        if (request.method !== 'GET') return new Response('Method not allowed', { status: 405 });
        const file = resourcePath(path.join(__dirname, 'renderer'), request.url);
        const response = await net.fetch(pathToFileURL(file).href);
        const headers = new Headers(response.headers);
        headers.set('Content-Security-Policy', "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self' data:; connect-src 'none'; object-src 'none'; base-uri 'none'; frame-src 'none'; form-action 'none'");
        return new Response(response.body, { status: response.status, headers });
      } catch { return new Response('Resource not found', { status: 404 }); }
    });
    startBridge();
    await createWindow();
    if (smoke) return runSmoke();
    tray = new Tray(path.join(__dirname, 'assets', 'app.ico'));
    tray.setToolTip(APP_NAME);
    tray.setContextMenu(Menu.buildFromTemplate([
      { label: `Show ${APP_NAME}`, click: showWindow },
      { label: 'Refresh', click: () => { void client.request('refresh').catch(() => {}); } },
      { type: 'separator' }, { label: 'Quit', click: () => app.quit() },
    ]));
    tray.on('click', () => window.isVisible() ? window.hide() : showWindow());
    screen.on('display-metrics-changed', positionCompact);
    // A tray utility starts quietly. The popover is shown only by a tray click or hotkey.
  }).catch(error => { process.stderr.write(smoke ? `Desktop fixture verification failed: ${error.message}\n` : `${APP_NAME} could not start.\n`); process.exitCode = 1; app.quit(); });
}

app.on('window-all-closed', () => { if (smoke) app.quit(); });
app.on('before-quit', event => {
  if (quitting) return;
  event.preventDefault();
  quitting = true;
  globalShortcut.unregisterAll();
  tray?.destroy();
  void Promise.resolve(client?.stop()).finally(() => app.exit(process.exitCode || 0));
});
