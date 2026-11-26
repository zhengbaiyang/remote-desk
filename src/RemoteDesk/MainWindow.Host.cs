using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using RemoteDesk.UI;

namespace RemoteDesk;

public partial class MainWindow
{
    private bool isSyncingSecurityPinToggle;

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

    private async void HostModeToggled(object sender, RoutedEventArgs e)
    {
        if (isSyncingHostToggle)
            return;

        var enabled = HostModeToggle.IsChecked == true;
        HostModeToggle.Content = enabled ? "已开启" : "已关闭";
        try
        {
            SaveSettings(settings with { HostModeEnabled = enabled });
            UpdateHostNavIndicator();

            await PushConfigToDaemonIfRunningAsync();
            ReconcileLocalPeerHost();
            if (enabled)
            {
                var sunshineExe = ResolveSunshineExecutable();
                if (sunshineExe is not null)
                {
                    if (OperatingSystem.IsWindows() && WindowsServiceInstaller.GetServiceStatus().Running)
                    {
                        var (ok, _, err) = await AgentIpcClient.StartSunshineAsync();
                        ShowStatus(ok
                            ? "已开启本机被控模式，已由后台托管 Sunshine（支持同账号自动静默配对）。"
                            : (err ?? "守护服务启动 Sunshine 失败。"), warning: !ok);
                    }
                    else if (localHostPeerHost is not null)
                    {
                        var (ok, msg) = await localHostPeerHost.EnsureProvisionedAndStartedAsync(sunshineExe);
                        ShowStatus(msg, warning: !ok);
                    }
                    else
                    {
                        ShowStatus("已开启本机被控模式。");
                    }
                }
                else
                {
                    ShowStatus("已开启本机被控模式。正在准备后台安装 Sunshine 或请前往「引擎与设置」安装。", warning: true);
                    _ = EnsureRequiredEnginesSilentlyAsync();
                }
            }
            else
            {
                ShowStatus("已关闭本机被控开关（如需停止运行中的 Sunshine，请点击「停止 Sunshine」）。");
            }

            RefreshHostStatus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus("无法保存本机被控状态，请检查本机用户目录写入权限。", warning: true);
            RefreshHostStatus();
        }
    }

    private async void HostSecurityPinToggled(object sender, RoutedEventArgs e)
    {
        if (isSyncingSecurityPinToggle)
            return;

        var required = HostSecurityPinToggle.IsChecked == true;
        HostSecurityPinToggle.Content = required ? "已开启" : "已关闭";
        HostSecurityPinEditRow.Visibility = required ? Visibility.Visible : Visibility.Collapsed;

        if (!required)
        {
            SaveSettings(settings with
            {
                SecurityPinRequired = false,
                SecurityPinSalt = null,
                SecurityPinHash = null,
            });
            HostSecurityPinInput.Password = "";

            if (OperatingSystem.IsWindows() && WindowsServiceInstaller.GetServiceStatus().Running)
                await AgentIpcClient.SetSecurityPinAsync(required: false, newPin: null);

            ReconcileLocalPeerHost();
            _ = SyncWithServerAndApplyAsync(userInitiated: false);
            ShowStatus("已关闭独立安全密码验证（恢复同账号设备免确认自动配对直连）。");
        }
        else
        {
            HostSecurityPinInput.Focus();
            ShowStatus("请输入 4 位及以上独立安全密码并点击「保存安全密码」。");
        }
    }

