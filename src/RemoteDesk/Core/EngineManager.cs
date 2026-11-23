using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace RemoteDesk;

internal sealed class EngineManager
{
    private const string MetadataFileName = "engine-meta.json";
    private const string UserAgentProduct = "RemoteDesk";
    private const int MaxExecutableSearchDepth = 3;
    private const int MaxZipEntries = 20000;
    private const long MaxExtractedBytes = 2L * 1024 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly HttpClient SharedHttpClient = CreateDefaultHttpClient();

    private readonly HttpClient _httpClient;

    public EngineManager(string? rootDirectory = null, HttpClient? httpClient = null)
    {
        var resolvedRoot = string.IsNullOrWhiteSpace(rootDirectory)
            ? Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "RemoteDesk",
                "engines")
            : rootDirectory;

        RootDirectory = Path.GetFullPath(resolvedRoot);
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public string RootDirectory { get; }

    public string GetEngineDirectory(EngineKind kind)
    {
        var descriptor = EngineCatalog.Get(kind);
        var path = Path.GetFullPath(Path.Combine(RootDirectory, descriptor.Id));
        var normalizedRoot = EnsureTrailingSeparator(RootDirectory);
        if (!path.StartsWith(normalizedRoot, PathComparison))
            throw new InvalidOperationException($"引擎目录路径无效：{descriptor.Id}");
        return path;
    }

    public string? FindManagedExecutable(EngineKind kind)
    {
        var descriptor = EngineCatalog.Get(kind);
        var engineDir = GetEngineDirectory(kind);
        return FindExecutableInDirectory(engineDir, descriptor.ExecutableName, MaxExecutableSearchDepth);
    }

    public EngineStatus Inspect(EngineKind kind, string? fallbackSystemPath = null)
    {
        var descriptor = EngineCatalog.Get(kind);
        if (descriptor.IsPhase2Placeholder)
        {
            return new EngineStatus(
                Descriptor: descriptor,
                State: EngineInstallState.PlannedPhase2,
                ExecutablePath: null,
                InstalledVersion: null,
                DetailMessage: "阶段 2 计划集成（暂未启用）");
        }

        var managedExecutable = FindManagedExecutable(kind);
        if (managedExecutable is not null)
        {
            var metadata = TryReadMetadata(GetEngineDirectory(kind));
            var version = string.IsNullOrWhiteSpace(metadata?.Version) ? null : metadata.Version;
            var detail = version is not null
                ? $"托管引擎已就绪 ({version})"
                : "托管引擎已就绪";

            return new EngineStatus(
                Descriptor: descriptor,
                State: EngineInstallState.ManagedInstalled,
                ExecutablePath: managedExecutable,
                InstalledVersion: version,
                DetailMessage: detail);
        }

        if (IsExecutableFile(fallbackSystemPath))
        {
            return new EngineStatus(
                Descriptor: descriptor,
                State: EngineInstallState.SystemInstalled,
                ExecutablePath: fallbackSystemPath,
                InstalledVersion: null,
                DetailMessage: "检测到系统已安装版本");
        }

        return new EngineStatus(
            Descriptor: descriptor,
            State: EngineInstallState.NotInstalled,
            ExecutablePath: null,
            InstalledVersion: null,
            DetailMessage: "尚未安装");
    }

