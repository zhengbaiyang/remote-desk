using System.Runtime.InteropServices;

namespace RemoteDesk;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsServiceHost
{
    private static IntPtr _statusHandle;
    private static WindowsServiceInterop.ServiceStatusNative _serviceStatus;
    private static AgentDaemonRuntime? _runtime;
    private static ManualResetEventSlim? _stopEvent;

    // Keep delegates rooted so GC does not collect callbacks passed to native SCM.
    private static WindowsServiceInterop.ServiceMainDelegate? _serviceMainCallback;
    private static WindowsServiceInterop.ServiceControlHandlerExDelegate? _controlHandlerCallback;

    public static void RequestGracefulStop()
    {
        try
        {
            if (_statusHandle != IntPtr.Zero)
            {
                _serviceStatus.CurrentState = WindowsServiceInterop.ServiceStopPending;
                _serviceStatus.ControlsAccepted = 0;
                _serviceStatus.WaitHint = 5000;
                WindowsServiceInterop.SetServiceStatus(_statusHandle, ref _serviceStatus);
            }

            _stopEvent?.Set();
        }
        catch
        {
        }
    }

    public static void RunService()
    {
        _serviceMainCallback = ServiceMain;
        var table = new[]
        {
            new WindowsServiceInterop.ServiceTableEntry
            {
                ServiceName = AgentIpcConstants.ServiceName,
                ServiceProc = _serviceMainCallback,
            },
            new WindowsServiceInterop.ServiceTableEntry
            {
                ServiceName = null,
                ServiceProc = null,
            },
        };

        WindowsServiceInterop.StartServiceCtrlDispatcherW(table);
    }

    private static void ServiceMain(int argc, IntPtr argv)
    {
        _controlHandlerCallback = ServiceControlHandler;
        _statusHandle = WindowsServiceInterop.RegisterServiceCtrlHandlerExW(
            AgentIpcConstants.ServiceName,
            _controlHandlerCallback,
            IntPtr.Zero);

        if (_statusHandle == IntPtr.Zero)
            return;

        _serviceStatus = new WindowsServiceInterop.ServiceStatusNative
        {
            ServiceType = WindowsServiceInterop.ServiceWin32OwnProcess,
            CurrentState = WindowsServiceInterop.ServiceStartPending,
            ControlsAccepted = 0,
            Win32ExitCode = 0,
            CheckPoint = 1,
            WaitHint = 5000,
        };
        WindowsServiceInterop.SetServiceStatus(_statusHandle, ref _serviceStatus);

        _stopEvent = new ManualResetEventSlim(false);
        _runtime = new AgentDaemonRuntime(isSystemService: true);

        try
        {
            _runtime.StartAsync().GetAwaiter().GetResult();
            _serviceStatus.CurrentState = WindowsServiceInterop.ServiceRunning;
            _serviceStatus.ControlsAccepted = WindowsServiceInterop.ServiceAcceptStop | WindowsServiceInterop.ServiceAcceptShutdown;
            _serviceStatus.CheckPoint = 0;
            _serviceStatus.WaitHint = 0;
            WindowsServiceInterop.SetServiceStatus(_statusHandle, ref _serviceStatus);

            _stopEvent.Wait();
        }
        catch
        {
            _serviceStatus.Win32ExitCode = 1;
        }
        finally
        {
            _serviceStatus.CurrentState = WindowsServiceInterop.ServiceStopPending;
            _serviceStatus.ControlsAccepted = 0;
            _serviceStatus.WaitHint = 5000;
            WindowsServiceInterop.SetServiceStatus(_statusHandle, ref _serviceStatus);

            try
            {
                _runtime.StopAsync().GetAwaiter().GetResult();
            }
            catch
            {
            }

            _runtime.Dispose();
            _stopEvent.Dispose();

            _serviceStatus.CurrentState = WindowsServiceInterop.ServiceStopped;
            _serviceStatus.WaitHint = 0;
            WindowsServiceInterop.SetServiceStatus(_statusHandle, ref _serviceStatus);
        }
    }

    private static uint ServiceControlHandler(uint control, uint eventType, IntPtr eventData, IntPtr context)
    {
        switch (control)
        {
            case WindowsServiceInterop.ServiceControlStop:
            case WindowsServiceInterop.ServiceControlShutdown:
                _serviceStatus.CurrentState = WindowsServiceInterop.ServiceStopPending;
                _serviceStatus.ControlsAccepted = 0;
                _serviceStatus.WaitHint = 5000;
                WindowsServiceInterop.SetServiceStatus(_statusHandle, ref _serviceStatus);
                _stopEvent?.Set();
                return 0;

            case WindowsServiceInterop.ServiceControlInterrogate:
                return 0;

            default:
                return 120; // ERROR_CALL_NOT_IMPLEMENTED
        }
    }
}
