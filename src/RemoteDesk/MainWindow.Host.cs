using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;

namespace RemoteDesk;

public partial class MainWindow
{
    private void RefreshHostAndNetworkInfo()
    {
        RefreshHostStatus();
        RefreshLocalNetworkAndGpus();
        RefreshNetworkSummary();
    }

    private void RefreshHostStatusClicked(object sender, RoutedEventArgs e)
    {
        RefreshHostAndNetworkInfo();
        ShowStatus("已刷新本机被控服务、网卡与显卡编码状态。");
    }

    private void HostModeToggled(object sender, RoutedEventArgs e)
    {
        if (isSyncingHostToggle)
            return;

        var enabled = HostModeToggle.IsChecked == true;
        HostModeToggle.Content = enabled ? "已开启" : "已关闭";
        try
        {
            SaveSettings(settings with { HostModeEnabled = enabled });
            UpdateHostNavIndicator();

            if (enabled)
            {
                var sunshineExe = ResolveSunshineExecutable();
                if (sunshineExe is not null)
                    ShowStatus("已开启本机被控模式，可点击下方按钮启动 Sunshine 或 RustDesk 服务。");
                else
                    ShowStatus("已开启本机被控模式。检测到 Sunshine 尚未安装，建议前往「引擎与设置」一键安装。", warning: true);
            }
            else
            {
                ShowStatus("已关闭本机被控开关（如需停止运行中的 Sunshine，请点击「停止 Sunshine」）。");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus("无法保存本机被控状态，请检查本机用户目录写入权限。", warning: true);
            RefreshHostStatus();
        }
    }

    private void RefreshHostStatus()
    {
        isSyncingHostToggle = true;
        try
        {
            HostModeToggle.IsChecked = settings.HostModeEnabled;
            HostModeToggle.Content = settings.HostModeEnabled ? "已开启" : "已关闭";
        }
        finally
        {
            isSyncingHostToggle = false;
        }

        var sunshineExe = ResolveSunshineExecutable();
        var rustDeskExe = ResolveRustDeskExecutable();
        var status = HostServiceManager.GetHostStatus(sunshineExe, rustDeskExe);

        // Sunshine status badge
        if (status.SunshineRunning)
        {
            SetHostBadge(HostSunshineBadge, HostSunshineBadgeText, "运行中", "ReadySoft", "Ready");
            HostSunshinePathText.Text = status.SunshineExecutablePath ?? "Sunshine 服务正在运行";
        }
        else if (status.SunshineInstalled)
        {
            SetHostBadge(HostSunshineBadge, HostSunshineBadgeText, "已安装 · 未运行", "WarningSoft", "Warning");
            HostSunshinePathText.Text = status.SunshineExecutablePath ?? "";
        }
        else
        {
            SetHostBadge(HostSunshineBadge, HostSunshineBadgeText, "未安装", "SurfaceSubtle", "Muted");
            HostSunshinePathText.Text = "未检测到 Sunshine，请前往引擎中心一键安装。";
        }

        // RustDesk status badge
        if (status.RustDeskRunning)
        {
            SetHostBadge(HostRustDeskBadge, HostRustDeskBadgeText, "运行中", "ReadySoft", "Ready");
            HostRustDeskPathText.Text = status.RustDeskExecutablePath ?? "RustDesk 进程正在运行";
        }
        else if (status.RustDeskInstalled)
        {
            SetHostBadge(HostRustDeskBadge, HostRustDeskBadgeText, "已安装 · 未运行", "WarningSoft", "Warning");
            HostRustDeskPathText.Text = status.RustDeskExecutablePath ?? "";
        }
        else
        {
            SetHostBadge(HostRustDeskBadge, HostRustDeskBadgeText, "未安装", "SurfaceSubtle", "Muted");
            HostRustDeskPathText.Text = "未检测到 RustDesk，请前往引擎中心一键安装。";
        }

        StartSunshineButton.IsEnabled = status.SunshineInstalled && !status.SunshineRunning;
        StopSunshineButton.IsEnabled = status.SunshineRunning;
        OpenSunshineUiButton.IsEnabled = status.SunshineInstalled || status.SunshineRunning;
        StartRustDeskHostButton.IsEnabled = status.RustDeskInstalled;

        UpdateHostNavIndicator(status.SunshineRunning || status.RustDeskRunning);
    }

    private void UpdateHostNavIndicator(bool? anyHostRunning = null)
    {
        var active = anyHostRunning ?? false;
        if (active)
        {
            HostNavDot.SetResourceReference(Shape.FillProperty, "Ready");
            HostNavDot.Opacity = 1.0;
        }
        else if (settings.HostModeEnabled)
        {
            HostNavDot.SetResourceReference(Shape.FillProperty, "Accent");
            HostNavDot.Opacity = 0.9;
        }
        else
        {
            HostNavDot.SetResourceReference(Shape.FillProperty, "Muted");
            HostNavDot.Opacity = 0.4;
        }
    }

    private void RefreshLocalNetworkAndGpus()
    {
        var netInfo = HostServiceManager.GetLocalNetworkInfo();
        LocalHostNameText.Text = netInfo.HostName;

        LocalIpv4List.ItemsSource = netInfo.IPv4Addresses;
        LocalIpv4Empty.Visibility = netInfo.IPv4Addresses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        LocalIpv6List.ItemsSource = netInfo.IPv6Addresses;
        LocalIpv6Empty.Visibility = netInfo.IPv6Addresses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var gpus = HostServiceManager.DetectGpuEncoders();
        GpuEncodersList.ItemsSource = gpus;
        GpuEncodersEmpty.Visibility = gpus.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void StartSunshineClicked(object sender, RoutedEventArgs e)
    {
        var sunshineExe = ResolveSunshineExecutable();
        if (sunshineExe is null)
        {
            NavigateTo(AppPage.Engines);
            ShowStatus("未找到 Sunshine 程序，请先在引擎中心一键安装或导入本地包。", warning: true);
            return;
        }

        try
        {
            var started = HostServiceManager.StartSunshineHost(sunshineExe);
            if (!settings.HostModeEnabled)
                SaveSettings(settings with { HostModeEnabled = true });

            RefreshHostStatus();
            ShowStatus(started
                ? "已启动 Sunshine 高刷被控端，可点击「打开 Sunshine 面板」完成首次配对。"
                : "Sunshine 进程未能启动，请检查安装目录。", warning: !started);
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException)
        {
            ShowStatus($"启动 Sunshine 失败：{error.Message}", warning: true);
        }
    }

    private void StopSunshineClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var stopped = HostServiceManager.StopSunshineHost();
            RefreshHostStatus();
            ShowStatus(stopped
                ? "已停止本机 Sunshine 被控进程。"
                : "未能完全停止 Sunshine（如作为系统服务运行，可能需要管理员权限停止）。", warning: !stopped);
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException)
        {
            ShowStatus($"停止 Sunshine 失败：{error.Message}", warning: true);
        }
    }

