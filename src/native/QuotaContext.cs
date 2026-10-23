using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Costats.Bridge;
using Costats.Bridge.Notifications;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Quota.Native;

internal sealed class QuotaContext : ApplicationContext
{
    private readonly NativeOptions _options;
    private readonly NativeWindow _window;
    private readonly WebView2 _web;
    private readonly System.Windows.Forms.Timer _startTimer;
    private readonly System.Windows.Forms.Timer _timeoutTimer;
    private NotifyIcon? _tray;
    private Icon? _icon;
    private BridgeRuntime? _runtime;
    private bool _hotkeyAvailable;
    private bool _initialized;
    private bool _showWhenReady;
    private bool _fixtureStartWithWindows;
    private bool _exiting;
    private bool _smokeRecorded;
    private int _activeRequests;
    private Point? _lastTrayAnchor;
    private readonly HashSet<string> _pendingIds = new(StringComparer.Ordinal);
    private readonly string _rendererRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "renderer"));
    private readonly LowQuotaMonitor _quotaAlerts;
    private readonly CancellationTokenSource _alertShutdown = new();
    private readonly object _alertLock = new();
    private Task _alertTail = Task.CompletedTask;
    private bool _alertStorageErrorReported;
    public int ExitCode { get; private set; }

    public QuotaContext(NativeOptions options)
    {
        _options = options;
        _quotaAlerts = new LowQuotaMonitor(Path.Combine(Path.GetDirectoryName(options.SettingsFile)!, "notification-history.json"));
        _window = new NativeWindow { SmokeMode = options.Smoke };
        // Position and size in physical monitor pixels before creating the hidden HWND.
        _window.PositionAt(Cursor.Position);
        _ = _window.Handle;
        _web = new WebView2 { Dock = DockStyle.Fill, DefaultBackgroundColor = Color.FromArgb(32, 32, 32) };
        _window.Controls.Add(_web);
        _window.HotkeyPressed += Toggle;
        _window.FormClosing += (_, e) => { if (!_exiting) { e.Cancel = true; _window.Hide(); } };
        _startTimer = new() { Interval = 1 };
        _startTimer.Tick += async (_, _) => { _startTimer.Stop(); await InitializeAsync(); };
        _startTimer.Start();
        _timeoutTimer = new() { Interval = 60000 };
        _timeoutTimer.Tick += async (_, _) =>
        {
            _timeoutTimer.Stop();
            if (_options.Smoke) { await RecordSmokeAsync(JsonDocument.Parse("{\"pass\":false,\"reason\":\"Native smoke timed out.\"}").RootElement.Clone()); }
        };
        if (options.Smoke) _timeoutTimer.Start();
        else CreateTray();
    }

    private void CreateTray()
    {
        var iconPath = Path.Combine(AppContext.BaseDirectory, "app.ico");
        _icon = File.Exists(iconPath) ? new Icon(iconPath) : (Icon)SystemIcons.Application.Clone();
        _window.Icon = _icon;
        var menu = new ContextMenuStrip();
        menu.Items.Add("Show Quota", null, (_, _) => Toggle());
        menu.Items.Add("Refresh usage", null, (_, _) => _runtime?.RequestRefresh());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, async (_, _) => await ExitAsync());
        _tray = new NotifyIcon { Text = "Quota", Icon = _icon, ContextMenuStrip = menu, Visible = true };
        _tray.MouseUp += (_, e) => { _lastTrayAnchor = Cursor.Position; if (e.Button == MouseButtons.Left) Toggle(); };
        _tray.BalloonTipClicked += (_, _) => { if (!_window.Visible) Toggle(); else { _window.Activate(); _web.Focus(); } };
        _hotkeyAvailable = NativeWindow.RegisterHotKey(_window.Handle, 0x5155, 0x0001 | 0x0002 | 0x4000, (uint)Keys.U);
    }

    private void Toggle()
    {
        if (_options.Smoke || _exiting) return;
        if (!_initialized)
        {
            // A normal early click is an open request, not an application error.
            _showWhenReady = !_showWhenReady;
            return;
        }
        var screen = Screen.FromPoint(Cursor.Position).WorkingArea;
        var anchor = _lastTrayAnchor is { } saved && Screen.AllScreens.Any(display => display.Bounds.Contains(saved))
            ? saved : new Point(screen.Right - 20, screen.Bottom);
        _window.ToggleAt(anchor);
        if (_window.Visible) { _web.Focus(); _runtime?.RequestRefresh(); }
    }

    private async Task InitializeAsync()
    {
        try
        {
            if (!File.Exists(Path.Combine(_rendererRoot, "index.html")) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "preload.js")))
                throw new InvalidOperationException("Local renderer assets are missing.");
            // Smoke never creates real sources, profiles, or settings stores.
            var store = new ModernSettingsStore(_options.SettingsFile);
            var settings = await store.LoadAsync();
            var sources = _options.Fixture ? SourceFactory.CreateFixtures() : SourceFactory.CreateLive(settings, Path.GetDirectoryName(_options.SettingsFile));
            _runtime = new BridgeRuntime(settings, store, _options.Fixture, sources);
            _runtime.StateChanged += OnStateChanged;
            Directory.CreateDirectory(_options.ProfileDirectory);
            var environmentOptions = new CoreWebView2EnvironmentOptions("--disable-background-networking --disable-component-update --disable-sync");
            var environment = await CoreWebView2Environment.CreateAsync(null, _options.ProfileDirectory, environmentOptions);
            await _web.EnsureCoreWebView2Async(environment);
            var core = _web.CoreWebView2;
            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.AreDefaultScriptDialogsEnabled = false;
            core.Settings.IsPasswordAutosaveEnabled = false;
            core.Settings.IsGeneralAutofillEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.AreHostObjectsAllowed = false;
            core.Settings.IsWebMessageEnabled = true;
            core.SetVirtualHostNameToFolderMapping("quota.local", _rendererRoot, CoreWebView2HostResourceAccessKind.DenyCors);
            core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
            core.WebResourceRequested += OnResourceRequested;
            core.NavigationStarting += (_, e) =>
            {
                if (!IsMainDocument(e.Uri)) e.Cancel = true;
            };
            core.NewWindowRequested += (_, e) => e.Handled = true;
            core.PermissionRequested += (_, e) => { e.State = CoreWebView2PermissionState.Deny; e.Handled = true; };
            core.DownloadStarting += (_, e) => e.Cancel = true;
            core.WebMessageReceived += OnWebMessage;
            core.NavigationCompleted += OnNavigationCompleted;
            await core.AddScriptToExecuteOnDocumentCreatedAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "preload.js")));
            // React handles Escape in its modal before calling hide through the narrow API.
            core.Navigate("https://quota.local/index.html");
            await _runtime.StartAsync();
        }
        catch
        {
            if (_options.Smoke)
                await RecordSmokeAsync(JsonDocument.Parse("{\"pass\":false,\"reason\":\"Native host initialization failed. Check local assets and WebView2 Runtime.\"}").RootElement.Clone());
            else ReportTrayError("Quota could not load. Keep the renderer and preload files beside quota.exe and install or repair Microsoft Edge WebView2 Runtime, then restart Quota.");
        }
    }

    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            if (_options.Smoke) await RecordSmokeAsync(JsonDocument.Parse("{\"pass\":false,\"reason\":\"Local UI navigation failed.\"}").RootElement.Clone());
            else ReportTrayError("Quota could not load its local UI. Restore the renderer folder beside quota.exe and restart.");
            return;
        }
        _initialized = true;
        Notify("connection", new { connected = true, message = (string?)null });
        if (_runtime is not null) Notify("state", _runtime.GetState());
        if (_showWhenReady) { _showWhenReady = false; Toggle(); }
        if (_options.Smoke)
        {
            try
            {
                var script = Path.Combine(AppContext.BaseDirectory, "smoke.js");
                if (!File.Exists(script)) throw new FileNotFoundException();
                await _web.CoreWebView2.ExecuteScriptAsync(await File.ReadAllTextAsync(script));
            }
            catch { await RecordSmokeAsync(JsonDocument.Parse("{\"pass\":false,\"reason\":\"Native smoke script is missing or could not execute.\"}").RootElement.Clone()); }
        }
    }

    private static bool IsMainDocument(string source) => Uri.TryCreate(source, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.Host == "quota.local" && uri.IsDefaultPort && string.IsNullOrEmpty(uri.UserInfo) && (uri.AbsolutePath is "/" or "/index.html");

    private void OnResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        const string csp = "default-src 'none'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'none'; frame-src 'none'; frame-ancestors 'none'; object-src 'none'; base-uri 'none'; form-action 'none'";
        try
        {
            var uri = new Uri(e.Request.Uri);
            if (uri.Scheme != "https" || uri.Host != "quota.local" || !uri.IsDefaultPort || e.Request.Method != "GET")
            { RespondResource(e, [], 403, "Forbidden", "text/plain", csp); return; }
            var relative = Uri.UnescapeDataString(uri.AbsolutePath).TrimStart('/');
            if (relative.Length == 0) relative = "index.html";
            var file = Path.GetFullPath(Path.Combine(_rendererRoot, relative));
            if (!file.StartsWith(_rendererRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || !File.Exists(file))
            { RespondResource(e, [], 404, "Not Found", "text/plain", csp); return; }
            var mime = Path.GetExtension(file).ToLowerInvariant() switch
            {
                ".html" => "text/html; charset=utf-8", ".js" => "text/javascript; charset=utf-8", ".css" => "text/css; charset=utf-8",
                ".svg" => "image/svg+xml", ".png" => "image/png", ".ico" => "image/x-icon", ".woff" => "font/woff", ".woff2" => "font/woff2",
                ".json" => "application/json", _ => "application/octet-stream"
            };
            RespondResource(e, File.ReadAllBytes(file), 200, "OK", mime, csp);
        }
        catch { RespondResource(e, [], 403, "Forbidden", "text/plain", csp); }
    }

    private void RespondResource(CoreWebView2WebResourceRequestedEventArgs e, byte[] bytes, int status, string reason, string mime, string csp)
    {
        e.Response = _web.CoreWebView2.Environment.CreateWebResourceResponse(new MemoryStream(bytes, writable: false), status, reason,
            $"Content-Type: {mime}\r\nContent-Security-Policy: {csp}\r\nX-Content-Type-Options: nosniff\r\nCache-Control: no-store\r\nReferrer-Policy: no-referrer");
    }

    private async void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (_exiting || !IsMainDocument(e.Source)) return;
        string? id = null;
        try
        {
            var json = e.WebMessageAsJson;
            if (Encoding.UTF8.GetByteCount(json) > 65536) return;
            using var document = JsonDocument.Parse(json, new() { MaxDepth = 24 });
            var message = document.RootElement;
            if (message.ValueKind != JsonValueKind.Object) return;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var prop in message.EnumerateObject()) if (prop.Name is not ("id" or "method" or "params") || !names.Add(prop.Name)) return;
            if (!message.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.String ||
                idElement.GetString() is not { Length: > 0 and <= 128 } validId || validId.Any(char.IsControl)) return;
            id = validId;
            if (!message.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String || _runtime is null) throw new ArgumentException();
            if (_activeRequests >= 64 || !_pendingIds.Add(id)) { Error(id, "Too many pending requests."); return; }
            _activeRequests++;
            try
            {
                var hasParams = message.TryGetProperty("params", out var parameters);
                var name = method.GetString();
                if (name is not ("updateSettings" or "setStartWithWindows" or "openUsage" or "__layout" or "__smokeResult") && hasParams &&
                    (parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Any())) throw new ArgumentException();
                switch (name)
                {
                    case "getState": Result(id, _runtime.GetState()); break;
                    case "getSettings": Result(id, _runtime.GetSettings()); break;
                    case "testNotification":
                        var accepted = _options.Smoke || ShowQuotaAlert(new("Quota notification test", "Notifications are ready. Low-quota alerts use your saved remaining-percentage thresholds."));
                        Result(id, new { accepted });
                        break;
                    case "__smokeWindow" when _options.Smoke:
                        Result(id, _window.Inspect());
                        break;
                    case "getRuntimeInfo": Result(id, new { version = "1.0.0", connected = true, hotkeyAvailable = _hotkeyAvailable, demo = _options.Fixture, startWithWindows = ReadStartupSetting() }); break;
                    case "setStartWithWindows":
                        if (!hasParams || parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Count() != 1 ||
                            !parameters.TryGetProperty("enabled", out var startupEnabled) || startupEnabled.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                            throw new ArgumentException();
                        var enabled = startupEnabled.GetBoolean();
                        // All synthetic sessions simulate this OS preference, never touching the real Run key.
                        var savedStartup = _options.Fixture ? (_fixtureStartWithWindows = enabled) : StartupRegistration().SetEnabled(enabled);
                        Result(id, new { enabled = savedStartup });
                        break;
                    case "__layout":
                        if (!hasParams || parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Count() != 1 ||
                            !parameters.TryGetProperty("height", out var height) || height.ValueKind != JsonValueKind.Number ||
                            !height.TryGetInt32(out var pixels) || pixels is < 180 or > 520) throw new ArgumentException();
                        _window.FitContent(pixels);
                        // WinForms docking can defer invisible children. Size WebView2
                        // explicitly so the first shown frame uses the fitted viewport.
                        _web.Bounds = _window.ClientRectangle;
                        Result(id, new { accepted = true });
                        break;
                    case "refresh": Result(id, new { accepted = _runtime.RequestRefresh() }); break;
                    case "updateSettings":
                        if (!hasParams) throw new ArgumentException();
                        Result(id, await _runtime.UpdateSettingsAsync(parameters));
                        break;
                    case "hide": _window.Hide(); Result(id, new { accepted = true }); break;
                    case "openUsage":
                        OpenUsage(parameters, hasParams); Result(id, new { accepted = true }); break;
                    case "chooseDirectory": Result(id, ChooseDirectory()!); break;
                    case "__smokeResult" when _options.Smoke && hasParams && parameters.ValueKind == JsonValueKind.Object:
                        await RecordSmokeAsync(parameters.Clone()); break;
                    default: throw new ArgumentException();
                }
            }
            finally { _pendingIds.Remove(id); _activeRequests--; }
        }
        catch (InvalidSettingsException) { if (id is not null) Error(id, "Invalid preferences."); }
        catch { if (id is not null && !_exiting) Error(id, "Request could not be completed. Check the source or settings location and try again."); }
    }

    private static string? UsageUrl(string id) => id switch
    {
        "codex" or "codex-cli" => "https://chatgpt.com/codex/settings/usage",
        "claude" => "https://claude.ai/settings/usage",
        "copilot" => "https://github.com/settings/copilot/features",
        _ when Regex.IsMatch(id, @"^claude:[a-zA-Z0-9_. @-]{1,200}$", RegexOptions.CultureInvariant) => "https://claude.ai/settings/usage",
        _ => null
    };

    private static WindowsStartup StartupRegistration() => new(Environment.ProcessPath ?? "");

    private bool? ReadStartupSetting()
    {
        if (_options.Fixture) return _fixtureStartWithWindows;
        try { return StartupRegistration().IsEnabled(); }
        catch { return null; }
    }

    private void OpenUsage(JsonElement parameters, bool hasParams)
    {
        if (!hasParams || parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Count() != 1 ||
            !parameters.TryGetProperty("providerId", out var provider) || provider.ValueKind != JsonValueKind.String ||
            provider.GetString() is not { Length: > 0 and <= 256 } id || UsageUrl(id) is not { } url) throw new ArgumentException();
        // Hidden smoke must never start a browser or any external process.
        if (_options.Smoke) return;
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
    }

    private string? ChooseDirectory()
    {
        if (_options.Smoke) return null;
        _window.GuardDeactivation = true;
        try
        {
            using var dialog = new FolderBrowserDialog { Description = "Choose a source directory", UseDescriptionForTitle = true, ShowNewFolderButton = false };
            return dialog.ShowDialog(_window) == DialogResult.OK ? dialog.SelectedPath : null;
        }
        finally { _window.GuardDeactivation = false; if (_window.Visible) { _window.Activate(); _web.Focus(); } }
    }

    private void OnStateChanged(DashboardDto state)
    {
        if (_exiting || !_window.IsHandleCreated || _window.IsDisposed) return;
        QueueQuotaAlerts(state);
        try { _window.BeginInvoke(() => { if (!_exiting && _initialized) Notify("state", state); }); }
        catch (InvalidOperationException) { }
    }

    private void QueueQuotaAlerts(DashboardDto state)
    {
        if (_options.Fixture || _runtime is null || state.IsRefreshing) return;
        lock (_alertLock)
        {
            var previous = _alertTail;
            _alertTail = Task.Run(async () =>
            {
                try
                {
                    try { await previous.ConfigureAwait(false); } catch { }
                    if (_exiting) return;
                    // Evaluate the latest publication/settings after earlier history writes finish.
                    var latest = _runtime.GetState();
                    var settings = _runtime.GetSettings().Notifications;
                    var alerts = await _quotaAlerts.EvaluateAsync(latest, settings, _alertShutdown.Token).ConfigureAwait(false);
                    if (alerts.Count == 0 || _exiting) return;
                    var alert = alerts.Count == 1 ? alerts[0] : new QuotaAlert("Quota — low limits",
                        string.Join(" · ", alerts.Take(2).Select(item => item.Title)) + (alerts.Count > 2 ? $" and {alerts.Count - 2} more. Open Quota for details." : ". Open Quota for details."));
                    _window.BeginInvoke(() => {
                        if (_exiting || _runtime.GetSettings().Notifications != settings) return;
                        var current = _runtime.GetState();
                        var identitiesStillEnabled = latest.Providers.Where(p => p.Status == "ok").All(previousReading =>
                            current.Providers.Any(p => p.Id == previousReading.Id && p.Status == "ok" &&
                                p.AccountId == previousReading.AccountId && p.AccountEmail == previousReading.AccountEmail));
                        if (identitiesStillEnabled) ShowQuotaAlert(alert);
                    });
                }
                catch (OperationCanceledException) when (_alertShutdown.IsCancellationRequested) { }
                catch
                {
                    // Fixed text only; no state, account identity or storage paths enter a Windows notification.
                    try { _window.BeginInvoke(() => {
                        if (!_exiting && !_alertStorageErrorReported) {
                            _alertStorageErrorReported = true;
                            ReportTrayError("Quota could not save notification history. Check access to the Quota settings folder.");
                        }
                    }); } catch (InvalidOperationException) { }
                }
            });
        }
    }

    private bool ShowQuotaAlert(QuotaAlert alert)
    {
        if (_options.Smoke || _tray is null || _exiting) return false;
        var title = StateMapper.DisplayText(alert.Title, 256) ?? "Quota";
        var message = StateMapper.DisplayText(alert.Message, 1024) ?? "A quota is low. Open Quota to see remaining limits.";
        try
        {
            _tray.ShowBalloonTip(8000, title.Length > 63 ? title[..63] : title,
                message.Length > 255 ? message[..252] + "…" : message, ToolTipIcon.Warning);
            return true;
        }
        catch { Console.Error.WriteLine("Windows notification could not be submitted."); return false; }
    }

    private void Notify(string name, object data) => Send(new { @event = name, data });
    private void Result(string id, object? result) => Send(new { id, result });
    private void Error(string id, string error) => Send(new { id, error });
    private void Send(object frame)
    {
        if (!_exiting && !_web.IsDisposed && _web.CoreWebView2 is not null)
            _web.CoreWebView2.PostWebMessageAsJson(WireJson.Serialize(frame));
    }

    private void ReportTrayError(string message)
    {
        if (_options.Smoke) return;
        _tray?.ShowBalloonTip(8000, "Quota", message, ToolTipIcon.Warning);
    }

    private async Task RecordSmokeAsync(JsonElement report)
    {
        if (!_options.Smoke || _smokeRecorded) return;
        _smokeRecorded = true;
        _timeoutTimer.Stop();
        var pass = report.TryGetProperty("pass", out var flag) && flag.ValueKind == JsonValueKind.True && _window.HasSafePopupStyles();
        try
        {
            var node = JsonNode.Parse(report.GetRawText())!.AsObject();
            node["pass"] = pass;
            node["nativeWindow"] = JsonSerializer.SerializeToNode(_window.Inspect(), WireJson.Options);
            node["completedAt"] = DateTimeOffset.UtcNow.ToString("O");
            var output = string.IsNullOrWhiteSpace(_options.SmokeOutput) ? Path.Combine(Path.GetTempPath(), "quota-native-smoke-output") : Path.GetFullPath(_options.SmokeOutput);
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "native-smoke.json"), node.ToJsonString(new() { WriteIndented = true }));
        }
        catch { pass = false; }
        ExitCode = pass ? 0 : 1;
        await ExitAsync();
    }

    private async Task ExitAsync()
    {
        if (_exiting) return;
        _exiting = true;
        _alertShutdown.Cancel();
        _timeoutTimer.Stop(); _startTimer.Stop();
        if (_tray is not null) _tray.Visible = false;
        if (_hotkeyAvailable) NativeWindow.UnregisterHotKey(_window.Handle, 0x5155);
        if (_runtime is not null)
        {
            _runtime.StateChanged -= OnStateChanged;
            try { await _runtime.DisposeAsync(); } catch { ExitCode = 1; }
        }
        try { await _alertTail.WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
        _web.Dispose();
        _window.Close();
        ExitThread();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _startTimer.Dispose(); _timeoutTimer.Dispose();
            _tray?.ContextMenuStrip?.Dispose(); _tray?.Dispose(); _icon?.Dispose();
            _web.Dispose(); _window.Dispose();
        }
        base.Dispose(disposing);
    }
}
