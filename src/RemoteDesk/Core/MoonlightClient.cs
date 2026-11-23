using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Security;
using Microsoft.Win32;

namespace RemoteDesk;

internal static class MoonlightClient
{
    public static string? FindExecutable(string? selectedPath, string? managedPath = null)
    {
        if (IsExecutable(selectedPath))
            return selectedPath;

        if (IsExecutable(managedPath))
            return managedPath;

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Moonlight Game Streaming", "Moonlight.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Moonlight Game Streaming", "Moonlight.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Moonlight", "Moonlight.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Moonlight Game Streaming", "Moonlight.exe"),
            Path.Combine(AppContext.BaseDirectory, "Moonlight.exe"),
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
        foreach (var process in Process.GetProcessesByName("Moonlight"))
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
        using var appPath = root.OpenSubKey(software + @"App Paths\Moonlight.exe");
        var path = RegisteredExecutable(appPath?.GetValue("") as string);
        if (path != null)
            return path;

        using var uninstall = root.OpenSubKey(software + "Uninstall");
        if (uninstall is null)
            return null;

        foreach (var name in uninstall.GetSubKeyNames())
        {
            using var entry = uninstall.OpenSubKey(name);
            if (entry is null)
                continue;

            var displayName = entry.GetValue("DisplayName") as string;
            bool isMoonlight =
                name.Contains("Moonlight", StringComparison.OrdinalIgnoreCase) ||
                (displayName != null && displayName.Contains("Moonlight", StringComparison.OrdinalIgnoreCase));
            if (!isMoonlight)
                continue;

            if (entry.GetValue("InstallLocation") is string location && !string.IsNullOrWhiteSpace(location))
            {
                path = RegisteredExecutable(Path.Combine(location.Trim().Trim('"'), "Moonlight.exe"));
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
            if (endsAtFilename && Path.GetFileName(path).Equals("Moonlight.exe", StringComparison.OrdinalIgnoreCase) && IsExecutable(path))
                return path;
            end = expanded.IndexOf(".exe", end + 4, StringComparison.OrdinalIgnoreCase);
        }

        return null;
    }

    public static ProcessStartInfo CreateStreamConnection(
        string executable,
        DeviceProfile device,
        StreamOptions? overrideOptions = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!IsExecutable(executable))
            throw new FileNotFoundException("找不到 Moonlight 程序。", executable);

        var host = DeviceProfile.ValidateHostAddress(device.HostAddress, nameof(device));
        var appName = DeviceProfile.NormalizeStreamAppName(device.StreamAppName, nameof(device));
        var options = (overrideOptions ?? device.Stream).Validate();

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("stream");
        startInfo.ArgumentList.Add(host);
        startInfo.ArgumentList.Add(appName);
        startInfo.ArgumentList.Add("--resolution");
        startInfo.ArgumentList.Add(options.GetResolutionArgument());
        startInfo.ArgumentList.Add("--fps");
        startInfo.ArgumentList.Add(options.Fps.ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--bitrate");
        startInfo.ArgumentList.Add(options.GetBitrateKbps().ToString(CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--video-codec");
        startInfo.ArgumentList.Add(options.GetCodecArgument());
        startInfo.ArgumentList.Add("--video-decoder");
        startInfo.ArgumentList.Add(options.GetDecoderArgument());
        return startInfo;
    }

    public static ProcessStartInfo CreatePairConnection(string executable, string hostAddress)
    {
        if (!IsExecutable(executable))
            throw new FileNotFoundException("找不到 Moonlight 程序。", executable);

        var host = DeviceProfile.ValidateHostAddress(hostAddress, nameof(hostAddress));

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("pair");
        startInfo.ArgumentList.Add(host);
        return startInfo;
    }

    private static bool IsExecutable(string? path) =>
        path is not null && Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
}
