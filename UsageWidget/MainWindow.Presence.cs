using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace UsageWidget;

public partial class MainWindow
{
    private const long NoActivateStyle = 0x08000000;
    private readonly Func<IntPtr> _foregroundWindow;
    private bool _cardPinned = true, _logoPinned = true, _hideLogoInFullscreen = true;
    private bool _presenceHidden;
    private DateTimeOffset? _hiddenUntil;
    private IntPtr _fullscreenOverrideWindow;
    private IntPtr _lastFullscreenWindow;
    private readonly DispatcherTimer _presenceTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private Forms.NotifyIcon? _tray;
    private Forms.ContextMenuStrip? _presenceMenu;
    private System.Drawing.Icon? _trayIcon;

    private void InitializePresence()
    {
        ApplyPresencePolicy();
        // Use the existing triangle resource for both the window and recovery control.
        using var resource = Application.GetResourceStream(new Uri("pack://application:,,,/AIUsageWidget;component/Assets/usage-widget.ico"))!.Stream;
        _trayIcon = new System.Drawing.Icon(resource);
        _presenceMenu = new Forms.ContextMenuStrip();
        _presenceMenu.Opening += (_, e) => { PopulatePresenceMenu(); e.Cancel = false; };
        _tray = new Forms.NotifyIcon { Icon = _trayIcon, Text = "AI Usage · 클릭해서 표시", ContextMenuStrip = _presenceMenu, Visible = true };
        _tray.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) RestoreFromTray();
        };
        _presenceTimer.Tick += (_, _) => { if (_ready && !_closed) UpdatePresence(); };
        _presenceTimer.Start();
    }

    private void ApplyPresencePolicy()
    {
        var logo = _viewMode == WidgetViewMode.Logo;
        // Topmost is a placement policy; never activate the window to enforce it.
        Topmost = logo ? _logoPinned : _cardPinned;
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == IntPtr.Zero) return;
        var previous = GetWindowLongPtr(handle, -20).ToInt64(); // GWL_EXSTYLE
        var next = logo ? previous | NoActivateStyle : previous & ~NoActivateStyle;
        if (next != previous) SetWindowLongPtr(handle, -20, new IntPtr(next));
    }

    private void ToggleCardPin()
    {
        _cardPinned = !_cardPinned;
        ApplyPresencePolicy(); SavePosition(); Publish();
    }

    private void ToggleLogoPin()
    {
        _logoPinned = !_logoPinned;
        ApplyPresencePolicy(); SavePosition(); Publish();
    }

    private void ToggleFullscreenHide()
    {
        _hideLogoInFullscreen = !_hideLogoInFullscreen;
        _fullscreenOverrideWindow = IntPtr.Zero;
        UpdatePresence(); SavePosition(); Publish();
    }

    private void HideLogoTemporarily()
    {
        if (_viewMode != WidgetViewMode.Logo) return;
        _hiddenUntil = DateTimeOffset.UtcNow.AddMinutes(5);
        UpdatePresence();
    }

    private void RestoreFromTray()
    {
        if (_closed) return;
        _hiddenUntil = null;
        // An explicit request can show the logo over this fullscreen app until focus changes.
        _fullscreenOverrideWindow = _lastFullscreenWindow != IntPtr.Zero ? _lastFullscreenWindow : _foregroundWindow();
        UpdatePresence();
        if (!IsVisible) Show();
    }

    private void UpdatePresence()
    {
        if (_closed || _presenceMenu?.Visible == true) return;
        var foreground = _foregroundWindow();
        var transient = IsPresenceOrTrayWindow(foreground);
        if (_fullscreenOverrideWindow != foreground && !transient) _fullscreenOverrideWindow = IntPtr.Zero;
        if (_hiddenUntil <= DateTimeOffset.UtcNow) _hiddenUntil = null;
        var fullscreen = _viewMode == WidgetViewMode.Logo && _hideLogoInFullscreen && IsFullscreenOnWidgetMonitor(foreground);
        if (fullscreen) _lastFullscreenWindow = foreground;
        else if (!transient) _lastFullscreenWindow = IntPtr.Zero;
        var hide = _viewMode == WidgetViewMode.Logo && _transitionPhase == "settled" && !IsResizing
            && (_hiddenUntil is not null || (_hideLogoInFullscreen && _fullscreenOverrideWindow != foreground
                && fullscreen));
        if (hide == _presenceHidden) return;
        _presenceHidden = hide;
        if (hide) Hide();
        else Show(); // ShowActivated=False also applies to automatic restoration.
        if (_tray is not null) _tray.Text = hide ? "AI Usage · 로고 숨김 · 클릭해서 표시" : "AI Usage · 클릭해서 표시";
    }

    private bool IsFullscreenOnWidgetMonitor(IntPtr foreground)
    {
        if (foreground == IntPtr.Zero || foreground == GetShellWindow() || foreground == GetDesktopWindow()
            || IsIconic(foreground) || !IsWindowVisible(foreground)) return false;
        GetWindowThreadProcessId(foreground, out var process);
        if (process == Environment.ProcessId) return false;
        var widgetMonitor = MonitorFromWindow(new WindowInteropHelper(this).Handle, 2);
        if (MonitorFromWindow(foreground, 2) != widgetMonitor) return false;
        var className = new System.Text.StringBuilder(256);
        GetClassName(foreground, className, className.Capacity);
        if (className.ToString() is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd") return false;
        // A maximized document window is still ordinary work, even with an auto-hidden taskbar.
        var hasCaption = (GetWindowLongPtr(foreground, -16).ToInt64() & 0x00C00000) != 0;
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        return GetWindowRect(foreground, out var bounds) && GetMonitorInfo(widgetMonitor, ref info)
            && CoversMonitor(bounds, info.Monitor, hasCaption);
    }

    private static bool IsPresenceOrTrayWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out var process);
        if (process == Environment.ProcessId) return true;
        var className = new System.Text.StringBuilder(256);
        GetClassName(window, className, className.Capacity);
        return className.ToString() is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd" or "NotifyIconOverflowWindow";
    }

    internal static bool CoversMonitor(NativeRect bounds, NativeRect monitor, bool hasCaption) => !hasCaption
        && Math.Abs((long)bounds.Left - monitor.Left) <= 1 && Math.Abs((long)bounds.Top - monitor.Top) <= 1
        && Math.Abs((long)bounds.Right - monitor.Right) <= 1 && Math.Abs((long)bounds.Bottom - monitor.Bottom) <= 1;

    private void ShowLogoMenu()
    {
        if (_closed || _viewMode != WidgetViewMode.Logo || _presenceMenu is null || _presenceMenu.Visible) return;
        _presenceMenu.Show(Forms.Cursor.Position);
    }

    private void PopulatePresenceMenu()
    {
        if (_presenceMenu is null) return;
        // Dispose old items so reopening the menu does not retain click delegates.
        foreach (var item in _presenceMenu.Items.Cast<Forms.ToolStripItem>().ToArray()) item.Dispose();
        _presenceMenu.Items.Clear();
        void Add(string text, Action action, bool? check = null, bool enabled = true)
        {
            var item = new Forms.ToolStripMenuItem(text) { Checked = check == true, Enabled = enabled };
            item.Click += (_, _) => action();
            _presenceMenu.Items.Add(item);
        }
        Add("다시 표시", RestoreFromTray);
        Add("이전 카드로 펼치기", () => { _hiddenUntil = null; SetViewMode(_lastCardView); UpdatePresence(); });
        _presenceMenu.Items.Add(new Forms.ToolStripSeparator());
        Add("로고 항상 위에 표시", ToggleLogoPin, _logoPinned);
        Add("전체 화면에서 로고 숨기기", ToggleFullscreenHide, _hideLogoInFullscreen);
        Add("5분간 로고 숨기기", HideLogoTemporarily, enabled: _viewMode == WidgetViewMode.Logo);
        _presenceMenu.Items.Add(new Forms.ToolStripSeparator());
        Add("위젯 종료", Close);
    }

    private void DisposePresence()
    {
        _presenceTimer.Stop();
        if (_tray is not null) { _tray.Visible = false; _tray.Dispose(); }
        _presenceMenu?.Dispose();
        _trayIcon?.Dispose();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetDesktopWindow();
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int capacity);
}
