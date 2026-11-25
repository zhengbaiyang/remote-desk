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
                var cleanup = args.Any(a => string.Equals(a, "--cleanup-uninstall", StringComparison.OrdinalIgnoreCase));
                return WindowsServiceInstaller.StopAndUninstall(cleanupAllData: cleanup);
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

        VelopackApp.Build()
            .OnBeforeUpdateFastCallback(_ => HandleBeforeVelopackUpdate())
            .OnBeforeUninstallFastCallback(_ => HandleBeforeVelopackUninstall())
            .Run();

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

    private static void HandleBeforeVelopackUpdate()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            var (_, running) = WindowsServiceInstaller.GetServiceStatus();
            if (running)
            {
                try
                {
                    var marker = WindowsServiceInstaller.GetRestartDaemonMarkerPath();
                    Directory.CreateDirectory(Path.GetDirectoryName(marker)!);
                    File.WriteAllText(marker, DateTimeOffset.UtcNow.ToString("O"), Encoding.UTF8);
                }
                catch
                {
                }

                AgentIpcClient.TryRequestServiceStopSync(timeoutMs: 2500);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
                while (DateTime.UtcNow < deadline && WindowsServiceInstaller.GetServiceStatus().Running)
                    Thread.Sleep(200);
            }

            WindowsServiceInstaller.StopLocalAuxiliaryProcesses();
        }
        catch
        {
        }
    }

    private static void HandleBeforeVelopackUninstall()
    {
        if (!OperatingSystem.IsWindows())
            return;

        try
        {
            WindowsServiceInstaller.StopLocalAuxiliaryProcesses();

            var (installed, running) = WindowsServiceInstaller.GetServiceStatus();
            if (running)
                AgentIpcClient.TryRequestServiceStopSync(timeoutMs: 2500);

            if (installed)
            {
                if (WindowsServiceInstaller.IsCurrentProcessElevated())
                    WindowsServiceInstaller.StopAndUninstall(cleanupAllData: true);
                else
                    WindowsServiceInstaller.RunElevatedServiceCommandSync("--uninstall-service --cleanup-uninstall", timeoutMs: 22000);
            }

            var installDir = !string.IsNullOrEmpty(Environment.ProcessPath)
                ? Path.GetDirectoryName(Path.GetFullPath(Environment.ProcessPath))
                : null;
            WindowsServiceInstaller.WaitForSiblingProcessesExit(installDir, graceMs: 3000, forceKillAfterGrace: true);
        }
        catch
        {
        }
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
