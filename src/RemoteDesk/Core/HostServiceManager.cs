using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security;
using Microsoft.Win32;

namespace RemoteDesk;

internal sealed record LocalNetworkInfo(
    string HostName,
    IReadOnlyList<string> IPv4Addresses,
    IReadOnlyList<string> IPv6Addresses);

internal sealed record GpuEncoderInfo(
    string AdapterName,
    string Vendor,
    string EncoderName);

internal sealed record HostStatus(
    bool SunshineInstalled,
    bool SunshineRunning,
    bool SunshineServiceRegistered,
    string? SunshineExecutablePath);

internal static class HostServiceManager
{
    private const string DisplayAdapterClassSubKey = @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
    public const string SunshineWebUiUrl = "https://localhost:47990";

    public static string GetSunshineWebUiUrl() => SunshineWebUiUrl;

    public static LocalNetworkInfo GetLocalNetworkInfo()
    {
        var hostName = Environment.MachineName;
        try
        {
            var dnsHost = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(dnsHost))
                hostName = dnsHost;
        }
        catch (SocketException)
        {
            // Fall back to MachineName.
        }

        var rawAddresses = new List<IPAddress>();
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(nic =>
                    nic.OperationalStatus == OperationalStatus.Up &&
                    nic.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel) &&
                    !IsVirtualOrProxyAdapter(nic.Name, nic.Description))
                .OrderBy(nic => GetAdapterPriority(nic.NetworkInterfaceType));

            foreach (var nic in interfaces)
            {
                IPInterfaceProperties properties;
                try
                {
                    properties = nic.GetIPProperties();
                }
                catch (NetworkInformationException)
                {
                    continue;
                }

                foreach (var unicast in properties.UnicastAddresses)
                {
                    rawAddresses.Add(unicast.Address);
                }
            }
        }
        catch (NetworkInformationException)
        {
            // Return hostname with empty address lists if network enumeration fails.
        }

        var (ipv4, ipv6) = FilterIpAddresses(rawAddresses);
        return new LocalNetworkInfo(hostName, ipv4, ipv6);
    }

    internal static bool IsVirtualOrProxyAdapter(string? name, string? description)
    {
        var combined = $"{name ?? ""} {description ?? ""}";
        if (string.IsNullOrWhiteSpace(combined))
            return false;

        string[] blockedKeywords =
        [
            "clash",
            "mihomo",
            "sing-box",
            "wintun",
            "easytier",
            "tailscale",
            "zerotier",
            "wireguard",
            "tap-windows",
            "vethernet",
            "hyper-v",
            "vmware",
            "virtualbox",
            "bluetooth",
            "loopback",
            "npcap",
            "sangfor",
            "easyconnect",
        ];

        foreach (var keyword in blockedKeywords)
        {
            if (combined.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static int GetAdapterPriority(NetworkInterfaceType type) => type switch
    {
        NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.FastEthernetT => 0,
        NetworkInterfaceType.Wireless80211 => 1,
        _ => 2,
    };

    internal static (IReadOnlyList<string> IPv4, IReadOnlyList<string> IPv6) FilterIpAddresses(IEnumerable<IPAddress> addresses)
    {
        var ipv4 = new List<string>();
        var ipv6 = new List<string>();
        var seenV4 = new HashSet<string>(StringComparer.Ordinal);
        var seenV6 = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var address in addresses)
        {
            if (IPAddress.IsLoopback(address))
                continue;

            if (address.AddressFamily == AddressFamily.InterNetwork)
            {
                if (address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast))
                    continue;

                var bytes = address.GetAddressBytes();
                if (bytes.Length == 4)
                {
                    // Skip APIPA (169.254.0.0/16) and RFC 2544 / proxy Fake-IP (198.18.0.0/15).
                    if (bytes[0] == 169 && bytes[1] == 254)
                        continue;
                    if (bytes[0] == 198 && (bytes[1] == 18 || bytes[1] == 19))
                        continue;
                }

                var text = address.ToString();
                if (seenV4.Add(text))
                    ipv4.Add(text);
            }
            else if (address.AddressFamily == AddressFamily.InterNetworkV6)
            {
                if (address.Equals(IPAddress.IPv6Any) ||
                    address.Equals(IPAddress.IPv6None) ||
                    address.IsIPv6LinkLocal ||
                    address.IsIPv6Multicast ||
                    address.IsIPv6SiteLocal)
                {
                    continue;
                }

                var text = address.ToString();
                var scopeIndex = text.IndexOf('%');
                if (scopeIndex >= 0)
                    text = text[..scopeIndex];

                if (seenV6.Add(text))
                    ipv6.Add(text);
            }
        }

        return (ipv4, ipv6);
    }

    public static IReadOnlyList<GpuEncoderInfo> DetectGpuEncoders()
    {
        if (!OperatingSystem.IsWindows())
            return [];

        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            return DetectGpuEncoders(root);
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
        {
            return [];
        }
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static IReadOnlyList<GpuEncoderInfo> DetectGpuEncoders(RegistryKey root)
    {
        var encoders = new List<GpuEncoderInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        using var displayClass = root.OpenSubKey(DisplayAdapterClassSubKey);
        if (displayClass is null)
            return encoders;

        foreach (var subKeyName in displayClass.GetSubKeyNames())
        {
            try
            {
                using var adapterKey = displayClass.OpenSubKey(subKeyName);
                if (adapterKey is null)
                    continue;

                var driverDesc = (adapterKey.GetValue("DriverDesc") as string)?.Trim();
                if (string.IsNullOrEmpty(driverDesc))
                    continue;

                var provider = (adapterKey.GetValue("ProviderName") as string)?.Trim() ?? "";
                var deviceId = (adapterKey.GetValue("MatchingDeviceId") as string)?.Trim() ?? "";

                if (TryClassifyGpu(driverDesc, provider, deviceId) is not { } info)
                    continue;

                var dedupeKey = $"{info.AdapterName}|{info.EncoderName}";
                if (seen.Add(dedupeKey))
                    encoders.Add(info);
            }
            catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
            {
                // Skip inaccessible registry entries.
            }
        }

        return encoders;
    }

    public static string? FindSunshineExecutable(string? selectedPath, string? managedPath = null)
    {
        if (IsExecutable(selectedPath))
            return selectedPath;

        if (IsExecutable(managedPath))
            return managedPath;

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Sunshine", "sunshine.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Sunshine", "sunshine.exe"),
            Path.Combine(AppContext.BaseDirectory, "sunshine.exe"),
        ];

        var standard = candidates.FirstOrDefault(IsExecutable);
        if (standard != null || !OperatingSystem.IsWindows())
            return standard;

        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            try
            {
                using var root = RegistryKey.OpenBaseKey(hive, view);
                var installed = FindRegisteredSunshine(root);
                if (installed != null)
                    return installed;
            }
            catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
            {
                // Continue with other registry views.
            }
        }

        return null;
    }

    public static HostStatus GetHostStatus(string? sunshineExe)
    {
        var resolvedSunshine = IsExecutable(sunshineExe) ? sunshineExe : null;
        var sunshineServiceRegistered = IsWindowsServiceRegistered("SunshineService") || IsWindowsServiceRegistered("sunshinesvc");
        var sunshineRunning = IsSunshineRunning();

        return new HostStatus(
            SunshineInstalled: resolvedSunshine is not null,
            SunshineRunning: sunshineRunning,
            SunshineServiceRegistered: sunshineServiceRegistered,
            SunshineExecutablePath: resolvedSunshine);
    }

    public static ProcessStartInfo CreateSunshineHostStart(string sunshineExe)
    {
        if (!IsExecutable(sunshineExe))
            throw new FileNotFoundException("找不到 Sunshine 程序。", sunshineExe);

        return new ProcessStartInfo(sunshineExe)
        {
            WorkingDirectory = Path.GetDirectoryName(sunshineExe)!,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    public static bool StartSunshineHost(string sunshineExe)
    {
        var startInfo = CreateSunshineHostStart(sunshineExe);
        if (IsSunshineRunning())
            return true;

        using var process = Process.Start(startInfo);
        return process is not null;
    }

    public static bool StopSunshineHost()
    {
        var allStopped = true;
        foreach (var processName in new[] { "sunshine", "sunshinesvc" })
        {
            foreach (var process in Process.GetProcessesByName(processName))
            {
                using (process)
                {
                    try
                    {
                        if (process.HasExited)
                            continue;

                        process.CloseMainWindow();
                        if (!process.WaitForExit(1500))
                        {
                            process.Kill(entireProcessTree: false);
                            process.WaitForExit(1500);
                        }
                    }
                    catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException)
                    {
                        allStopped = false;
                    }
                }
            }
        }

        return allStopped && !IsSunshineRunning();
    }

    public static void StopProcessesUnderDirectory(string processName, string directoryPath, int? excludePid = null)
    {
        if (string.IsNullOrWhiteSpace(processName) || string.IsNullOrWhiteSpace(directoryPath))
            return;

        string normalizedDir;
        try
        {
            normalizedDir = Path.GetFullPath(directoryPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return;
        }

        foreach (var proc in Process.GetProcessesByName(processName))
        {
            using (proc)
            {
                try
                {
                    if (excludePid is int skip && proc.Id == skip)
                        continue;
                    if (proc.HasExited)
                        continue;

                    var modulePath = proc.MainModule?.FileName;
                    if (string.IsNullOrEmpty(modulePath))
                        continue;

                    var fullModule = Path.GetFullPath(modulePath);
                    if (!fullModule.StartsWith(normalizedDir, StringComparison.OrdinalIgnoreCase))
                        continue;

                    proc.Kill(entireProcessTree: true);
                    proc.WaitForExit(2000);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or NotSupportedException)
                {
                }
            }
        }
    }

    private static bool IsSunshineRunning() =>
        IsProcessRunning("sunshine") ||
        IsProcessRunning("sunshinesvc") ||
        IsWindowsServiceRunning("SunshineService") ||
        IsWindowsServiceRunning("sunshinesvc");

    private static bool IsProcessRunning(string processName)
    {
        var processes = Process.GetProcessesByName(processName);
        var running = processes.Length > 0;
        foreach (var process in processes)
            process.Dispose();
        return running;
    }

    public static bool TryStartSunshineService()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        foreach (var serviceName in new[] { "SunshineService", "sunshinesvc" })
        {
            if (TryStartWindowsService(serviceName))
                return true;
        }
        return false;
    }

    public static bool TryStopSunshineService()
    {
        if (!OperatingSystem.IsWindows())
            return false;

        var stoppedAny = false;
        foreach (var serviceName in new[] { "SunshineService", "sunshinesvc" })
        {
            if (TryStopWindowsService(serviceName))
                stoppedAny = true;
        }
        return stoppedAny;
    }

    private static bool TryStartWindowsService(string serviceName)
    {
        const uint ScManagerConnect = 0x0001;
        const uint ServiceQueryStatus = 0x0004;
        const uint ServiceStart = 0x0010;
        const uint ServiceRunning = 0x00000004;

        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            return false;

        try
        {
            var service = OpenServiceW(scm, serviceName, ServiceQueryStatus | ServiceStart);
            if (service == IntPtr.Zero)
                return false;

            try
            {
                if (QueryServiceStatus(service, out var status) && status.CurrentState == ServiceRunning)
                    return true;

                if (!StartServiceW(service, 0, null))
                {
                    var err = Marshal.GetLastWin32Error();
                    if (err != 1056) // ERROR_SERVICE_ALREADY_RUNNING
                        return false;
                }

                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    if (QueryServiceStatus(service, out status) && status.CurrentState == ServiceRunning)
                        return true;
                    Thread.Sleep(150);
                }
                return false;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static bool TryStopWindowsService(string serviceName)
    {
        const uint ScManagerConnect = 0x0001;
        const uint ServiceQueryStatus = 0x0004;
        const uint ServiceStop = 0x0020;
        const uint ServiceControlStop = 0x00000001;
        const uint ServiceStopped = 0x00000001;

        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            return false;

        try
        {
            var service = OpenServiceW(scm, serviceName, ServiceQueryStatus | ServiceStop);
            if (service == IntPtr.Zero)
                return false;

            try
            {
                if (QueryServiceStatus(service, out var status) && status.CurrentState == ServiceStopped)
                    return true;

                ControlService(service, ServiceControlStop, out _);
                var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
                while (DateTime.UtcNow < deadline)
                {
                    if (QueryServiceStatus(service, out status) && status.CurrentState == ServiceStopped)
                        return true;
                    Thread.Sleep(150);
                }
                return false;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    private static bool IsWindowsServiceRegistered(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var key = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
            return key is not null;
        }
        catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    private static bool IsWindowsServiceRunning(string serviceName)
    {
        if (!OperatingSystem.IsWindows())
            return false;

        const uint ScManagerConnect = 0x0001;
        const uint ServiceQueryStatus = 0x0004;
        const uint ServiceRunning = 0x00000004;

        var scm = OpenSCManagerW(null, null, ScManagerConnect);
        if (scm == IntPtr.Zero)
            return false;

        try
        {
            var service = OpenServiceW(scm, serviceName, ServiceQueryStatus);
            if (service == IntPtr.Zero)
                return false;

            try
            {
                return QueryServiceStatus(service, out var status) && status.CurrentState == ServiceRunning;
            }
            finally
            {
                CloseServiceHandle(service);
            }
        }
        finally
        {
            CloseServiceHandle(scm);
        }
    }

    internal static GpuEncoderInfo? TryClassifyGpu(string driverDesc, string provider, string deviceId)
    {
        if (driverDesc.Contains("Basic Display", StringComparison.OrdinalIgnoreCase) ||
            driverDesc.Contains("Basic Render", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (deviceId.Contains("VEN_10DE", StringComparison.OrdinalIgnoreCase) ||
            driverDesc.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
        {
            return new GpuEncoderInfo(driverDesc, "NVIDIA", "NVENC");
        }

        if (deviceId.Contains("VEN_1002", StringComparison.OrdinalIgnoreCase) ||
            driverDesc.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            driverDesc.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
        {
            return new GpuEncoderInfo(driverDesc, "AMD", "AMF");
        }

        if (deviceId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase) ||
            driverDesc.Contains("Intel", StringComparison.OrdinalIgnoreCase) ||
            provider.Contains("Intel", StringComparison.OrdinalIgnoreCase))
        {
            return new GpuEncoderInfo(driverDesc, "Intel", "QSV");
        }

        return null;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static string? FindRegisteredSunshine(RegistryKey root)
    {
        foreach (var serviceName in new[] { "SunshineService", "sunshinesvc" })
        {
            using var service = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\" + serviceName);
            var serviceExe = RegisteredSunshineExecutable(service?.GetValue("ImagePath") as string);
            if (serviceExe != null)
                return serviceExe;
        }

        using var uninstall = root.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall");
        if (uninstall is null)
            return null;

        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var entry = uninstall.OpenSubKey(name);
            if (entry is null)
                continue;

            var displayName = entry.GetValue("DisplayName") as string;
            if (!name.Contains("Sunshine", StringComparison.OrdinalIgnoreCase) &&
                !(displayName?.Contains("Sunshine", StringComparison.OrdinalIgnoreCase) ?? false))
            {
                continue;
            }

            if (entry.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
            {
                var candidate = RegisteredSunshineExecutable(Path.Combine(location.Trim().Trim('"'), "sunshine.exe"));
                if (candidate != null)
                    return candidate;
            }

            var iconPath = RegisteredSunshineExecutable(entry.GetValue("DisplayIcon") as string);
            if (iconPath != null)
                return iconPath;
        }

        return null;
    }

    private static string? RegisteredSunshineExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var expanded = Environment.ExpandEnvironmentVariables(value.Trim()).TrimStart('"');
        var end = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        while (end >= 0)
        {
            var path = expanded[..(end + 4)];
            bool endsAtFilename = path.Length == expanded.Length || expanded[path.Length] is '"' or ',' or ' ';
            if (endsAtFilename && Path.GetFileName(path).Equals("sunshine.exe", StringComparison.OrdinalIgnoreCase) && IsExecutable(path))
                return path;
            end = expanded.IndexOf(".exe", end + 4, StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }

    private static bool IsExecutable(string? path) =>
        path is not null && Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatusNative
    {
        public uint ServiceType;
        public uint CurrentState;
        public uint ControlsAccepted;
        public uint Win32ExitCode;
        public uint ServiceSpecificExitCode;
        public uint CheckPoint;
        public uint WaitHint;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenSCManagerW(string? machineName, string? databaseName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr OpenServiceW(IntPtr scManager, string serviceName, uint desiredAccess);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool StartServiceW(IntPtr service, int numServiceArgs, string[]? serviceArgVectors);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ControlService(IntPtr service, uint control, out ServiceStatusNative serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryServiceStatus(IntPtr service, out ServiceStatusNative serviceStatus);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseServiceHandle(IntPtr handle);
}