    private async void SaveHostSecurityPinClicked(object sender, RoutedEventArgs e)
    {
        var pin = HostSecurityPinInput.Password;
        try
        {
            var (salt, hash) = SecurityPinHasher.HashPin(pin);
            SaveSettings(settings with
            {
                SecurityPinRequired = true,
                SecurityPinSalt = salt,
                SecurityPinHash = hash,
            });

            if (OperatingSystem.IsWindows() && WindowsServiceInstaller.GetServiceStatus().Running)
                await AgentIpcClient.SetSecurityPinAsync(required: true, newPin: pin);

            HostSecurityPinInput.Password = "";
            ReconcileLocalPeerHost();
            _ = SyncWithServerAndApplyAsync(userInitiated: false);
            ShowStatus("已保存独立被控安全密码，新设备首次自动配对本机时需校验此密码。");
        }
        catch (ArgumentException ex)
        {
            ShowStatus(ex.Message, warning: true);
            HostSecurityPinInput.Focus();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ShowStatus($"无法保存安全密码：{ex.Message}", warning: true);
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

        isSyncingSecurityPinToggle = true;
        try
        {
            HostSecurityPinToggle.IsChecked = settings.SecurityPinRequired;
            HostSecurityPinToggle.Content = settings.SecurityPinRequired ? "已开启" : "已关闭";
            HostSecurityPinEditRow.Visibility = settings.SecurityPinRequired ? Visibility.Visible : Visibility.Collapsed;
        }
        finally
        {
            isSyncingSecurityPinToggle = false;
        }

        RefreshDaemonServiceUi();

        var sunshineExe = ResolveSunshineExecutable();
        var status = HostServiceManager.GetHostStatus(sunshineExe);

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

        var bridgeReady = sessionBridgeServer?.IsListening == true ||
                          (OperatingSystem.IsWindows() && WindowsServiceInstaller.GetServiceStatus().Running);
        if (bridgeReady)
        {
            SetHostBadge(HostBridgeBadge, HostBridgeBadgeText, "协同就绪", "ReadySoft", "Ready");
            HostBridgeDetailText.Text = "支持双向剪贴板、拖拽收文件、分辨率自动匹配、WDA 防窥黑屏与远程锁屏/电源控制。";
        }
        else
        {
            SetHostBadge(HostBridgeBadge, HostBridgeBadgeText, "待启动", "WarningSoft", "Warning");
            HostBridgeDetailText.Text = "桌面交互通道未就绪，请启用后台守护服务或保持主窗口运行。";
        }

        StartSunshineButton.IsEnabled = status.SunshineInstalled && !status.SunshineRunning;
        StopSunshineButton.IsEnabled = status.SunshineRunning;
        OpenSunshineUiButton.IsEnabled = status.SunshineInstalled || status.SunshineRunning;

        UpdateHostNavIndicator(status.SunshineRunning);
    }

    private void UpdateHostNavIndicator(bool? anyHostRunning = null)
    {
        var active = anyHostRunning ?? false;
        if (active)
        {
            HostNavDot.SetResourceReference(Shape.FillProperty, "Ready");
            HostNavDot.Opacity = 1.0;
            MotionBehaviors.SetEnablePulse(HostNavDot, true);
        }
        else if (settings.HostModeEnabled)
        {
            MotionBehaviors.SetEnablePulse(HostNavDot, false);
            HostNavDot.SetResourceReference(Shape.FillProperty, "Accent");
            HostNavDot.Opacity = 0.9;
        }
        else
        {
            MotionBehaviors.SetEnablePulse(HostNavDot, false);
            HostNavDot.SetResourceReference(Shape.FillProperty, "Muted");
            HostNavDot.Opacity = 0.4;
        }
    }

    private void RefreshLocalNetworkAndGpus()
    {
        var netInfo = HostServiceManager.GetLocalNetworkInfo();
        RefreshLocalIdentityBadges();

        LocalIpv4List.ItemsSource = netInfo.IPv4Addresses;
        LocalIpv4Empty.Visibility = netInfo.IPv4Addresses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        LocalIpv6List.ItemsSource = netInfo.IPv6Addresses;
        LocalIpv6Empty.Visibility = netInfo.IPv6Addresses.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        var gpus = HostServiceManager.DetectGpuEncoders();
        GpuEncodersList.ItemsSource = gpus;
        GpuEncodersEmpty.Visibility = gpus.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void StartSunshineClicked(object sender, RoutedEventArgs e)
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
            if (!settings.HostModeEnabled)
                SaveSettings(settings with { HostModeEnabled = true });

            if (OperatingSystem.IsWindows() && WindowsServiceInstaller.GetServiceStatus().Running)
            {
                await PushConfigToDaemonIfRunningAsync();
                var (ok, _, err) = await AgentIpcClient.StartSunshineAsync();
                RefreshHostStatus();
                ShowStatus(ok
                    ? "后台守护服务已启动 Sunshine 并开启同账号零配置自动配对。"
                    : (err ?? "守护服务启动 Sunshine 失败。"), warning: !ok);
                return;
            }

            if (localHostPeerHost is not null)
            {
                var (ok, msg) = await localHostPeerHost.EnsureProvisionedAndStartedAsync(sunshineExe);
                RefreshHostStatus();
                ShowStatus(msg, warning: !ok);
                return;
            }

            var started = HostServiceManager.StartSunshineHost(sunshineExe);
            RefreshHostStatus();
            ShowStatus(started
                ? "已启动 Sunshine 高刷被控端（建议同时启用「后台守护服务」实现同账号零配置自动配对）。"
                : "Sunshine 进程未能启动，请检查安装目录。", warning: !started);
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException)
        {
            ShowStatus($"启动 Sunshine 失败：{error.Message}", warning: true);
        }
    }

