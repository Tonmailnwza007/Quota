using System.Text;

namespace Costats.Bridge;

public static class Program
{
    public static async Task<int> Main(string[] args)
    {
        Console.InputEncoding = new UTF8Encoding(false);
        Console.OutputEncoding = new UTF8Encoding(false);
        try
        {
            var fixture = false;
            string? settingsFile = null;
            var slow = 0;
            for (var i = 0; i < args.Length; i++)
            {
                switch (args[i])
                {
                    case "--fixture": fixture = true; break;
                    case "--settings-file" when i + 1 < args.Length: settingsFile = args[++i]; break;
                    case "--fixture-delay-ms" when i + 1 < args.Length && int.TryParse(args[++i], out slow) && slow is >= 0 and <= 30000: break;
                    default: Console.Error.WriteLine("Invalid worker arguments."); return 2;
                }
            }
            if (!fixture && slow != 0) { Console.Error.WriteLine("Fixture delay requires fixture mode."); return 2; }
            // Unconfigured fixtures are ephemeral, never the real modern or legacy settings store.
            settingsFile ??= fixture ? Path.Combine(Path.GetTempPath(), "costats-ui-fixture", Guid.NewGuid().ToString("N"), "settings.json")
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "costats-ui", "settings.json");
            var store = new ModernSettingsStore(settingsFile);
            var settings = await store.LoadAsync().ConfigureAwait(false);
            var sources = fixture ? SourceFactory.CreateFixtures(slow) : SourceFactory.CreateLive(settings, Path.GetDirectoryName(Path.GetFullPath(settingsFile)));
            await using var writer = new ProtocolWriter(Console.Out);
            await using var runtime = new BridgeRuntime(settings, store, fixture, sources);
            var server = new ProtocolServer(runtime, writer);
            await server.RunAsync(Console.In).ConfigureAwait(false);
            return 0;
        }
        catch { Console.Error.WriteLine("Worker could not start or the protocol connection closed."); return 1; }
    }
}
