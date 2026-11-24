using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Velopack;

namespace RemoteDesk;

public partial class MainWindow
{
    private readonly AppUpdateService updateService = new();
    private readonly DispatcherTimer updateTimer = new() { Interval = TimeSpan.FromHours(6) };
    private CancellationTokenSource? updateCts;
    private UpdateManager? activeVelopackManager;
    private VelopackAsset? readyToInstall;
    private ReleaseCheckResult? latestReleaseResult;
    private bool isVelopackInstalled;
    private bool isCheckingOrDownloadingUpdate;

    private string CurrentAppVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "2.1.0";

    private string GetEffectiveUpdateRepository()
    {
        try
        {
            return AppUpdateService.NormalizeRepository(settings.UpdateRepository);
        }
        catch (ArgumentException)
        {
            return AppUpdateService.DefaultRepository;
        }
    }

    private void InitializeUpdates()
    {
        AppVersion.Text = $"RemoteDesk  {CurrentAppVersion}";
        SyncUpdateRepoInputFromSettings();

        isVelopackInstalled = ProbeVelopackInstalled();
        if (isVelopackInstalled)
        {
            UpdateModeBadge.SetResourceReference(Border.BackgroundProperty, "ReadySoft");
            UpdateModeBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "Ready");
            UpdateModeBadgeText.Text = "安装版自动更新";
            UpdateStatus.Text = "启动时与每 6 小时自动检查 GitHub 最新稳定版并后台下载。";
        }
        else
        {
            UpdateModeBadge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            UpdateModeBadgeText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            UpdateModeBadgeText.Text = "直连 Release 更新";
            UpdateStatus.Text = "自动检查配置仓库的最新 GitHub Release，发现新版本可一键下载安装包并启动升级。";
        }

        UpdateButton.IsEnabled = true;
        UpdateButton.Content = "检查更新";

