using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Windows;
using Velopack;

namespace RemoteDesk;

public partial class App : Application
{
    [STAThread]
    private static int Main(string[] args)
    {
        RegisterGlobalCrashHandlers();

        if (OperatingSystem.IsWindows() && args.Length > 0)
        {
            if (args.Any(a => string.Equals(a, "--service", StringComparison.OrdinalIgnoreCase)))
            {
                WindowsServiceHost.RunService();
                return 0;
            }

            if (args.Any(a => string.Equals(a, "--install-service", StringComparison.OrdinalIgnoreCase)))
            {
                var exe = Environment.ProcessPath ?? "";
                return WindowsServiceInstaller.InstallOrUpdateAndStart(exe);
            }

            if (args.Any(a => string.Equals(a, "--uninstall-service", StringComparison.OrdinalIgnoreCase)))
            {
                return WindowsServiceInstaller.StopAndUninstall();
            }

            if (args.Any(a => string.Equals(a, "--agent-console", StringComparison.OrdinalIgnoreCase)))
            {
                using var runtime = new AgentDaemonRuntime(isSystemService: false);
                using var cts = new CancellationTokenSource();
                Console.CancelKeyPress += (_, e) =>
                {
                    e.Cancel = true;
                    cts.Cancel();
                };
                runtime.StartAsync(cts.Token).GetAwaiter().GetResult();
                cts.Token.WaitHandle.WaitOne();
                runtime.StopAsync().GetAwaiter().GetResult();
                return 0;
            }

            if (args.Any(a => string.Equals(a, "--session-bridge", StringComparison.OrdinalIgnoreCase)))
            {
                return SessionBridgeServer.RunStandaloneBridgeApp();
            }
        }

        VelopackApp.Build().Run();
        var app = new App();
        app.DispatcherUnhandledException += (_, e) =>
        {
            WriteCrashLog("DispatcherUnhandledException", e.Exception);
            if (IsRecoverableUiException(e.Exception) && Current?.MainWindow is { IsLoaded: true })
                e.Handled = true;
        };
        app.InitializeComponent();
        return app.Run();
    }

    private static void RegisterGlobalCrashHandlers()
    {
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex)
                WriteCrashLog("AppDomain.UnhandledException", ex);
        };

        TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            WriteCrashLog("TaskScheduler.UnobservedTaskException", e.Exception);
            e.SetObserved();
        };
    }

    private static bool IsRecoverableUiException(Exception ex) =>
        ex is JsonException or IOException or InvalidOperationException or TimeoutException
            or HttpRequestException or TaskCanceledException or UnauthorizedAccessException;

    private static void WriteCrashLog(string source, Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RemoteDesk",
                "logs");
            Directory.CreateDirectory(dir);
            var entry = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] [{source}] {ex}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(Path.Combine(dir, "crash.log"), entry, Encoding.UTF8);
        }
        catch
        {
        }
    }
}
