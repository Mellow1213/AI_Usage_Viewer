using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace UsageWidget;

// A separate layered HWND keeps the ring above WebView2 and outside the 48px logo bounds.
// It never participates in activation, hit testing, the taskbar or Alt+Tab.
internal sealed class LogoLocatorWindow : Window
{
    private const double Diameter = 160;
    private readonly Window _target;
    private readonly bool _logo;
    private readonly RingVisual _ring;
    private readonly Stopwatch _clock = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private (int X, int Y, int Width, int Height)? _lastBounds;

    internal LogoLocatorWindow(Window target, bool logo, bool reducedMotion)
    {
        _target = target;
        _logo = logo;
        Title = "AI Usage · 로고 위치";
        Width = Height = Diameter;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowActivated = false;
        ShowInTaskbar = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        Content = _ring = new RingVisual(reducedMotion);
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(handle, -20).ToInt64();
            SetWindowLongPtr(handle, -20, new IntPtr(style | 0x080000A0)); // NOACTIVATE | TOOLWINDOW | TRANSPARENT
            PositionRing();
        };
        Loaded += (_, _) =>
        {
            PositionRing();
            _clock.Start();
            _timer.Start();
        };
        _timer.Tick += (_, _) =>
        {
            if (_clock.Elapsed.TotalMilliseconds >= 1400 || !_target.IsVisible) { Close(); return; }
            PositionRing();
            _ring.Progress = _clock.Elapsed.TotalMilliseconds / 1400;
        };
        Closed += (_, _) => { _timer.Stop(); _clock.Stop(); };
    }

    private void PositionRing()
    {
        var center = _target.PointToScreen(_logo
            ? new Point(_target.ActualWidth / 2, _target.ActualHeight / 2)
            : new Point(31, 33)); // Center of the card's existing header logo, including host borders.
        var dpi = VisualTreeHelper.GetDpi(_target);
        var width = (int)Math.Round(Diameter * dpi.DpiScaleX);
        var height = (int)Math.Round(Diameter * dpi.DpiScaleY);
        var bounds = ((int)Math.Round(center.X - width / 2d), (int)Math.Round(center.Y - height / 2d), width, height);
        if (_lastBounds == bounds) return;
        SetWindowPos(new WindowInteropHelper(this).Handle, new IntPtr(-1), bounds.Item1, bounds.Item2, width, height, 0x10);
        _lastBounds = bounds;
    }

    private sealed class RingVisual(bool reducedMotion) : FrameworkElement
    {
        private double _progress;
        internal double Progress { set { _progress = value; InvalidateVisual(); } }

        protected override void OnRender(DrawingContext drawing)
        {
            var center = new Point(ActualWidth / 2, ActualHeight / 2);
            if (reducedMotion) { DrawRing(drawing, center, 34, 0.95); return; }
            DrawPulse(drawing, center, _progress / 0.8);
            DrawPulse(drawing, center, (_progress - 0.24) / 0.76);
        }

        private static void DrawPulse(DrawingContext drawing, Point center, double progress)
        {
            if (progress is < 0 or >= 1) return;
            DrawRing(drawing, center, 27 + 45 * (1 - Math.Pow(1 - progress, 2)), 1 - progress);
        }

        private static void DrawRing(DrawingContext drawing, Point center, double radius, double opacity)
        {
            // Dark edging preserves contrast over bright applications as well as dark backgrounds.
            drawing.PushOpacity(opacity);
            drawing.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromArgb(190, 17, 19, 22)), 6), center, radius, radius);
            drawing.DrawEllipse(null, new Pen(new SolidColorBrush(Color.FromRgb(196, 232, 161)), 3), center, radius, radius);
            drawing.Pop();
        }
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