    private async void StopSunshineClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (OperatingSystem.IsWindows() && WindowsServiceInstaller.GetServiceStatus().Running)
            {
                var (ok, _, err) = await AgentIpcClient.StopSunshineAsync();
                RefreshHostStatus();
                ShowStatus(ok ? "后台守护服务已停止 Sunshine 被控进程。" : (err ?? "停止 Sunshine 失败。"), warning: !ok);
                return;
            }

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
            ShowStatus($"已在浏览器打开 Sunshine 控制台（日常远控无需登录此面板；若需登录，用户名 {SunshineManager.DefaultAdminUser}，可点击「复制面板密码」）。");
        }
        catch (Win32Exception)
        {
            ShowStatus($"无法打开浏览器，请手动访问 {HostServiceManager.GetSunshineWebUiUrl()}。", warning: true);
        }
    }

    private async void CopySunshineCredsClicked(object sender, RoutedEventArgs e)
    {
        var sunshineExe = ResolveSunshineExecutable();
        if (sunshineExe is null)
        {
            ShowStatus("未检测到 Sunshine 安装程序，请先在「引擎与设置」安装。", warning: true);
            return;
        }

        CopySunshineCredsButton.IsEnabled = false;
        try
        {
            AgentSunshineCredsDto? creds = null;
            if (OperatingSystem.IsWindows() && WindowsServiceInstaller.GetServiceStatus().Running)
                creds = await AgentIpcClient.TryGetSunshineCredentialsAsync();

            creds ??= localHostPeerHost?.TryGetOrProvisionManagedCredentials(sunshineExe);
            if (creds is null || string.IsNullOrEmpty(creds.Password))
            {
                ShowStatus("未能获取或初始化 Sunshine 面板凭据，请确认已开启被控服务后重试。", warning: true);
                return;
            }

            CopyTextToClipboard(creds.Password, $"Sunshine 面板密码（用户名 {creds.Username}）");
        }
        finally
        {
            CopySunshineCredsButton.IsEnabled = true;
        }
    }

    private async void SubmitHostQuickPinClicked(object sender, RoutedEventArgs e)
    {
        var pin = HostQuickPinInput.Text.Trim();
        if (pin.Length != 4 || !pin.All(char.IsAsciiDigit))
        {
            ShowStatus("请输入控制端显示的 4 位数字配对 PIN 码。", warning: true);
            HostQuickPinInput.Focus();
            return;
        }

        var sunshineExe = ResolveSunshineExecutable();
        if (sunshineExe is null)
        {
            ShowStatus("未检测到 Sunshine 安装程序，请先在「引擎与设置」安装。", warning: true);
            return;
        }

        SubmitHostQuickPinButton.IsEnabled = false;
        try
        {
            (bool ok, string msg) result;
            if (OperatingSystem.IsWindows() && WindowsServiceInstaller.GetServiceStatus().Running)
            {
                result = await AgentIpcClient.SubmitLocalPinAsync(pin, "LAN-Client");
            }
            else if (localHostPeerHost is not null)
            {
                result = await localHostPeerHost.SubmitLocalPinAsync(pin, "LAN-Client");
            }
            else
            {
                result = (false, "本地托管组件未就绪，请先开启被控服务。");
            }

            if (result.ok)
            {
                HostQuickPinInput.Text = "";
                RefreshHostStatus();
                ShowStatus(result.msg);
            }
            else
            {
                ShowStatus(result.msg, warning: true);
            }
        }
        finally
        {
            SubmitHostQuickPinButton.IsEnabled = true;
        }
    }

    private void BeginLocalHostRenameClicked(object sender, RoutedEventArgs e)
    {
        LocalHostRenameInput.Text = GetEffectiveLocalDeviceName();
        LocalHostRenameRow.Visibility = Visibility.Visible;
        LocalHostRenameInput.Focus();
        LocalHostRenameInput.SelectAll();
    }

    private void CancelLocalHostRenameClicked(object sender, RoutedEventArgs e)
    {
        LocalHostRenameRow.Visibility = Visibility.Collapsed;
    }

    private void LocalHostRenameInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            ConfirmLocalHostRenameClicked(sender, e);
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            CancelLocalHostRenameClicked(sender, e);
        }
    }

    private async void ConfirmLocalHostRenameClicked(object sender, RoutedEventArgs e)
    {
        string normalizedName;
        try
        {
            normalizedName = DeviceIdentityHelper.NormalizeDeviceName(LocalHostRenameInput.Text);
        }
        catch (ArgumentException ex)
        {
            ShowStatus(ex.Message, warning: true);
            LocalHostRenameInput.Focus();
            return;
        }

        ConfirmLocalHostRenameButton.IsEnabled = false;
        try
        {
            var (_, message, selectKey) = await ApplyLocalDeviceNameAsync(normalizedName);
            LocalHostRenameRow.Visibility = Visibility.Collapsed;
            RefreshDevices(DeviceList.SelectedIndex, selectKey, preserveTransientUi: true);
            ShowStatus(message);
        }
        finally
        {
            ConfirmLocalHostRenameButton.IsEnabled = true;
        }
    }

    private void CopyHostNameClicked(object sender, RoutedEventArgs e) =>
        CopyTextToClipboard(HostServiceManager.GetLocalNetworkInfo().HostName, "主机名");

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
