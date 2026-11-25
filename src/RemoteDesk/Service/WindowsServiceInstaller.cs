using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace RemoteDesk;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsServiceInstaller
{
    public static string GetRestartDaemonMarkerPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "RemoteDesk",
        ".restart-daemon-after-update");

    public static bool IsCurrentProcessElevated()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    public static (bool Installed, bool Running) GetServiceStatus()
    {
        var scm = WindowsServiceInterop.OpenSCManagerW(null, null, WindowsServiceInterop.ScManagerConnect);
        if (scm == IntPtr.Zero)
            return (false, false);

        try
        {
            var svc = WindowsServiceInterop.OpenServiceW(scm, AgentIpcConstants.ServiceName, WindowsServiceInterop.ServiceQueryStatus);
            if (svc == IntPtr.Zero)
                return (false, false);

            try
            {
                var running = WindowsServiceInterop.QueryServiceStatus(svc, out var status) &&
                              status.CurrentState == WindowsServiceInterop.ServiceRunning;
                return (true, running);
            }
            finally
            {
                WindowsServiceInterop.CloseServiceHandle(svc);
            }
        }
        finally
        {
            WindowsServiceInterop.CloseServiceHandle(scm);
        }
    }

    public static async Task<(bool Ok, bool CancelledByUser, string Message)> RunElevatedServiceCommandAsync(string flag)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return (false, false, "无法定位当前程序路径。");

        var startInfo = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = flag,
        };

        try
        {
            using var proc = Process.Start(startInfo);
            if (proc is null)
                return (false, false, "未能拉起提权安装进程。");

            await proc.WaitForExitAsync().ConfigureAwait(false);
            return proc.ExitCode == 0
                ? (true, false, "操作成功。")
                : (false, false, $"后台守护服务操作失败（退出码 {proc.ExitCode}）。");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return (false, true, "已取消管理员授权请求。");
        }
        catch (Win32Exception ex)
        {
            return (false, false, $"提权启动失败：{ex.Message}");
        }
    }

    public static bool RunElevatedServiceCommandSync(string arguments, int timeoutMs = 22000)
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return false;

        var startInfo = new ProcessStartInfo(exePath)
        {
            WorkingDirectory = Path.GetDirectoryName(exePath)!,
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = arguments,
        };

        try
        {
            using var proc = Process.Start(startInfo);
            if (proc is null)
                return false;

            if (!proc.WaitForExit(timeoutMs))
                return false;

            return proc.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static int InstallOrUpdateAndStart(string exePath)
    {
        if (string.IsNullOrWhiteSpace(exePath) || !File.Exists(exePath))
            return 2;

        // Avoid orphaned non-service easytier-core holding the RPC portal before service starts.
        EasyTierManager.StopMesh();

        var fullExePath = Path.GetFullPath(exePath);
        var binaryPath = $"\"{fullExePath}\" --service";
        var scm = WindowsServiceInterop.OpenSCManagerW(null, null, WindowsServiceInterop.ScManagerAllAccess);
        if (scm == IntPtr.Zero)
            return Marshal.GetLastWin32Error();

        try
        {
            var svc = WindowsServiceInterop.OpenServiceW(scm, AgentIpcConstants.ServiceName, WindowsServiceInterop.ServiceAllAccess);
            if (svc == IntPtr.Zero)
            {
                svc = WindowsServiceInterop.CreateServiceW(
                    scm,
                    AgentIpcConstants.ServiceName,
                    AgentIpcConstants.ServiceDisplayName,
                    WindowsServiceInterop.ServiceAllAccess,
                    WindowsServiceInterop.ServiceWin32OwnProcess,
                    WindowsServiceInterop.ServiceAutoStart,
                    WindowsServiceInterop.ServiceErrorNormal,
                    binaryPath,
                    null,
                    IntPtr.Zero,
                    "Tcpip\0",
                    null,
                    null);

                if (svc == IntPtr.Zero)
                    return Marshal.GetLastWin32Error();
            }
            else
            {
                if (WindowsServiceInterop.QueryServiceStatus(svc, out var curStatus) &&
                    curStatus.CurrentState != WindowsServiceInterop.ServiceStopped)
                {
                    WindowsServiceInterop.ControlService(svc, WindowsServiceInterop.ServiceControlStop, out _);
                    WaitForState(svc, WindowsServiceInterop.ServiceStopped, TimeSpan.FromSeconds(10));
                }

                WaitForSiblingProcessesExit(Path.GetDirectoryName(fullExePath), graceMs: 3000, forceKillAfterGrace: true);

                WindowsServiceInterop.ChangeServiceConfigW(
                    svc,
                    WindowsServiceInterop.ServiceWin32OwnProcess,
                    WindowsServiceInterop.ServiceAutoStart,
                    WindowsServiceInterop.ServiceErrorNormal,
                    binaryPath,
                    null,
                    IntPtr.Zero,
                    "Tcpip\0",
                    null,
                    null,
                    AgentIpcConstants.ServiceDisplayName);
            }

            try
            {
                ConfigureDescriptionAndRecovery(svc);
                ConfigureFirewallRules();

                if (WindowsServiceInterop.QueryServiceStatus(svc, out var status) &&
                    status.CurrentState != WindowsServiceInterop.ServiceRunning)
                {
                    if (status.CurrentState == WindowsServiceInterop.ServiceStartPending ||
                        status.CurrentState == WindowsServiceInterop.ServiceStopPending)
                    {
                        WaitForState(svc, WindowsServiceInterop.ServiceRunning, TimeSpan.FromSeconds(5));
                    }
                    else if (!WindowsServiceInterop.StartServiceW(svc, 0, null))
                    {
                        var err = Marshal.GetLastWin32Error();
                        if (err != 1056) // ERROR_SERVICE_ALREADY_RUNNING
                            return err;
                    }
                }

                WaitForState(svc, WindowsServiceInterop.ServiceRunning, TimeSpan.FromSeconds(8));
                return 0;
            }
            finally
            {
                WindowsServiceInterop.CloseServiceHandle(svc);
            }
        }
        finally
        {
            WindowsServiceInterop.CloseServiceHandle(scm);
        }
    }

    public static int StopAndUninstall(bool cleanupAllData = false)
    {
        var configuredSunshineDir = TryReadConfiguredSunshineDirectory();

        var exitCode = 0;
        var scm = WindowsServiceInterop.OpenSCManagerW(null, null, WindowsServiceInterop.ScManagerConnect);
        if (scm == IntPtr.Zero)
        {
            exitCode = Marshal.GetLastWin32Error();
        }
        else
        {
            try
            {
                var svc = WindowsServiceInterop.OpenServiceW(
                    scm,
                    AgentIpcConstants.ServiceName,
                    WindowsServiceInterop.ServiceStop | WindowsServiceInterop.ServiceQueryStatus | WindowsServiceInterop.DeleteAccess);

                if (svc != IntPtr.Zero)
                {
                    try
                    {
                        if (WindowsServiceInterop.QueryServiceStatus(svc, out var status) &&
                            status.CurrentState != WindowsServiceInterop.ServiceStopped)
                        {
                            WindowsServiceInterop.ControlService(svc, WindowsServiceInterop.ServiceControlStop, out _);
                            WaitForState(svc, WindowsServiceInterop.ServiceStopped, TimeSpan.FromSeconds(10));
                        }

                        if (!WindowsServiceInterop.DeleteService(svc))
                        {
                            var err = Marshal.GetLastWin32Error();
                            if (err != 1072) // ERROR_SERVICE_MARKED_FOR_DELETE
                                exitCode = err;
                        }
                    }
                    finally
                    {
                        WindowsServiceInterop.CloseServiceHandle(svc);
                    }
                }
            }
            finally
            {
                WindowsServiceInterop.CloseServiceHandle(scm);
            }
        }

        // Release all file locks on the current application directory (including exiting --service/--session-bridge).
        var currentInstallDir = !string.IsNullOrEmpty(Environment.ProcessPath)
            ? Path.GetDirectoryName(Path.GetFullPath(Environment.ProcessPath))
            : null;
        WaitForSiblingProcessesExit(currentInstallDir, graceMs: 4000, forceKillAfterGrace: true);

        EasyTierManager.StopMesh();
        HostServiceManager.TryStopSunshineService();
        StopManagedSunshineProcesses(configuredSunshineDir);
        RemoveFirewallRules();

        if (cleanupAllData)
        {
            try
            {
                var programDataDir = AgentIpcConstants.GetProgramDataDirectory();
                if (Directory.Exists(programDataDir))
                    Directory.Delete(programDataDir, recursive: true);
            }
            catch
            {
            }
        }

        return exitCode;
    }

    public static void StopLocalAuxiliaryProcesses()
    {
        try
        {
            EasyTierManager.StopMesh();
        }
        catch
        {
        }

        try
        {
            StopManagedSunshineProcesses(TryReadConfiguredSunshineDirectory());
        }
        catch
        {
        }

        try
        {
            var currentInstallDir = !string.IsNullOrEmpty(Environment.ProcessPath)
                ? Path.GetDirectoryName(Path.GetFullPath(Environment.ProcessPath))
                : null;
            WaitForSiblingProcessesExit(currentInstallDir, graceMs: 1200, forceKillAfterGrace: true);
        }
        catch
        {
        }
    }

    public static void WaitForSiblingProcessesExit(string? installDir, int graceMs, bool forceKillAfterGrace)
    {
        if (string.IsNullOrWhiteSpace(installDir))
            return;

        string normalizedDir;
        try
        {
            normalizedDir = Path.GetFullPath(installDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        catch
        {
            return;
        }

        var exeName = !string.IsNullOrEmpty(Environment.ProcessPath)
            ? Path.GetFileNameWithoutExtension(Environment.ProcessPath)
            : "RemoteDesk";
        var selfPid = Environment.ProcessId;
        var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(Math.Max(200, graceMs));

        while (DateTime.UtcNow < deadline)
        {
            if (!AnySiblingInDirectory(exeName, normalizedDir, selfPid))
                return;
            Thread.Sleep(150);
        }

        if (forceKillAfterGrace)
            HostServiceManager.StopProcessesUnderDirectory(exeName, normalizedDir, excludePid: selfPid);
    }

    private static bool AnySiblingInDirectory(string processName, string normalizedDir, int selfPid)
    {
        foreach (var proc in Process.GetProcessesByName(processName))
        {
            using (proc)
            {
                try
                {
                    if (proc.Id == selfPid || proc.HasExited)
                        continue;

                    var modulePath = proc.MainModule?.FileName;
                    if (!string.IsNullOrEmpty(modulePath) &&
                        Path.GetFullPath(modulePath).StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
                catch
                {
                    // System/elevated sibling process cannot be inspected from non-elevated caller; assume still running.
                    return true;
                }
            }
        }
        return false;
    }

    private static void StopManagedSunshineProcesses(string? extraSunshineDir)
    {
        var localEnginesDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RemoteDesk",
            "engines");

        HostServiceManager.StopProcessesUnderDirectory("sunshine", localEnginesDir);
        HostServiceManager.StopProcessesUnderDirectory("sunshinesvc", localEnginesDir);

        if (!string.IsNullOrEmpty(extraSunshineDir))
        {
            HostServiceManager.StopProcessesUnderDirectory("sunshine", extraSunshineDir);
            HostServiceManager.StopProcessesUnderDirectory("sunshinesvc", extraSunshineDir);
        }
    }

    private static string? TryReadConfiguredSunshineDirectory()
    {
        try
        {
            var cfgPath = AgentIpcConstants.GetAgentConfigPath();
            if (!File.Exists(cfgPath))
                return null;

            var doc = JsonSerializer.Deserialize<AgentConfigDocument>(
                File.ReadAllText(cfgPath, Encoding.UTF8),
                new JsonSerializerOptions(JsonSerializerDefaults.Web));

            if (!string.IsNullOrWhiteSpace(doc?.SunshineExecutablePath) &&
                doc.SunshineExecutablePath.Contains($"{Path.DirectorySeparatorChar}RemoteDesk{Path.DirectorySeparatorChar}engines{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(doc.SunshineExecutablePath);
            }
        }
        catch
        {
        }
        return null;
    }

    private static void ConfigureDescriptionAndRecovery(IntPtr svc)
    {
        var desc = new WindowsServiceInterop.ServiceDescriptionNative
        {
            Description = AgentIpcConstants.ServiceDescription,
        };
        var descPtr = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsServiceInterop.ServiceDescriptionNative>());
        try
        {
            Marshal.StructureToPtr(desc, descPtr, false);
            WindowsServiceInterop.ChangeServiceConfig2W(svc, WindowsServiceInterop.ServiceConfigDescription, descPtr);
        }
        finally
        {
            Marshal.DestroyStructure<WindowsServiceInterop.ServiceDescriptionNative>(descPtr);
            Marshal.FreeHGlobal(descPtr);
        }

        var actions = new[]
        {
            new WindowsServiceInterop.ScActionNative { Type = WindowsServiceInterop.ScActionRestart, Delay = 5000 },
            new WindowsServiceInterop.ScActionNative { Type = WindowsServiceInterop.ScActionRestart, Delay = 15000 },
            new WindowsServiceInterop.ScActionNative { Type = WindowsServiceInterop.ScActionRestart, Delay = 30000 },
        };
        var actionSize = Marshal.SizeOf<WindowsServiceInterop.ScActionNative>();
        var actionsPtr = Marshal.AllocHGlobal(actionSize * actions.Length);
        var failurePtr = Marshal.AllocHGlobal(Marshal.SizeOf<WindowsServiceInterop.ServiceFailureActionsNative>());
        try
        {
            for (var i = 0; i < actions.Length; i++)
                Marshal.StructureToPtr(actions[i], actionsPtr + i * actionSize, false);

            var failure = new WindowsServiceInterop.ServiceFailureActionsNative
            {
                ResetPeriod = 86400,
                RebootMsg = null,
                Command = null,
                ActionsCount = (uint)actions.Length,
                Actions = actionsPtr,
            };
            Marshal.StructureToPtr(failure, failurePtr, false);
            WindowsServiceInterop.ChangeServiceConfig2W(svc, WindowsServiceInterop.ServiceConfigFailureActions, failurePtr);
        }
        finally
        {
            Marshal.FreeHGlobal(actionsPtr);
            Marshal.FreeHGlobal(failurePtr);
        }
    }

    private static void ConfigureFirewallRules()
    {
        RemoveFirewallRules();
        TryRunNetsh($"advfirewall firewall add rule name=\"RemoteDesk Agent 47999\" dir=in action=allow protocol=TCP localport={AgentIpcConstants.DefaultAgentPort}");
        TryRunNetsh("advfirewall firewall add rule name=\"RemoteDesk Sunshine Stream\" dir=in action=allow protocol=TCP localport=47984,47989,48010");
        TryRunNetsh("advfirewall firewall add rule name=\"RemoteDesk Sunshine Stream UDP\" dir=in action=allow protocol=UDP localport=47998-48010");
    }

    private static void RemoveFirewallRules()
    {
        TryRunNetsh("advfirewall firewall delete rule name=\"RemoteDesk Agent 47999\"");
        TryRunNetsh("advfirewall firewall delete rule name=\"RemoteDesk Sunshine Stream\"");
        TryRunNetsh("advfirewall firewall delete rule name=\"RemoteDesk Sunshine Stream UDP\"");
    }

    private static void TryRunNetsh(string args)
    {
        try
        {
            var si = new ProcessStartInfo("netsh.exe", args)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(si);
            proc?.WaitForExit(3000);
        }
        catch
        {
            // Best-effort firewall rule setup.
        }
    }

    private static void WaitForState(IntPtr svc, uint desiredState, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (WindowsServiceInterop.QueryServiceStatus(svc, out var status) && status.CurrentState == desiredState)
                return;
            Thread.Sleep(200);
        }
    }
}
