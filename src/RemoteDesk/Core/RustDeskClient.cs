using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace RemoteDesk;

internal static class RustDeskClient
{
    public static string? FindExecutable(string? selectedPath, string? managedPath = null)
    {
        if (IsExecutable(selectedPath))
            return selectedPath;

        if (IsExecutable(managedPath))
            return managedPath;

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "RustDesk.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RustDesk", "RustDesk.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RustDesk", "RustDesk.exe"),
            Path.Combine(AppContext.BaseDirectory, "RustDesk.exe"),
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
                var installed = FindRegisteredClient(root);
                if (installed != null)
                    return installed;
            }
            catch (Exception error) when (error is UnauthorizedAccessException or SecurityException or IOException)
            {
                // Continue with the other registry views.
            }
        }

        string? running = null;
        foreach (var process in Process.GetProcessesByName("rustdesk"))
        {
            using (process)
            {
                try { running ??= RegisteredExecutable(process.MainModule?.FileName); }
                catch (Exception error) when (error is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        return running;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    internal static string? FindRegisteredClient(RegistryKey root)
    {
        const string software = @"SOFTWARE\Microsoft\Windows\CurrentVersion\";
        using var appPath = root.OpenSubKey(software + @"App Paths\rustdesk.exe");
        using var service = root.OpenSubKey(@"SYSTEM\CurrentControlSet\Services\RustDesk");
        var path = RegisteredExecutable(appPath?.GetValue("") as string)
            ?? RegisteredExecutable(service?.GetValue("ImagePath") as string);
        if (path != null)
            return path;

        using var uninstall = root.OpenSubKey(software + "Uninstall");
        if (uninstall is null)
            return null;
        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var entry = uninstall.OpenSubKey(name);
            if (entry is null || !(name.Equals("RustDesk", StringComparison.OrdinalIgnoreCase)
                || string.Equals(entry.GetValue("DisplayName") as string, "RustDesk", StringComparison.OrdinalIgnoreCase)))
                continue;
            if (entry.GetValue("InstallLocation") is string location)
            {
                path = RegisteredExecutable(Path.Combine(location.Trim().Trim('"'), "rustdesk.exe"));
                if (path != null)
                    return path;
            }
            path = RegisteredExecutable(entry.GetValue("DisplayIcon") as string);
            if (path != null)
                return path;
        }
        return null;
    }

    internal static string? RegisteredExecutable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;
        var expanded = Environment.ExpandEnvironmentVariables(value.Trim()).TrimStart('"');
        var end = expanded.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
        while (end >= 0)
        {
            var path = expanded[..(end + 4)];
            bool endsAtFilename = path.Length == expanded.Length || expanded[path.Length] is '"' or ',' or ' ';
            if (endsAtFilename && Path.GetFileName(path).Equals("rustdesk.exe", StringComparison.OrdinalIgnoreCase) && IsExecutable(path))
                return path;
            end = expanded.IndexOf(".exe", end + 4, StringComparison.OrdinalIgnoreCase);
        }
        return null;
    }

    public static ProcessStartInfo CreateConnection(string executable, DeviceProfile device)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!IsExecutable(executable))
            throw new FileNotFoundException("找不到 RustDesk 程序。", executable);

        var target = !string.IsNullOrEmpty(device.Id) ? device.Id : device.HostAddress;
        if (string.IsNullOrEmpty(target) || target.StartsWith('-'))
            throw new ArgumentException("设备缺少有效的 RustDesk 设备 ID 或主机地址。", nameof(device));

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--connect");
        startInfo.ArgumentList.Add(target);
        return startInfo;
    }

    public static ProcessStartInfo CreateHostStart(string executable)
    {
        if (!IsExecutable(executable))
            throw new FileNotFoundException("找不到 RustDesk 程序。", executable);

        return new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
        };
    }

    private static bool IsExecutable(string? path) =>
        path is not null && Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
}
