using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using RemoteDesk.Streaming;
using Path = System.IO.Path;

namespace RemoteDesk;

internal sealed record DeviceListItem(
    DeviceProfile? LocalProfile,
    string Name,
    string? HostAddress,
    StreamOptions Stream,
    string StreamAppName,
    string TargetSubtitle,
    string ModeBadgeText,
    bool IsCloudSynced = false,
    bool IsOnline = true,
    SyncedDeviceInfo? SyncedDevice = null)
{
    public string UniqueKey => SyncedDevice is not null
        ? $"cloud:{SyncedDevice.Id}"
        : $"local:{Name}:{HostAddress}";

    public bool SupportsStream => !string.IsNullOrWhiteSpace(HostAddress);

    public DeviceProfile ToProfile(string? hostOverride = null)
    {
        var effectiveHost = !string.IsNullOrWhiteSpace(hostOverride)
            ? hostOverride
            : HostAddress;
        if (string.IsNullOrWhiteSpace(effectiveHost))
            throw new ArgumentException("该设备缺少有效主机地址。");

        return LocalProfile is not null && string.Equals(LocalProfile.HostAddress, effectiveHost, StringComparison.OrdinalIgnoreCase)
            ? LocalProfile
            : DeviceProfile.Create(Name, effectiveHost, Stream, StreamAppName);
    }

    public static DeviceListItem FromProfile(DeviceProfile profile) =>
        new(
            LocalProfile: profile,
            Name: profile.Name,
            HostAddress: profile.HostAddress,
            Stream: profile.Stream,
            StreamAppName: profile.StreamAppName,
            TargetSubtitle: profile.HostAddress,
            ModeBadgeText: "高刷",
            IsCloudSynced: false,
            IsOnline: true,
            SyncedDevice: null);

    public static DeviceListItem FromSyncedDevice(SyncedDeviceInfo synced, StreamOptions defaultStream)
    {
        var preferredHost = !string.IsNullOrWhiteSpace(synced.VirtualIpv4)
            ? synced.VirtualIpv4
            : synced.LanIpv4;
        var statusText = synced.IsOnline ? "在线" : "离线";
        var pinTag = synced.SecurityPinRequired ? " · 需密码" : "";
        var subtitle = $"{preferredHost ?? "待分配 IP"} · {statusText}{pinTag}";
        var badge = string.IsNullOrWhiteSpace(synced.GpuEncoder) ? "云同步" : $"云 · {synced.GpuEncoder}";

        return new DeviceListItem(
            LocalProfile: null,
            Name: synced.Name,
            HostAddress: preferredHost,
            Stream: defaultStream,
            StreamAppName: DeviceProfile.DefaultStreamAppName,
            TargetSubtitle: subtitle,
            ModeBadgeText: badge,
            IsCloudSynced: true,
            IsOnline: synced.IsOnline,
            SyncedDevice: synced);
    }
}

public partial class MainWindow : Window
{
    private enum AppPage
    {
        Remote,
        Host,
        Network,
        Engines,
    }

    private readonly SettingsStore store = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RemoteDesk", "settings.json"));
    private readonly EngineManager engineManager = new();

    private SessionBridgeServer? sessionBridgeServer;
    private AppSettings settings = new();
    private DeviceProfile? editingDevice;
    private AppPage currentPage = AppPage.Remote;
    private bool isSyncingThemeToggle;
    private bool isSyncingStreamUi;
    private bool isDraggingBitrate;
    private bool isSyncingHostToggle;

    private DeviceListItem? SelectedListItem => DeviceList.SelectedItem as DeviceListItem;