    public async Task<EngineStatus> InstallFromGitHubAsync(
        EngineKind kind,
        string? mirrorPrefix = null,
        IProgress<EngineInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var descriptor = EngineCatalog.Get(kind);
        EnsureInstallable(descriptor);

        progress?.Report(new EngineInstallProgress(kind, "正在获取最新发行版信息...", null));

        var release = await FetchLatestReleaseAsync(descriptor, ct).ConfigureAwait(false);
        var asset = EngineCatalog.SelectBestAsset(descriptor, release.Assets)
            ?? throw new InvalidOperationException(
                $"在 {descriptor.DisplayName} 最新版本中未找到适配 Windows x64 的便携安装包。");

        var stagingRoot = CreateStagingDirectory(descriptor.Id);
        try
        {
            var extension = Path.GetExtension(asset.Name);
            var downloadedFilePath = Path.Combine(stagingRoot, "download" + extension);
            var effectiveUrl = await DownloadAssetWithFallbackAsync(
                kind,
                asset.DownloadUrl,
                mirrorPrefix,
                downloadedFilePath,
                progress,
                ct).ConfigureAwait(false);

            return await DeployPackageAsync(
                descriptor,
                downloadedFilePath,
                version: release.Version,
                sourceUrl: effectiveUrl,
                stagingRoot,
                progress,
                ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    public async Task<EngineStatus> InstallFromLocalFileAsync(
        EngineKind kind,
        string localPath,
        IProgress<EngineInstallProgress>? progress = null,
        CancellationToken ct = default)
    {
        var descriptor = EngineCatalog.Get(kind);
        EnsureInstallable(descriptor);

        ArgumentException.ThrowIfNullOrWhiteSpace(localPath);
        var fullLocalPath = Path.GetFullPath(localPath);
        if (!File.Exists(fullLocalPath))
            throw new FileNotFoundException("找不到指定的本地引擎安装包。", fullLocalPath);

        var extension = Path.GetExtension(fullLocalPath);
        if (!string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new NotSupportedException("仅支持导入 .zip 便携压缩包或 .exe 可执行文件。");
        }

        var stagingRoot = CreateStagingDirectory(descriptor.Id);
        try
        {
            progress?.Report(new EngineInstallProgress(kind, "正在读取本地安装包...", 10));
            return await DeployPackageAsync(
                descriptor,
                fullLocalPath,
                version: "local",
                sourceUrl: fullLocalPath,
                stagingRoot,
                progress,
                ct).ConfigureAwait(false);
        }
        finally
        {
            TryDeleteDirectory(stagingRoot);
        }
    }

    public void UninstallManaged(EngineKind kind)
    {
        var engineDir = GetEngineDirectory(kind);
        if (Directory.Exists(engineDir))
            Directory.Delete(engineDir, recursive: true);
    }

    internal static void ExtractZipSafely(string zipPath, string destinationDirectory, CancellationToken ct = default)
    {
        var fullDestRoot = Path.GetFullPath(destinationDirectory);
        Directory.CreateDirectory(fullDestRoot);
        var normalizedRoot = EnsureTrailingSeparator(fullDestRoot);

        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count > MaxZipEntries)
            throw new InvalidDataException($"压缩包条目数过多（超过 {MaxZipEntries} 个），已拒绝解压。");

        long totalExtractedBytes = 0;
        var buffer = new byte[81920];

        foreach (var entry in archive.Entries)
        {
            ct.ThrowIfCancellationRequested();

            var rawEntryName = entry.FullName;
            if (string.IsNullOrWhiteSpace(rawEntryName))
                continue;

            ValidateZipEntryPath(rawEntryName);

            if (IsUnixSymlink(entry))
                throw new InvalidDataException($"压缩包包含不安全的符号链接条目：{rawEntryName}");

            var isDirectoryEntry = rawEntryName.EndsWith('/') || rawEntryName.EndsWith('\\') || string.IsNullOrEmpty(entry.Name);
            var relativePath = rawEntryName
                .Replace('/', Path.DirectorySeparatorChar)
                .Replace('\\', Path.DirectorySeparatorChar)
                .TrimEnd(Path.DirectorySeparatorChar);

            if (string.IsNullOrEmpty(relativePath))
                continue;

            var destinationPath = Path.GetFullPath(Path.Combine(fullDestRoot, relativePath));

            if (isDirectoryEntry)
            {
                if (!destinationPath.Equals(fullDestRoot, PathComparison) &&
                    !destinationPath.StartsWith(normalizedRoot, PathComparison))
                {
                    throw new InvalidDataException($"压缩包包含路径穿越目录条目：{rawEntryName}");
                }

                Directory.CreateDirectory(destinationPath);
                continue;
            }

            if (!destinationPath.StartsWith(normalizedRoot, PathComparison))
                throw new InvalidDataException($"压缩包包含路径穿越文件条目：{rawEntryName}");

            var parentDir = Path.GetDirectoryName(destinationPath);
            if (string.IsNullOrEmpty(parentDir) ||
                (!parentDir.Equals(fullDestRoot, PathComparison) &&
                 !parentDir.StartsWith(normalizedRoot, PathComparison)))
            {
                throw new InvalidDataException($"压缩包目标父目录越界：{rawEntryName}");
            }

            Directory.CreateDirectory(parentDir);

            using var sourceStream = entry.Open();
            using var targetStream = new FileStream(destinationPath, FileMode.Create, FileAccess.Write, FileShare.None);
            int bytesRead;
            while ((bytesRead = sourceStream.Read(buffer, 0, buffer.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                totalExtractedBytes += bytesRead;
                if (totalExtractedBytes > MaxExtractedBytes)
                    throw new InvalidDataException("压缩包解压后体积超出安全上限，已中止解压。");
                targetStream.Write(buffer, 0, bytesRead);
            }
        }
    }

    private async Task<EngineStatus> DeployPackageAsync(
        EngineDescriptor descriptor,
        string packagePath,
        string? version,
        string sourceUrl,
        string stagingRoot,
        IProgress<EngineInstallProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var contentDir = Path.Combine(stagingRoot, "content");
        Directory.CreateDirectory(contentDir);

        var extension = Path.GetExtension(packagePath);
        if (string.Equals(extension, ".zip", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new EngineInstallProgress(descriptor.Kind, "正在安全解压安装包...", 85));
            await Task.Run(() => ExtractZipSafely(packagePath, contentDir, ct), ct).ConfigureAwait(false);
        }
        else if (string.Equals(extension, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            progress?.Report(new EngineInstallProgress(descriptor.Kind, "正在复制引擎可执行文件...", 85));
            var targetExePath = Path.Combine(contentDir, descriptor.ExecutableName);
            await CopyFileAsync(packagePath, targetExePath, ct).ConfigureAwait(false);
        }
        else
        {
            throw new NotSupportedException($"不支持的安装包格式：{extension}");
        }

        progress?.Report(new EngineInstallProgress(descriptor.Kind, "正在校验引擎文件...", 92));
        var verifiedExe = FindExecutableInDirectory(contentDir, descriptor.ExecutableName, MaxExecutableSearchDepth);
        if (verifiedExe is null)
            throw new InvalidDataException($"安装包中未找到预期的可执行文件 {descriptor.ExecutableName}。");

        var metadata = new EngineMetadata(
            Version: string.IsNullOrWhiteSpace(version) ? null : version.Trim(),
            SourceUrl: sourceUrl,
            InstalledAtUtc: DateTimeOffset.UtcNow);
        var metadataPath = Path.Combine(contentDir, MetadataFileName);
        await File.WriteAllTextAsync(metadataPath, JsonSerializer.Serialize(metadata, JsonOptions), ct)
            .ConfigureAwait(false);

        progress?.Report(new EngineInstallProgress(descriptor.Kind, "正在完成部署...", 96));
        var targetDir = GetEngineDirectory(descriptor.Kind);
        AtomicReplaceDirectory(contentDir, targetDir);

        progress?.Report(new EngineInstallProgress(descriptor.Kind, "安装完成", 100));
        return Inspect(descriptor.Kind);
    }

    private async Task<ReleaseInfo> FetchLatestReleaseAsync(EngineDescriptor descriptor, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, descriptor.ReleasesApiUrl);
        request.Headers.UserAgent.ParseAdd(UserAgentProduct);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var root = doc.RootElement;
        string? version = null;
        if (root.TryGetProperty("tag_name", out var tagProp) && tagProp.ValueKind == JsonValueKind.String)
            version = tagProp.GetString();
        else if (root.TryGetProperty("name", out var nameProp) && nameProp.ValueKind == JsonValueKind.String)
            version = nameProp.GetString();

        var assets = new List<(string Name, string DownloadUrl)>();
        if (root.TryGetProperty("assets", out var assetsProp) && assetsProp.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in assetsProp.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                    continue;

                var name = item.TryGetProperty("name", out var assetName) && assetName.ValueKind == JsonValueKind.String
                    ? assetName.GetString()
                    : null;
                var url = item.TryGetProperty("browser_download_url", out var assetUrl) && assetUrl.ValueKind == JsonValueKind.String
                    ? assetUrl.GetString()
                    : null;

                if (!string.IsNullOrWhiteSpace(name) && !string.IsNullOrWhiteSpace(url))
                    assets.Add((name, url));
            }
        }

        return new ReleaseInfo(version, assets);
    }

    private async Task<string> DownloadAssetWithFallbackAsync(
        EngineKind kind,
        string directUrl,
        string? mirrorPrefix,
        string destinationFilePath,
        IProgress<EngineInstallProgress>? progress,
        CancellationToken ct)
    {
        var mirroredUrl = EngineCatalog.ApplyMirror(directUrl, mirrorPrefix);
        var hasMirror = !string.Equals(mirroredUrl, directUrl, StringComparison.Ordinal);

        if (hasMirror)
        {
            try
            {
                progress?.Report(new EngineInstallProgress(kind, "正在通过加速镜像下载...", 0));
                await DownloadFileAsync(kind, mirroredUrl, destinationFilePath, "正在通过加速镜像下载...", progress, ct)
                    .ConfigureAwait(false);
                return mirroredUrl;
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                TryDeleteFile(destinationFilePath);
                progress?.Report(new EngineInstallProgress(kind, "镜像下载失败，正在回退直连 GitHub...", 0));
            }
        }

        progress?.Report(new EngineInstallProgress(kind, "正在从 GitHub 下载...", 0));
        await DownloadFileAsync(kind, directUrl, destinationFilePath, "正在从 GitHub 下载...", progress, ct)
            .ConfigureAwait(false);
        return directUrl;
    }

    private async Task DownloadFileAsync(
        EngineKind kind,
        string url,
        string destinationFilePath,
        string stageLabel,
        IProgress<EngineInstallProgress>? progress,
        CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd(UserAgentProduct);

        using var response = await _httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var totalBytes = response.Content.Headers.ContentLength;
        await using var contentStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            destinationFilePath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);

        var buffer = new byte[81920];
        long totalRead = 0;
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
                    // Reserve 0..80% for download, 80..100% for extraction & verification.
                    var pct = Math.Clamp((double)totalRead / totalBytes.Value * 80.0, 0.0, 80.0);
                    if (pct - lastReportedPct >= 1.0 || totalRead >= totalBytes.Value)
                    {
                        lastReportedPct = pct;
                        progress.Report(new EngineInstallProgress(kind, stageLabel, Math.Round(pct, 1)));
                    }
                }
                else if (lastReportedPct < 0)
                {
                    lastReportedPct = 0;
                    progress.Report(new EngineInstallProgress(kind, stageLabel, null));
                }
            }
        }

