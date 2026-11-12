using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using UsageWidget;
using Forms = System.Windows.Forms;

internal static partial class Program
{
    private static async Task VerifyLogoLocatorAsync()
    {
        var window = _window!;
        var handle = new WindowInteropHelper(window).Handle;
        var menu = GetPresenceField<Forms.ContextMenuStrip>("_presenceMenu");
        InvokePresence("ToggleLogoPin");
        Check(!window.Topmost, "Locator fixture starts with an unpinned logo");
        PresenceGetWindowRect(handle, out var before);
        var saved = File.ReadAllText(Path.Combine(GetPresenceField<string>("_dataDirectory"), "window-state.json"));
        // The verification profile normally skips hotkeys; exercise registration explicitly here.
        InvokePresence("RegisterLocatorHotkey");
        InvokePresence("PopulatePresenceMenu");
        var find = menu.Items.OfType<Forms.ToolStripMenuItem>().Single(item => item.Text == "로고 찾기");
        Check(find.Enabled, "Tray locator stays available even if another process owns the shortcut");
        var registered = GetPresenceField<bool>("_locatorHotkeyRegistered");
        Check(registered ? find.ShortcutKeyDisplayString == "Ctrl+Alt+L"
            : menu.Items.OfType<Forms.ToolStripMenuItem>().Any(item => !item.Enabled && item.Text?.Contains("단축키 등록 안 됨") == true),
            "Tray reports the actual global shortcut registration status");

        SetPresenceField("_reducedMotion", false);
        InvokePresence("HideLogoTemporarily");
        // Let the earlier native menu and hiding finish restoring their foreground window.
        await Task.Delay(250);
        var foreground = PresenceGetForegroundWindow();
        using var activation = new LocatorActivationProbe();
        find.PerformClick();
        await WaitAsync(() => GetPresenceField<LogoLocatorWindow?>("_logoLocator")?.IsVisible == true, "Tray locator visible");
        var first = GetPresenceField<LogoLocatorWindow>("_logoLocator");
        var ringHandle = new WindowInteropHelper(first).Handle;
        Check(window.IsVisible && window.Topmost && GetPresenceField<DateTimeOffset?>("_hiddenUntil") is null,
            "Find logo cancels temporary hiding and temporarily raises an unpinned widget");
        Check(window.ViewMode == WidgetViewMode.Logo && PresenceGetForegroundWindow() == foreground,
            "Tray locator preserves the current view and keyboard focus");
        Check((PresenceGetWindowLongPtr(ringHandle, -20).ToInt64() & 0x080000A0) == 0x080000A0
            && !first.ShowInTaskbar && !first.IsHitTestVisible,
            "Locator overlay is non-activating, click-through and excluded from task switching");
        PresenceGetWindowRect(handle, out var after);
        Check(before.Left == after.Left && before.Top == after.Top && before.Right == after.Right && before.Bottom == after.Bottom,
            "Finding a logo preserves its native bounds");
        PresenceGetWindowRect(ringHandle, out var ringBounds);
        Check(Math.Abs((ringBounds.Left + ringBounds.Right) - (after.Left + after.Right)) <= 2
            && Math.Abs((ringBounds.Top + ringBounds.Bottom) - (after.Top + after.Bottom)) <= 2,
            "Locator ring is centered on the actual logo in physical screen pixels");
        await Task.Delay(260);
        await CaptureAsync("locator-logo.png");
        CaptureLocatorRing(first, "locator-ring.png");
        var handled = false;
        window.WindowProc(handle, 0x0312, new IntPtr(MainWindow.LocatorHotkeyId), IntPtr.Zero, ref handled);
        Check(handled && !first.IsVisible && GetPresenceField<LogoLocatorWindow>("_logoLocator") != first,
            "Global hotkey message restarts one highlight instead of stacking overlays");
        await WaitAsync(() => GetPresenceField<LogoLocatorWindow?>("_logoLocator") is null, "Locator expires");
        Check(!window.Topmost, "Highlight expiry restores the unpinned policy");
        Check(!activation.Activated, "Locator never activates a widget or overlay while the user may switch other applications");
        Check(File.ReadAllText(Path.Combine(GetPresenceField<string>("_dataDirectory"), "window-state.json")) == saved,
            "Finding a logo does not rewrite saved position or pin preferences");

        SetPresenceField("_reducedMotion", true);
        InvokePresence("FindLogo");
        var reduced = GetPresenceField<LogoLocatorWindow>("_logoLocator");
        await Task.Delay(100);
        CaptureLocatorRing(reduced, "locator-ring-reduced-motion.png");
        InvokePresence("HideLogoTemporarily");
        Check(!reduced.IsVisible && !window.IsVisible && !window.Topmost,
            "Explicit hiding cancels a running locator and restores the original pin policy");
        InvokePresence("RestoreFromTray");
        InvokePresence("ToggleLogoPin");
    }

    private static void CaptureLocatorRing(LogoLocatorWindow locator, string name)
    {
        var visual = (FrameworkElement)locator.Content;
        var bitmap = new RenderTargetBitmap((int)Math.Ceiling(visual.ActualWidth), (int)Math.Ceiling(visual.ActualHeight),
            96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var file = File.Create(Path.Combine(_output, name));
        encoder.Save(file);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowRect")] private static extern bool PresenceGetWindowRect(IntPtr window, out MainWindow.NativeRect rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowThreadProcessId")] private static extern uint PresenceWindowProcess(IntPtr window, out uint process);

    // Observe actual foreground transitions instead of treating normal user activity on the live
    // desktop as a focus-stealing regression. WinForms context-menu activation is intentional.
    private sealed class LocatorActivationProbe : IDisposable
    {
        private readonly ForegroundEvent _callback;
        private readonly IntPtr _hook;
        internal bool Activated { get; private set; }

        internal LocatorActivationProbe()
        {
            _callback = (_, _, window, _, _, _, _) =>
            {
                PresenceWindowProcess(window, out var process);
                if (process != Environment.ProcessId) return;
                var name = new StringBuilder(256);
                LocatorGetClassName(window, name, name.Capacity);
                if (name.ToString().StartsWith("HwndWrapper", StringComparison.Ordinal)) Activated = true;
            };
            _hook = LocatorSetWinEventHook(3, 3, IntPtr.Zero, _callback, (uint)Environment.ProcessId, 0, 0);
            if (_hook == IntPtr.Zero) throw new InvalidOperationException("Could not observe locator foreground events.");
        }

        public void Dispose() { LocatorUnhookWinEvent(_hook); GC.KeepAlive(_callback); }
    }

    private delegate void ForegroundEvent(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint threadId, uint time);
    [DllImport("user32.dll", EntryPoint = "SetWinEventHook")] private static extern IntPtr LocatorSetWinEventHook(uint min, uint max, IntPtr module, ForegroundEvent callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll", EntryPoint = "UnhookWinEvent")] private static extern bool LocatorUnhookWinEvent(IntPtr hook);
    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode)] private static extern int LocatorGetClassName(IntPtr window, StringBuilder name, int capacity);
}
