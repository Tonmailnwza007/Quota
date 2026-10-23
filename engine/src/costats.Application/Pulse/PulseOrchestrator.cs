using costats.Application.Abstractions;
using costats.Application.Settings;
using costats.Core.Pulse;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace costats.Application.Pulse;

public sealed class PulseOrchestrator : BackgroundService, IPulseOrchestrator
{
    private readonly IEnumerable<ISignalSource> _sources;
    private readonly ISourceSelector _selector;
    private readonly IClock _clock;
    private readonly PulseBroadcaster _broadcaster;
    private readonly IPulseSnapshotWriter _snapshotWriter;
    private readonly AppSettings? _settings;
    private readonly ILogger<PulseOrchestrator> _logger;
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly object _intervalLock = new();
    private readonly object _stateLock = new();

    private TimeSpan _refreshInterval;
    private CancellationTokenSource? _timerCts;
    private PulseState? _lastState;
    private bool _hasSuccessfulLoad;
    private int _pendingRefreshCount;
    private Task _snapshotTail = Task.CompletedTask;

    public PulseOrchestrator(
        IEnumerable<ISignalSource> sources,
        ISourceSelector selector,
        IClock clock,
        PulseBroadcaster broadcaster,
        IPulseSnapshotWriter snapshotWriter,
        IOptions<PulseOptions> options,
        ILogger<PulseOrchestrator> logger,
        AppSettings? settings = null)
    {
        _sources = sources;
        _selector = selector;
        _clock = clock;
        _broadcaster = broadcaster;
        _snapshotWriter = snapshotWriter;
        _refreshInterval = options.Value.RefreshInterval;
        _logger = logger;
        _settings = settings;
    }

    public IObservable<PulseState> PulseStream => _broadcaster;

    public void UpdateRefreshInterval(TimeSpan interval)
    {
        lock (_intervalLock)
        {
            _refreshInterval = interval;
            try { _timerCts?.Cancel(); }
            catch (ObjectDisposedException) { }
        }
        _logger.LogInformation("Refresh interval updated to {Interval}", interval);
    }

    public void NotifySettingsChanged()
    {
        _logger.LogInformation("Settings changed notification received, checking immediate eviction");

        PulseState? immediateState = null;
        Task? snapshotTask = null;
        lock (_stateLock)
        {
            if (_lastState is not null)
            {
                var filtered = _lastState.Providers
                    .Where(p => _settings?.IsProviderEnabled(p.Key) ?? true)
                    .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase);

                ClearSharedAccountAttributions(filtered);
                ApplySharedAccountAttributions(filtered);

                immediateState = _lastState with
                {
                    Providers = filtered,
                    IsRefreshing = false
                };
                _lastState = immediateState;
            }
            else if (_settings is not null)
            {
                var anyEnabled = _sources.Any(s => _settings.IsProviderEnabled(s.Profile.ProviderId));
                if (!anyEnabled)
                {
                    immediateState = new PulseState(
                        new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase),
                        _clock.UtcNow,
                        Array.Empty<string>(),
                        false,
                        RefreshTrigger.Manual);
                    _lastState = immediateState;
                }
            }

