$ErrorActionPreference = 'Stop'
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class QuotaFlyoutStyles {
  [DllImport("user32.dll", EntryPoint="GetWindowLongPtrW")] public static extern IntPtr GetStyle(IntPtr hwnd, int index);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hwnd);
  [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
}
'@
$taskHandle = [IntPtr]::new([long]::Parse($env:QUOTA_INSPECT_HWND))
if (-not [QuotaFlyoutStyles]::IsWindow($taskHandle)) { throw 'Native flyout handle is invalid.' }
[uint32]$taskOwnerPid = 0
[void][QuotaFlyoutStyles]::GetWindowThreadProcessId($taskHandle, [ref]$taskOwnerPid)
if ($taskOwnerPid -ne [uint32]::Parse($env:QUOTA_INSPECT_PID)) { throw 'Native flyout owner does not match.' }
$taskStyle = [QuotaFlyoutStyles]::GetStyle($taskHandle, -16).ToInt64() -band 0xffffffffL
$taskExtended = [QuotaFlyoutStyles]::GetStyle($taskHandle, -20).ToInt64() -band 0xffffffffL
[pscustomobject]@{
  style = ('0x{0:X8}' -f $taskStyle)
  extendedStyle = ('0x{0:X8}' -f $taskExtended)
  toolWindow = ($taskExtended -band 0x80L) -ne 0
  appWindow = ($taskExtended -band 0x40000L) -ne 0
  caption = ($taskStyle -band 0xc00000L) -ne 0
  thickFrame = ($taskStyle -band 0x40000L) -ne 0
  windowEdge = ($taskExtended -band 0x100L) -ne 0
} | ConvertTo-Json -Compress
