(async () => {
  'use strict';
  const report = { pass: false };
  const waitFor = async (predicate, description) => {
    const end = Date.now() + 15000;
    while (!await predicate()) {
      if (Date.now() > end) throw new Error('Timed out: ' + description);
      await new Promise(resolve => setTimeout(resolve, 40));
    }
  };
  try {
    // A hidden WebView can retain its previous JS viewport until it paints.
    // Inspect the actual native HWND instead when verifying host dimensions.
    let inspectSerial = 0;
    const nativeRequest = (method, params) => new Promise((resolve, reject) => {
      const id = `native-inspect-${++inspectSerial}`;
      const listener = event => {
        if (event.data?.id !== id) return;
        window.chrome.webview.removeEventListener('message', listener);
        clearTimeout(timeout);
        event.data.error ? reject(new Error('Native inspection failed.')) : resolve(event.data.result);
      };
      const timeout = setTimeout(() => { window.chrome.webview.removeEventListener('message', listener); reject(new Error('Native inspection timed out.')); }, 5000);
      window.chrome.webview.addEventListener('message', listener);
      window.chrome.webview.postMessage({id,method,...(params === undefined ? {} : {params})});
    });
    const inspectWindow = () => nativeRequest('__smokeWindow');
    const nativeHeight = async () => { const native = await inspectWindow(); return native.height * 96 / native.deviceDpi; };
    await waitFor(() => window.costats, 'native preload');
    const api = window.costats;
    if ((await api.getRuntimeInfo()).startWithWindows !== false) throw new Error('Synthetic startup must default off.');
    for (const params of [{enabled:'true'}, {enabled:true,path:'C:\\other.exe'}]) {
      let rejected = false;
      try { await nativeRequest('setStartWithWindows', params); } catch { rejected = true; }
      if (!rejected) throw new Error('Invalid native startup parameters accepted.');
    }
    const defaults = await api.getSettings();
    await waitFor(async () => !(await api.getState()).isRefreshing, 'synthetic account scan');
    let state = await api.getState();
    if (state.mode !== 'demo') throw new Error('Smoke requires synthetic source mode.');
    if (Object.keys(defaults.enabled).length !== 5 || defaults.enabled.claude || defaults.enabled.copilot) throw new Error('Expected five switches with optional providers initially off.');
    if (state.providers.length !== 3 || state.providers.filter(p => p.family === 'antigravity').length !== 1 || state.providers.some(p => p.id === 'antigravity-cli' || p.family === 'antigravity' && p.isSharedAccountQuota)) throw new Error('Expected two Codex accounts and one unlinked Antigravity account.');
    report.unifiedAntigravity = true;
    report.defaultThreeProviders = true;
    // Enable optional sources through the real UI so its preferences and backend state stay in sync.
    await waitFor(() => document.querySelectorAll('article').length === 3, 'default rendered accounts');
    const initialSettingsButton = [...document.querySelectorAll('button')].find(button => /settings/i.test(button.getAttribute('aria-label') || ''));
    initialSettingsButton.click();
    await waitFor(() => document.querySelector('[role="dialog"]'), 'initial provider settings');
    const startupSwitch = () => [...document.querySelectorAll('[role="switch"]')].find(button =>
      document.getElementById(button.getAttribute('aria-labelledby'))?.textContent === 'Start with Windows');
    await waitFor(() => startupSwitch() && !startupSwitch().disabled, 'startup preference ready');
    startupSwitch().click();
    await waitFor(async () => (await api.getRuntimeInfo()).startWithWindows === true && startupSwitch().getAttribute('aria-checked') === 'true' && !startupSwitch().disabled, 'startup enable');
    startupSwitch().click();
    await waitFor(async () => (await api.getRuntimeInfo()).startWithWindows === false && startupSwitch().getAttribute('aria-checked') === 'false', 'startup disable');
    report.startupToggleSimulatedWithoutRegistration = true;
    for (const id of ['claude','copilot']) {
      const selector = `[role="switch"][aria-labelledby="label-${id}"]`;
      await waitFor(() => document.querySelector(selector) && !document.querySelector(selector).disabled, 'optional source control ready');
      document.querySelector(selector).click();
      await waitFor(async () => (await api.getSettings()).enabled[id], 'optional source enabled');
    }
    document.querySelector('[role="dialog"] button[aria-label^="Close"]').click();
    const settings = await api.getSettings();
    await waitFor(async () => (await api.getState()).providers.length === 6 && !(await api.getState()).isRefreshing, 'all optional synthetic sources');
    state = await api.getState();
    await waitFor(() => document.querySelectorAll('article').length === 6 && document.querySelector('article')?.innerText.includes('alpha@example.test'), 'rendered accounts');
    const cards = [...document.querySelectorAll('article')];
    const first = cards[0].innerText; const second = cards[1].innerText;
    if (!first.includes('alpha@example.test') || !second.includes('beta@example.test') || !first.includes('66%') || !second.includes('100%') || !second.includes('--')) throw new Error('Distinct identity or remaining quota mapping failed.');
    report.title = document.title;
    report.viewport = { width: innerWidth, height: innerHeight };
    if (innerWidth < 350 || innerWidth > 365 || innerHeight > 522) throw new Error('Popover must remain compact across display scaling.');
    report.bothPlusAccountsFit = cards[1].getBoundingClientRect().bottom <= innerHeight;
    if (!report.bothPlusAccountsFit) throw new Error('Plus cards clipped.');
    const unavailableCard = document.querySelector('article[data-provider-id="copilot"]');
    await waitFor(() => unavailableCard.querySelector('[aria-label="Expand GitHub Copilot"]'), 'unavailable provider collapsed by default');
    if (unavailableCard.querySelector('#provider-copilot-body')) throw new Error('Unavailable provider body starts open.');
    unavailableCard.querySelector('[aria-label="Expand GitHub Copilot"]').click();
    await waitFor(() => unavailableCard.querySelector('#provider-copilot-body'), 'manual unavailable provider expansion');
    await api.refresh();
    await waitFor(async () => !(await api.getState()).isRefreshing, 'refresh after manual expansion');
    if (!unavailableCard.querySelector('#provider-copilot-body')) throw new Error('Refresh closed a manually expanded unavailable provider.');
    unavailableCard.querySelector('[aria-label="Collapse GitHub Copilot"]').click();
    report.unavailableProvidersCollapsedByDefault = true;
    report.unavailableProvidersManuallyExpandable = true;
    report.distinctCodexIdentities = state.providers[0].accountId !== state.providers[1].accountId;
    report.noNodeGlobals = typeof require === 'undefined' && typeof process === 'undefined';
    report.noSizeApi = !('setWindowMode' in api) && !('onWindowMode' in api);
    if (!report.distinctCodexIdentities || !report.noNodeGlobals || !report.noSizeApi) throw new Error('Preload boundary failed.');
    let invalidRejected = false;
    try { await api.updateSettings({ enabled: { arbitrary: true } }); } catch { invalidRejected = true; }
    if (!invalidRejected) throw new Error('Unknown settings key accepted.');
    let arbitraryUrlRejected = false;
    try { await api.openUsage('https://invalid.example'); } catch { arbitraryUrlRejected = true; }
    if (!arbitraryUrlRejected) throw new Error('Arbitrary URL accepted.');
    const pairOnly = Object.fromEntries(Object.keys(settings.enabled).map(id => [id, id === 'codex' || id === 'codex-cli']));
    await api.updateSettings({enabled: pairOnly});
    await waitFor(async () => document.querySelectorAll('article').length === 2 && await nativeHeight() >= 519, 'fixed-height account pair');
    report.twoAccountHeight = await nativeHeight();
    report.fixedPanelHeight = true;
    const appCard = document.querySelector('article[data-provider-id="codex"]');
    if (appCard.querySelector('[aria-label^="Usage details"]')) throw new Error('Usage metadata must be optional and hidden by default.');
    appCard.querySelector('[aria-label="Collapse Codex App"]').click();
    await waitFor(() => appCard.querySelector('[aria-label="Expand Codex App"]'), 'collapsing account body');
    await new Promise(resolve => setTimeout(resolve, 80));
    report.collapsedAccountHeight = await nativeHeight();
    if (Math.abs(report.collapsedAccountHeight - report.twoAccountHeight) > 1) throw new Error('Folding resized the native window.');
    if (appCard.innerText.includes('Session limit') || !appCard.innerText.includes('alpha@example.test')) throw new Error('Collapse must leave account header only.');
    await api.refresh();
    await waitFor(async () => !(await api.getState()).isRefreshing, 'refresh while collapsed');
    if (!appCard.querySelector('[aria-label="Expand Codex App"]')) throw new Error('Refresh reset collapse state.');
    appCard.querySelector('[aria-label="Expand Codex App"]').click();
    if (Math.abs(await nativeHeight() - report.twoAccountHeight) > 1) throw new Error('Unfolding resized the native window.');
    report.wholeAccountCollapse = true;
    let invalidAlertsRejected = false;
    try { await api.updateSettings({notifications:{warningPercent:5,criticalPercent:5}}); } catch { invalidAlertsRejected = true; }
    if (!invalidAlertsRejected) throw new Error('Invalid thresholds accepted.');
    if (!(await api.testNotification()).accepted) throw new Error('Fixed notification test method unavailable.');
    report.notificationTestSimulatedWithoutBanner = true;
    const disabled = Object.fromEntries(Object.keys(settings.enabled).map(id => [id, false]));
    await api.updateSettings({enabled: disabled});
    if ((await api.getState()).providers.length !== 0) throw new Error('All-disabled state retained accounts.');
    await waitFor(() => document.querySelectorAll('article').length === 0, 'empty display');
    if (Math.abs(await nativeHeight() - report.twoAccountHeight) > 1) throw new Error('Empty view resized the native window.');
    report.emptyHeight = await nativeHeight();
    await api.updateSettings({enabled: settings.enabled});
    await waitFor(() => document.querySelectorAll('article').length === 6, 'restored display');
    const settingsButton = [...document.querySelectorAll('button')].find(button => /settings/i.test(button.getAttribute('aria-label') || ''));
    if (!settingsButton) throw new Error('Settings control missing.');
    settingsButton.click();
    await waitFor(() => document.querySelector('[role="dialog"]'), 'settings dialog');
    await waitFor(async () => await nativeHeight() >= 519, 'settings expands the native flyout');
    report.settingsHeight = await nativeHeight();
    const switches = ['codex','codex-cli','antigravity','claude','copilot'].map(id => document.querySelector(`[role="switch"][aria-labelledby="label-${id}"]`));
    if (switches.some(item => !item) || document.querySelector('[aria-labelledby="label-antigravity-cli"]')) throw new Error('Five unified provider switches required.');
    const setInput = (id, value) => {
      const input = document.getElementById(id);
      if (!input) throw new Error('Alert threshold field missing.');
      Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set.call(input, value);
      input.dispatchEvent(new Event('input',{bubbles:true}));
    };
    setInput('warning-threshold-input','5'); setInput('critical-threshold-input','5');
    await new Promise(resolve => setTimeout(resolve, 50));
    const saveAlerts = [...document.querySelectorAll('button')].find(button => button.textContent.trim() === 'Save alerts');
    saveAlerts.click();
    await waitFor(() => [...document.querySelectorAll('[role="alert"]')].some(element => element.textContent.includes('Critical % remaining must be less')), 'accessible alert validation feedback');
    setInput('warning-threshold-input','25'); setInput('critical-threshold-input','7');
    await new Promise(resolve => setTimeout(resolve, 50));
    saveAlerts.click();
    await waitFor(async () => (await api.getSettings()).notifications.warningPercent === 25 && (await api.getSettings()).notifications.criticalPercent === 7, 'editable alert thresholds persisted');
    await waitFor(() => [...document.querySelectorAll('[role="status"]')].some(element => element.textContent === 'Alert thresholds saved'), 'accessible alert success feedback');
    report.accessibleNotificationFeedback = true;
    report.editableNotificationThresholds = true;
    const cli = document.querySelector('[aria-labelledby="label-codex-cli"]');
    cli.click();
    await waitFor(async () => !(await api.getSettings()).enabled['codex-cli'], 'native switch update');
    await waitFor(() => !document.querySelector('[aria-labelledby="provider-codex-cli-title"]'), 'native display filtering');
    cli.click();
    await waitFor(async () => (await api.getSettings()).enabled['codex-cli'], 'native switch restoration');
    await waitFor(() => [...document.querySelectorAll('[role="status"]')].some(element => element.textContent === 'Saved'), 'native switch persistence');
    document.querySelector('[role="dialog"] button[aria-label^="Close"]').click();
    await waitFor(() => document.querySelectorAll('article').length === 6, 'final display');
    const logos = [...document.querySelectorAll('article img')];
    await waitFor(() => logos.length >= 6 && logos.every(img => img.complete && img.naturalWidth > 0), 'bundled logos');
    await api.updateSettings({enabled: {...pairOnly, antigravity: true}});
    await waitFor(() => document.querySelectorAll('article').length === 3, 'requested three providers');
    await new Promise(resolve => setTimeout(resolve, 200));
    report.requestedViewHeight = await nativeHeight();
    report.remainingQuotaPresentation = true;
    report.switches = 5;
    report.settingsFilterAndPersist = true;
    report.allDisabledClears = true;
    report.bundledLogos = true;
    report.validationRejectsUnknownAndArbitraryUrls = true;
    report.pass = true;
  } catch (error) { report.error = error.message || 'Native integration failed.'; }
  window.chrome.webview.postMessage({id:'smoke-result',method:'__smokeResult',params:report});
})();
