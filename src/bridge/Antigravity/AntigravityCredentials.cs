using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using costats.Application.Settings;

namespace Costats.Bridge.Antigravity;

public sealed record AntigravityCredentials(
    [property: JsonIgnore] string AccessToken,
    [property: JsonIgnore] string? RefreshToken,
    [property: JsonIgnore] string? IdToken,
    DateTimeOffset? ExpiresAt, string? Email, string? Subject, string? ClientId,
    [property: JsonIgnore] string? ClientSecret)
{
    public override string ToString() => $"AntigravityCredentials {{ HasAccessToken={AccessToken.Length > 0}, HasRefreshToken={!string.IsNullOrEmpty(RefreshToken)}, HasIdentity={!string.IsNullOrEmpty(Subject)}, ExpiresAt={ExpiresAt:O} }}";
}

public sealed record AntigravityCredentialLocation(string? FilePath, string TargetName)
{
    public static AntigravityCredentialLocation Shared => new(null, "gemini:antigravity");
    public static AntigravityCredentialLocation ForSettings(AppSettings settings) => settings.AntigravitySource switch
    {
        "saved" => Shared,
        "custom" when !string.IsNullOrWhiteSpace(settings.AntigravityAuthPath) => new(Path.GetFullPath(settings.AntigravityAuthPath.Trim()), ""),
        "custom" when !string.IsNullOrWhiteSpace(settings.AntigravityCredentialTarget) => new(null, settings.AntigravityCredentialTarget.Trim()),
        _ => throw new AntigravitySessionException(AntigravityFailure.SourceSelectionRequired)
    };
    public static AntigravityCredentialLocation ForCli(AppSettings settings) =>
        !string.IsNullOrWhiteSpace(settings.AntigravityCliAuthPath)
            ? new(Path.GetFullPath(settings.AntigravityCliAuthPath.Trim()), "")
            : new(null, string.IsNullOrWhiteSpace(settings.AntigravityCliCredentialTarget) ? Shared.TargetName : settings.AntigravityCliCredentialTarget.Trim());
    public string Key => FilePath is not null ? "file:" + Path.GetFullPath(FilePath).ToUpperInvariant() : "credential:" + TargetName.ToUpperInvariant();
}

public sealed class AntigravityCredentialSnapshot
{
    public AntigravityCredentialLocation Location { get; }
    public AntigravityCredentials Credentials { get; }
    [JsonIgnore] internal string RawJson { get; }
    public string Fingerprint => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RawJson)));
    private AntigravityCredentialSnapshot(AntigravityCredentialLocation location, string json, AntigravityCredentials credentials)
        => (Location, RawJson, Credentials) = (location, json, credentials);
    public override string ToString() => "AntigravityCredentialSnapshot { Credentials=<redacted> }";

    public static AntigravityCredentialSnapshot Parse(AntigravityCredentialLocation location, string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException();
        var token = root.TryGetProperty("token", out var nested) && nested.ValueKind == JsonValueKind.Object ? nested : root;
        string? Text(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var access = Text(token, "access_token");
        var refresh = Text(token, "refresh_token");
        if (string.IsNullOrWhiteSpace(access) && string.IsNullOrWhiteSpace(refresh)) throw new FormatException();
        var id = Text(root, "id_token") ?? Text(token, "id_token");
        var claims = Claims(id);
        DateTimeOffset? expiry = null;
        if (DateTimeOffset.TryParse(Text(token, "expiry") ?? Text(token, "expires_at"), CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal, out var parsed)) expiry = parsed;
        else if (token.TryGetProperty("expires_at", out var seconds) && seconds.ValueKind == JsonValueKind.Number && seconds.TryGetInt64(out var n))
        { try { expiry = n > 10_000_000_000L ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n); } catch (ArgumentOutOfRangeException) { } }
        var credentials = new AntigravityCredentials(access ?? "", refresh, id, expiry, claims.Email, claims.Subject,
            Text(root, "client_id") ?? Text(token, "client_id") ?? claims.Audience, Text(root, "client_secret") ?? Text(token, "client_secret"));
        return new(location, json, credentials);
    }

    public AntigravityCredentialSnapshot WithTokens(string access, string? refresh, string? id, DateTimeOffset expiry)
    {
        var root = JsonNode.Parse(RawJson)!.AsObject();
        var token = root["token"] as JsonObject ?? root;
        token["access_token"] = access;
        token["expiry"] = expiry.ToUniversalTime().ToString("O");
        if (token["expires_at"] is JsonValue previousExpiry)
        {
            if (previousExpiry.TryGetValue<long>(out var numeric))
                token["expires_at"] = numeric > 10_000_000_000L ? expiry.ToUnixTimeMilliseconds() : expiry.ToUnixTimeSeconds();
            else token["expires_at"] = expiry.ToUniversalTime().ToString("O");
        }
        if (!string.IsNullOrWhiteSpace(refresh)) token["refresh_token"] = refresh;
        if (!string.IsNullOrWhiteSpace(id))
        {
            if (root.ContainsKey("id_token") || ReferenceEquals(root, token)) root["id_token"] = id;
            else token["id_token"] = id;
        }
        return Parse(Location, root.ToJsonString());
    }

    private static (string? Email, string? Subject, string? Audience) Claims(string? jwt)
    {
        try
        {
            var part = jwt?.Split('.')[1].Replace('-', '+').Replace('_', '/');
            if (part is null) return default;
            part = part.PadRight((part.Length + 3) / 4 * 4, '=');
            using var doc = JsonDocument.Parse(Convert.FromBase64String(part));
            string? Text(string name) => doc.RootElement.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            return (Text("email"), Text("sub"), Text("aud"));
        }
        catch (Exception e) when (e is FormatException or JsonException or IndexOutOfRangeException or InvalidOperationException) { return default; }
    }
}