    public MainWindow()
    {
        InitializeComponent();
        InitializeEngineHub();

        if (OperatingSystem.IsWindows())
        {
            sessionBridgeServer = new SessionBridgeServer(Dispatcher);
            sessionBridgeServer.Start();
        }

        try
        {
            settings = store.Load();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            ShowStatus("无法读取之前的设置，已使用默认配置。你可以重新添加电脑。", warning: true);
        }

        ApplyTheme(settings.UseDarkTheme);
        SyncMirrorInputFromSettings();
        InitializeAccount();
        InitializeNetwork();
        InitializeUpdates();
        RefreshDevices(0);
        RefreshAllEngines();
        RefreshHostAndNetworkInfo();

        Loaded += async (_, _) =>
        {
            FocusDeviceAction();
            await OnMainWindowLoadedAsync();
        };
        Activated += (_, _) =>
        {
            RefreshAllEngines();
            if (currentPage == AppPage.Host)
                RefreshHostStatus();
            else if (currentPage == AppPage.Network)
                _ = RefreshDaemonAndEasyTierAsync();
        };
        Closed += (_, _) =>
        {
            sessionBridgeServer?.Dispose();
            sessionBridgeServer = null;
            accountSyncTimer.Stop();
            updateTimer.Stop();
            updateCts?.Cancel();
            updateCts?.Dispose();
            easyTierQueryCts?.Cancel();
            easyTierQueryCts?.Dispose();
        };
    }

    private async Task OnMainWindowLoadedAsync()
    {
        try
        {
            await TryAutoStartEasyTierOnLaunchAsync();
        }
        catch (Exception)
        {
            // Keep UI responsive even if auto-start/status query fails.
        }

        try
        {
            await TryAutoSyncOnLaunchAsync();
        }
        catch (Exception)
        {
        }

        await SafeCheckUpdatesAsync(userInitiated: false);
    }

    private void MinimizeWindow(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);

