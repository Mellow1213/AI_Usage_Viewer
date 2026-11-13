using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Web.WebView2.Core;

namespace UsageWidget;

public partial class MainWindow
{
    private static readonly TimeSpan LogoMemoryDelay = TimeSpan.FromSeconds(5);
    private long? _settledLogoSince;
    private bool _webViewMemoryLow;
    private bool _webViewMemoryUnavailable;

    private void UpdateWebViewMemoryTarget()
    {
        if (!_ready || _closed) return;

        if (_viewMode == WidgetViewMode.Logo && _transitionPhase == "settled" && !IsResizing)
            _settledLogoSince ??= Stopwatch.GetTimestamp();
        else
            _settledLogoSince = null;

        var useLowMemory = _settledLogoSince is { } since && Stopwatch.GetElapsedTime(since) >= LogoMemoryDelay;
        if (useLowMemory == _webViewMemoryLow || (_webViewMemoryUnavailable && useLowMemory) || WebView is not { } web) return;

        try
        {
            // Low keeps scripts running, including the logo animation and its click handler.
            web.MemoryUsageTargetLevel = useLowMemory
                ? CoreWebView2MemoryUsageTargetLevel.Low : CoreWebView2MemoryUsageTargetLevel.Normal;
            _webViewMemoryLow = useLowMemory;
        }
        catch (Exception ex) when (ex is NotImplementedException or COMException)
        {
            if (!_webViewMemoryUnavailable) WidgetStorage.Log("WebView2 memory target could not be changed", ex);
            _webViewMemoryUnavailable = true;
        }
    }
}