        updateTimer.Tick += async (_, _) => await SafeCheckUpdatesAsync(userInitiated: false);
        updateTimer.Start();
    }

    private bool ProbeVelopackInstalled()
    {
        try
        {
            var probe = new UpdateManager(AppUpdateService.BuildVelopackFeedUrl(GetEffectiveUpdateRepository()));
            return probe.IsInstalled;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private void SyncUpdateRepoInputFromSettings()
    {
        UpdateRepoInput.Text = GetEffectiveUpdateRepository();
        UpdateRepoPlaceholder.Visibility = string.IsNullOrEmpty(UpdateRepoInput.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateRepoInputChanged(object sender, TextChangedEventArgs e)
    {
        if (UpdateRepoPlaceholder is not null)
            UpdateRepoPlaceholder.Visibility = string.IsNullOrEmpty(UpdateRepoInput.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void SaveUpdateRepoClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var normalized = AppUpdateService.NormalizeRepository(UpdateRepoInput.Text);
            SaveSettings(settings with { UpdateRepository = normalized });
            UpdateRepoInput.Text = normalized;
            ResetCachedUpdateState();
            ShowUpdateRepoFeedback($"已保存更新仓库：{normalized}", warning: false);
            ShowStatus($"已更新自动更新仓库源为 {normalized}。");
        }
        catch (ArgumentException error)
        {
            ShowUpdateRepoFeedback(error.Message, warning: true);
            UpdateRepoInput.Focus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowUpdateRepoFeedback("无法保存更新仓库设置，请检查本机用户目录写入权限。", warning: true);
        }
    }

    private void ResetUpdateRepoClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            SaveSettings(settings with { UpdateRepository = null });
            UpdateRepoInput.Text = AppUpdateService.DefaultRepository;
            ResetCachedUpdateState();
            ShowUpdateRepoFeedback($"已恢复默认仓库：{AppUpdateService.DefaultRepository}", warning: false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowUpdateRepoFeedback("无法恢复默认仓库设置，请检查本机用户目录写入权限。", warning: true);
        }
    }

    private void OpenUpdateReleasePageClicked(object sender, RoutedEventArgs e)
    {
        var url = latestReleaseResult?.ReleasePageUrl ?? AppUpdateService.BuildReleasesPageUrl(GetEffectiveUpdateRepository());
        try
        {
            using var process = Process.Start(new ProcessStartInfo(url)
            {
                UseShellExecute = true,
            });
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            ShowStatus($"无法打开浏览器，请手动访问 {url}（{error.Message}）。", warning: true);
        }
    }

    private async void UpdateClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (isVelopackInstalled && readyToInstall is not null && activeVelopackManager is not null)
            {
                try
                {
                    activeVelopackManager.ApplyUpdatesAndRestart(readyToInstall);
                }
                catch (Exception)
                {
                    UpdateStatus.Text = "暂时无法重启更新，请关闭 RemoteDesk 后重新打开。";
                }
                return;
            }

            if (!isVelopackInstalled && latestReleaseResult is { IsUpdateAvailable: true } availableRelease)
            {
                if (availableRelease.SetupAsset is { } setupAsset)
                {
                    await DownloadAndLaunchInstallerAsync(setupAsset);
                    return;
                }

                OpenUpdateReleasePageClicked(sender, e);
                return;
            }

            await SafeCheckUpdatesAsync(userInitiated: true);
        }
        catch (Exception error)
        {
            UpdateStatus.Text = $"更新操作失败：{error.Message}";
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            isCheckingOrDownloadingUpdate = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private async Task SafeCheckUpdatesAsync(bool userInitiated)
    {
        if (isCheckingOrDownloadingUpdate || (isVelopackInstalled && readyToInstall is not null))
            return;

        isCheckingOrDownloadingUpdate = true;
        UpdateButton.IsEnabled = false;
        UpdateProgressPanel.Visibility = Visibility.Collapsed;

        updateCts?.Cancel();
        updateCts?.Dispose();
        var cts = new CancellationTokenSource();
        updateCts = cts;

        try
        {
            if (isVelopackInstalled)
                await CheckVelopackUpdatesAsync(cts.Token);
            else
                await CheckGitHubReleaseUpdatesAsync(userInitiated, cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Window closing or superseded check.
        }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException or ArgumentException or InvalidOperationException)
        {
            UpdateStatus.Text = userInitiated
                ? $"检查更新失败：{error.Message}（可稍后重试或点击「打开发布页」手动查看）。"
                : "暂未能连接 GitHub 检查更新，可点击「检查更新」重试。";
            UpdateButton.Content = "检查更新";
        }
        catch (Exception)
        {
            UpdateStatus.Text = "检查更新时发生异常，请稍后重试。";
            UpdateButton.Content = "检查更新";
        }
        finally
        {
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            isCheckingOrDownloadingUpdate = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private async Task CheckVelopackUpdatesAsync(CancellationToken ct)
    {
        var repo = GetEffectiveUpdateRepository();
        var mirrorPrefix = settings.DownloadMirrorPrefix;
        var primaryFeedUrl = AppUpdateService.BuildVelopackFeedUrl(repo, mirrorPrefix);
        var directFeedUrl = AppUpdateService.BuildVelopackFeedUrl(repo, null);
        var hasMirror = !string.Equals(primaryFeedUrl, directFeedUrl, StringComparison.Ordinal);

        UpdateStatus.Text = hasMirror
            ? $"正在通过加速镜像检查 {repo} 的新版本…"
            : $"正在检查 {repo} 的新版本…";

        UpdateManager manager;
        UpdateInfo? updateInfo;

        try
        {
            manager = new UpdateManager(primaryFeedUrl);
            updateInfo = await manager.CheckForUpdatesAsync();
        }
        catch (Exception) when (hasMirror)
        {
            UpdateStatus.Text = "镜像更新源访问失败，正在回退直连 GitHub…";
            manager = new UpdateManager(directFeedUrl);
            updateInfo = await manager.CheckForUpdatesAsync();
        }

        ct.ThrowIfCancellationRequested();

        if (updateInfo is null)
        {
            UpdateStatus.Text = $"当前已是最新版本（{CurrentAppVersion}，仓库：{repo}）。";
            UpdateButton.Content = "检查更新";
            return;
        }

        UpdateStatus.Text = $"发现新版本 {updateInfo.TargetFullRelease.Version}，正在后台下载…";
        UpdateProgressPanel.Visibility = Visibility.Visible;
        UpdateProgressStageText.Text = $"正在下载 RemoteDesk {updateInfo.TargetFullRelease.Version}…";
        UpdateProgressPercentText.Text = "0%";
        UpdateProgressBar.IsIndeterminate = false;
        UpdateProgressBar.Value = 0;

        await manager.DownloadUpdatesAsync(updateInfo, pct =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                UpdateProgressBar.Value = Math.Clamp(pct, 0, 100);
                UpdateProgressPercentText.Text = $"{pct}%";
            });
        }, ct);

        activeVelopackManager = manager;
        readyToInstall = updateInfo.TargetFullRelease;
        UpdateStatus.Text = $"已准备好新版本 {readyToInstall.Version}。点击「立即重启更新」或下次启动时自动应用。";
        UpdateButton.Content = "立即重启更新";
    }

    private async Task CheckGitHubReleaseUpdatesAsync(bool userInitiated, CancellationToken ct)
    {
        var repo = GetEffectiveUpdateRepository();
        UpdateStatus.Text = $"正在检查 {repo} 最新 GitHub Release…";

        var result = await updateService.CheckLatestReleaseAsync(CurrentAppVersion, repo, ct);
        ct.ThrowIfCancellationRequested();

        latestReleaseResult = result;
        ApplyReleaseNotesPreview(result);

        if (!result.IsUpdateAvailable)
        {
            UpdateStatus.Text = string.IsNullOrEmpty(result.TagName)
                ? $"当前已是最新版本（{CurrentAppVersion}）。"
                : $"当前已是最新版本（当前 {CurrentAppVersion}，仓库最新 {result.TagName}）。";
            UpdateButton.Content = "检查更新";
            if (userInitiated)
                ShowStatus($"当前 RemoteDesk {CurrentAppVersion} 已是最新版本。");
            return;
        }

        if (result.SetupAsset is not null)
        {
            var sizeMb = result.SetupAsset.SizeBytes > 0
                ? $"，约 {result.SetupAsset.SizeBytes / (1024.0 * 1024.0):0.0} MB"
                : "";
            UpdateStatus.Text = $"发现新版本 {result.TagName}（当前 {CurrentAppVersion}{sizeMb}），点击「下载并安装最新版」即可自动升级。";
            UpdateButton.Content = "下载并安装最新版";
        }
        else
        {
            UpdateStatus.Text = $"发现新版本 {result.TagName}（未附带 Windows 安装包），可点击「打开发布页」手动下载。";
            UpdateButton.Content = "打开发布页";
        }
    }

    private async Task DownloadAndLaunchInstallerAsync(ReleaseAssetInfo asset)
    {
        if (isCheckingOrDownloadingUpdate)
            return;

        isCheckingOrDownloadingUpdate = true;
        UpdateButton.IsEnabled = false;
        UpdateProgressPanel.Visibility = Visibility.Visible;
        UpdateProgressStageText.Text = "正在准备下载更新安装包…";
        UpdateProgressPercentText.Text = "";
        UpdateProgressBar.IsIndeterminate = true;
        UpdateProgressBar.Value = 0;

        updateCts?.Cancel();
        updateCts?.Dispose();
        var cts = new CancellationTokenSource();
        updateCts = cts;

        var progress = new Progress<UpdateDownloadProgress>(p =>
        {
            UpdateProgressStageText.Text = p.Stage;
            if (p.Percentage is { } pct)
            {
                UpdateProgressBar.IsIndeterminate = false;
                UpdateProgressBar.Value = Math.Clamp(pct, 0, 100);
                UpdateProgressPercentText.Text = $"{pct:0}%";
            }
            else
            {
                UpdateProgressBar.IsIndeterminate = true;
                UpdateProgressPercentText.Text = "";
            }
        });

        try
        {
            UpdateStatus.Text = $"正在下载 {asset.Name}…";
            var installerPath = await updateService.DownloadInstallerAsync(
                asset,
                settings.DownloadMirrorPrefix,
                progress,
                cts.Token);

            var launchInfo = AppUpdateService.CreateInstallerLaunch(installerPath);
            using var installerProcess = Process.Start(launchInfo);
            UpdateStatus.Text = $"安装包已下载并启动（{Path.GetFileName(installerPath)}），请按向导完成升级。";
            ShowStatus("已启动新版 RemoteDesk 安装程序。");
        }
        catch (OperationCanceledException)
        {
            UpdateStatus.Text = "已取消更新下载。";
        }
        catch (Exception error) when (error is HttpRequestException or IOException or InvalidDataException or Win32Exception or InvalidOperationException or ArgumentException)
        {
            UpdateStatus.Text = $"下载或启动更新失败：{error.Message}（可检查网络/镜像设置或点击「打开发布页」）。";
            ShowStatus($"下载更新失败：{error.Message}", warning: true);
        }
        finally
        {
            UpdateProgressPanel.Visibility = Visibility.Collapsed;
            isCheckingOrDownloadingUpdate = false;
            UpdateButton.IsEnabled = true;
        }
    }

    private void ApplyReleaseNotesPreview(ReleaseCheckResult result)
    {
        if (string.IsNullOrWhiteSpace(result.ReleaseNotes))
        {
            UpdateReleaseNotesBorder.Visibility = Visibility.Collapsed;
            return;
        }

        var title = string.IsNullOrWhiteSpace(result.ReleaseName)
            ? $"最新版本 {result.TagName}"
            : $"{result.ReleaseName} 更新说明";
        UpdateReleaseTitle.Text = title;
        UpdateReleaseNotesText.Text = SummarizeReleaseNotes(result.ReleaseNotes);
        UpdateReleaseNotesBorder.Visibility = Visibility.Visible;
    }

    private static string SummarizeReleaseNotes(string notes)
    {
        var lines = notes
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(5)
            .ToArray();

        var summary = string.Join('\n', lines);
        return summary.Length > 360 ? summary[..360].TrimEnd() + "…" : summary;
    }

    private void ResetCachedUpdateState()
    {
        readyToInstall = null;
        activeVelopackManager = null;
        latestReleaseResult = null;
        UpdateButton.Content = "检查更新";
        UpdateReleaseNotesBorder.Visibility = Visibility.Collapsed;
    }

    private void ShowUpdateRepoFeedback(string message, bool warning)
    {
        UpdateRepoFeedback.Text = message;
        UpdateRepoFeedback.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Warning" : "Ready");
        UpdateRepoFeedback.Visibility = Visibility.Visible;
    }
}