    private void CloseWindow(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void ToggleMaximized(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            SystemCommands.RestoreWindow(this);
        else
            SystemCommands.MaximizeWindow(this);
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        bool maximized = WindowState == WindowState.Maximized;
        RootLayout.Margin = maximized ? new Thickness(7) : new Thickness(0);
        MaximizeButton.Content = maximized ? "\uE923" : "\uE922";
        MaximizeButton.ToolTip = maximized ? "还原" : "最大化";
        AutomationProperties.SetName(MaximizeButton, (string)MaximizeButton.ToolTip);
    }

    private void ShowRemotePage(object sender, RoutedEventArgs e)
    {
        NavigateTo(AppPage.Remote);
        if (RemovalView.IsVisible)
            KeepDeviceButton.Focus();
        else if (EditorView.Visibility == Visibility.Visible)
            NameInput.Focus();
        else
            FocusDeviceAction();
    }

    private void ShowHostPage(object sender, RoutedEventArgs e)
    {
        NavigateTo(AppPage.Host);
        RefreshHostAndNetworkInfo();
    }

    private void ShowNetworkPage(object sender, RoutedEventArgs e)
    {
        NavigateTo(AppPage.Network);
        RefreshNetworkSummary();
        _ = RefreshEasyTierStatusAsync();
    }

    private void ShowEnginesPage(object sender, RoutedEventArgs e)
    {
        NavigateTo(AppPage.Engines);
        RefreshAllEngines();
    }

    private void NavigateTo(AppPage page)
    {
        currentPage = page;
        RemoteControlPage.Visibility = page == AppPage.Remote ? Visibility.Visible : Visibility.Collapsed;
        LocalHostPage.Visibility = page == AppPage.Host ? Visibility.Visible : Visibility.Collapsed;
        NetworkNodesPage.Visibility = page == AppPage.Network ? Visibility.Visible : Visibility.Collapsed;
        EnginesSettingsPage.Visibility = page == AppPage.Engines ? Visibility.Visible : Visibility.Collapsed;

        RemoteNav.Tag = page == AppPage.Remote ? "Selected" : null;
        HostNav.Tag = page == AppPage.Host ? "Selected" : null;
        NetworkNav.Tag = page == AppPage.Network ? "Selected" : null;
        EnginesNav.Tag = page == AppPage.Engines ? "Selected" : null;

        ShowStatus("");
    }

    // ==================== PAGE 1: REMOTE CONTROL ====================

    private void AddDevice(object sender, RoutedEventArgs e)
    {
        NavigateTo(AppPage.Remote);
        OpenEditor(null);
    }

    private void OpenAddDeviceFromPeer(string hostname, string virtualIpv4)
    {
        NavigateTo(AppPage.Remote);
        OpenEditor(
            device: null,
            prefillName: hostname,
            prefillHostAddress: virtualIpv4);
        ShowEditorMessage($"已从 EasyTier 节点预填主机名与虚拟 IP（{virtualIpv4}），确认后点击「保存电脑」即可发起高刷串流。");
    }

    private void EditDevice(object sender, RoutedEventArgs e)
    {
        if (SelectedListItem is { IsCloudSynced: true } cloudItem)
        {
            ShowStatus($"「{cloudItem.Name}」由账号云同步自动管理，无需手动编辑地址。");
            return;
        }
        OpenEditor(SelectedListItem?.LocalProfile);
    }

    private void OpenEditor(
        DeviceProfile? device,
        string? prefillName = null,
        string? prefillHostAddress = null)
    {
        editingDevice = device;
        RemoveButton.Visibility = device is null ? Visibility.Collapsed : Visibility.Visible;
        EditorTitle.Text = device is null ? "添加电脑" : "编辑电脑";
        NameInput.Text = device?.Name ?? prefillName ?? "";
        HostAddressInput.Text = device?.HostAddress ?? prefillHostAddress ?? "";
        StreamAppNameInput.Text = device?.StreamAppName ?? DeviceProfile.DefaultStreamAppName;
        ShowEditorMessage("填写目标主机的校园网 / 局域网地址或 EasyTier 虚拟 IP，保存后将使用内置原生高刷窗口控制。");
        ShowStatus("");

        EmptyView.Visibility = Visibility.Collapsed;
        DeviceView.Visibility = Visibility.Collapsed;
        RemovalView.Visibility = Visibility.Collapsed;
        SetDeviceListEnabled(false);
        EditorView.Visibility = Visibility.Visible;
        NameInput.Focus();
    }

    private void CancelEdit(object sender, RoutedEventArgs e)
    {
        ShowDevice();
        FocusDeviceAction();
    }

    private void RequestRemoval(object sender, RoutedEventArgs e)
    {
        if (editingDevice is not { } device)
            return;

        RemovalMessage.Text = $"将从本机列表中移除「{device.Name}」。之后可以重新添加该电脑。";
        EditorView.Visibility = Visibility.Collapsed;
        RemovalView.Visibility = Visibility.Visible;
        KeepDeviceButton.Focus();
    }

    private void KeepDevice(object sender, RoutedEventArgs e)
    {
        RemovalView.Visibility = Visibility.Collapsed;
        EditorView.Visibility = Visibility.Visible;
        RemoveButton.Focus();
    }

    private void RemoveDevice(object sender, RoutedEventArgs e)
    {
        try
        {
            var localIndex = FindLocalDeviceIndex(SelectedListItem);
            if (editingDevice is null || localIndex < 0 || !ReferenceEquals(editingDevice, SelectedListItem?.LocalProfile))
                return;

            var listIndex = DeviceList.SelectedIndex;
            var devices = settings.Devices.ToList();
            devices.RemoveAt(localIndex);
            SaveSettings(settings with { Devices = devices });
            RefreshDevices(Math.Max(0, listIndex - 1));
            ShowStatus("已从本机列表移除电脑。");
            FocusDeviceAction();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            KeepDevice(sender, e);
            ShowEditorMessage("无法保存更改，请检查本机用户目录的写入权限。", warning: true);
        }
    }

    private void SaveDevice(object sender, RoutedEventArgs e)
    {
        try
        {
            var streamOptions = editingDevice?.Stream ?? settings.DefaultStream ?? StreamOptions.FromPreset(StreamPresetKind.High2K);
            var appName = string.IsNullOrWhiteSpace(StreamAppNameInput.Text) ? null : StreamAppNameInput.Text;

            var device = DeviceProfile.Create(
                NameInput.Text,
                HostAddressInput.Text,
                streamOptions,
                appName);

            var devices = settings.Devices.ToList();
            if (devices.Any(existing =>
                !ReferenceEquals(existing, editingDevice) &&
                string.Equals(existing.HostAddress, device.HostAddress, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.StreamAppName, device.StreamAppName, StringComparison.OrdinalIgnoreCase)))
            {
                ShowEditorMessage("相同主机地址与串流应用名的电脑已存在于列表中。", warning: true);
                HostAddressInput.Focus();
                return;
            }

            if (editingDevice is null)
            {
                devices.Add(device);
            }
            else
            {
                var localIndex = FindLocalDeviceIndex(SelectedListItem);
                if (localIndex < 0)
                    return;
                devices[localIndex] = device;
            }

            SaveSettings(settings with { Devices = devices });
            RefreshDevices(0, DeviceListItem.FromProfile(device).UniqueKey);
            ShowStatus($"已保存设备「{device.Name}」。");
            FocusDeviceAction();
        }
        catch (ArgumentException error)
        {
            ShowEditorMessage(error.Message, warning: true);
            if (error.ParamName == "name")
                NameInput.Focus();
            else if (error.ParamName == "streamAppName")
                StreamAppNameInput.Focus();
            else
                HostAddressInput.Focus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowEditorMessage("无法保存设置，请检查本机用户目录的写入权限。", warning: true);
        }
    }

    private void SelectPresetUltra120(object sender, RoutedEventArgs e) => ApplyStreamPreset(StreamPresetKind.Ultra120);

    private void SelectPresetHigh2K(object sender, RoutedEventArgs e) => ApplyStreamPreset(StreamPresetKind.High2K);

    private void SelectPresetSaver(object sender, RoutedEventArgs e) => ApplyStreamPreset(StreamPresetKind.Saver);

    private void SelectPresetCustom(object sender, RoutedEventArgs e)
    {
        if (SelectedListItem is not { } item)
            return;

        var custom = item.Stream with { Preset = StreamPresetKind.Custom };
        UpdateSelectedDeviceStream(custom, persist: true);
    }

    private void ApplyStreamPreset(StreamPresetKind preset)
    {
        if (SelectedListItem is null)
            return;

        var options = StreamOptions.FromPreset(preset);
        UpdateSelectedDeviceStream(options, persist: true);
    }

    private void StreamParamChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isSyncingStreamUi || SelectedListItem is null)
            return;

