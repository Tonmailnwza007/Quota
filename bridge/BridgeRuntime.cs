using System.Text.Json;
using costats.Application.Pulse;
using costats.Application.Settings;
using costats.Core.Pulse;
using costats.Infrastructure.Time;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Costats.Bridge;

public sealed class NullSnapshotWriter : IPulseSnapshotWriter
{
    public Task WriteAsync(PulseState state, CancellationToken cancellationToken) => Task.CompletedTask;
}

public sealed class BridgeRuntime : IObserver<PulseState>, IAsyncDisposable
{
    private readonly object _settingsLock = new();
    private readonly AppSettings _settings;
    private readonly ModernSettingsStore _store;
    private readonly PulseOrchestrator _orchestrator;
    private readonly bool _fixture;
    private readonly IDisposable _subscription;
    private readonly CancellationTokenSource _shutdown = new();
    private DashboardDto _state;
    private int _manualRefresh;
    public event Action<DashboardDto>? StateChanged;

    public BridgeRuntime(AppSettings settings, ModernSettingsStore store, bool fixture, IReadOnlyList<ISignalSource> sources)
    {
        _settings = settings;
        _store = store;
        _fixture = fixture;
        _state = new(1, fixture ? "demo" : "live", [], true, null, []);
        _orchestrator = new(sources, new SourceSelector(NullLogger<SourceSelector>.Instance), new SystemClock(),
            new PulseBroadcaster(), new NullSnapshotWriter(), Options.Create(new PulseOptions { RefreshInterval = TimeSpan.FromMinutes(settings.RefreshMinutes) }),
            NullLogger<PulseOrchestrator>.Instance, settings);
        _subscription = _orchestrator.PulseStream.Subscribe(this);
    }

    public DashboardDto GetState() => Volatile.Read(ref _state);
    public SettingsDto GetSettings() { lock (_settingsLock) return SettingsValidation.Snapshot(_settings); }
    public Task StartAsync() => _orchestrator.StartAsync(_shutdown.Token);

    public bool RequestRefresh()
    {
        if (GetState().IsRefreshing || Interlocked.CompareExchange(ref _manualRefresh, 1, 0) != 0) return false;
        _ = Task.Run(async () =>
        {
            try { await _orchestrator.RefreshOnceAsync(RefreshTrigger.Manual, _shutdown.Token).ConfigureAwait(false); }
            catch { Console.Error.WriteLine("Refresh could not complete."); }
            finally { Interlocked.Exchange(ref _manualRefresh, 0); }
        });
        return true;
    }

    public Task<SettingsDto> UpdateSettingsAsync(JsonElement patch)
    {
        SettingsDto next;
        Task save;
        lock (_settingsLock)
        {
            // Validate the complete patch before modifying the shared settings object.
            next = SettingsValidation.ValidatePatch(patch, SettingsValidation.Snapshot(_settings));
            SettingsValidation.Apply(next, _settings);
            _orchestrator.UpdateRefreshInterval(TimeSpan.FromMinutes(next.RefreshMinutes));
            _orchestrator.NotifySettingsChanged();
            save = _store.SaveAsync(next);
        }
        return SaveAndReturnAsync(save, next);
    }

    private static async Task<SettingsDto> SaveAndReturnAsync(Task save, SettingsDto next)
    {
        await save.ConfigureAwait(false);
        return next;
    }

    public void OnNext(PulseState value)
    {
        var next = StateMapper.Map(value, _fixture);
        Volatile.Write(ref _state, next);
        StateChanged?.Invoke(next);
    }
    public void OnCompleted() { }
    public void OnError(Exception error) => Console.Error.WriteLine("A source update could not complete.");

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        try { await _orchestrator.StopAsync(timeout.Token).ConfigureAwait(false); } catch { }
        try { await _store.FlushAsync().ConfigureAwait(false); } catch { Console.Error.WriteLine("Modern settings could not be saved."); }
        _subscription.Dispose();
        _orchestrator.Dispose();
        _shutdown.Dispose();
    }
}