        if (totalRead == 0)
            throw new InvalidDataException("下载的文件为空。");
    }

    private string CreateStagingDirectory(string engineId)
    {
        var stagingRoot = Path.Combine(RootDirectory, ".staging", $"{engineId}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingRoot);
        return stagingRoot;
    }

    private static void AtomicReplaceDirectory(string stagingContentDir, string targetDir)
    {
        var parentDir = Path.GetDirectoryName(targetDir)!;
        Directory.CreateDirectory(parentDir);

        if (!Directory.Exists(targetDir))
        {
            Directory.Move(stagingContentDir, targetDir);
            return;
        }

        var backupDir = targetDir + ".old-" + Guid.NewGuid().ToString("N");
        Directory.Move(targetDir, backupDir);
        try
        {
            Directory.Move(stagingContentDir, targetDir);
        }
        catch
        {
            try
            {
                if (!Directory.Exists(targetDir) && Directory.Exists(backupDir))
                    Directory.Move(backupDir, targetDir);
            }
            catch
            {
                // Best-effort rollback; preserve original exception.
            }

            throw;
        }

        TryDeleteDirectory(backupDir);
    }

    private static string? FindExecutableInDirectory(string rootDir, string executableName, int maxDepth)
    {
        if (!Directory.Exists(rootDir))
            return null;

        var fullRoot = Path.GetFullPath(rootDir);
        var normalizedRoot = EnsureTrailingSeparator(fullRoot);
        var queue = new Queue<(string Dir, int Depth)>();
        queue.Enqueue((fullRoot, 0));

        while (queue.Count > 0)
        {
            var (currentDir, depth) = queue.Dequeue();

            string[] files;
            try
            {
                files = Directory.GetFiles(currentDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            foreach (var file in files)
            {
                if (!string.Equals(Path.GetFileName(file), executableName, StringComparison.OrdinalIgnoreCase))
                    continue;

                var fullFile = Path.GetFullPath(file);
                if (!fullFile.StartsWith(normalizedRoot, PathComparison))
                    continue;

                if (IsReparsePoint(fullFile))
                    continue;

                return fullFile;
            }

            if (depth >= maxDepth)
                continue;

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(currentDir);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            Array.Sort(subDirs, StringComparer.OrdinalIgnoreCase);
            foreach (var subDir in subDirs)
            {
                var fullSubDir = Path.GetFullPath(subDir);
                if (!fullSubDir.StartsWith(normalizedRoot, PathComparison))
                    continue;

                if (IsReparsePoint(fullSubDir))
                    continue;

                queue.Enqueue((fullSubDir, depth + 1));
            }
        }

        return null;
    }

    private static void ValidateZipEntryPath(string entryFullName)
    {
        if (entryFullName.IndexOf('\0') >= 0)
            throw new InvalidDataException($"压缩包条目包含非法空字符：{entryFullName}");

        if (entryFullName.StartsWith('/') ||
            entryFullName.StartsWith('\\') ||
            Path.IsPathRooted(entryFullName) ||
            entryFullName.Contains(':'))
        {
            throw new InvalidDataException($"压缩包包含绝对路径或盘符条目：{entryFullName}");
        }

        var segments = entryFullName.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var segment in segments)
        {
            if (segment == "..")
                throw new InvalidDataException($"压缩包包含路径穿越条目：{entryFullName}");
        }
    }

    private static bool IsUnixSymlink(ZipArchiveEntry entry)
    {
        // High 16 bits of ExternalAttributes store Unix file mode when created on Unix/POSIX.
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000;
    }

    private static bool IsReparsePoint(string path)
    {
        try
        {
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static bool IsExecutableFile(string? path) =>
        !string.IsNullOrWhiteSpace(path) &&
        Path.IsPathFullyQualified(path) &&
        string.Equals(Path.GetExtension(path), ".exe", StringComparison.OrdinalIgnoreCase) &&
        File.Exists(path);

    private static EngineMetadata? TryReadMetadata(string engineDir)
    {
        var metadataPath = Path.Combine(engineDir, MetadataFileName);
        if (!File.Exists(metadataPath))
            return null;

        try
        {
            var json = File.ReadAllText(metadataPath);
            return JsonSerializer.Deserialize<EngineMetadata>(json, JsonOptions);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }

    private static void EnsureInstallable(EngineDescriptor descriptor)
    {
        if (descriptor.IsPhase2Placeholder)
            throw new NotSupportedException($"{descriptor.DisplayName} 将在阶段 2 提供，当前暂不支持安装。");
    }

    private static async Task CopyFileAsync(string sourcePath, string destinationPath, CancellationToken ct)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 81920,
            useAsync: true);
        await using var destination = new FileStream(
            destinationPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 81920,
            useAsync: true);
        await source.CopyToAsync(destination, ct).ConfigureAwait(false);
    }

    private static string EnsureTrailingSeparator(string fullPath) =>
        fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static HttpClient CreateDefaultHttpClient() => new()
    {
        Timeout = TimeSpan.FromMinutes(15),
    };

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Ignore best-effort cleanup errors.
        }
    }

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

    private sealed record ReleaseInfo(
        string? Version,
        IReadOnlyList<(string Name, string DownloadUrl)> Assets);

    private sealed record EngineMetadata(
        string? Version,
        string? SourceUrl,
        DateTimeOffset InstalledAtUtc);
}
