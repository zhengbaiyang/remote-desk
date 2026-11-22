using System.Diagnostics;
using System.IO;

namespace RemoteDesk;

internal static class RustDeskClient
{
    public static string? FindExecutable(string? selectedPath)
    {
        if (IsExecutable(selectedPath))
            return selectedPath;

        string[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "RustDesk", "RustDesk.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "RustDesk", "RustDesk.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "RustDesk", "RustDesk.exe"),
            Path.Combine(AppContext.BaseDirectory, "RustDesk.exe"),
        ];

        return candidates.FirstOrDefault(IsExecutable);
    }

    public static ProcessStartInfo CreateConnection(string executable, DeviceProfile device)
    {
        if (!IsExecutable(executable))
            throw new FileNotFoundException("找不到 RustDesk 程序。", executable);

        var startInfo = new ProcessStartInfo(executable)
        {
            WorkingDirectory = Path.GetDirectoryName(executable)!,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("--connect");
        startInfo.ArgumentList.Add(device.Id);
        return startInfo;
    }

    private static bool IsExecutable(string? path) =>
        path is not null && Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(path);
}
