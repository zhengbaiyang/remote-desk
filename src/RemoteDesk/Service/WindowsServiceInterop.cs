using System.Runtime.InteropServices;

namespace RemoteDesk;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal static class WindowsServiceInterop
{
    public const uint StandardRightsRequired = 0x000F0000;
    public const uint ScManagerConnect = 0x0001;
    public const uint ScManagerCreateService = 0x0002;
    public const uint ScManagerAllAccess = 0xF003F;

    public const uint ServiceQueryConfig = 0x0001;
    public const uint ServiceChangeConfig = 0x0002;
    public const uint ServiceQueryStatus = 0x0004;
    public const uint ServiceStart = 0x0010;
    public const uint ServiceStop = 0x0020;
    public const uint ServiceAllAccess = 0xF01FF;
    public const uint DeleteAccess = 0x00010000;

    public const uint ServiceWin32OwnProcess = 0x00000010;
    public const uint ServiceAutoStart = 0x00000002;
    public const uint ServiceDemandStart = 0x00000003;
    public const uint ServiceErrorNormal = 0x00000001;

    public const uint ServiceStopped = 0x00000001;
    public const uint ServiceStartPending = 0x00000002;
    public const uint ServiceStopPending = 0x00000003;
    public const uint ServiceRunning = 0x00000004;

    public const uint ServiceAcceptStop = 0x00000001;
    public const uint ServiceAcceptShutdown = 0x00000004;

    public const uint ServiceControlStop = 0x00000001;
    public const uint ServiceControlInterrogate = 0x00000004;
    public const uint ServiceControlShutdown = 0x00000005;

    public const uint ServiceConfigDescription = 1;
    public const uint ServiceConfigFailureActions = 2;

    public const uint ScActionRestart = 1;
    public const uint NoChange = 0xFFFFFFFF;

    public delegate void ServiceMainDelegate(int argc, IntPtr argv);
    public delegate uint ServiceControlHandlerExDelegate(uint control, uint eventType, IntPtr eventData, IntPtr context);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ServiceTableEntry
    {
        public string? ServiceName;
        public ServiceMainDelegate? ServiceProc;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ServiceStatusNative
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ServiceDescriptionNative
    {
        public string? Description;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ScActionNative
    {
        public uint Type;
        public uint Delay;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct ServiceFailureActionsNative
    {
        public uint ResetPeriod;
        public string? RebootMsg;
        public string? Command;
        public uint ActionsCount;
        public IntPtr Actions;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StartServiceCtrlDispatcherW([In] ServiceTableEntry[] serviceTable);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr RegisterServiceCtrlHandlerExW(
        string serviceName,
        ServiceControlHandlerExDelegate handlerProc,
        IntPtr context);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetServiceStatus(IntPtr serviceStatusHandle, ref ServiceStatusNative serviceStatus);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateServiceW(
        IntPtr scManager,
        string serviceName,
        string displayName,
        uint desiredAccess,
        uint serviceType,
        uint startType,
        uint errorControl,
        string binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfigW(
        IntPtr service,
        uint serviceType,
        uint startType,
        uint errorControl,
        string? binaryPathName,
        string? loadOrderGroup,
        IntPtr tagId,
        string? dependencies,
        string? serviceStartName,
        string? password,
        string? displayName);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ChangeServiceConfig2W(IntPtr service, uint infoLevel, IntPtr info);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool StartServiceW(IntPtr service, int numServiceArgs, string[]? serviceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ControlService(IntPtr service, uint control, out ServiceStatusNative serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteService(IntPtr service);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool QueryServiceStatus(IntPtr service, out ServiceStatusNative serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool CloseServiceHandle(IntPtr handle);
}
