using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RemoteDesk;

internal sealed record ReleaseAssetInfo(
    string Name,
    string DownloadUrl,
    long SizeBytes);

internal sealed record ReleaseCheckResult(
    bool IsUpdateAvailable,
    string CurrentVersion,
    string LatestVersion,
    string TagName,
    string ReleaseName,
    string ReleaseNotes,
    string ReleasePageUrl,
    ReleaseAssetInfo? SetupAsset,
    bool SupportsInPlaceVelopack);

internal sealed record UpdateDownloadProgress(
    string Stage,
    double? Percentage,
    long BytesReceived,
    long? TotalBytes);

internal sealed class AppUpdateService
{
    public const string DefaultRepository = "zhengbaiyang/remote-desk";

    private const string UserAgentProduct = "RemoteDesk";
    private const string PreferredSetupAssetName = "RemoteDesk.Windows-win-Setup.exe";
    private const string ReleasesManifestAssetName = "releases.win.json";
    private const string UpdatesFolderName = "RemoteDesk-Updates";
    private const int MaxRepositorySegmentLength = 100;
    private const int MaxReleaseNotesLength = 4000;
    private const int MaxFileNameLength = 120;

    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(15);
    private static readonly HttpClient SharedHttpClient = CreateDefaultHttpClient();

    private static readonly HashSet<string> WindowsReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9",
    };

    private readonly HttpClient _httpClient;
    private readonly string _downloadDirectory;

    public AppUpdateService(HttpClient? httpClient = null)
        : this(httpClient, null)
    {
    }

    internal AppUpdateService(HttpClient? httpClient, string? downloadDirectory)
    {
        _httpClient = httpClient ?? SharedHttpClient;
        var resolvedDir = string.IsNullOrWhiteSpace(downloadDirectory)
            ? Path.Combine(Path.GetTempPath(), UpdatesFolderName)
            : downloadDirectory;
        _downloadDirectory = Path.GetFullPath(resolvedDir);
    }

    public static string NormalizeRepository(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return DefaultRepository;

        if (input.Any(char.IsControl))
            throw new ArgumentException("仓库地址包含非法控制字符。", nameof(input));

        var trimmed = input.Trim();
        if (trimmed.Any(char.IsWhiteSpace))
            throw new ArgumentException("仓库地址不能包含空白字符。", nameof(input));

        string rawPath;
        if (trimmed.Contains("://", StringComparison.Ordinal))
        {
            const string githubHttpsPrefix = "https://github.com/";
            if (!trimmed.StartsWith(githubHttpsPrefix, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("仅支持 GitHub HTTPS 仓库地址（https://github.com/owner/repo）。", nameof(input));

            if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) ||
                !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) ||
                !string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase) ||
                !uri.IsDefaultPort ||
                !string.IsNullOrEmpty(uri.UserInfo) ||
                !string.IsNullOrEmpty(uri.Query) ||
                !string.IsNullOrEmpty(uri.Fragment))
            {
                throw new ArgumentException("GitHub 仓库 URL 格式无效。", nameof(input));
            }

            rawPath = trimmed[githubHttpsPrefix.Length..];
        }
        else
        {
            rawPath = trimmed;
        }

        rawPath = rawPath.TrimEnd('/');
        if (rawPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            rawPath = rawPath[..^4];

        var segments = rawPath.Split('/');
        if (segments.Length != 2)
            throw new ArgumentException("仓库格式必须为 owner/repo 或 https://github.com/owner/repo。", nameof(input));

        var owner = segments[0];
        var repo = segments[1];
        if (!IsValidRepositorySegment(owner) || !IsValidRepositorySegment(repo))
            throw new ArgumentException("仓库拥有者或仓库名包含非法字符或路径穿越序列。", nameof(input));

        return $"{owner}/{repo}";
    }

    public static string BuildVelopackFeedUrl(string? repository, string? mirrorPrefix = null)
    {
        var normalizedRepo = NormalizeRepository(repository);
        var directUrl = $"https://github.com/{normalizedRepo}/releases/latest/download/";
        return EngineCatalog.ApplyMirror(directUrl, mirrorPrefix);
    }

    public static string BuildLatestReleaseApiUrl(string? repository)
    {
        var normalizedRepo = NormalizeRepository(repository);
        return $"https://api.github.com/repos/{normalizedRepo}/releases/latest";
    }

    public static string BuildReleasesPageUrl(string? repository)
    {
        var normalizedRepo = NormalizeRepository(repository);
        return $"https://github.com/{normalizedRepo}/releases/latest";
    }

    public static bool TryParseSemVersion(string? raw, out Version version)
    {
        version = new Version(0, 0, 0);
        if (string.IsNullOrWhiteSpace(raw))
            return false;

        if (raw.Any(char.IsControl))
            return false;

        var text = raw.Trim();
        if (text.Any(char.IsWhiteSpace))
            return false;

        if (text.Length > 1 && text[0] is 'v' or 'V')
            text = text[1..];

        var suffixIndex = text.IndexOfAny(['-', '+']);
        if (suffixIndex >= 0)
        {
            if (suffixIndex == 0 || suffixIndex == text.Length - 1)
                return false;
            text = text[..suffixIndex];
        }

        var parts = text.Split('.');
        if (parts.Length is < 2 or > 4)
            return false;

        var numbers = new int[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0 || !part.All(IsAsciiDigit))
                return false;

            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var value) || value < 0)
                return false;

            numbers[i] = value;
        }

        version = parts.Length switch
        {
            2 => new Version(numbers[0], numbers[1], 0),
            3 => new Version(numbers[0], numbers[1], numbers[2]),
            _ => new Version(numbers[0], numbers[1], numbers[2], numbers[3]),
        };
        return true;
    }

    public static bool IsNewerVersion(string? currentVersion, string? latestTagOrVersion)
    {
        if (!TryParseSemVersion(currentVersion, out var current) ||
            !TryParseSemVersion(latestTagOrVersion, out var latest))
        {
            return false;
        }

        return NormalizeForComparison(latest) > NormalizeForComparison(current);
    }

    public static ReleaseCheckResult ParseLatestReleaseJson(string json, string currentVersion, string? repository = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);
        var normalizedRepo = NormalizeRepository(repository);
        var normalizedCurrent = string.IsNullOrWhiteSpace(currentVersion) ? "0.0.0" : currentVersion.Trim();

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new JsonException("GitHub Release JSON 根节点必须为对象。");

        var tagName = GetOptionalString(root, "tag_name");
        var releaseName = GetOptionalString(root, "name");
        if (string.IsNullOrEmpty(releaseName))
            releaseName = tagName;

        var releaseNotes = TruncateReleaseNotes(GetOptionalString(root, "body"));

        var htmlUrl = GetOptionalString(root, "html_url");
        var releasePageUrl = IsValidHttpsUrl(htmlUrl)
            ? htmlUrl
            : BuildReleasesPageUrl(normalizedRepo);

        var versionSource = !string.IsNullOrEmpty(tagName) ? tagName : releaseName;
        var latestVersion = StripLeadingVersionPrefix(versionSource);
        var isUpdateAvailable = IsNewerVersion(normalizedCurrent, versionSource);

        var assets = ParseAssets(root);
        var setupAsset = SelectBestSetupAsset(assets);
        var hasReleasesJson = assets.Any(a =>
            string.Equals(a.Name, ReleasesManifestAssetName, StringComparison.OrdinalIgnoreCase));
        var hasNupkg = assets.Any(a =>
            a.Name.EndsWith(".nupkg", StringComparison.OrdinalIgnoreCase));
        var supportsInPlaceVelopack = hasReleasesJson && hasNupkg;

        return new ReleaseCheckResult(
            IsUpdateAvailable: isUpdateAvailable,
            CurrentVersion: normalizedCurrent,
            LatestVersion: latestVersion,
            TagName: tagName,
            ReleaseName: releaseName,
            ReleaseNotes: releaseNotes,
            ReleasePageUrl: releasePageUrl,
            SetupAsset: setupAsset,
            SupportsInPlaceVelopack: supportsInPlaceVelopack);
    }

    public async Task<ReleaseCheckResult> CheckLatestReleaseAsync(
        string currentVersion,
        string? repository = null,
        CancellationToken ct = default)
    {
        var normalizedRepo = NormalizeRepository(repository);
        var apiUrl = BuildLatestReleaseApiUrl(normalizedRepo);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(CheckTimeout);

        using var request = new HttpRequestMessage(HttpMethod.Get, apiUrl);
        request.Headers.UserAgent.ParseAdd(UserAgentProduct);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeoutCts.Token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
        return ParseLatestReleaseJson(json, currentVersion, normalizedRepo);
    }

    public async Task<string> DownloadInstallerAsync(
        ReleaseAssetInfo asset,
        string? mirrorPrefix = null,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);

        var directUrl = asset.DownloadUrl?.Trim() ?? string.Empty;
        if (!IsValidHttpsUrl(directUrl))
            throw new ArgumentException("更新安装包下载地址必须是有效的 HTTPS URL。", nameof(asset));

        Directory.CreateDirectory(_downloadDirectory);

        var safeFileName = SanitizeInstallerFileName(asset.Name);
        var normalizedDir = EnsureTrailingSeparator(_downloadDirectory);
        var finalPath = Path.GetFullPath(Path.Combine(_downloadDirectory, safeFileName));
        if (!finalPath.StartsWith(normalizedDir, PathComparison))
            throw new InvalidOperationException("安装包保存路径越界。");

        var partPath = Path.GetFullPath(Path.Combine(_downloadDirectory, $"{safeFileName}.{Guid.NewGuid():N}.part"));
        if (!partPath.StartsWith(normalizedDir, PathComparison))
            throw new InvalidOperationException("临时下载路径越界。");

        var expectedSize = asset.SizeBytes > 0 ? asset.SizeBytes : (long?)null;
        var mirroredUrl = EngineCatalog.ApplyMirror(directUrl, mirrorPrefix);
        var hasMirror = !string.Equals(mirroredUrl, directUrl, StringComparison.Ordinal);

        try
        {
            long bytesDownloaded;
            if (hasMirror)
            {
                try
                {
                    bytesDownloaded = await DownloadToPartFileAsync(
                        mirroredUrl,
                        partPath,
                        "正在通过加速镜像下载更新安装包...",
                        expectedSize,
                        progress,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    TryDeleteFile(partPath);
                    progress?.Report(new UpdateDownloadProgress(
                        "镜像下载失败，正在回退直连 GitHub...",
                        0,
                        0,
                        expectedSize));

                    bytesDownloaded = await DownloadToPartFileAsync(
                        directUrl,
                        partPath,
                        "正在从 GitHub 下载更新安装包...",
                        expectedSize,
                        progress,
                        ct).ConfigureAwait(false);
                }
            }
            else
            {
                bytesDownloaded = await DownloadToPartFileAsync(
                    directUrl,
                    partPath,
                    "正在从 GitHub 下载更新安装包...",
                    expectedSize,
                    progress,
                    ct).ConfigureAwait(false);
            }

            if (bytesDownloaded <= 0)
                throw new InvalidDataException("下载的更新安装包为空。");

            File.Move(partPath, finalPath, overwrite: true);

            var fileInfo = new FileInfo(finalPath);
            if (!fileInfo.Exists || fileInfo.Length <= 0)
                throw new InvalidDataException("更新安装包校验失败：目标文件为空。");

            progress?.Report(new UpdateDownloadProgress(
                "更新安装包下载完成",
                100.0,
                fileInfo.Length,
                fileInfo.Length));

            return finalPath;
        }
        catch
        {
            TryDeleteFile(partPath);
            throw;
        }
    }

    public static ProcessStartInfo CreateInstallerLaunch(string installerPath)
    {
        if (string.IsNullOrWhiteSpace(installerPath))
            throw new ArgumentException("安装包路径不能为空。", nameof(installerPath));

        if (!Path.IsPathFullyQualified(installerPath))
            throw new ArgumentException("安装包路径必须是完整的绝对路径。", nameof(installerPath));

        if (!string.Equals(Path.GetExtension(installerPath), ".exe", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("更新安装包必须是 .exe 可执行文件。", nameof(installerPath));

        if (!File.Exists(installerPath))
            throw new FileNotFoundException("找不到指定的更新安装包。", installerPath);

        return new ProcessStartInfo(installerPath)
        {
            WorkingDirectory = Path.GetDirectoryName(installerPath) ?? string.Empty,
            UseShellExecute = true,
        };
    }

    private async Task<long> DownloadToPartFileAsync(
        string url,
        string partPath,
        string stageLabel,
        long? fallbackTotalBytes,
        IProgress<UpdateDownloadProgress>? progress,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgentProduct);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength is > 0
            ? response.Content.Headers.ContentLength
            : fallbackTotalBytes;

        progress?.Report(new UpdateDownloadProgress(
            stageLabel,
            totalBytes is > 0 ? 0.0 : null,
            0,
            totalBytes));

        long totalRead = 0;
        await using (var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var fileStream = new FileStream(
            partPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true))
        {
            var buffer = new byte[81920];
            double lastReportedPct = -1;

            while (true)
            {
                var bytesRead = await contentStream.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
                if (bytesRead == 0)
                    break;

                await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), ct).ConfigureAwait(false);
                totalRead += bytesRead;

                if (progress is not null)
                {
                    if (totalBytes is > 0)
                    {
                        var pct = Math.Clamp((double)totalRead / totalBytes.Value * 100.0, 0.0, 99.9);
                        if (pct - lastReportedPct >= 1.0 || totalRead >= totalBytes.Value)
                        {
                            lastReportedPct = pct;
                            progress.Report(new UpdateDownloadProgress(
                                stageLabel,
                                Math.Round(pct, 1),
                                totalRead,
                                totalBytes));
                        }
                    }
                    else if (lastReportedPct < 0)
                    {
                        lastReportedPct = 0;
                        progress.Report(new UpdateDownloadProgress(stageLabel, null, totalRead, null));
                    }
                }
            }

            await fileStream.FlushAsync(ct).ConfigureAwait(false);
        }

        if (totalRead == 0)
            throw new InvalidDataException("下载的更新安装包为空。");

        return totalRead;
    }

    private static List<ReleaseAssetInfo> ParseAssets(JsonElement root)
    {
        var result = new List<ReleaseAssetInfo>();
        if (!root.TryGetProperty("assets", out var assetsProp) || assetsProp.ValueKind != JsonValueKind.Array)
            return result;

        foreach (var item in assetsProp.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object)
                continue;

            var name = GetOptionalString(item, "name");
            var downloadUrl = GetOptionalString(item, "browser_download_url");
            if (string.IsNullOrEmpty(name) || !IsValidHttpsUrl(downloadUrl))
                continue;

            long sizeBytes = 0;
            if (item.TryGetProperty("size", out var sizeProp) &&
                sizeProp.ValueKind == JsonValueKind.Number &&
                sizeProp.TryGetInt64(out var parsedSize) &&
                parsedSize >= 0)
            {
                sizeBytes = parsedSize;
            }

            result.Add(new ReleaseAssetInfo(name, downloadUrl, sizeBytes));
        }

        return result;
    }

    private static ReleaseAssetInfo? SelectBestSetupAsset(IReadOnlyList<ReleaseAssetInfo> assets)
    {
        ReleaseAssetInfo? best = null;
        int bestTier = int.MaxValue;
        int bestScore = int.MinValue;

        foreach (var asset in assets)
        {
            var name = asset.Name;
            if (!name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                continue;

            if (name.Contains(".sha256", StringComparison.OrdinalIgnoreCase) ||
                name.Contains(".sig", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            int tier;
            if (string.Equals(name, PreferredSetupAssetName, StringComparison.OrdinalIgnoreCase))
                tier = 0;
            else if (name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                tier = 1;
            else
                tier = 2;

            var score = 0;
            if (name.Contains("RemoteDesk", StringComparison.OrdinalIgnoreCase))
                score += 20;
            if (name.Contains("win", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("x64", StringComparison.OrdinalIgnoreCase))
            {
                score += 10;
            }

            if (best is null ||
                tier < bestTier ||
                (tier == bestTier && score > bestScore) ||
                (tier == bestTier && score == bestScore && name.Length < best.Name.Length) ||
                (tier == bestTier && score == bestScore && name.Length == best.Name.Length &&
                 StringComparer.OrdinalIgnoreCase.Compare(name, best.Name) < 0))
            {
                best = asset;
                bestTier = tier;
                bestScore = score;
            }
        }

        return best;
    }

    private static string SanitizeInstallerFileName(string? rawName)
    {
        var candidate = rawName?.Trim() ?? string.Empty;
        var lastSlash = candidate.LastIndexOfAny(['/', '\\']);
        if (lastSlash >= 0)
            candidate = candidate[(lastSlash + 1)..];

        Span<char> chars = candidate.Length <= MaxFileNameLength
            ? stackalloc char[candidate.Length]
            : new char[MaxFileNameLength];

        var written = 0;
        for (var i = 0; i < candidate.Length && written < chars.Length; i++)
        {
            var c = candidate[i];
            chars[written++] = IsSafeFileNameChar(c) ? c : '_';
        }

        var sanitized = new string(chars[..written]).Trim('.', ' ');
        while (sanitized.Contains("..", StringComparison.Ordinal))
            sanitized = sanitized.Replace("..", "_", StringComparison.Ordinal);

        if (!sanitized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            sanitized += ".exe";

        var stem = Path.GetFileNameWithoutExtension(sanitized).Trim('.', ' ');
        var firstSegment = stem.Split('.')[0];
        if (string.IsNullOrEmpty(stem) || WindowsReservedNames.Contains(firstSegment))
            return PreferredSetupAssetName;

        return stem + ".exe";
    }

    private static string TruncateReleaseNotes(string rawNotes)
    {
        if (string.IsNullOrWhiteSpace(rawNotes))
            return string.Empty;

        var normalized = rawNotes.Replace("\r\n", "\n", StringComparison.Ordinal).Trim();
        if (normalized.Length <= MaxReleaseNotesLength)
            return normalized;

        var slice = normalized[..MaxReleaseNotesLength];
        var lastNewLine = slice.LastIndexOf('\n');
        if (lastNewLine >= MaxReleaseNotesLength - 200)
            slice = slice[..lastNewLine];

        return slice.TrimEnd() + "\n…";
    }

    private static string StripLeadingVersionPrefix(string raw)
    {
        var trimmed = raw.Trim();
        if (trimmed.Length > 1 && trimmed[0] is 'v' or 'V' && IsAsciiDigit(trimmed[1]))
            return trimmed[1..];
        return trimmed;
    }

    private static string GetOptionalString(JsonElement element, string propertyName) =>
        element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String
            ? (prop.GetString()?.Trim() ?? string.Empty)
            : string.Empty;

    private static bool IsValidRepositorySegment(string segment)
    {
        if (segment.Length is 0 or > MaxRepositorySegmentLength)
            return false;

        if (segment is "." or ".." || segment.Contains("..", StringComparison.Ordinal))
            return false;

        foreach (var c in segment)
        {
            if (!IsSafeFileNameChar(c))
                return false;
        }

        return true;
    }

    private static bool IsSafeFileNameChar(char c) =>
        (c is >= 'A' and <= 'Z') ||
        (c is >= 'a' and <= 'z') ||
        IsAsciiDigit(c) ||
        c is '.' or '_' or '-';

    private static bool IsAsciiDigit(char c) => c is >= '0' and <= '9';

    private static bool IsValidHttpsUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url) &&
        Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
        string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
        !string.IsNullOrWhiteSpace(uri.Host) &&
        string.IsNullOrEmpty(uri.UserInfo);

    private static Version NormalizeForComparison(Version version) =>
        new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));

    private static string EnsureTrailingSeparator(string fullPath) =>
        fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static HttpClient CreateDefaultHttpClient() => new()
    {
        Timeout = TimeSpan.FromMinutes(15),
    };

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
            // Ignore best-effort cleanup errors.
        }
    }
}
