using System.Windows;
using System.Security.Cryptography;
using System.Text;
using Velopack;

namespace UsageWidget;

public partial class App : Application
{
    [STAThread]
    private static void Main(string[] args)
    {
        VelopackApp.Build().SetAutoApplyOnStartup(false).Run();
        var app = new App();
        app.InitializeComponent();
        app.Run();
    }

    private Mutex? _mutex;
    private bool _owner;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        WidgetStorage.UseDataDirectory(UsageWidget.MainWindow.GetDataDirectory());
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
        // Development profiles may run beside the installed app without sharing credentials.
        var directory = System.IO.Path.GetFullPath(UsageWidget.MainWindow.GetDataDirectory()).TrimEnd('\\', '/').ToUpperInvariant();
        var identity = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(directory)))[..24];
        var standardDirectory = System.IO.Path.GetFullPath(WidgetStorage.DataDirectory).TrimEnd('\\', '/').ToUpperInvariant();
        _mutex = new Mutex(true, directory == standardDirectory ? @"Local\AIUsageWidget" : @"Local\AIUsageWidget-" + identity, out _owner);
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