        CommitCustomStreamFromControls(persist: true);
    }

    private void BitrateSliderDragStarted(object sender, DragStartedEventArgs e) => isDraggingBitrate = true;

    private void BitrateSliderDragCompleted(object sender, DragCompletedEventArgs e)
    {
        isDraggingBitrate = false;
        if (isSyncingStreamUi || SelectedListItem is null)
            return;

        CommitCustomStreamFromControls(persist: true);
    }

    private void BitrateSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BitrateValueText is null)
            return;

        var mbps = (int)Math.Round(e.NewValue);
        BitrateValueText.Text = $"{mbps} Mbps";

        if (isSyncingStreamUi || SelectedListItem is null)
            return;

        CommitCustomStreamFromControls(persist: !isDraggingBitrate);
    }

    private void HardwareDecodeChanged(object sender, RoutedEventArgs e)
    {
        if (HardwareDecodeToggle is null)
            return;

        HardwareDecodeToggle.Content = HardwareDecodeToggle.IsChecked == true ? "已开启" : "已关闭";
        if (isSyncingStreamUi || SelectedListItem is null)
            return;

        CommitCustomStreamFromControls(persist: true);
    }

    private void CommitCustomStreamFromControls(bool persist)
    {
        if (SelectedListItem is not { } item)
            return;

        var (width, height) = ParseSelectedResolution(item.Stream.Width, item.Stream.Height);
        var fps = ParseSelectedFps(item.Stream.Fps);
        var codec = ParseSelectedCodec(item.Stream.Codec);
        var bitrateMbps = Math.Clamp((int)Math.Round(BitrateSlider.Value), StreamOptions.MinBitrateMbps, StreamOptions.MaxBitrateMbps);
        var hwDecode = HardwareDecodeToggle.IsChecked == true;

        var customOptions = StreamOptions.Create(
            StreamPresetKind.Custom,
            width,
            height,
            fps,
            bitrateMbps,
            codec,
            hwDecode);

        UpdateSelectedDeviceStream(customOptions, persist);
    }

    private void UpdateSelectedDeviceStream(StreamOptions options, bool persist)
    {
        if (SelectedListItem is not { } item)
            return;

        if (persist)
            PersistUpdatedSelectedItemStream(item, options);
        else
            SyncStreamControls(options, syncSliderValue: false);
    }

    private void PersistUpdatedSelectedItemStream(DeviceListItem item, StreamOptions nextStream)
    {
        if (item.IsCloudSynced)
        {
            try
            {
                SaveSettings(settings with { DefaultStream = nextStream });
                RefreshDevices(DeviceList.SelectedIndex, item.UniqueKey);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                ShowStatus("无法保存设备参数，请检查本机用户目录写入权限。", warning: true);
            }
            return;
        }

        var localIdx = FindLocalDeviceIndex(item);
        if (localIdx < 0 || localIdx >= settings.Devices.Count)
            return;

        try
        {
            var devices = settings.Devices.ToList();
            var existing = devices[localIdx];
            devices[localIdx] = DeviceProfile.Create(existing.Name, existing.HostAddress, nextStream, existing.StreamAppName);
            SaveSettings(settings with { Devices = devices, DefaultStream = nextStream });
            RefreshDevices(DeviceList.SelectedIndex, item.UniqueKey);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus("无法保存设备参数，请检查本机用户目录写入权限。", warning: true);
        }
    }

    private int FindLocalDeviceIndex(DeviceListItem? item)
    {
        if (item is null || item.IsCloudSynced || item.LocalProfile is null)
            return -1;

        for (var i = 0; i < settings.Devices.Count; i++)
        {
            if (ReferenceEquals(settings.Devices[i], item.LocalProfile))
                return i;
        }
        return -1;
    }

    private async void ConnectDevice(object sender, RoutedEventArgs e)
    {
        if (SelectedListItem is not { } item)
            return;

        if (!NativeStreamBridge.IsAvailable())
        {
            ShowStatus("未检测到内置原生控屏组件 RemoteDesk.StreamCore.dll，请重新构建或完整安装客户端。", warning: true);
            return;
        }

        if (item.IsCloudSynced)
        {
            await ConnectSyncedOrAutoPairDeviceAsync(item, securityPin: null, forcePair: false);
            return;
        }

        if (!item.SupportsStream)
        {
            ShowStatus("该设备未配置目标主机地址，请点击「编辑」补充主机地址。", warning: true);
            return;
        }

        OpenNativeSessionWindow(item.ToProfile(), item.HostAddress!, item);
    }

    private async void PairDevice(object sender, RoutedEventArgs e)
    {
        if (SelectedListItem is not { } item)
            return;

        if (item.IsCloudSynced)
        {
            await ConnectSyncedOrAutoPairDeviceAsync(item, securityPin: null, forcePair: true);
            return;
        }

        if (!NativeStreamBridge.IsAvailable())
        {
            ShowStatus("未检测到内置原生控屏组件 RemoteDesk.StreamCore.dll，无法发起配对。", warning: true);
            return;
        }

        if (string.IsNullOrEmpty(item.HostAddress))
        {
            ShowStatus("配对需要目标主机的校园网/局域网地址，请先点击「编辑」填写地址。", warning: true);
            return;
        }

        PairButton.IsEnabled = false;
        try
        {
            using var identity = GameStreamIdentity.LoadOrCreate();
            var gsClient = new GameStreamClient(identity);
            var info = await gsClient.GetServerInfoAsync(item.HostAddress);
            if (info.PairStatus == 1)
            {
                ShowStatus($"目标主机 {item.HostAddress} 已完成配对，可直接点击「发起高刷连接」。");
                return;
            }

            var pin = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4", CultureInfo.InvariantCulture);
            ShowStatus($"正在向 {item.HostAddress} 发起原生配对，请在目标电脑 Sunshine 控制台 (:47990) 输入 PIN 码：{pin}");
            var (ok, message) = await gsClient.PairWithPinAsync(item.HostAddress, pin);
            ShowStatus(ok ? $"配对成功：{item.HostAddress}，可直接发起高刷连接。" : $"配对失败：{message}", warning: !ok);
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or InvalidDataException or IOException or InvalidOperationException)
        {
            ShowStatus($"原生配对失败：{error.Message}", warning: true);
        }
        finally
        {
            PairButton.IsEnabled = true;
        }
    }

    private void RefreshDevices(int index, string? selectUniqueKey = null)
    {
        var items = new List<DeviceListItem>();
        var localKey = EnsureLocalDeviceKeyPersisted();
        var defaultStream = settings.DefaultStream ?? StreamOptions.FromPreset(StreamPresetKind.High2K);
        var cloudHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var synced in syncedDevices)
        {
            if (string.Equals(synced.DeviceKey, localKey, StringComparison.OrdinalIgnoreCase))
                continue;

            var cloudItem = DeviceListItem.FromSyncedDevice(synced, defaultStream);
            items.Add(cloudItem);
            if (!string.IsNullOrWhiteSpace(synced.VirtualIpv4))
                cloudHosts.Add(synced.VirtualIpv4);
        }

        foreach (var local in settings.Devices)
        {
            if (!string.IsNullOrWhiteSpace(local.HostAddress) && cloudHosts.Contains(local.HostAddress))
                continue;
            items.Add(DeviceListItem.FromProfile(local));
        }

        DeviceList.ItemsSource = items;
        var resolvedIndex = -1;
        if (!string.IsNullOrEmpty(selectUniqueKey))
            resolvedIndex = items.FindIndex(i => string.Equals(i.UniqueKey, selectUniqueKey, StringComparison.Ordinal));
        if (resolvedIndex < 0 && items.Count > 0)
            resolvedIndex = Math.Clamp(index, 0, items.Count - 1);

        DeviceList.SelectedIndex = resolvedIndex;
        DeviceCount.Text = $"{items.Count} 台";
        DeviceListEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DeviceList.SelectedItem is not null)
            DeviceList.ScrollIntoView(DeviceList.SelectedItem);
        ShowDevice();
    }

    private void DeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedListItem is not null && currentPage != AppPage.Remote)
            NavigateTo(AppPage.Remote);
        ShowDevice();
        ShowStatus("");
    }

    private void SetDeviceListEnabled(bool enabled)
    {
        DeviceList.IsEnabled = enabled;
        AddFromListButton.IsEnabled = enabled;
    }

    private void ShowDevice()
    {
        var item = SelectedListItem;
        editingDevice = null;
        pendingSecurityPinItem = null;
        if (SecurityPinPromptCard is not null)
            SecurityPinPromptCard.Visibility = Visibility.Collapsed;
        SetDeviceListEnabled(true);

        EmptyView.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        DeviceView.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        EditorView.Visibility = Visibility.Collapsed;
        RemovalView.Visibility = Visibility.Collapsed;

        if (item is null)
            return;

        DeviceNameText.Text = item.Name;
        if (item is { IsCloudSynced: true, SyncedDevice: { } synced })
        {
            DeviceActiveModeText.Text = synced.IsOnline ? "云同步 · 在线" : "云同步 · 离线";
            DeviceActiveModeBadge.SetResourceReference(Border.BackgroundProperty, synced.IsOnline ? "ReadySoft" : "SurfaceSubtle");
            DeviceActiveModeText.SetResourceReference(TextBlock.ForegroundProperty, synced.IsOnline ? "Ready" : "Muted");
            DeviceHostText.Text = synced.VirtualIpv4 is not null ? $"虚拟内网 {synced.VirtualIpv4}" : "待分配虚拟 IP";
            DeviceSecondaryText.Text = synced.LanIpv4 is not null ? $"局域网 {synced.LanIpv4}" : (synced.GpuEncoder is not null ? $"编码 {synced.GpuEncoder}" : "自动选路");
        }
        else
        {
            DeviceActiveModeText.Text = "原生高刷";
            DeviceActiveModeBadge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            DeviceActiveModeText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            DeviceHostText.Text = item.HostAddress is not null ? $"直连 {item.HostAddress}" : "未配置直连地址";
            DeviceSecondaryText.Text = "办公/游戏双模";
        }

        DeviceAppText.Text = $"应用 {item.StreamAppName}";
        DeviceAppPill.Visibility = Visibility.Visible;

        SyncStreamControls(item.Stream, syncSliderValue: true);

        ConnectButtonText.Text = "发起高刷连接";
        ConnectButtonIcon.Text = "\uE945";
        PairButton.Visibility = Visibility.Visible;
        ActionTargetSummary.Text = item.HostAddress is not null
            ? $"目标 {item.HostAddress} · {item.StreamAppName}"
            : "等待分配虚拟内网地址";

        UpdateDeviceActionEngineReadiness();
    }

    private void SyncStreamControls(StreamOptions stream, bool syncSliderValue)
    {
        isSyncingStreamUi = true;
        try
        {
            PresetUltra120Button.Tag = stream.Preset == StreamPresetKind.Ultra120 ? "Selected" : null;
            PresetHigh2KButton.Tag = stream.Preset == StreamPresetKind.High2K ? "Selected" : null;
            PresetSaverButton.Tag = stream.Preset == StreamPresetKind.Saver ? "Selected" : null;
            PresetCustomButton.Tag = stream.Preset == StreamPresetKind.Custom ? "Selected" : null;

            CustomPresetSubtitle.Text = stream.Preset == StreamPresetKind.Custom
                ? $"{stream.Width}×{stream.Height} · {stream.Fps}fps · {stream.BitrateMbps}M"
                : "手动微调参数";

            StreamSummaryText.Text = $"{stream.Width}×{stream.Height} · {stream.Fps}fps · {stream.BitrateMbps} Mbps · {stream.GetCodecArgument()}";

            SelectComboByTag(ResolutionCombo, $"{stream.Width}x{stream.Height}");
            SelectComboByTag(FpsCombo, stream.Fps.ToString(CultureInfo.InvariantCulture));
            SelectComboByTag(CodecCombo, stream.Codec.ToString());

            if (syncSliderValue)
                BitrateSlider.Value = Math.Clamp(stream.BitrateMbps, (int)BitrateSlider.Minimum, (int)BitrateSlider.Maximum);

            BitrateValueText.Text = $"{stream.BitrateMbps} Mbps";
            HardwareDecodeToggle.IsChecked = stream.HardwareDecoding;
            HardwareDecodeToggle.Content = stream.HardwareDecoding ? "已开启" : "已关闭";
        }
        finally
        {
            isSyncingStreamUi = false;
        }
    }

    private void UpdateDeviceActionEngineReadiness()
    {
        if (SelectedListItem is not { } item)
            return;

        if (!NativeStreamBridge.IsAvailable())
        {
            ActionEngineDot.SetResourceReference(Shape.FillProperty, "Danger");
            ActionEngineHint.Text = "未检测到内置原生控屏引擎 RemoteDesk.StreamCore.dll，请检查程序安装完整性。";
            ActionEngineLink.Visibility = Visibility.Collapsed;
        }
        else if (!item.SupportsStream)
        {
            ActionEngineDot.SetResourceReference(Shape.FillProperty, "Warning");
            ActionEngineHint.Text = item.IsCloudSynced
                ? "等待目标电脑上线同步虚拟内网 IP（建议在被控端开启后台守护服务）。"
                : "请点击右上角「编辑」补充目标电脑主机地址。";
            ActionEngineLink.Visibility = Visibility.Collapsed;
        }
        else
        {
            ActionEngineDot.SetResourceReference(Shape.FillProperty, "Ready");
            ActionEngineHint.Text = item.IsCloudSynced
                ? "内置原生高刷引擎已就绪 · 支持剪贴板 / 文件 / 分辨率适配 / 防窥"
                : "内置原生高刷控屏引擎已就绪（D3D11 硬件解码）";
            ActionEngineLink.Visibility = Visibility.Collapsed;
        }
    }

    private void FocusDeviceAction()
    {
        if (currentPage != AppPage.Remote)
            return;
        (SelectedListItem is null ? AddDeviceButton : ConnectButton).Focus();
    }

    private (int Width, int Height) ParseSelectedResolution(int fallbackW, int fallbackH)
    {
        var tag = (ResolutionCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (tag is null)
            return (fallbackW, fallbackH);

        var parts = tag.Split('x');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var w) &&
            int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var h))
        {
            return (w, h);
        }

        return (fallbackW, fallbackH);
    }

    private int ParseSelectedFps(int fallback)
    {
        var tag = (FpsCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return int.TryParse(tag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fps) ? fps : fallback;
    }

    private VideoCodecPreference ParseSelectedCodec(VideoCodecPreference fallback)
    {
        var tag = (CodecCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        return Enum.TryParse<VideoCodecPreference>(tag, out var codec) ? codec : fallback;
    }

    private static void SelectComboByTag(ComboBox comboBox, string tag)
    {
        foreach (var item in comboBox.Items)
        {
            if (item is ComboBoxItem cbi && string.Equals(cbi.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                comboBox.SelectedItem = cbi;
                return;
            }
        }
    }

    // ==================== THEME, SETTINGS & STATUS ====================

    private void SaveSettings(AppSettings updated)
    {
        store.Save(updated);
        settings = updated;
    }

    private void ShowStatus(string message, bool warning = false)
    {
        StatusMessage.Text = message;
        StatusMessage.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Warning" : "Ink");
        StatusBanner.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void DismissStatusClicked(object sender, RoutedEventArgs e) => ShowStatus("");

    private void ShowEditorMessage(string message, bool warning = false)
    {
        EditorMessage.Text = message;
        EditorMessage.SetResourceReference(TextBlock.ForegroundProperty, warning ? "Warning" : "Muted");
    }

    private void ToggleTheme(object sender, RoutedEventArgs e) => SetDarkTheme(!settings.UseDarkTheme);

    private void SettingsThemeToggled(object sender, RoutedEventArgs e)
    {
        if (isSyncingThemeToggle)
            return;

        SetDarkTheme(SettingsThemeToggle.IsChecked == true);
    }

    private void SetDarkTheme(bool dark)
    {
        var updated = settings with { UseDarkTheme = dark };
        settings = updated;
        ApplyTheme(dark);
        try
        {
            store.Save(updated);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus("外观已切换，但无法保存到磁盘，请检查本机用户目录写入权限。", warning: true);
        }
    }

    private void ApplyTheme(bool dark)
    {
        var dictionaries = Resources.MergedDictionaries;
        var palette = dark ? "DarkTheme.xaml" : "LightTheme.xaml";
        if (dictionaries.Count > 0 && dictionaries[0].Source?.OriginalString != palette)
            dictionaries[0] = new ResourceDictionary { Source = new Uri(palette, UriKind.Relative) };

        ThemeButton.Content = dark ? "\uE706" : "\uE708";
        ThemeButton.ToolTip = dark ? "切换到浅色外观" : "切换到深色外观";
        AutomationProperties.SetName(ThemeButton, (string)ThemeButton.ToolTip);

        isSyncingThemeToggle = true;
        try
        {
            SettingsThemeToggle.IsChecked = dark;
            SettingsThemeToggle.Content = dark ? "深色模式" : "浅色模式";
        }
        finally
        {
            isSyncingThemeToggle = false;
        }
    }
}
