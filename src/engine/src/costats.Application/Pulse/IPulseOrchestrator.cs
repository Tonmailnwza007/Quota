using costats.Core.Pulse;

namespace costats.Application.Pulse;

public interface IPulseOrchestrator
{
    IObservable<PulseState> PulseStream { get; }

    Task RefreshOnceAsync(RefreshTrigger trigger, CancellationToken cancellationToken);

    /// <summary>
    /// Silently refresh a specific provider (no loading indicator).
    /// </summary>
    Task RefreshProviderAsync(string providerId, CancellationToken cancellationToken);

    void UpdateRefreshInterval(TimeSpan interval);

    /// <summary>
    /// Notifies orchestrator that settings changed. Immediately and synchronously evicts disabled providers
    /// from the active published state, then triggers an async refresh.
    /// </summary>
    void NotifySettingsChanged();
}
