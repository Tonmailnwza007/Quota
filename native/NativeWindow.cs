using System.Runtime.InteropServices;
using System.Text;

namespace Quota.Native;

internal sealed class NativeWindow : Form
{
    private const long WsPopup = 0x80000000L;
    private const int WsCaption = 0x00C00000;
    private const int WsThickFrame = 0x00040000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExAppWindow = 0x00040000;
    private Point _anchor;
    private int _contentHeight = 520;
    private bool _roundedCornersRequested;
    public event Action? HotkeyPressed;
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool GuardDeactivation { get; set; }
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public bool SmokeMode { get; init; }

    public NativeWindow()
    {
        Text = "Quota";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(360, 520);
        BackColor = Color.FromArgb(32, 32, 32);
        Deactivate += (_, _) => { if (!GuardDeactivation && !SmokeMode) Hide(); };
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        try
        {
            var rounded = 2; // DWMWCP_ROUND; preserves native popup/tool-window styles.
            _roundedCornersRequested = DwmSetWindowAttribute(Handle, 33, ref rounded, sizeof(int)) == 0;
            var dark = 1;
            DwmSetWindowAttribute(Handle, 20, ref dark, sizeof(int));
            var borderColor = 0x00383838;
            DwmSetWindowAttribute(Handle, 34, ref borderColor, sizeof(int));
            var margins = new Margins { Left = 1, Right = 1, Top = 1, Bottom = 1 };
            DwmExtendFrameIntoClientArea(Handle, ref margins);
        }
        catch (DllNotFoundException) { }
        catch (EntryPointNotFoundException) { }
    }

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.Style = unchecked((int)WsPopup) | 0x02000000 | 0x04000000; // POPUP, CLIPCHILDREN, CLIPSIBLINGS
            parameters.Style &= ~(WsCaption | WsThickFrame);
            parameters.ExStyle = (parameters.ExStyle | WsExToolWindow) & ~WsExAppWindow;
            return parameters;
        }
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x0312 && message.WParam.ToInt32() == 0x5155) HotkeyPressed?.Invoke();
        base.WndProc(ref message);
    }

    public void ToggleAt(Point trayClick)
    {
        if (SmokeMode) return;
        if (Visible) { Hide(); return; }
        PositionAt(trayClick);
        Show();
        Activate();
    }

    public void PositionAt(Point anchor)
    {
        _anchor = anchor;
        var screen = Screen.FromPoint(anchor);
        var area = screen.WorkingArea;
        var dpi = MonitorDpi(anchor);
        var margin = Math.Max(4, (int)Math.Round(8 * dpi / 96d));
        var width = Math.Min((int)Math.Round(360 * dpi / 96d), Math.Max(1, area.Width - margin * 2));
        var height = Math.Min((int)Math.Round(_contentHeight * dpi / 96d), Math.Max(1, area.Height - margin * 2));
        var x = anchor.X - width + (int)Math.Round(20 * dpi / 96d);
        var y = anchor.Y >= area.Bottom ? area.Bottom - height - margin : anchor.Y - height - margin;
        if (anchor.Y <= area.Top) y = area.Top + margin;
        x = Math.Clamp(x, area.Left + margin, Math.Max(area.Left + margin, area.Right - width - margin));
        y = Math.Clamp(y, area.Top + margin, Math.Max(area.Top + margin, area.Bottom - height - margin));
        Bounds = new Rectangle(x, y, width, height);
    }

    public void FitContent(int height)
    {
        // Enforce fixed normal height 520 DIPs across collapsing, unfolding, provider switches, and settings.
        const int fixedHeight = 520;
        if (_contentHeight == fixedHeight) return;
        _contentHeight = fixedHeight;
        PositionAt(_anchor);
    }

    public object Inspect()
    {
        var style = GetWindowLongPtr(Handle, -16).ToInt64();
        var extended = GetWindowLongPtr(Handle, -20).ToInt64();
        var name = new StringBuilder(256);
        GetClassName(Handle, name, name.Capacity);
        GetWindowRect(Handle, out var rectangle);
        return new
        {
            hwnd = Handle.ToInt64().ToString("X"), className = name.ToString(),
            style = $"0x{unchecked((uint)style):X8}", extendedStyle = $"0x{unchecked((uint)extended):X8}",
            popup = (style & WsPopup) != 0, toolWindow = (extended & WsExToolWindow) != 0,
            caption = (style & WsCaption) != 0, thickFrame = (style & WsThickFrame) != 0,
            appWindow = (extended & WsExAppWindow) != 0,
            visible = IsWindowVisible(Handle), isChromeTopLevel = name.ToString().Equals("Chrome_WidgetWin_1", StringComparison.Ordinal),
            width = rectangle.Right - rectangle.Left, height = rectangle.Bottom - rectangle.Top, deviceDpi = DeviceDpi,
            preferredContentHeight = _contentHeight, roundedCornersRequested = _roundedCornersRequested,
            withinWorkArea = Screen.FromPoint(_anchor).WorkingArea.Contains(Bounds)
        };
    }

    public bool HasSafePopupStyles()
    {
        var style = GetWindowLongPtr(Handle, -16).ToInt64();
        var extended = GetWindowLongPtr(Handle, -20).ToInt64();
        var name = new StringBuilder(256);
        GetClassName(Handle, name, name.Capacity);
        return (style & WsPopup) != 0 && (style & (WsCaption | WsThickFrame)) == 0 && (extended & WsExToolWindow) != 0 &&
            (extended & WsExAppWindow) == 0 && name.ToString() != "Chrome_WidgetWin_1" && (!SmokeMode || !IsWindowVisible(Handle));
    }

    private static uint MonitorDpi(Point point)
    {
        try
        {
            var monitor = MonitorFromPoint(point, 2);
            return GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi : 96;
        }
        catch { return 96; }
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder buffer, int length);
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [StructLayout(LayoutKind.Sequential)] private struct WindowRectangle { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr window, out WindowRectangle rectangle);
    [DllImport("user32.dll")] private static extern IntPtr MonitorFromPoint(Point point, uint flags);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [StructLayout(LayoutKind.Sequential)] private struct Margins { public int Left, Right, Top, Bottom; }
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmExtendFrameIntoClientArea(IntPtr window, ref Margins margins);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr window, int id);
}