    private void OpenSunshineWebUiClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(HostServiceManager.GetSunshineWebUiUrl())
            {
                UseShellExecute = true,
            });
            ShowStatus($"已在浏览器打开 Sunshine 控制台（{HostServiceManager.GetSunshineWebUiUrl()}）。");
        }
        catch (Win32Exception)
        {
            ShowStatus($"无法打开浏览器，请手动访问 {HostServiceManager.GetSunshineWebUiUrl()}。", warning: true);
        }
    }

    private void StartRustDeskHostClicked(object sender, RoutedEventArgs e)
    {
        var rustDeskExe = ResolveRustDeskExecutable();
        if (rustDeskExe is null)
        {
            NavigateTo(AppPage.Engines);
            ShowStatus("未找到 RustDesk 程序，请先在引擎中心一键安装或指定路径。", warning: true);
            return;
        }

        try
        {
            HostServiceManager.StartRustDeskHost(rustDeskExe);
            if (!settings.HostModeEnabled)
                SaveSettings(settings with { HostModeEnabled = true });

            RefreshHostStatus();
            ShowStatus("已启动 RustDesk 客户端，可在 RustDesk 主界面查看本机设备 ID 与一次性密码。");
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException)
        {
            ShowStatus($"启动 RustDesk 失败：{error.Message}", warning: true);
        }
    }

    private void CopyHostNameClicked(object sender, RoutedEventArgs e) => CopyTextToClipboard(LocalHostNameText.Text, "主机名");

    private void CopyAddressClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string address } && !string.IsNullOrWhiteSpace(address))
            CopyTextToClipboard(address, "直连地址");
    }

    private void CopyTextToClipboard(string value, string label)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        try
        {
            Clipboard.SetText(value);
            ShowStatus($"已复制{label}：{value}");
        }
        catch (ExternalException)
        {
            ShowStatus("剪贴板正被其他程序占用，请稍后重试复制。", warning: true);
        }
    }

    private static void SetHostBadge(Border badge, TextBlock text, string label, string backgroundKey, string foregroundKey)
    {
        text.Text = label;
        badge.SetResourceReference(Border.BackgroundProperty, backgroundKey);
        text.SetResourceReference(TextBlock.ForegroundProperty, foregroundKey);
    }
}
