using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static IntPtr _presenceTestForeground;
    private static async Task VerifyPresenceAsync()
    {
        var profile = Path.Combine(_output, "profile-" + Guid.NewGuid().ToString("N"));
        var settings = Path.Combine(profile, "claude-settings.json");
        Directory.CreateDirectory(profile);
        File.WriteAllText(settings, "{}");
        // This is the old state schema: an unpinned card must migrate to a pinned logo.
        WidgetStorage.Write(Path.Combine(profile, "window-state.json"), new { left = 200, top = 200, pinned = false, collapsed = false, viewMode = 0, animateTransitions = false });
        _presenceTestForeground = IntPtr.Zero;
        _window = new MainWindow(profile, testMode: true, claudeSettings: settings, foregroundWindow: () => _presenceTestForeground);
        _window.Show();
        await WaitAsync(() => _window.Ready, "Presence verification ready");
        Check(!_window.Topmost && !_window.ShowActivated, "Old card pin setting is preserved and startup does not request activation");
        await ScriptAsync("window.chrome.webview.postMessage({action:'collapse'})");
        await WaitAsync(() => _window.ViewMode == WidgetViewMode.Logo && _window.ActualWidth == 48, "Logo collapsed");
        var handle = new WindowInteropHelper(_window).Handle;
        Check(_window.Topmost && (PresenceGetWindowLongPtr(handle, -20).ToInt64() & 0x08000000) != 0,
            "Logo is topmost by default and has the native no-activation style");
        var handled = false;
        Check(_window.WindowProc(handle, 0x0021, IntPtr.Zero, IntPtr.Zero, ref handled).ToInt64() == 3 && handled,
            "Logo pointer interaction allows clicks and dragging without activation");
        await ScriptAsync("document.querySelector('.logo-shell').dispatchEvent(new MouseEvent('contextmenu',{bubbles:true,cancelable:true}))");
        var menu = GetPresenceField<Forms.ContextMenuStrip>("_presenceMenu");
        await WaitAsync(() => menu.Visible, "Logo right click menu");
        Check(menu.Items.OfType<Forms.ToolStripMenuItem>().Any(item => item.Text == "5분간 로고 숨기기")
            && menu.Items.OfType<Forms.ToolStripMenuItem>().Any(item => item.Text == "위젯 종료"), "Logo right click exposes hide, pin, fullscreen and exit controls");
        menu.Close();
        InvokePresence("PopulatePresenceMenu");
        menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == "로고 항상 위에 표시").PerformClick();
        Check(!_window.Topmost, "Native menu can unpin the logo");
        InvokePresence("ToggleLogoPin");
        await ScriptAsync("window.chrome.webview.postMessage({action:'expand'})");
        await WaitAsync(() => _window.ViewMode == WidgetViewMode.Expanded, "Card expanded");
        Check(!_window.Topmost && (PresenceGetWindowLongPtr(handle, -20).ToInt64() & 0x08000000) == 0,
            "Restoring a card restores its unpinned setting and normal activation style");
        await ScriptAsync("document.querySelector('[aria-label=\"설정\"]').click()");
        await Task.Delay(400);
        await ScriptAsync("document.querySelector('[aria-label=\"화면 설정\"]').click()");
        await Task.Delay(200);
        await AssertScriptAsync("document.querySelector('[aria-label=\"로고 항상 위에 표시\"]').getAttribute('aria-checked')==='true' && document.querySelector('[aria-label=\"전체 화면에서 로고 숨기기\"]').getAttribute('aria-checked')==='true'",
            "Card settings show independent logo and fullscreen preferences");
        await CaptureAsync("presence-settings.png");
        await AssertScriptAsync("document.querySelector('.menu').getBoundingClientRect().bottom < innerHeight", "Additional presence settings fit the native window");
        await ScriptAsync("document.querySelector('[aria-label=\"설정 닫기\"]').click();window.chrome.webview.postMessage({action:'collapse'})");
        await WaitAsync(() => _window.ViewMode == WidgetViewMode.Logo && _window.ActualWidth == 48, "Hide checks logo");
        menu.Show(Forms.Cursor.Position);
        menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == "5분간 로고 숨기기").PerformClick();
        menu.Close();
        await WaitAsync(() => !_window.IsVisible, "Native menu temporary hide");
        Check(!_window.IsVisible && GetPresenceField<Forms.NotifyIcon>("_tray").Visible, "Temporary hide keeps a recovery icon in the tray");
        InvokePresence("RestoreFromTray");
        Check(_window.IsVisible, "Tray restoration immediately cancels temporary hiding");
        InvokePresence("HideLogoTemporarily");
        SetPresenceField("_hiddenUntil", DateTimeOffset.UtcNow.AddSeconds(-1));
        InvokePresence("UpdatePresence");
        Check(_window.IsVisible, "Expired temporary hiding restores the logo automatically");

        var monitor = new PresenceMonitorInfo { Size = Marshal.SizeOf<PresenceMonitorInfo>() };
        PresenceGetMonitorInfo(PresenceMonitorFromWindow(handle, 2), ref monitor);
        var bounds = monitor.Monitor;
        Check(!MainWindow.CoversMonitor(bounds, bounds, true), "A maximized captioned window is not treated as fullscreen");
        Check(!MainWindow.CoversMonitor(monitor.Work, bounds, true), "Ordinary work-area windows do not hide the logo");
        await VerifyFullscreenPresenceAsync(bounds);

        InvokePresence("ToggleLogoPin");
        InvokePresence("ToggleFullscreenHide");
        _window.Close();
        using (var state = JsonDocument.Parse(File.ReadAllText(Path.Combine(profile, "window-state.json"))))
            Check(!state.RootElement.GetProperty("pinned").GetBoolean() && !state.RootElement.GetProperty("logoPinned").GetBoolean()
                && !state.RootElement.GetProperty("hideLogoInFullscreen").GetBoolean(), "Card pin, logo pin and fullscreen hiding persist independently");
        _window = new MainWindow(profile, testMode: true, claudeSettings: settings, foregroundWindow: () => _presenceTestForeground);
        _window.Show();
        await WaitAsync(() => _window.Ready, "Restarted presence preferences");
        Check(_window.ViewMode == WidgetViewMode.Logo && !_window.Topmost && _window.IsVisible,
            "Restart restores the logo view and the user's unpinned preference without a temporary hide");
        await ScriptAsync("window.chrome.webview.postMessage({action:'expand'})");
        await WaitAsync(() => _window.ViewMode == WidgetViewMode.Expanded, "Restarted card");
        Check(!_window.Topmost, "Restart also retains the independent card pin preference");
        var tray = GetPresenceField<Forms.NotifyIcon>("_tray");
        _window.Close();
        Check(!tray.Visible, "Closing the widget removes its tray icon");
        _window = null;
    }

    private static async Task VerifyFullscreenPresenceAsync(MainWindow.NativeRect bounds)
    {
        var title = "AI Usage presence fixture " + Guid.NewGuid().ToString("N");
        var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
        foreach (var arg in new[] { "--presence-fixture", title, bounds.Left.ToString(), bounds.Top.ToString(),
            (bounds.Right - bounds.Left).ToString(), (bounds.Bottom - bounds.Top).ToString() }) start.ArgumentList.Add(arg);
        using var fixture = Process.Start(start)!;
        try
        {
            IntPtr foreground = IntPtr.Zero;
            await WaitAsync(() => (foreground = PresenceFindWindow(null, title)) != IntPtr.Zero, "Fullscreen fixture opened");
            _presenceTestForeground = foreground;
            await WaitAsync(() => !_window!.IsVisible, "Same-monitor fullscreen hides the logo");
            Check(true, "A real fullscreen window selected by the foreground probe hides the logo on its monitor");
            var menu = GetPresenceField<Forms.ContextMenuStrip>("_presenceMenu");
            var nativeForeground = PresenceGetForegroundWindow();
            menu.Show(Forms.Cursor.Position);
            menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == "다시 표시").PerformClick();
            menu.Close();
            await Task.Delay(750);
            Check(_window!.IsVisible && PresenceGetForegroundWindow() == nativeForeground, "Explicit tray restoration stays visible over fullscreen without taking focus");
            // Clear the explicit override by toggling the user's setting off and back on.
            InvokePresence("ToggleFullscreenHide");
            Check(_window.IsVisible && PresenceGetForegroundWindow() == nativeForeground, "Disabling fullscreen hiding leaves the logo visible without activation");
            InvokePresence("ToggleFullscreenHide");
            Check(!_window.IsVisible, "Re-enabling fullscreen hiding immediately hides the logo");
            var other = new Window { Title = "Ordinary presence fixture", Width = 120, Height = 100, ShowActivated = false };
            other.Show();
            try
            {
                var ordinaryHandle = new WindowInteropHelper(other).Handle;
                _presenceTestForeground = ordinaryHandle;
                await WaitAsync(() => _window.IsVisible, "Logo restores after fullscreen loses focus");
                Check(PresenceGetForegroundWindow() == nativeForeground, "Automatic restoration leaves actual keyboard focus unchanged");
            }
            finally { other.Close(); }
        }
        finally
        {
            _presenceTestForeground = IntPtr.Zero;
            if (!fixture.HasExited) { fixture.CloseMainWindow(); await Task.Delay(200); }
            if (!fixture.HasExited) fixture.Kill();
        }
    }

    private static int RunPresenceFixture(string[] args)
    {
        var app = new Application();
        var window = new Window { Title = args[1], WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize, ShowActivated = false,
            Background = new SolidColorBrush(Color.FromRgb(24, 28, 25)) };
        window.Loaded += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            PresenceSetWindowPos(handle, IntPtr.Zero, int.Parse(args[2]), int.Parse(args[3]), int.Parse(args[4]), int.Parse(args[5]), 0x14);
        };
        app.Run(window);
        return 0;
    }

    private static T GetPresenceField<T>(string name) => (T)typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(_window)!;
    private static void SetPresenceField(string name, object value) => typeof(MainWindow).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(_window, value);
    private static void InvokePresence(string name) => typeof(MainWindow).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(_window, null);

    [StructLayout(LayoutKind.Sequential)] private struct PresenceMonitorInfo { public int Size; public MainWindow.NativeRect Monitor, Work; public uint Flags; }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr PresenceGetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "GetForegroundWindow")] private static extern IntPtr PresenceGetForegroundWindow();
    [DllImport("user32.dll", EntryPoint = "FindWindowW", CharSet = CharSet.Unicode)] private static extern IntPtr PresenceFindWindow(string? className, string title);
    [DllImport("user32.dll", EntryPoint = "MonitorFromWindow")] private static extern IntPtr PresenceMonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW")] private static extern bool PresenceGetMonitorInfo(IntPtr monitor, ref PresenceMonitorInfo info);
    [DllImport("user32.dll", EntryPoint = "SetWindowPos")] private static extern bool PresenceSetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
