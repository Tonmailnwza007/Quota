using System.Runtime.Versioning;
using Microsoft.Win32;
using Quota.Native;

namespace Costats.Bridge.Tests;

[SupportedOSPlatform("windows")]
public sealed class WindowsStartupTests
{
    [Fact]
    public void Toggle_QuotesPortablePath_PersistsAndRemovesOnlyQuotaValue()
    {
        if (!OperatingSystem.IsWindows()) return;
        var testKey = @"Software\Quota.Tests\" + Guid.NewGuid().ToString("N");
        try
        {
            var startup = new WindowsStartup(@"C:\Apps With Spaces\Quota\quota.exe", testKey);
            Assert.False(startup.IsEnabled());
            using (var key = Registry.CurrentUser.CreateSubKey(testKey)) key.SetValue("unrelated", "preserve");
            Assert.True(startup.SetEnabled(true));
            using (var key = Registry.CurrentUser.OpenSubKey(testKey))
                Assert.Equal("\"C:\\Apps With Spaces\\Quota\\quota.exe\"", key!.GetValue(WindowsStartup.ValueName));
            Assert.True(new WindowsStartup(@"C:\Apps With Spaces\Quota\quota.exe", testKey).IsEnabled());
            Assert.False(new WindowsStartup(@"C:\Other\quota.exe", testKey).IsEnabled());
            Assert.False(startup.SetEnabled(false));
            Assert.False(startup.SetEnabled(false));
            using var remaining = Registry.CurrentUser.OpenSubKey(testKey);
            Assert.Null(remaining!.GetValue(WindowsStartup.ValueName));
            Assert.Equal("preserve", remaining.GetValue("unrelated"));
        }
        finally { Registry.CurrentUser.DeleteSubKeyTree(testKey, throwOnMissingSubKey: false); }
    }

    [Theory]
    [InlineData("quota.exe")]
    [InlineData(@"C:\dotnet\dotnet.exe")]
    [InlineData("C:\\bad\"path\\quota.exe")]
    [InlineData("C:\\bad\npath\\quota.exe")]
    public void InvalidExecutableCannotBeRegistered(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Throws<ArgumentException>(() => WindowsStartup.CommandFor(path));
    }

    [Fact]
    public void Command_RespectsWindowsRunLimit()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal(260, WindowsStartup.CommandFor("C:\\" + new string('a', 245) + "\\quota.exe").Length);
        Assert.Throws<ArgumentException>(() => WindowsStartup.CommandFor("C:\\" + new string('a', 246) + "\\quota.exe"));
    }
}