public interface IAntigravityCredentialStore
{
    AntigravityCredentialSnapshot? Read(AntigravityCredentialLocation location);
    bool TrySave(AntigravityCredentialSnapshot expected, AntigravityCredentialSnapshot replacement);
}

public sealed class WindowsAntigravityCredentialStore : IAntigravityCredentialStore
{
    private readonly object _writeLock = new();
    public AntigravityCredentialSnapshot? Read(AntigravityCredentialLocation location)
    {
        string? raw;
        if (location.FilePath is { } path)
        {
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > 1024 * 1024) throw new FormatException();
            raw = File.ReadAllText(path);
        }
        else
        {
            if (!OperatingSystem.IsWindows() || !CredReadW(location.TargetName, 1, 0, out var pointer)) return null;
            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
                if (credential.Size <= 0 || credential.Size > 1024 * 1024 || credential.Blob == IntPtr.Zero) return null;
                var bytes = new byte[credential.Size];
                Marshal.Copy(credential.Blob, bytes, 0, bytes.Length);
                raw = Decode(bytes);
            }
            finally { CredFree(pointer); }
        }
        return string.IsNullOrWhiteSpace(raw) ? null : AntigravityCredentialSnapshot.Parse(location, raw);
    }
    public bool TrySave(AntigravityCredentialSnapshot expected, AntigravityCredentialSnapshot replacement)
    {
        if (expected.Location.Key != replacement.Location.Key) return false;
        lock (_writeLock)
        {
            if (expected.Location.FilePath is { } path)
            {
                // Exclusive open prevents another process changing the file during comparison/write.
                using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                using var reader = new StreamReader(stream, Encoding.UTF8, true, 1024, leaveOpen: true);
                if (reader.ReadToEnd() != expected.RawJson) return false;
                var original = Encoding.UTF8.GetBytes(expected.RawJson);
                var bytes = Encoding.UTF8.GetBytes(replacement.RawJson);
                try { stream.Position = 0; stream.Write(bytes); stream.SetLength(bytes.Length); stream.Flush(true); }
                catch { try { stream.Position = 0; stream.Write(original); stream.SetLength(original.Length); stream.Flush(true); } catch { } throw; }
                return true;
            }
            if (!OperatingSystem.IsWindows() || !CredReadW(expected.Location.TargetName, 1, 0, out var pointer)) return false;
            try
            {
                var credential = Marshal.PtrToStructure<NativeCredential>(pointer);
                var original = new byte[credential.Size];
                Marshal.Copy(credential.Blob, original, 0, original.Length);
                var raw = Decode(original);
                var unicode = original.Length > 1 && (original[1] == 0 || original[0] == 0xff && original[1] == 0xfe);
                if (raw != expected.RawJson) return false; // Do not overwrite a newer sign-in.
                var bytes = (unicode ? Encoding.Unicode : Encoding.UTF8).GetBytes(replacement.RawJson);
                var blob = Marshal.AllocHGlobal(bytes.Length);
                try
                {
                    Marshal.Copy(bytes, 0, blob, bytes.Length);
                    credential.Blob = blob; credential.Size = bytes.Length;
                    return CredWriteW(ref credential, 0); // Preserve user, persistence, attributes and other metadata.
                }
                finally { Marshal.FreeHGlobal(blob); }
            }
            finally { CredFree(pointer); }
        }
    }
    private static string Decode(byte[] bytes) => (bytes.Length > 1 && (bytes[1] == 0 || bytes[0] == 0xff && bytes[1] == 0xfe)
        ? Encoding.Unicode.GetString(bytes) : Encoding.UTF8.GetString(bytes)).TrimStart('\uFEFF');
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags, Type;
        public string TargetName, Comment;
        public long LastWritten;
        public int Size;
        public IntPtr Blob;
        public int Persist, AttributeCount;
        public IntPtr Attributes;
        public string TargetAlias, UserName;
    }
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredReadW(string target, int type, int reserved, out IntPtr credential);
    [DllImport("Advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool CredWriteW(ref NativeCredential credential, int flags);
    [DllImport("Advapi32.dll")] private static extern void CredFree(IntPtr credential);
}
