using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Shapes;
using RemoteDesk.UI;
using Path = System.IO.Path;

namespace RemoteDesk;

internal sealed record EasyTierPeerViewModel(
    EasyTierPeerInfo Peer,
    string DisplayHostname,
    string DisplayVirtualIpv4,
    string ConnectionType,
    string ConnectionTone,
    string LatencyText,
    string ProtocolText,
    bool CanAddAsDevice)
{
    public static EasyTierPeerViewModel FromPeer(EasyTierPeerInfo peer)
    {
        var hostname = string.IsNullOrWhiteSpace(peer.Hostname) ? "未命名节点" : peer.Hostname;
        var isRelayOnly = EasyTierManager.IsRelayOnlyPeer(peer);
        var ipv4 = !string.IsNullOrWhiteSpace(peer.VirtualIpv4)
            ? peer.VirtualIpv4
            : isRelayOnly
                ? "中继节点（无虚拟 IP）"
                : "等待分配 IP（需守护服务）";
        var tone = peer.IsSelf
            ? "Self"
            : peer.ConnectionType == EasyTierManager.ConnectionTypeP2P
                ? "P2P"
                : peer.ConnectionType == EasyTierManager.ConnectionTypeRelay
                    ? "Relay"
                    : "Unknown";
        var latency = peer.LatencyMs is { } ms ? $"{ms:0.#} ms" : "--";
        var protocol = string.IsNullOrWhiteSpace(peer.Protocol) ? "--" : peer.Protocol.ToUpperInvariant();
        var canAdd = !peer.IsSelf && !isRelayOnly && !string.IsNullOrWhiteSpace(peer.VirtualIpv4);

        return new EasyTierPeerViewModel(peer, hostname, ipv4, peer.ConnectionType, tone, latency, protocol, canAdd);
    }
}

public partial class MainWindow
{
    private readonly ObservableCollection<EasyTierPeerViewModel> easyTierPeers = [];
    private CancellationTokenSource? easyTierQueryCts;
    private bool isSyncingEasyTierForm;
    private bool isSyncingEasyTierSecret;
    private bool isEasyTierSecretVisible;
    private string? currentEasyTierVirtualIpv4;

    private void InitializeNetwork()
    {
        EasyTierPeersList.ItemsSource = easyTierPeers;
        SyncEasyTierFormFromSettings();
    }

    private void SyncEasyTierFormFromSettings()
    {
        isSyncingEasyTierForm = true;
        try
        {
            var cfg = settings.EasyTier ?? new EasyTierConfig();
            EasyTierPeerUrlInput.Text = cfg.PeerUrl ?? "";
            EasyTierNetworkNameInput.Text = cfg.NetworkName ?? "";
            SetEasyTierSecretText(cfg.NetworkSecret ?? "");
            EasyTierVirtualIpv4Input.Text = cfg.VirtualIpv4 ?? "";
            EasyTierAutoAssignToggle.IsChecked = cfg.AutoAssignIpv4;
            EasyTierAutoStartToggle.IsChecked = cfg.AutoStartWithApp;

            UpdateEasyTierVirtualIpFieldVisibility();
            UpdateEasyTierPlaceholders();
        }
        finally
        {
            isSyncingEasyTierForm = false;
        }
    }