            if (immediateState is not null)
            {
                _broadcaster.Publish(immediateState);
                snapshotTask = EnqueueSnapshotWriteLocked(immediateState);
            }
        }

        if (snapshotTask is not null)
        {
            _ = snapshotTask.ContinueWith(t =>
            {
                if (t.IsFaulted && t.Exception is not null)
                {
                    _logger.LogWarning(t.Exception.InnerException ?? t.Exception, "Snapshot write from NotifySettingsChanged failed");
                }
            }, TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously);
        }

        // Coalesce refresh requests: if a background refresh is already pending or queued, don't pile up
        if (Interlocked.Increment(ref _pendingRefreshCount) == 1)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await RefreshOnceAsync(RefreshTrigger.Manual, CancellationToken.None).ConfigureAwait(false);
                        if (Interlocked.Decrement(ref _pendingRefreshCount) == 0)
                        {
                            break;
                        }
                        // Reset count to 1 and run one more coalesced cycle
                        Interlocked.Exchange(ref _pendingRefreshCount, 1);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.Exchange(ref _pendingRefreshCount, 0);
                    _logger.LogError(ex, "Coalesced refresh failed");
                }
            });
        }
    }

    private Task EnqueueSnapshotWriteLocked(PulseState state)
    {
        var previous = _snapshotTail;
        var writer = _snapshotWriter;
        var logger = _logger;

        var current = Task.Run(async () =>
        {
            try
            {
                await previous.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                // Continue write chain even if previous write failed
                logger.LogDebug(ex, "Previous snapshot write failed; continuing write chain");
            }

            if (writer is not null)
            {
                try
                {
                    await writer.WriteAsync(state, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to write snapshot asynchronously");
                    throw;
                }
            }
        });

        _snapshotTail = current;
        return current;
    }

    public Task EnqueueSnapshotWrite(PulseState state)
    {
        lock (_stateLock)
        {
            return EnqueueSnapshotWriteLocked(state);
        }
    }

    public Task WaitForSnapshotsAsync()
    {
        lock (_stateLock)
        {
            return _snapshotTail;
        }
    }

    public async Task RefreshOnceAsync(RefreshTrigger trigger, CancellationToken cancellationToken)
    {
        if (trigger == RefreshTrigger.Silent)
        {
            if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
            {
                _logger.LogDebug("Skipping silent overview refresh - refresh already in progress");
                return;
            }
        }
        else
        {
            await _refreshGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        try
        {
            // Filter only enabled sources
            var enabledSources = _sources
                .Where(s => _settings?.IsProviderEnabled(s.Profile.ProviderId) ?? true)
                .ToList();

            if (enabledSources.Count == 0)
            {
                // All providers disabled: clear state immediately
                var emptyState = new PulseState(
                    new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase),
                    _clock.UtcNow,
                    Array.Empty<string>(),
                    false,
                    trigger);
                Task snapshotTask;
                lock (_stateLock)
                {
                    _lastState = emptyState;
                    _hasSuccessfulLoad = true;
                    _broadcaster.Publish(emptyState);
                    snapshotTask = EnqueueSnapshotWriteLocked(emptyState);
                }
                await snapshotTask.ConfigureAwait(false);
                return;
            }

            if (ShouldShowShimmer(trigger))
            {
                PublishRefreshing(trigger);
            }

            var byProvider = enabledSources
                .GroupBy(source => source.Profile.ProviderId)
                .ToDictionary(group => group.Key, group => (IReadOnlyList<ISignalSource>)group.ToList());

            var errors = new List<string>();

            // Keep provider reads sequential to avoid overlapping heavy file scans.
            var providerReads = new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase);
            foreach (var (providerId, providerSources) in byProvider)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (_settings is not null && !_settings.IsProviderEnabled(providerId))
                {
                    continue;
                }

                var reading = await _selector.SelectAsync(providerId, providerSources, cancellationToken).ConfigureAwait(false);

                // Recheck after in-flight fetch
                if (_settings is not null && !_settings.IsProviderEnabled(providerId))
                {
                    continue;
                }

                providerReads[providerId] = reading;
            }

            PulseState state;
            Task writeTask;
            lock (_stateLock)
            {
                // At final publish: ALL collected results must be filtered by enabled providers
                // (e.g. provider A finished, then user disabled provider A while provider B was in flight)
                var finalProviders = providerReads
                    .Where(pair => _settings?.IsProviderEnabled(pair.Key) ?? true)
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

                // Check and mark shared-account quota without combining allowances (cleared each time)
                ClearSharedAccountAttributions(finalProviders);
                ApplySharedAccountAttributions(finalProviders);

                state = new PulseState(finalProviders, _clock.UtcNow, errors, false, trigger);
                _lastState = state;
                _hasSuccessfulLoad = true;
                _broadcaster.Publish(state);
                writeTask = EnqueueSnapshotWriteLocked(state);
            }

            await writeTask.ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Pulse refresh failed");
            var keepRefreshing = trigger == RefreshTrigger.Initial && !_hasSuccessfulLoad;

            PulseState state;
            Task writeTask;
            lock (_stateLock)
            {
                var baseState = _lastState ?? new PulseState(
                    new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase),
                    _clock.UtcNow,
                    Array.Empty<string>(),
                    keepRefreshing,
                    trigger);

                state = baseState with
                {
                    LastRefresh = _clock.UtcNow,
                    Errors = new List<string> { ex.Message },
                    IsRefreshing = keepRefreshing,
                    Trigger = trigger
                };

                if (!keepRefreshing)
                {
                    _lastState ??= state;
                }

                _broadcaster.Publish(state);
                writeTask = EnqueueSnapshotWriteLocked(state);
            }

            await writeTask.ConfigureAwait(false);
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    public async Task RefreshProviderAsync(string providerId, CancellationToken cancellationToken)
    {
        if (_settings is not null && !_settings.IsProviderEnabled(providerId))
        {
            _logger.LogDebug("Skipping refresh for disabled provider {ProviderId}", providerId);
            return;
        }

        // Silent refresh - don't wait if another refresh is in progress
        if (!await _refreshGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            _logger.LogDebug("Skipping silent refresh for {ProviderId} - refresh already in progress", providerId);
            return;
        }

        try
        {
            var providerSources = _sources
                .Where(s => string.Equals(s.Profile.ProviderId, providerId, StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (providerSources.Count == 0)
            {
                _logger.LogWarning("No sources found for provider {ProviderId}", providerId);
                return;
            }

            var reading = await _selector.SelectAsync(providerId, providerSources, cancellationToken).ConfigureAwait(false);

            // Recheck after in-flight fetch
            if (_settings is not null && !_settings.IsProviderEnabled(providerId))
            {
                return;
            }

            PulseState state;
            Task writeTask;
            lock (_stateLock)
            {
                var existingProviders = _lastState?.Providers
                    ?? new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase);

                var updatedProviders = new Dictionary<string, ProviderReading>(existingProviders, StringComparer.OrdinalIgnoreCase);

                // Evict any providers that became disabled
                if (_settings is not null)
                {
                    foreach (var key in updatedProviders.Keys.ToList())
                    {
                        if (!_settings.IsProviderEnabled(key))
                        {
                            updatedProviders.Remove(key);
                        }
                    }
                }

                if (_settings?.IsProviderEnabled(providerId) ?? true)
                {
                    updatedProviders[providerId] = reading;
                }

                // Recompute shared-account attributions from cleared state
                ClearSharedAccountAttributions(updatedProviders);
                ApplySharedAccountAttributions(updatedProviders);

                state = new PulseState(updatedProviders, _clock.UtcNow, Array.Empty<string>(), false, RefreshTrigger.Silent);
                _lastState = state;
                _broadcaster.Publish(state);
                writeTask = EnqueueSnapshotWriteLocked(state);
            }

            await writeTask.ConfigureAwait(false);
            _logger.LogDebug("Silent refresh completed for {ProviderId}", providerId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Silent refresh failed for {ProviderId}", providerId);
            // Silent refresh failures are non-blocking - don't propagate
        }
        finally
        {
            _refreshGate.Release();
        }
    }

    private static void ClearSharedAccountAttributions(Dictionary<string, ProviderReading> readings)
    {
        foreach (var (key, reading) in readings.ToList())
        {
            if (reading.Usage is not null && (reading.Usage.IsSharedAccountQuota || reading.Usage.StatusMessage != null))
            {
                var statusMsg = reading.Usage.StatusMessage;
                if (statusMsg != null && statusMsg.StartsWith("Shared account", StringComparison.OrdinalIgnoreCase))
                {
                    statusMsg = null;
                }

                readings[key] = reading with
                {
                    Usage = reading.Usage with
                    {
                        IsSharedAccountQuota = false,
                        StatusMessage = statusMsg
                    }
                };
            }
        }
    }

    private static void ApplySharedAccountAttributions(Dictionary<string, ProviderReading> readings)
    {
        // 1. Antigravity App vs Antigravity CLI shared account check
        if (readings.TryGetValue("antigravity", out var appReading) &&
            readings.TryGetValue("antigravity-cli", out var cliReading) &&
            HasMeasuredAntigravityQuota(appReading) && HasMeasuredAntigravityQuota(cliReading))
        {
            var appEmail = appReading.Identity?.Email ?? appReading.Usage?.AccountEmail;
            var cliEmail = cliReading.Identity?.Email ?? cliReading.Usage?.AccountEmail;

            var appId = appReading.Usage?.AccountId;
            var cliId = cliReading.Usage?.AccountId;
            var sameAccount = !string.IsNullOrWhiteSpace(appId) && !string.IsNullOrWhiteSpace(cliId)
                ? string.Equals(appId.Trim(), cliId.Trim(), StringComparison.Ordinal)
                : !string.IsNullOrWhiteSpace(appEmail) && string.Equals(appEmail.Trim(), cliEmail?.Trim(), StringComparison.OrdinalIgnoreCase);
            if (sameAccount)
            {
                if (appReading.Usage is not null)
                {
                    readings["antigravity"] = appReading with
                    {
                        Usage = appReading.Usage with
                        {
                            IsSharedAccountQuota = true,
                            StatusMessage = "Shared account quota with Antigravity CLI"
                        }
                    };
                }

                if (cliReading.Usage is not null)
                {
                    readings["antigravity-cli"] = cliReading with
                    {
                        Usage = cliReading.Usage with
                        {
                            IsSharedAccountQuota = true,
                            StatusMessage = "Shared account quota with Antigravity App"
                        }
                    };
                }
            }
        }

        // 2. Codex Desktop vs Codex CLI shared account check
        if (readings.TryGetValue("codex", out var deskReading) &&
            readings.TryGetValue("codex-cli", out var cliCodexReading))
        {
            var deskAcc = deskReading.Usage?.AccountId ?? deskReading.Identity?.Email;
            var cliAcc = cliCodexReading.Usage?.AccountId ?? cliCodexReading.Identity?.Email;

            if (!string.IsNullOrWhiteSpace(deskAcc) && string.Equals(deskAcc, cliAcc, StringComparison.OrdinalIgnoreCase))
            {
                if (deskReading.Usage is not null)
                {
                    readings["codex"] = deskReading with
                    {
                        Usage = deskReading.Usage with
                        {
                            IsSharedAccountQuota = true,
                            StatusMessage = "Shared account with Codex CLI"
                        }
                    };
                }

                if (cliCodexReading.Usage is not null)
                {
                    readings["codex-cli"] = cliCodexReading with
                    {
                        Usage = cliCodexReading.Usage with
                        {
                            IsSharedAccountQuota = true,
                            StatusMessage = "Shared account with Codex Desktop"
                        }
                    };
                }
            }
        }
    }

    private static bool HasMeasuredAntigravityQuota(ProviderReading reading) =>
        reading.Confidence == ReadingConfidence.High &&
        reading.Usage?.QuotaBuckets?.Any(bucket => bucket.UsedPercent is { } percent && float.IsFinite(percent)) == true;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await RefreshOnceAsync(RefreshTrigger.Initial, stoppingToken).ConfigureAwait(false);

            while (!stoppingToken.IsCancellationRequested)
            {
                TimeSpan currentInterval;
                lock (_intervalLock)
                {
                    currentInterval = _refreshInterval;
                    _timerCts?.Dispose();
                    _timerCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                }

                try
                {
                    using var timer = new PeriodicTimer(currentInterval);
                    while (await timer.WaitForNextTickAsync(_timerCts.Token).ConfigureAwait(false))
                    {
                        await RefreshOnceAsync(RefreshTrigger.Scheduled, _timerCts.Token).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Timer was cancelled due to interval change, restart with new interval
                    _logger.LogDebug("Restarting timer with new interval");
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Pulse orchestrator cancelled");
        }
    }

    private void PublishRefreshing(RefreshTrigger trigger)
    {
        lock (_stateLock)
        {
            var existing = _lastState?.Providers
                ?.Where(p => _settings?.IsProviderEnabled(p.Key) ?? true)
                .ToDictionary(p => p.Key, p => p.Value, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, ProviderReading>(StringComparer.OrdinalIgnoreCase);

            ClearSharedAccountAttributions(existing);
            ApplySharedAccountAttributions(existing);

            var refreshingState = new PulseState(
                existing,
                _clock.UtcNow,
                Array.Empty<string>(),
                true,
                trigger);

            _broadcaster.Publish(refreshingState);
        }
    }

    private bool ShouldShowShimmer(RefreshTrigger trigger)
    {
        if (trigger == RefreshTrigger.Initial)
        {
            return true;
        }

        if (trigger == RefreshTrigger.Manual)
        {
            return true;
        }

        return !_hasSuccessfulLoad;
    }
}
