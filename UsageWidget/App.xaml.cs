using System.Windows;

namespace UsageWidget;

public partial class App : Application
{
    private Mutex? _mutex;
    private bool _owner;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--disconnect-claude"))
        {
            try
            {
                var bridge = new ClaudeBridge(UsageWidget.MainWindow.GetDataDirectory());
                bridge.Uninstall();
                Shutdown(0);
            }
            catch (Exception error) { WidgetStorage.Log("Claude bridge setup failed", error); Shutdown(1); }
            return;
        }
        _mutex = new Mutex(true, @"Local\AIUsageWidget", out _owner);
        if (!_owner) { MessageBox.Show("사용량 카드가 이미 실행 중입니다.", "AI Usage"); Shutdown(); return; }
        DispatcherUnhandledException += (_, error) =>
        {
            WidgetStorage.Log("UI error", error.Exception);
            if (MainWindow is MainWindow window) window.ShowError("화면을 표시하지 못했습니다. 앱을 다시 실행해주세요.");
            error.Handled = true;
        };
        MainWindow = new MainWindow();
        MainWindow.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        if (_owner) _mutex?.ReleaseMutex();
        _mutex?.Dispose();
        base.OnExit(e);
    }
}