    private async Task TryAutoStartEasyTierOnLaunchAsync()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var (_, svcRunning) = WindowsServiceInstaller.GetServiceStatus();
                if (svcRunning)
                {
                    await PushConfigToDaemonIfRunningAsync();
                    await RefreshEasyTierStatusAsync();
                    return;
                }
            }

            var cfg = settings.EasyTier;
            if (cfg is { AutoStartWithApp: true })
            {
                var coreExe = ResolveEasyTierExecutable();
                if (coreExe is not null && !EasyTierManager.IsMeshRunning())
                {
                    var validated = EasyTierConfig.Validate(cfg);
                    EasyTierManager.StartMesh(coreExe, validated);
                    await Task.Delay(450);
                }
            }
        }
        catch (Exception error) when (error is ArgumentException or IOException or Win32Exception or InvalidOperationException)
        {
            ShowStatus($"自动启动 EasyTier 组网失败：{error.Message}", warning: true);
        }

        await RefreshEasyTierStatusAsync();
    }

    private async Task RefreshDaemonAndEasyTierAsync()
    {
        RefreshDaemonServiceUi();
        await RefreshEasyTierStatusAsync();
    }

    private void RefreshNetworkSummary()
    {
        var info = HostServiceManager.GetLocalNetworkInfo();
        NetPageHostName.Text = info.HostName;
        NetPagePrimaryIpv4.Text = info.IPv4Addresses.Count > 0 ? info.IPv4Addresses[0] : "未检测到";
        NetPagePrimaryIpv6.Text = info.IPv6Addresses.Count > 0 ? info.IPv6Addresses[0] : "未分配公网/校园网 IPv6";
    }

    private async void RefreshNetworkInfoClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            RefreshHostAndNetworkInfo();
            await RefreshEasyTierStatusAsync();
            ShowStatus("已刷新本机网卡与 EasyTier 组网节点状态。");
        }
        catch (Exception error)
        {
            ShowStatus($"刷新网络状态失败：{error.Message}", warning: true);
        }
    }

    private async void RefreshEasyTierClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            await RefreshEasyTierStatusAsync();
            ShowStatus("已刷新 EasyTier 组网状态与在线节点。");
        }
        catch (Exception error)
        {
            ShowStatus($"刷新 EasyTier 节点失败：{error.Message}", warning: true);
        }
    }

    private void EasyTierFieldTextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateEasyTierPlaceholders();
    }

    private void EasyTierSecretPasswordChanged(object sender, RoutedEventArgs e)
    {
        if (isSyncingEasyTierSecret)
            return;

        isSyncingEasyTierSecret = true;
        try
        {
            EasyTierSecretTextBox.Text = EasyTierSecretPasswordBox.Password;
        }
        finally
        {
            isSyncingEasyTierSecret = false;
        }

        UpdateEasyTierPlaceholders();
    }

    private void EasyTierSecretTextChanged(object sender, TextChangedEventArgs e)
    {
        if (isSyncingEasyTierSecret)
            return;

        isSyncingEasyTierSecret = true;
        try
        {
            EasyTierSecretPasswordBox.Password = EasyTierSecretTextBox.Text;
        }
        finally
        {
            isSyncingEasyTierSecret = false;
        }

        UpdateEasyTierPlaceholders();
    }

    private void ToggleEasyTierSecretVisibilityClicked(object sender, RoutedEventArgs e)
    {
        isEasyTierSecretVisible = !isEasyTierSecretVisible;
        EasyTierSecretPasswordBox.Visibility = isEasyTierSecretVisible ? Visibility.Collapsed : Visibility.Visible;
        EasyTierSecretTextBox.Visibility = isEasyTierSecretVisible ? Visibility.Visible : Visibility.Collapsed;
        ToggleEasyTierSecretButton.Content = isEasyTierSecretVisible ? "\uED1A" : "\uE7B3";
        ToggleEasyTierSecretButton.ToolTip = isEasyTierSecretVisible ? "隐藏网络密钥" : "显示网络密钥";

        if (isEasyTierSecretVisible)
        {
            EasyTierSecretTextBox.Focus();
            EasyTierSecretTextBox.CaretIndex = EasyTierSecretTextBox.Text.Length;
        }
        else
        {
            EasyTierSecretPasswordBox.Focus();
        }
    }

    private void EasyTierAutoAssignToggled(object sender, RoutedEventArgs e)
    {
        if (isSyncingEasyTierForm)
            return;

        UpdateEasyTierVirtualIpFieldVisibility();
    }

    private void SaveEasyTierConfigClicked(object sender, RoutedEventArgs e)
    {
        if (!TryBuildAndSaveEasyTierConfig(enabled: true, out _))
            return;

        ShowEasyTierConfigFeedback("已保存 EasyTier 组网配置。若正在运行，请停止后重新启动以应用新参数。", warning: false);
        ShowStatus("已保存 EasyTier 跨网组网配置。");
    }

    private async void StartEasyTierClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            var coreExe = ResolveEasyTierExecutable();
            if (coreExe is null)
            {
                NavigateTo(AppPage.Engines);
                ShowStatus("尚未安装 EasyTier 引擎，请在「引擎与设置」点击「一键安装」或「导入本地包」。", warning: true);
                return;
            }

            if (!TryBuildAndSaveEasyTierConfig(enabled: true, out var validated))
                return;

            StartEasyTierButton.IsEnabled = false;

            if (OperatingSystem.IsWindows())
            {
                var (_, svcRunning) = WindowsServiceInstaller.GetServiceStatus();
                if (svcRunning)
                {
                    await PushConfigToDaemonIfRunningAsync();
                    var (ok, _, err) = await AgentIpcClient.StartEasyTierAsync();
                    if (!ok)
                    {
                        ShowEasyTierConfigFeedback(err ?? "守护服务启动 EasyTier 失败。", warning: true);
                        ShowStatus(err ?? "守护服务启动 EasyTier 失败。", warning: true);
                    }
                    else
                    {
                        ShowEasyTierConfigFeedback("后台守护服务已启动 EasyTier，正在分配虚拟 IP…", warning: false);
                        ShowStatus($"守护服务已启动 EasyTier 组网（网络：{validated.NetworkName}）。");
                    }
                    await Task.Delay(500);
                    await RefreshEasyTierStatusAsync();
                    return;
                }
            }

            bool started;
            if (EasyTierManager.IsCurrentProcessElevated())
            {
                started = EasyTierManager.StartMesh(coreExe, validated);
            }
            else
            {
                try
                {
                    started = EasyTierManager.StartMeshElevated(coreExe, validated);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    ShowEasyTierConfigFeedback("已取消管理员授权，无法创建 EasyTier 虚拟网卡。建议启用后台守护服务实现免 UAC。", warning: true);
                    ShowStatus("已取消提权授权。", warning: true);
                    await RefreshEasyTierStatusAsync();
                    return;
                }
            }

            if (!started)
            {
                ShowEasyTierConfigFeedback("EasyTier 进程未能启动，请检查 easytier-core.exe 是否完整。", warning: true);
                ShowStatus("EasyTier 启动失败，请检查引擎安装状态。", warning: true);
                await RefreshEasyTierStatusAsync();
                return;
            }

            ShowEasyTierConfigFeedback("已启动 EasyTier 组网进程，正在同步虚拟 IP 与在线节点…", warning: false);
            ShowStatus($"已启动 EasyTier 组网（网络：{validated.NetworkName}）。");
            await Task.Delay(500);
            await RefreshEasyTierStatusAsync();
        }
        catch (Exception error) when (error is ArgumentException or IOException or Win32Exception or InvalidOperationException)
        {
            ShowEasyTierConfigFeedback($"启动组网失败：{error.Message}", warning: true);
            ShowStatus($"启动 EasyTier 失败：{error.Message}", warning: true);
            await RefreshEasyTierStatusAsync();
        }
    }

    private async void StopEasyTierClicked(object sender, RoutedEventArgs e)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var (_, svcRunning) = WindowsServiceInstaller.GetServiceStatus();
                if (svcRunning)
                {
                    var (ok, _, err) = await AgentIpcClient.StopEasyTierAsync();
                    await RefreshEasyTierStatusAsync();
                    ShowStatus(ok ? "后台守护服务已停止 EasyTier 虚拟组网。" : (err ?? "停止 EasyTier 失败。"), warning: !ok);
                    return;
                }
            }

            var stopped = EasyTierManager.StopMesh();
            await RefreshEasyTierStatusAsync();
            ShowStatus(stopped
                ? "已停止本机 EasyTier 虚拟组网。"
                : "未能完全停止 EasyTier 进程（若以管理员权限启动，请尝试提升权限或使用守护服务停止）。", warning: !stopped);
        }
        catch (Exception error)
        {
            ShowStatus($"停止 EasyTier 失败：{error.Message}", warning: true);
        }
    }

    private void CopyEasyTierIpClicked(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(currentEasyTierVirtualIpv4))
            CopyTextToClipboard(currentEasyTierVirtualIpv4, "虚拟 IPv4");
    }

    private void OpenAliyunGuideClicked(object sender, RoutedEventArgs e)
    {
        var localGuidePath = FindLocalAliyunGuidePath();
        var target = localGuidePath ?? $"https://github.com/{GetEffectiveUpdateRepository()}/blob/main/docs/aliyun-server-guide.md";

        try
        {
            using var process = Process.Start(new ProcessStartInfo(target)
            {
                UseShellExecute = true,
            });
            ShowStatus(localGuidePath is not null
                ? $"已打开本地阿里云节点部署指南（{Path.GetFileName(localGuidePath)}）。"
                : "已在浏览器打开阿里云节点部署指南。");
        }
        catch (Exception error) when (error is Win32Exception or InvalidOperationException or IOException)
        {
            ShowStatus($"无法打开部署指南，请手动查看 docs/aliyun-server-guide.md（{error.Message}）。", warning: true);
        }
    }

    private void AddPeerAsDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: EasyTierPeerViewModel vm } || !vm.CanAddAsDevice)
            return;

        OpenAddDeviceFromPeer(vm.DisplayHostname, vm.Peer.VirtualIpv4);
    }

    private async Task RefreshEasyTierStatusAsync()
    {
        easyTierQueryCts?.Cancel();
        easyTierQueryCts?.Dispose();
        var cts = new CancellationTokenSource();
        easyTierQueryCts = cts;

        try
        {
            var coreExe = ResolveEasyTierExecutable();
            var cfg = settings.EasyTier ?? new EasyTierConfig();

            EasyTierStatus? status = null;
            if (OperatingSystem.IsWindows())
            {
                var (_, svcRunning) = WindowsServiceInstaller.GetServiceStatus();
                if (svcRunning)
                {
                    var runtime = await AgentIpcClient.TryGetStatusAsync(cts.Token);
                    if (runtime is not null)
                    {
                        var etInstalled = coreExe is not null;
                        status = new EasyTierStatus(
                            Installed: etInstalled,
                            Running: runtime.EasyTierRunning,
                            CoreExecutablePath: coreExe,
                            VirtualIpv4: runtime.EasyTierVirtualIpv4,
                            NetworkName: cfg.NetworkName,
                            PeerUrl: cfg.PeerUrl,
                            Peers: runtime.EasyTierPeers,
                            DetailMessage: runtime.EasyTierDetail,
                            NeedsElevationWarning: false,
                            ManagedByService: true);
                    }
                }
            }

            status ??= await EasyTierManager.QueryStatusAsync(coreExe, cfg, cts.Token);
            if (cts.IsCancellationRequested)
                return;

            ApplyEasyTierStatusToUi(status);
        }
        catch (OperationCanceledException)
        {
            // Superseded by a newer refresh or window closing.
        }
        catch (Exception error)
        {
            ShowStatus($"读取 EasyTier 状态失败：{error.Message}", warning: true);
        }
    }

    private void ApplyEasyTierStatusToUi(EasyTierStatus status)
    {
        currentEasyTierVirtualIpv4 = status.VirtualIpv4;

        if (status.Running)
        {
            SetHostBadge(EasyTierStatusBadge, EasyTierStatusBadgeText, "已组网", "ReadySoft", "Ready");
            NetworkNavDot.SetResourceReference(Shape.FillProperty, "Ready");
            NetworkNavDot.Opacity = 1.0;
            MotionBehaviors.SetEnablePulse(NetworkNavDot, true);
        }
        else if (status.Installed)
        {
            SetHostBadge(EasyTierStatusBadge, EasyTierStatusBadgeText, "未运行", "WarningSoft", "Warning");
            MotionBehaviors.SetEnablePulse(NetworkNavDot, false);
            var configured = settings.EasyTier is { Enabled: true };
            NetworkNavDot.SetResourceReference(Shape.FillProperty, configured ? "Accent" : "Muted");
            NetworkNavDot.Opacity = configured ? 0.9 : 0.45;
        }
        else
        {
            SetHostBadge(EasyTierStatusBadge, EasyTierStatusBadgeText, "未安装", "SurfaceSubtle", "Muted");
            MotionBehaviors.SetEnablePulse(NetworkNavDot, false);
            NetworkNavDot.SetResourceReference(Shape.FillProperty, "Muted");
            NetworkNavDot.Opacity = 0.45;
        }

        if (!string.IsNullOrWhiteSpace(status.VirtualIpv4))
        {
            EasyTierVirtualIpText.Text = status.VirtualIpv4;
            EasyTierVirtualIpPill.Visibility = Visibility.Visible;
        }
        else
        {
            EasyTierVirtualIpText.Text = "";
            EasyTierVirtualIpPill.Visibility = Visibility.Collapsed;
        }

        EasyTierDetailText.Text = !string.IsNullOrWhiteSpace(status.DetailMessage)
            ? status.DetailMessage
            : "登录后自动分配隔离网络与固定虚拟 IP；无账号时也可在下方高级选项手动组网。";

        EasyTierElevationBanner.Visibility = status.NeedsElevationWarning ? Visibility.Visible : Visibility.Collapsed;
        RefreshDaemonServiceUi();

        StartEasyTierButton.IsEnabled = status.Installed && !status.Running;
        StopEasyTierButton.IsEnabled = status.Running;
        EasyTierGoEnginesButton.Visibility = status.Installed ? Visibility.Collapsed : Visibility.Visible;

        easyTierPeers.Clear();
        foreach (var peer in status.Peers)
            easyTierPeers.Add(EasyTierPeerViewModel.FromPeer(peer));

        EasyTierPeerCountText.Text = $"{easyTierPeers.Count} 个节点";
        EasyTierPeersEmptyCard.Visibility = easyTierPeers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        if (easyTierPeers.Count == 0)
        {
            if (!status.Installed)
            {
                EasyTierEmptyTitle.Text = "尚未安装 EasyTier 引擎";
                EasyTierEmptySubtitle.Text = "请先前往「引擎与设置」一键安装或导入 EasyTier，再返回此处填写组网参数并启动。";
                EasyTierEmptyEnginesButton.Visibility = Visibility.Visible;
            }
            else if (!status.Running)
            {
                EasyTierEmptyTitle.Text = "EasyTier 组网未运行";
                EasyTierEmptySubtitle.Text = "在上方填写相同的网络名称、网络密钥与阿里云节点地址（如 tcp://<公网IP>:11010），点击「启动组网」即可发现同一网络下的其他设备。";
                EasyTierEmptyEnginesButton.Visibility = Visibility.Collapsed;
            }
            else
            {
                EasyTierEmptyTitle.Text = "正在等待节点同步";
                EasyTierEmptySubtitle.Text = "EasyTier 已启动，请确保另一台电脑或阿里云节点已加入相同网络名称与密钥，稍后点击「刷新节点」。";
                EasyTierEmptyEnginesButton.Visibility = Visibility.Collapsed;
            }
        }
    }

    private bool TryBuildAndSaveEasyTierConfig(bool enabled, out EasyTierConfig validated)
    {
        validated = new EasyTierConfig();

        var autoAssign = EasyTierAutoAssignToggle.IsChecked == true;
        var rawVirtualIp = EasyTierVirtualIpv4Input.Text.Trim();
        var draft = new EasyTierConfig
        {
            Enabled = enabled,
            PeerUrl = EasyTierPeerUrlInput.Text.Trim(),
            NetworkName = EasyTierNetworkNameInput.Text.Trim(),
            NetworkSecret = GetEasyTierSecretText().Trim(),
            AutoAssignIpv4 = autoAssign,
            VirtualIpv4 = autoAssign || string.IsNullOrEmpty(rawVirtualIp) ? null : rawVirtualIp,
            EnableRelayFallback = settings.EasyTier?.EnableRelayFallback ?? true,
            AutoStartWithApp = EasyTierAutoStartToggle.IsChecked == true,
        };

        try
        {
            validated = EasyTierConfig.Validate(draft);
        }
        catch (ArgumentException error)
        {
            ShowEasyTierConfigFeedback(error.Message, warning: true);
            if (error.ParamName == nameof(EasyTierConfig.PeerUrl) || error.ParamName == "url")
                EasyTierPeerUrlInput.Focus();
            else if (error.ParamName == nameof(EasyTierConfig.NetworkName))
                EasyTierNetworkNameInput.Focus();
            else if (error.ParamName == nameof(EasyTierConfig.NetworkSecret))
                (isEasyTierSecretVisible ? (Control)EasyTierSecretTextBox : EasyTierSecretPasswordBox).Focus();
            else if (error.ParamName == nameof(EasyTierConfig.VirtualIpv4))
                EasyTierVirtualIpv4Input.Focus();
            return false;
        }

        try
        {
            SaveSettings(settings with { EasyTier = validated });
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            ShowEasyTierConfigFeedback($"无法保存组网配置：{error.Message}", warning: true);
            return false;
        }
    }

    private void SetEasyTierSecretText(string value)
    {
        isSyncingEasyTierSecret = true;
        try
        {
            EasyTierSecretPasswordBox.Password = value;
            EasyTierSecretTextBox.Text = value;
        }
        finally
        {
            isSyncingEasyTierSecret = false;
        }
    }

    private string GetEasyTierSecretText() =>
        isEasyTierSecretVisible ? EasyTierSecretTextBox.Text : EasyTierSecretPasswordBox.Password;

    private void UpdateEasyTierVirtualIpFieldVisibility()
    {
        if (EasyTierVirtualIpv4Panel is null || EasyTierVirtualIpv4Input is null)
            return;

        var autoAssign = EasyTierAutoAssignToggle.IsChecked == true;
        EasyTierVirtualIpv4Input.IsEnabled = !autoAssign;
        EasyTierVirtualIpv4Panel.Visibility = autoAssign ? Visibility.Collapsed : Visibility.Visible;
    }

    private void UpdateEasyTierPlaceholders()
    {
        if (EasyTierPeerUrlPlaceholder is not null)
            EasyTierPeerUrlPlaceholder.Visibility = string.IsNullOrEmpty(EasyTierPeerUrlInput.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (EasyTierNetworkNamePlaceholder is not null)
            EasyTierNetworkNamePlaceholder.Visibility = string.IsNullOrEmpty(EasyTierNetworkNameInput.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (EasyTierSecretPlaceholder is not null)
            EasyTierSecretPlaceholder.Visibility = string.IsNullOrEmpty(GetEasyTierSecretText()) ? Visibility.Visible : Visibility.Collapsed;
        if (EasyTierVirtualIpv4Placeholder is not null)
            EasyTierVirtualIpv4Placeholder.Visibility = string.IsNullOrEmpty(EasyTierVirtualIpv4Input.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ShowEasyTierConfigFeedback(string message, bool warning)
    {
        EasyTierConfigFeedback.Text = message;
        EasyTierConfigFeedback.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Warning" : "Ready");
        EasyTierConfigFeedback.Visibility = Visibility.Visible;
    }

    private static string? FindLocalAliyunGuidePath()
    {
        string[] searchRoots = [AppContext.BaseDirectory, Environment.CurrentDirectory];
        foreach (var root in searchRoots)
        {
            if (string.IsNullOrWhiteSpace(root))
                continue;

            var dir = new DirectoryInfo(root);
            for (var depth = 0; depth < 6 && dir is not null; depth++, dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "docs", "aliyun-server-guide.md");
                if (File.Exists(candidate))
                    return candidate;
            }
        }

        return null;
    }
}
