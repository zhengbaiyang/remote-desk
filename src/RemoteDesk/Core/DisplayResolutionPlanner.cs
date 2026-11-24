using System.IO;

namespace RemoteDesk;

internal static class DisplayResolutionPlanner
{
    private const double AspectTolerance = 0.02;

    public static DisplayModeInfo SelectBestMode(
        int targetWidth,
        int targetHeight,
        int targetRefreshRate,
        DisplayModeInfo currentMode,
        IReadOnlyList<DisplayModeInfo> supportedModes)
    {
        if (targetWidth <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetWidth), "目标宽度必须大于 0。");
        if (targetHeight <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetHeight), "目标高度必须大于 0。");
        if (targetRefreshRate <= 0)
            throw new ArgumentOutOfRangeException(nameof(targetRefreshRate), "目标刷新率必须大于 0。");

        if (supportedModes is null || supportedModes.Count == 0)
            return currentMode;

        var exact = supportedModes
            .Where(m => m.Width == targetWidth && m.Height == targetHeight && m.RefreshRate > 0)
            .ToList();
        if (exact.Count > 0)
            return PickBestRefreshRate(exact, targetRefreshRate);

        var targetAspect = (double)targetWidth / targetHeight;
        var aspectMatched = supportedModes
            .Where(m =>
            {
                if (m.Width <= 0 || m.Height <= 0 || m.RefreshRate <= 0)
                    return false;
                var aspect = (double)m.Width / m.Height;
                return Math.Abs(aspect - targetAspect) / targetAspect <= AspectTolerance;
            })
            .ToList();

        if (aspectMatched.Count == 0)
            return currentMode;

        var notLargerPool = aspectMatched.Where(m => m.Width <= targetWidth).ToList();
        var pool = notLargerPool.Count > 0 ? notLargerPool : aspectMatched;

        var targetArea = (long)targetWidth * targetHeight;
        var bestArea = pool
            .OrderBy(m => Math.Abs((long)m.Width * m.Height - targetArea))
            .ThenByDescending(m => m.Width)
            .First();

        var sameRes = pool
            .Where(m => m.Width == bestArea.Width && m.Height == bestArea.Height)
            .ToList();

        return PickBestRefreshRate(sameRes, targetRefreshRate);
    }

    private static DisplayModeInfo PickBestRefreshRate(IReadOnlyList<DisplayModeInfo> candidates, int targetRefreshRate)
    {
        var atOrAbove = candidates
            .Where(m => m.RefreshRate >= targetRefreshRate)
            .OrderBy(m => m.RefreshRate)
            .FirstOrDefault();

        return atOrAbove ?? candidates.OrderByDescending(m => m.RefreshRate).First();
    }
}

internal static class FileTransferPathHelper
{
    private const int MaxFileNameLength = 180;

    private static readonly HashSet<char> InvalidChars =
    [
        '<', '>', ':', '"', '/', '\\', '|', '?', '*',
    ];

    private static readonly HashSet<string> ReservedDeviceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    public static string SanitizeFileName(string? rawName)
    {
        if (string.IsNullOrWhiteSpace(rawName))
            throw new ArgumentException("文件名不能为空。", nameof(rawName));

        if (rawName.Any(char.IsControl))
            throw new ArgumentException("文件名包含非法控制字符。", nameof(rawName));

        var rawSegments = rawName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        if (rawSegments.Length == 0)
            throw new ArgumentException("文件名无效。", nameof(rawName));

        foreach (var seg in rawSegments)
        {
            if (seg == "." || seg == "..")
                throw new ArgumentException("文件名不能包含路径跳转片段。", nameof(rawName));
        }

        var leaf = rawSegments[^1].Trim();
        var cleanedChars = new char[leaf.Length];
        var len = 0;
        foreach (var ch in leaf)
        {
            cleanedChars[len++] = InvalidChars.Contains(ch) ? '_' : ch;
        }

        var sanitized = new string(cleanedChars, 0, len).Trim().TrimEnd('.', ' ');
        if (string.IsNullOrWhiteSpace(sanitized) || sanitized is "." or "..")
            throw new ArgumentException("文件名无效。", nameof(rawName));

        var stem = Path.GetFileNameWithoutExtension(sanitized);
        if (ReservedDeviceNames.Contains(stem))
            throw new ArgumentException($"文件名不能使用系统保留名称「{stem}」。", nameof(rawName));

        if (sanitized.Length > MaxFileNameLength)
        {
            var ext = Path.GetExtension(sanitized);
            if (ext.Length >= MaxFileNameLength)
                sanitized = sanitized[..MaxFileNameLength];
            else
            {
                var allowedStemLen = Math.Max(1, MaxFileNameLength - ext.Length);
                var trimmedStem = stem.Length > allowedStemLen ? stem[..allowedStemLen].TrimEnd('.', ' ') : stem;
                sanitized = (string.IsNullOrEmpty(trimmedStem) ? "file" : trimmedStem) + ext;
            }
        }

        return sanitized;
    }

    public static string ResolveUniqueTargetPath(string targetDirectory, string sanitizedFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetDirectory);
        var safeLeaf = SanitizeFileName(sanitizedFileName);
        var candidate = Path.Combine(targetDirectory, safeLeaf);
        if (!File.Exists(candidate))
            return candidate;

        var stem = Path.GetFileNameWithoutExtension(safeLeaf);
        var ext = Path.GetExtension(safeLeaf);
        for (var i = 1; i < 10_000; i++)
        {
            var next = Path.Combine(targetDirectory, $"{stem} ({i}){ext}");
            if (!File.Exists(next))
                return next;
        }

        return Path.Combine(targetDirectory, $"{stem}-{Guid.NewGuid():N}{ext}");
    }
}
