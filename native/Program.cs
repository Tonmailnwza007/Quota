namespace Quota.Native;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        try
        {
            var options = NativeOptions.Parse(args);
            using var instance = options.Fixture ? null : new Mutex(false, "Local\\fmdz.quota.native");
            if (instance is not null)
            {
                try { if (!instance.WaitOne(0)) return 0; }
                catch (AbandonedMutexException) { /* Ownership transfers to this instance. */ }
            }
            try
            {
                using var context = new QuotaContext(options);
                Application.Run(context);
                return context.ExitCode;
            }
            finally { instance?.ReleaseMutex(); }
        }
        catch { return 1; }
    }
}

internal sealed record NativeOptions(bool Fixture, bool Smoke, string SettingsFile, string ProfileDirectory, string? SmokeOutput)
{
    public static NativeOptions Parse(string[] args)
    {
        var smoke = args.Contains("--smoke", StringComparer.Ordinal);
        var fixture = smoke || args.Contains("--fixture", StringComparer.Ordinal);
        string? explicitSettings = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--fixture" or "--smoke") continue;
            if (args[i] == "--settings-file" && i + 1 < args.Length) explicitSettings = Path.GetFullPath(args[++i]);
            else throw new ArgumentException("Invalid arguments.");
        }
        var root = fixture ? Path.Combine(Path.GetTempPath(), "quota-native-fixture", Guid.NewGuid().ToString("N"))
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "quota");
        // Smoke always has isolated preferences/profile, even if a caller mistakenly passes a real settings path.
        var settings = !smoke && explicitSettings is not null ? explicitSettings : Path.Combine(root, "settings.json");
        var smokeOutput = smoke ? Environment.GetEnvironmentVariable("COSTATS_SMOKE_OUTPUT") : null;
        return new(fixture, smoke, settings, Path.Combine(root, "webview"), smokeOutput);
    }
}
