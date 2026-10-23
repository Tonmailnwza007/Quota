using costats.Core.Pulse;

namespace costats.Infrastructure.Providers;

public static class ProviderCatalog
{
    public static ProviderProfile Codex { get; } = new("codex", "Codex", "#0A84FF");
    public static ProviderProfile CodexCli { get; } = new("codex-cli", "Codex CLI", "#00B4D8");
    public static ProviderProfile Claude { get; } = new("claude", "Claude", "#FF7A00");
    public static ProviderProfile Copilot { get; } = new("copilot", "Copilot", "#6E40C9");
    public static ProviderProfile Antigravity { get; } = new("antigravity", "Antigravity", "#4285F4");
    public static ProviderProfile AntigravityCli { get; } = new("antigravity-cli", "Antigravity CLI", "#34A853");
}
