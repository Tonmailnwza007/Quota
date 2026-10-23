using System.Runtime.Versioning;
using Microsoft.Win32;

namespace Quota.Native;

[SupportedOSPlatform("windows")]
internal sealed class WindowsStartup(string executablePath, string registryPath = @"Software\Microsoft\Windows\CurrentVersion\Run")
{
    internal const string ValueName = "fmdz.Quota";

    internal static string CommandFor(string executablePath)
    {
        if (!Path.IsPathFullyQualified(executablePath) || executablePath.Any(c => c == '"' || char.IsControl(c)) ||
            !string.Equals(Path.GetFileName(executablePath), "quota.exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Startup requires the Quota executable.");
        var command = $"\"{executablePath}\"";
        if (command.Length > 260) throw new ArgumentException("The startup command is too long.");
        return command;
    }

    internal bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(registryPath);
        return string.Equals(key?.GetValue(ValueName) as string, CommandFor(executablePath), StringComparison.OrdinalIgnoreCase);
    }

    internal bool SetEnabled(bool enabled)
    {
        if (enabled)
        {
            var command = CommandFor(executablePath);
            using var key = Registry.CurrentUser.CreateSubKey(registryPath, writable: true);
            key.SetValue(ValueName, command, RegistryValueKind.String);
        }
        else
        {
            using var key = Registry.CurrentUser.OpenSubKey(registryPath, writable: true);
            key?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
        return IsEnabled();
    }
}
