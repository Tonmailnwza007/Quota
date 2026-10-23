using System.Text;
using System.Text.Json;
using System.Threading.Channels;

namespace Costats.Bridge;

public sealed class ProtocolWriter : IAsyncDisposable
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new() { SingleReader = true, SingleWriter = false });
    private readonly Task _pump;
    public ProtocolWriter(TextWriter writer)
    {
        _pump = Task.Run(async () =>
        {
            await foreach (var frame in _queue.Reader.ReadAllAsync())
            {
                await writer.WriteLineAsync(frame).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        });
    }
    public void Result(string id, object result) => Emit(new { jsonrpc = "2.0", id, result });
    public void Error(string? id, int code, string message) => Emit(new { jsonrpc = "2.0", id, error = new { code, message } });
    public void Notify(string method, object parameters) => Emit(new { jsonrpc = "2.0", method, @params = parameters });
    private void Emit(object value) { if (!_queue.Writer.TryWrite(WireJson.Serialize(value))) throw new IOException("Protocol output closed."); }
    public async ValueTask DisposeAsync() { _queue.Writer.TryComplete(); await _pump.ConfigureAwait(false); }
}

public sealed class ProtocolServer(BridgeRuntime runtime, ProtocolWriter writer)
{
    public const int MaximumFrameCharacters = 65536;
    private readonly List<Task> _pending = [];
    private bool _stopping;

    public async Task RunAsync(TextReader input)
    {
        writer.Notify("ready", new { protocolVersion = 1 });
        writer.Notify("stateChanged", runtime.GetState());
        runtime.StateChanged += OnState;
        await runtime.StartAsync().ConfigureAwait(false);
        var buffer = new char[4096];
        var line = new StringBuilder();
        var tooLong = false;
        while (!_stopping)
        {
            var count = await input.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0) break;
            for (var i = 0; i < count && !_stopping; i++)
            {
                var ch = buffer[i];
                if (ch != '\n')
                {
                    if (!tooLong && line.Length < MaximumFrameCharacters) line.Append(ch);
                    else { tooLong = true; line.Clear(); }
                    continue;
                }
                if (tooLong) writer.Error(null, -32600, "Request exceeds the frame limit.");
                else if (line.Length > 0) Dispatch(line.ToString().TrimEnd('\r'));
                line.Clear(); tooLong = false;
                _pending.RemoveAll(t => t.IsCompleted);
                if (_pending.Count >= 64) await Task.WhenAny(_pending).ConfigureAwait(false);
            }
        }
        // A missing final newline is accepted only at EOF; partial JSON still receives a parse error.
        if (!_stopping && line.Length > 0 && !tooLong) Dispatch(line.ToString());
        await Task.WhenAll(_pending).ConfigureAwait(false);
        runtime.StateChanged -= OnState;
    }

    private void OnState(DashboardDto state) => writer.Notify("stateChanged", state);

    private void Dispatch(string line)
    {
        string? id = null;
        try
        {
            using var document = JsonDocument.Parse(line, new() { MaxDepth = 24 });
            var request = document.RootElement;
            if (request.ValueKind != JsonValueKind.Object) { writer.Error(null, -32600, "Invalid request."); return; }
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var field in request.EnumerateObject())
                if (field.Name is not ("jsonrpc" or "id" or "method" or "params") || !seen.Add(field.Name))
                { writer.Error(null, -32600, "Invalid request."); return; }
            if (!request.TryGetProperty("id", out var requestId) || requestId.ValueKind != JsonValueKind.String ||
                requestId.GetString() is not { Length: > 0 and <= 128 } validId || validId.Any(char.IsControl))
            { writer.Error(null, -32600, "Invalid request."); return; }
            id = validId;
            if (!request.TryGetProperty("jsonrpc", out var version) || version.ValueKind != JsonValueKind.String || version.GetString() != "2.0" ||
                !request.TryGetProperty("method", out var method) || method.ValueKind != JsonValueKind.String)
            { writer.Error(id, -32600, "Invalid request."); return; }
            var hasParams = request.TryGetProperty("params", out var parameters);
            var name = method.GetString();
            if (name != "updateSettings" && hasParams && (parameters.ValueKind != JsonValueKind.Object || parameters.EnumerateObject().Any()))
            { writer.Error(id, -32602, "Invalid params."); return; }
            switch (name)
            {
                case "getState": writer.Result(id, runtime.GetState()); break;
                case "getSettings": writer.Result(id, runtime.GetSettings()); break;
                case "refresh": writer.Result(id, new { accepted = runtime.RequestRefresh() }); break;
                case "updateSettings":
                    if (!hasParams) throw new InvalidSettingsException();
                    // Validation and mutation run synchronously; persistence and its response never hold the input loop.
                    _pending.Add(CompleteSettingsAsync(id, runtime.UpdateSettingsAsync(parameters)));
                    break;
                case "shutdown": writer.Result(id, new { accepted = true }); _stopping = true; break;
                default: writer.Error(id, -32601, "Method not found."); break;
            }
        }
        catch (JsonException) { writer.Error(null, -32700, "Parse error."); }
        catch (InvalidSettingsException) { writer.Error(id, -32602, "Invalid params."); }
        catch { writer.Error(id, -32603, "Request could not be completed."); }
    }

    private async Task CompleteSettingsAsync(string id, Task<SettingsDto> save)
    {
        try { writer.Result(id, await save.ConfigureAwait(false)); }
        catch { writer.Error(id, -32001, "Preferences changed in memory but could not be saved. Check the modern app settings location."); }
    }
}
