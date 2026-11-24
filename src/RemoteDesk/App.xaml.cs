using System.Windows;
using Velopack;

namespace RemoteDesk;

public partial class App : Application
{
    [STAThread]
    private static int Main(string[] args)
    {
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
        app.InitializeComponent();
        return app.Run();
    }
}
