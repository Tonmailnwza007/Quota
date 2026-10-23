using System.Text.Json;
using costats.Application.Settings;

namespace costats.Infrastructure.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private readonly string? _customPath;
    private readonly JsonSerializerOptions _serializerOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    private readonly object _saveLock = new();
    private Task _activeSave = Task.CompletedTask;

    public JsonSettingsStore(string? customPath = null)
    {
        _customPath = customPath;
    }

    public async Task<AppSettings> LoadAsync(CancellationToken cancellationToken)
    {
        var path = GetSettingsPath();
        if (!File.Exists(path))
        {
            return new AppSettings();
        }

        try
        {
            await using var stream = File.OpenRead(path);
            var settings = await JsonSerializer.DeserializeAsync<AppSettings>(stream, _serializerOptions, cancellationToken)
                .ConfigureAwait(false);
            return settings ?? new AppSettings();
        }
        catch (JsonException)
        {
            BackupCorruptSettings(path);
            return new AppSettings();
        }
        catch (Exception)
        {
            return new AppSettings();
        }
    }

    public async Task SaveAsync(AppSettings settings, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        // Capture coherent snapshot immediately in memory at time of call
        var snapshotBytes = JsonSerializer.SerializeToUtf8Bytes(settings, _serializerOptions);

        Task saveTask;
        lock (_saveLock)
        {
            var previous = _activeSave;
            saveTask = Task.Run(async () =>
            {
                try
                {
                    await previous.ConfigureAwait(false);
                }
                catch
                {
                    // Prior failure does not stop subsequent save
                }

                cancellationToken.ThrowIfCancellationRequested();
                await WriteSnapshotBytesAsync(snapshotBytes, cancellationToken).ConfigureAwait(false);
            }, CancellationToken.None);

            _activeSave = saveTask;
        }

        await saveTask.ConfigureAwait(false);
    }

    private async Task WriteSnapshotBytesAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        var path = GetSettingsPath();
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        var tempPath = path + ".tmp." + Guid.NewGuid().ToString("N");
        try
        {
            await using (var stream = File.Create(tempPath))
            {
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }

            File.Move(tempPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); } catch { }
            }
        }
    }

    public string GetSettingsPath()
    {
        if (!string.IsNullOrWhiteSpace(_customPath))
        {
            return _customPath;
        }

        var basePath = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, "costats", "settings.json");
    }

    private static void BackupCorruptSettings(string path)
    {
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (string.IsNullOrEmpty(directory))
            {
                return;
            }

            var backupPath = Path.Combine(directory, "settings.bad.json");
            File.Copy(path, backupPath, true);
        }
        catch
        {
            // Ignore backup failures.
        }
    }
}
