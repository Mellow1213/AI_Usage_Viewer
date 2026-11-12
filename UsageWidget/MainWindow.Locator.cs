using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace UsageWidget;

public partial class MainWindow
{
    internal const int LocatorHotkeyId = 0x4155;
    private const string LocatorShortcut = "Ctrl+Alt+L";
    private bool _locatorHotkeyRegistered;
    private bool _locatorPending;
    private LogoLocatorWindow? _logoLocator;
    private bool IsLogoLocatorActive => _locatorPending || _logoLocator is not null;

    private void InitializeLogoLocator()
    {
        // Other verification windows must not compete for the user's global shortcut.
        if (!_testMode) RegisterLocatorHotkey();
    }

    private void RegisterLocatorHotkey()
    {
        if (_locatorHotkeyRegistered) return;
        // MOD_CONTROL | MOD_ALT | MOD_NOREPEAT. Never steal an existing registration.
        _locatorHotkeyRegistered = RegisterHotKey(new WindowInteropHelper(this).Handle, LocatorHotkeyId, 0x4003, 0x4C);
        if (!_locatorHotkeyRegistered)
            WidgetStorage.Log($"Logo locator shortcut unavailable (Win32 {Marshal.GetLastWin32Error()}); tray action remains available.");
    }

    private void FindLogo()
    {
        if (_closed) return;
        _locatorPending = true;
        // Close the menu first so its transient foreground window cannot delay restoration.
        _presenceMenu?.Close();
        RestoreFromTray();
        var handle = new WindowInteropHelper(this).Handle;
        ShowWindow(handle, 4); // SW_SHOWNOACTIVATE also restores a minimized widget.
        ApplyPresencePolicy();
        // An explicit request raises even an unpinned widget, without moving it or taking focus.
        SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, 0x53); // TOPMOST, NOMOVE | NOSIZE | NOACTIVATE | SHOWWINDOW
        TryShowLogoLocator();
    }

    private void TryShowLogoLocator()
    {
        if (!_locatorPending || _closed || !_ready || _transitionPhase != "settled" || IsResizing) return;
        var previous = _logoLocator;
        var locator = new LogoLocatorWindow(this, _viewMode == WidgetViewMode.Logo,
            _reducedMotion || !SystemParameters.ClientAreaAnimation);
        _logoLocator = locator;
        _locatorPending = false;
        previous?.Close();
        locator.Closed += (_, _) =>
        {
            if (_logoLocator != locator) return;
            _logoLocator = null;
            if (!_closed) ApplyPresencePolicy();
        };
        locator.Show();
    }

    private void StopLogoLocator()
    {
        _locatorPending = false;
        var locator = _logoLocator;
        _logoLocator = null;
        locator?.Close();
        if (!_closed) ApplyPresencePolicy();
    }

    private void DisposeLogoLocator()
    {
        StopLogoLocator();
        if (!_locatorHotkeyRegistered) return;
        UnregisterHotKey(new WindowInteropHelper(this).Handle, LocatorHotkeyId);
        _locatorHotkeyRegistered = false;
    }

    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr window, int id);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
}
