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
using RemoteDesk.UI;
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
    SyncedDeviceInfo? SyncedDevice = null,
    bool IsBeingControlled = false,
    bool IsControllingOther = false,
    bool IsControlledByLocal = false,
    string? ControlledByName = null,
    string? ControllingTargetName = null,
    bool IsLocalDevice = false)
{
    public string UniqueKey => SyncedDevice is not null
        ? $"cloud:{SyncedDevice.Id}"
        : (LocalProfile is null && IsLocalDevice ? "local-self" : $"local:{Name}:{HostAddress}");

    public bool SupportsStream => !IsLocalDevice && !string.IsNullOrWhiteSpace(HostAddress);

    public bool IsBlockedByRemoteControl => (IsBeingControlled && !IsControlledByLocal) || IsControllingOther;

    public bool IsRemoteBusy => IsBeingControlled || IsControllingOther;

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

    public static DeviceListItem FromLocalSelf(
        string displayName,
        string? primaryAddress,
        StreamOptions defaultStream) =>
        new(
            LocalProfile: null,
            Name: displayName,
            HostAddress: primaryAddress,
            Stream: defaultStream,
            StreamAppName: DeviceProfile.DefaultStreamAppName,
            TargetSubtitle: !string.IsNullOrWhiteSpace(primaryAddress) ? $"{primaryAddress} · 本机（当前电脑）" : "当前电脑（未组网）",
            ModeBadgeText: "本机",
            IsCloudSynced: false,
            IsOnline: true,
            SyncedDevice: null,
            IsLocalDevice: true);

    public static DeviceListItem FromProfile(
        DeviceProfile profile,
        bool isControlledByLocal = false,
        bool isLocalDevice = false) =>
        new(
            LocalProfile: profile,
            Name: profile.Name,
            HostAddress: profile.HostAddress,
            Stream: profile.Stream,
            StreamAppName: profile.StreamAppName,
            TargetSubtitle: isLocalDevice
                ? $"{profile.HostAddress} · 本机"
                : (isControlledByLocal ? $"{profile.HostAddress} · 被远控（本机）" : profile.HostAddress),
            ModeBadgeText: isLocalDevice ? "本机" : (isControlledByLocal ? "被远控" : "高刷"),
            IsCloudSynced: false,
            IsOnline: true,
            SyncedDevice: null,
            IsBeingControlled: isControlledByLocal,
            IsControllingOther: false,
            IsControlledByLocal: isControlledByLocal,
            ControlledByName: isControlledByLocal ? "本机" : null,
            IsLocalDevice: isLocalDevice);

    public static DeviceListItem FromSyncedDevice(
        SyncedDeviceInfo synced,
        StreamOptions defaultStream,
        string localDeviceKey = "",
        bool isControlledByLocal = false,
        string? displayNameOverride = null,
        bool isLocalDevice = false)
    {
        var displayName = !string.IsNullOrWhiteSpace(displayNameOverride)
            ? displayNameOverride.Trim()
            : synced.Name;
        var preferredHost = !string.IsNullOrWhiteSpace(synced.VirtualIpv4)
            ? synced.VirtualIpv4
            : synced.LanIpv4;

        if (isLocalDevice)
        {
            var selfSubtitle = $"{preferredHost ?? "待分配 IP"} · 本机（当前电脑）";
            return new DeviceListItem(
                LocalProfile: null,
                Name: displayName,
                HostAddress: preferredHost,
                Stream: defaultStream,
                StreamAppName: DeviceProfile.DefaultStreamAppName,
                TargetSubtitle: selfSubtitle,
                ModeBadgeText: "本机",
                IsCloudSynced: true,
                IsOnline: true,
                SyncedDevice: synced,
                IsLocalDevice: true);
        }

        var status = RemoteControlLockPolicy.ParseStatus(synced.RemoteControlState);
        var isBeingControlled = isControlledByLocal || status == DeviceRemoteControlStatus.BeingControlled;
        var isControllingOther = !isBeingControlled && status == DeviceRemoteControlStatus.Controlling;
        var controlledByLocal = isControlledByLocal ||
            (isBeingControlled && string.Equals(synced.ControlledByDeviceKey, localDeviceKey, StringComparison.OrdinalIgnoreCase));

        string statusText;
        string badge;
        if (isBeingControlled)
        {
            badge = "被远控";
            statusText = controlledByLocal
                ? "被远控（本机）"
                : (!string.IsNullOrWhiteSpace(synced.ControlledByDeviceName)
                    ? $"被 {synced.ControlledByDeviceName} 远控"
                    : "被远控");
        }
        else if (isControllingOther)
        {
            badge = "远控中";
            statusText = !string.IsNullOrWhiteSpace(synced.ControllingTargetDeviceName)
                ? $"远控中 ({synced.ControllingTargetDeviceName})"
                : "远控中";
        }
        else
        {
            statusText = synced.IsOnline ? "在线" : "离线";
            badge = string.IsNullOrWhiteSpace(synced.GpuEncoder) ? "云同步" : $"云 · {synced.GpuEncoder}";
        }

        var pinTag = synced.SecurityPinRequired ? " · 需密码" : "";
        var subtitle = $"{preferredHost ?? "待分配 IP"} · {statusText}{pinTag}";

        return new DeviceListItem(
            LocalProfile: null,
            Name: displayName,
            HostAddress: preferredHost,
            Stream: defaultStream,
            StreamAppName: DeviceProfile.DefaultStreamAppName,
            TargetSubtitle: subtitle,
            ModeBadgeText: badge,
            IsCloudSynced: true,
            IsOnline: synced.IsOnline,
            SyncedDevice: synced,
            IsBeingControlled: isBeingControlled,
            IsControllingOther: isControllingOther,
            IsControlledByLocal: controlledByLocal,
            ControlledByName: controlledByLocal ? "本机" : synced.ControlledByDeviceName,
            ControllingTargetName: synced.ControllingTargetDeviceName,
            IsLocalDevice: false);
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
    private LocalHostPeerHost? localHostPeerHost;
    private AppSettings settings = new();
    private DeviceProfile? editingDevice;
    private string? previousSelectedUniqueKeyBeforeAdd;
    private AppPage currentPage = AppPage.Remote;
    private bool isSyncingThemeToggle;
    private bool isSyncingStreamUi;
    private bool isDraggingBitrate;
    private bool isSyncingHostToggle;
    private bool isRefreshingDevices;
    private bool isRenamingDevice;

    private DeviceListItem? SelectedListItem => DeviceList.SelectedItem as DeviceListItem;

    public MainWindow()
    {
        InitializeComponent();
        InitializeEngineHub();

        if (OperatingSystem.IsWindows())
        {
            sessionBridgeServer = new SessionBridgeServer(Dispatcher);
            sessionBridgeServer.Start();
            localHostPeerHost = new LocalHostPeerHost();
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
        RefreshLocalIdentityBadges();
        RefreshAllEngines();
        RefreshHostAndNetworkInfo();

        Loaded += async (_, _) =>
        {
            MotionBehaviors.AnimateVerticalIndicator(NavIndicatorTransform, (int)currentPage * 42.0, animate: false);
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
            foreach (var win in activeStreamWindows.Values.ToArray())
            {
                try { win.Close(); } catch { }
            }
            activeStreamWindows.Clear();
            activeStreamDeviceKeys.Clear();
            localHostPeerHost?.SetControllingState(false);

            localHostPeerHost?.Dispose();
            localHostPeerHost = null;
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
            await ResumeDaemonAfterUpdateIfNeededAsync();
        }
        catch (Exception)
        {
        }

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

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        WindowBackdropHelper.ApplyMainWindowBackdrop(this, settings.UseDarkTheme, SidebarBorder);
    }

    private void NavigateTo(AppPage page)
    {
        var changed = page != currentPage;
        currentPage = page;
        RemoteControlPage.Visibility = page == AppPage.Remote ? Visibility.Visible : Visibility.Collapsed;
        LocalHostPage.Visibility = page == AppPage.Host ? Visibility.Visible : Visibility.Collapsed;
        NetworkNodesPage.Visibility = page == AppPage.Network ? Visibility.Visible : Visibility.Collapsed;
        EnginesSettingsPage.Visibility = page == AppPage.Engines ? Visibility.Visible : Visibility.Collapsed;

        RemoteNav.Tag = page == AppPage.Remote ? "Selected" : null;
        HostNav.Tag = page == AppPage.Host ? "Selected" : null;
        NetworkNav.Tag = page == AppPage.Network ? "Selected" : null;
        EnginesNav.Tag = page == AppPage.Engines ? "Selected" : null;

        MotionBehaviors.AnimateVerticalIndicator(NavIndicatorTransform, (int)page * 42.0, animate: IsLoaded && changed);
        if (IsLoaded && changed)
        {
            FrameworkElement targetPage = page switch
            {
                AppPage.Remote => RemoteControlPage,
                AppPage.Host => LocalHostPage,
                AppPage.Network => NetworkNodesPage,
                AppPage.Engines => EnginesSettingsPage,
                _ => RemoteControlPage,
            };
            MotionBehaviors.AnimatePageEnter(targetPage);
        }

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
        ShowEditorMessage($"已从检测到的新节点预填名称与虚拟 IP（{virtualIpv4}），确认后点击「新增并保存」即可加入设备列表。");
    }

    private void EditDevice(object sender, RoutedEventArgs e)
    {
        if (SelectedListItem is { IsCloudSynced: true } cloudItem)
        {
            ShowStatus($"「{cloudItem.Name}」由账号云同步自动管理，如需修改显示名称请点击「重命名」。");
            return;
        }
        if (SelectedListItem is { IsLocalDevice: true, LocalProfile: null })
        {
            BeginRenameDeviceClicked(sender, e);
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
        InlineRenamePanel.Visibility = Visibility.Collapsed;
        RenameDeviceFeedback.Visibility = Visibility.Collapsed;

        if (device is null)
        {
            previousSelectedUniqueKeyBeforeAdd = SelectedListItem?.UniqueKey;
            isRefreshingDevices = true;
            try
            {
                DeviceList.SelectedIndex = -1;
            }
            finally
            {
                isRefreshingDevices = false;
            }
            RemoveButton.Visibility = Visibility.Collapsed;
            EditorTitle.Text = !string.IsNullOrWhiteSpace(prefillHostAddress) ? "新增检测到的设备" : "添加电脑";
            SaveDeviceButton.Content = !string.IsNullOrWhiteSpace(prefillHostAddress) ? "新增并保存" : "保存电脑";
        }
        else
        {
            previousSelectedUniqueKeyBeforeAdd = null;
            RemoveButton.Visibility = Visibility.Visible;
            EditorTitle.Text = "编辑电脑";
            SaveDeviceButton.Content = "保存修改";
        }

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
        if (SelectedListItem is null && DeviceList.Items.Count > 0)
        {
            var items = DeviceList.ItemsSource as IList<DeviceListItem>;
            var restoreIndex = 0;
            if (!string.IsNullOrEmpty(previousSelectedUniqueKeyBeforeAdd) && items is not null)
            {
                var found = -1;
                for (var i = 0; i < items.Count; i++)
                {
                    if (string.Equals(items[i].UniqueKey, previousSelectedUniqueKeyBeforeAdd, StringComparison.Ordinal))
                    {
                        found = i;
                        break;
                    }
                }
                if (found >= 0)
                    restoreIndex = found;
            }

            isRefreshingDevices = true;
            try
            {
                DeviceList.SelectedIndex = Math.Clamp(restoreIndex, 0, DeviceList.Items.Count - 1);
            }
            finally
            {
                isRefreshingDevices = false;
            }
        }
        previousSelectedUniqueKeyBeforeAdd = null;
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

            var localKey = EnsureLocalDeviceKeyPersisted();
            var netInfo = HostServiceManager.GetLocalNetworkInfo();
            var localVirtualIp = currentEasyTierVirtualIpv4 ?? settings.Account?.AssignedVirtualIpv4;
            if (editingDevice is null &&
                DeviceIdentityHelper.IsLocalHostAddress(
                    device.HostAddress,
                    localVirtualIp,
                    netInfo.IPv4Addresses,
                    netInfo.IPv6Addresses,
                    netInfo.HostName))
            {
                ShowEditorMessage("该地址属于当前本机，无需添加为远程控制目标。", warning: true);
                HostAddressInput.Focus();
                return;
            }

            if (editingDevice is null &&
                string.Equals(device.StreamAppName, DeviceProfile.DefaultStreamAppName, StringComparison.OrdinalIgnoreCase))
            {
                var existingCloud = syncedDevices.FirstOrDefault(s =>
                    !string.Equals(s.DeviceKey, localKey, StringComparison.OrdinalIgnoreCase) &&
                    ((!string.IsNullOrWhiteSpace(s.VirtualIpv4) && string.Equals(s.VirtualIpv4, device.HostAddress, StringComparison.OrdinalIgnoreCase)) ||
                     (!string.IsNullOrWhiteSpace(s.LanIpv4) && string.Equals(s.LanIpv4, device.HostAddress, StringComparison.OrdinalIgnoreCase))));
                if (existingCloud is not null)
                {
                    var existingName = DeviceIdentityHelper.ResolveSyncedDeviceDisplayName(
                        existingCloud, settings.DeviceAliases, localKey, settings.LocalDeviceName);
                    ShowEditorMessage($"该地址（{device.HostAddress}）已在账号云同步设备「{existingName}」中，请直接在设备列表连接或重命名，无需重复新增。", warning: true);
                    HostAddressInput.Focus();
                    return;
                }
            }

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

            previousSelectedUniqueKeyBeforeAdd = null;
            SaveSettings(settings with { Devices = devices });
            RefreshDevices(0, DeviceListItem.FromProfile(device).UniqueKey);
            ShowStatus(editingDevice is null ? $"已新增设备「{device.Name}」。" : $"已保存设备「{device.Name}」的修改。");
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

        if (item.IsLocalDevice)
        {
            ShowStatus("当前设备为本机，无需远控自己。", warning: true);
            return;
        }

        var lockStatus = item.IsBeingControlled
            ? DeviceRemoteControlStatus.BeingControlled
            : (item.IsControllingOther ? DeviceRemoteControlStatus.Controlling : DeviceRemoteControlStatus.Idle);
        if (!RemoteControlLockPolicy.CanInitiateConnection(lockStatus, item.IsControlledByLocal, out var blockReason))
        {
            ShowStatus(blockReason!, warning: true);
            return;
        }

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

        if (item.IsLocalDevice)
        {
            ShowStatus("当前设备为本机，无需配对本机。", warning: true);
            return;
        }

        var lockStatus = item.IsBeingControlled
            ? DeviceRemoteControlStatus.BeingControlled
            : (item.IsControllingOther ? DeviceRemoteControlStatus.Controlling : DeviceRemoteControlStatus.Idle);
        if (!RemoteControlLockPolicy.CanInitiateConnection(lockStatus, item.IsControlledByLocal, out var blockReason))
        {
            ShowStatus(blockReason!, warning: true);
            return;
        }

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

            if (IsAccountLoggedIn() &&
                !string.IsNullOrEmpty(settings.Account?.MeshConfig?.AgentAuthKey) &&
                SecretProtector.TryUnprotect(settings.Account.MeshConfig.AgentAuthKey, out var authKey) &&
                !string.IsNullOrEmpty(authKey))
            {
                ShowStatus($"正在尝试对 {item.HostAddress} 执行同账号静默自动配对…");
                var localHostName = HostServiceManager.GetLocalNetworkInfo().HostName;
                var autoRes = await autoPairOrchestrator.NativeAutoPairAsync(
                    gsClient,
                    item.HostAddress,
                    AgentIpcConstants.DefaultAgentPort,
                    authKey,
                    localHostName,
                    securityPin: null);
                if (autoRes.IsSuccess)
                {
                    ShowStatus($"已自动完成对「{item.Name}」的零 PIN 安全配对，可直接点击「发起高刷连接」。");
                    return;
                }
                if (autoRes.Status is AutoPairStatus.NeedSecurityPin or AutoPairStatus.InvalidSecurityPin)
                {
                    ShowStatus(autoRes.Message, warning: true);
                    return;
                }
            }

            var pin = RandomNumberGenerator.GetInt32(0, 10000).ToString("D4", CultureInfo.InvariantCulture);
            ShowStatus($"正在向 {item.HostAddress} 发起原生配对，请在目标电脑 RemoteDesk「本机被控」页输入 PIN 码：{pin}（无需打开 Sunshine 控制面板）");
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

    private void RefreshDevices(int index, string? selectUniqueKey = null, bool preserveTransientUi = false)
    {
        var items = new List<DeviceListItem>();
        var localKey = EnsureLocalDeviceKeyPersisted();
        var localName = GetEffectiveLocalDeviceName();
        var netInfo = HostServiceManager.GetLocalNetworkInfo();
        var localVirtualIp = currentEasyTierVirtualIpv4 ?? settings.Account?.AssignedVirtualIpv4;
        var defaultStream = settings.DefaultStream ?? StreamOptions.FromPreset(StreamPresetKind.High2K);
        var cloudHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var hasSelfItem = false;

        foreach (var synced in syncedDevices)
        {
            var isSelf = string.Equals(synced.DeviceKey, localKey, StringComparison.OrdinalIgnoreCase);
            var preferredHost = !string.IsNullOrWhiteSpace(synced.VirtualIpv4)
                ? synced.VirtualIpv4
                : synced.LanIpv4;
            var isLocalControlling = !isSelf &&
                (IsHostControlledByLocal(preferredHost, synced.DeviceKey) ||
                 (!string.IsNullOrWhiteSpace(synced.LanIpv4) && IsHostControlledByLocal(synced.LanIpv4, synced.DeviceKey)));

            var displayName = DeviceIdentityHelper.ResolveSyncedDeviceDisplayName(
                synced,
                settings.DeviceAliases,
                localKey,
                settings.LocalDeviceName);

            var cloudItem = DeviceListItem.FromSyncedDevice(
                synced,
                defaultStream,
                localKey,
                isLocalControlling,
                displayNameOverride: displayName,
                isLocalDevice: isSelf);

            if (isSelf)
            {
                items.Insert(0, cloudItem);
                hasSelfItem = true;
            }
            else
            {
                items.Add(cloudItem);
            }

            if (!string.IsNullOrWhiteSpace(synced.VirtualIpv4))
                cloudHosts.Add(synced.VirtualIpv4);
        }

        foreach (var local in settings.Devices)
        {
            if (!string.IsNullOrWhiteSpace(local.HostAddress) &&
                cloudHosts.Contains(local.HostAddress) &&
                string.Equals(local.StreamAppName, DeviceProfile.DefaultStreamAppName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var isSelfHost = DeviceIdentityHelper.IsLocalHostAddress(
                local.HostAddress,
                localVirtualIp,
                netInfo.IPv4Addresses,
                netInfo.IPv6Addresses,
                netInfo.HostName);
            hasSelfItem |= isSelfHost;
            items.Add(DeviceListItem.FromProfile(local, IsHostControlledByLocal(local.HostAddress, deviceKey: null), isLocalDevice: isSelfHost));
        }

        if (!hasSelfItem && items.Count > 0)
        {
            items.Insert(0, DeviceListItem.FromLocalSelf(
                localName,
                localVirtualIp ?? netInfo.IPv4Addresses.FirstOrDefault(),
                defaultStream));
        }

        var resolvedIndex = -1;
        if (!string.IsNullOrEmpty(selectUniqueKey))
            resolvedIndex = items.FindIndex(i => string.Equals(i.UniqueKey, selectUniqueKey, StringComparison.Ordinal));
        if (resolvedIndex < 0 && items.Count > 0)
        {
            if (index == 0 && string.IsNullOrEmpty(selectUniqueKey))
            {
                var firstRemote = items.FindIndex(i => !i.IsLocalDevice);
                resolvedIndex = firstRemote >= 0 ? firstRemote : 0;
            }
            else
            {
                resolvedIndex = Math.Clamp(index, 0, items.Count - 1);
            }
        }

        var keepEditorOrRemoval = preserveTransientUi &&
            (EditorView.Visibility == Visibility.Visible || RemovalView.Visibility == Visibility.Visible);
        var keepInlineRename = preserveTransientUi && InlineRenamePanel.Visibility == Visibility.Visible;
        var keepSecurityPinCard = preserveTransientUi &&
            SecurityPinPromptCard.Visibility == Visibility.Visible &&
            pendingSecurityPinItem is { } savedPinItem &&
            resolvedIndex >= 0 &&
            string.Equals(items[resolvedIndex].UniqueKey, savedPinItem.UniqueKey, StringComparison.Ordinal);

        isRefreshingDevices = true;
        try
        {
            DeviceList.ItemsSource = items;
            DeviceList.SelectedIndex = resolvedIndex;
            DeviceCount.Text = $"{items.Count} 台";
            DeviceListEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            if (DeviceList.SelectedItem is not null)
                DeviceList.ScrollIntoView(DeviceList.SelectedItem);
        }
        finally
        {
            isRefreshingDevices = false;
        }

        RefreshLocalIdentityBadges();
        RefreshEasyTierPeerViews();

        if (keepEditorOrRemoval)
            return;

        var preservedPinItem = keepSecurityPinCard ? pendingSecurityPinItem : null;
        ShowDevice(keepInlineRename);
        if (preservedPinItem is not null && SecurityPinPromptCard is not null)
        {
            pendingSecurityPinItem = preservedPinItem;
            SecurityPinPromptCard.Visibility = Visibility.Visible;
        }
    }

    private void DeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isRefreshingDevices)
            return;

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

    private void ShowDevice(bool preserveInlineRename = false)
    {
        var item = SelectedListItem;
        editingDevice = null;
        pendingSecurityPinItem = null;
        if (SecurityPinPromptCard is not null)
            SecurityPinPromptCard.Visibility = Visibility.Collapsed;
        if (!preserveInlineRename)
        {
            InlineRenamePanel.Visibility = Visibility.Collapsed;
            RenameDeviceFeedback.Visibility = Visibility.Collapsed;
        }
        SetDeviceListEnabled(true);

        EmptyView.Visibility = item is null ? Visibility.Visible : Visibility.Collapsed;
        DeviceView.Visibility = item is null ? Visibility.Collapsed : Visibility.Visible;
        EditorView.Visibility = Visibility.Collapsed;
        RemovalView.Visibility = Visibility.Collapsed;

        if (item is null)
            return;

        DeviceNameText.Text = item.Name;
        if (item.IsLocalDevice)
        {
            DeviceActiveModeText.Text = "本机（当前电脑）";
            DeviceActiveModeBadge.SetResourceReference(Border.BackgroundProperty, "AccentSoft");
            DeviceActiveModeText.SetResourceReference(TextBlock.ForegroundProperty, "Accent");
            DeviceHostText.Text = item.HostAddress is not null ? $"本机地址 {item.HostAddress}" : "当前电脑（未组网）";
            DeviceSecondaryText.Text = settings.HostModeEnabled ? "允许被其他设备控制" : "未开启被控";
        }
        else if (item.IsBeingControlled)
        {
            DeviceActiveModeText.Text = item.IsControlledByLocal ? "被远控（本机连接中）" : "被远控";
            DeviceActiveModeBadge.SetResourceReference(Border.BackgroundProperty, "WarningSoft");
            DeviceActiveModeText.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
            if (item is { IsCloudSynced: true, SyncedDevice: { } syncedBusy })
            {
                DeviceHostText.Text = syncedBusy.VirtualIpv4 is not null ? $"虚拟内网 {syncedBusy.VirtualIpv4}" : "待分配虚拟 IP";
                DeviceSecondaryText.Text = item.IsControlledByLocal
                    ? "当前正由本机远控"
                    : (!string.IsNullOrWhiteSpace(item.ControlledByName) ? $"控制端：{item.ControlledByName}" : "正被其他设备远控");
            }
            else
            {
                DeviceHostText.Text = item.HostAddress is not null ? $"直连 {item.HostAddress}" : "未配置直连地址";
                DeviceSecondaryText.Text = "当前正由本机远控";
            }
        }
        else if (item.IsControllingOther)
        {
            DeviceActiveModeText.Text = "远控中 · 不可被控";
            DeviceActiveModeBadge.SetResourceReference(Border.BackgroundProperty, "WarningSoft");
            DeviceActiveModeText.SetResourceReference(TextBlock.ForegroundProperty, "Warning");
            DeviceHostText.Text = item.SyncedDevice?.VirtualIpv4 is not null ? $"虚拟内网 {item.SyncedDevice.VirtualIpv4}" : "待分配虚拟 IP";
            DeviceSecondaryText.Text = !string.IsNullOrWhiteSpace(item.ControllingTargetName)
                ? $"正在远控 {item.ControllingTargetName}"
                : "正在远控其他设备";
        }
        else if (item is { IsCloudSynced: true, SyncedDevice: { } synced })
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

        RenameDeviceButton.Visibility = Visibility.Visible;
        EditDeviceButton.Visibility = item.IsCloudSynced || (item.IsLocalDevice && item.LocalProfile is null)
            ? Visibility.Collapsed
            : Visibility.Visible;

        SyncStreamControls(item.Stream, syncSliderValue: true);

        if (item.IsLocalDevice)
        {
            ConnectButtonText.Text = "当前为本机";
            ConnectButton.IsEnabled = false;
            PairButton.IsEnabled = false;
        }
        else if (item.IsBeingControlled && !item.IsControlledByLocal)
        {
            ConnectButtonText.Text = "设备正被远控";
            ConnectButton.IsEnabled = false;
            PairButton.IsEnabled = false;
        }
        else if (item.IsControllingOther)
        {
            ConnectButtonText.Text = "设备远控中";
            ConnectButton.IsEnabled = false;
            PairButton.IsEnabled = false;
        }
        else if (item.IsControlledByLocal)
        {
            ConnectButtonText.Text = "切换至远控窗";
            ConnectButton.IsEnabled = true;
            PairButton.IsEnabled = true;
        }
        else
        {
            ConnectButtonText.Text = item.IsCloudSynced ? "一键高刷连接" : "发起高刷连接";
            ConnectButton.IsEnabled = true;
            PairButton.IsEnabled = true;
        }

        ConnectButtonIcon.Text = "\uE945";
        PairButton.Visibility = item.IsCloudSynced || item.IsLocalDevice ? Visibility.Collapsed : Visibility.Visible;
        ActionTargetSummary.Text = item.IsLocalDevice
            ? "当前为本机设备"
            : (item.HostAddress is not null
                ? $"目标 {item.HostAddress} · {item.StreamAppName}"
                : "等待分配虚拟内网地址");

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

        if (item.IsLocalDevice)
        {
            ActionEngineDot.SetResourceReference(Shape.FillProperty, "Accent");
            ActionEngineHint.Text = "当前为本机设备，不可远控自己；点击右上角「重命名」可修改本机显示名称。";
            ActionEngineLink.Visibility = Visibility.Collapsed;
        }
        else if (item.IsBeingControlled && item.IsControlledByLocal)
        {
            ActionEngineDot.SetResourceReference(Shape.FillProperty, "Warning");
            ActionEngineHint.Text = "当前本机正在远控该设备，期间本机与目标设备均不可被其他设备远控。";
            ActionEngineLink.Visibility = Visibility.Collapsed;
        }
        else if (item.IsBeingControlled)
        {
            ActionEngineDot.SetResourceReference(Shape.FillProperty, "Warning");
            ActionEngineHint.Text = "该设备当前正被其他设备远控，暂不可发起连接。";
            ActionEngineLink.Visibility = Visibility.Collapsed;
        }
        else if (item.IsControllingOther)
        {
            ActionEngineDot.SetResourceReference(Shape.FillProperty, "Warning");
            ActionEngineHint.Text = "该设备当前正在远控其他设备，期间不可被其他设备远控。";
            ActionEngineLink.Visibility = Visibility.Collapsed;
        }
        else if (!NativeStreamBridge.IsAvailable())
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
                ? "同账号设备免配对一键直连 · 自动完成加密授权与协同通道"
                : "内置原生高刷控屏引擎已就绪（D3D11 硬件解码）";
            ActionEngineLink.Visibility = Visibility.Collapsed;
        }
    }

    private string GetEffectiveLocalDeviceName()
    {
        var osHost = HostServiceManager.GetLocalNetworkInfo().HostName;
        return DeviceIdentityHelper.GetEffectiveLocalDeviceName(settings.LocalDeviceName, osHost);
    }

    private void RefreshLocalIdentityBadges()
    {
        var netInfo = HostServiceManager.GetLocalNetworkInfo();
        var effectiveName = DeviceIdentityHelper.GetEffectiveLocalDeviceName(settings.LocalDeviceName, netInfo.HostName);
        var ipText = currentEasyTierVirtualIpv4
            ?? settings.Account?.AssignedVirtualIpv4
            ?? netInfo.IPv4Addresses.FirstOrDefault()
            ?? "当前电脑";

        if (SidebarLocalDeviceNameText is not null)
            SidebarLocalDeviceNameText.Text = effectiveName;
        if (SidebarLocalDeviceIpText is not null)
            SidebarLocalDeviceIpText.Text = ipText;
        if (SidebarLocalIdentityCard is not null)
            SidebarLocalIdentityCard.ToolTip = $"本机：{effectiveName}（主机名 {netInfo.HostName} · {ipText}），点击前往「本机被控」";
        if (LocalDisplayNameText is not null)
            LocalDisplayNameText.Text = effectiveName;
        if (LocalHostNameText is not null)
            LocalHostNameText.Text = $"系统主机名：{netInfo.HostName}";
    }

    private void SidebarLocalIdentityClicked(object sender, RoutedEventArgs e)
    {
        var items = DeviceList.ItemsSource as IList<DeviceListItem>;
        var selfItem = items?.FirstOrDefault(i => i.IsLocalDevice);
        if (selfItem is not null)
        {
            NavigateTo(AppPage.Remote);
            DeviceList.SelectedItem = selfItem;
            return;
        }
        NavigateTo(AppPage.Host);
        RefreshHostAndNetworkInfo();
    }

    private void RenameDeviceFromContextMenuClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: DeviceListItem clickedItem })
        {
            if (!ReferenceEquals(SelectedListItem, clickedItem))
                DeviceList.SelectedItem = clickedItem;
        }
        BeginRenameDeviceClicked(sender, e);
    }

    private void BeginRenameDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (SelectedListItem is not { } item)
            return;

        RenameDeviceFeedback.Visibility = Visibility.Collapsed;
        RenameDeviceInput.Text = item.Name;
        InlineRenamePanel.Visibility = Visibility.Visible;
        RenameDeviceInput.Focus();
        RenameDeviceInput.SelectAll();
    }

    private void CancelRenameDeviceClicked(object sender, RoutedEventArgs e)
    {
        InlineRenamePanel.Visibility = Visibility.Collapsed;
        RenameDeviceFeedback.Visibility = Visibility.Collapsed;
    }

    private void RenameDeviceInputKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter)
        {
            e.Handled = true;
            ConfirmRenameDeviceClicked(sender, e);
        }
        else if (e.Key == System.Windows.Input.Key.Escape)
        {
            e.Handled = true;
            CancelRenameDeviceClicked(sender, e);
        }
    }

    private async void ConfirmRenameDeviceClicked(object sender, RoutedEventArgs e)
    {
        if (isRenamingDevice || SelectedListItem is not { } item)
            return;

        string normalizedName;
        try
        {
            normalizedName = DeviceIdentityHelper.NormalizeDeviceName(RenameDeviceInput.Text);
        }
        catch (ArgumentException ex)
        {
            RenameDeviceFeedback.Text = ex.Message;
            RenameDeviceFeedback.Visibility = Visibility.Visible;
            RenameDeviceInput.Focus();
            return;
        }

        isRenamingDevice = true;
        ConfirmRenameDeviceButton.IsEnabled = false;
        try
        {
            var (ok, message, nextKey) = await ApplyDeviceRenameAsync(item, normalizedName);
            if (!ok)
            {
                RenameDeviceFeedback.Text = message;
                RenameDeviceFeedback.Visibility = Visibility.Visible;
                return;
            }

            InlineRenamePanel.Visibility = Visibility.Collapsed;
            RenameDeviceFeedback.Visibility = Visibility.Collapsed;
            RefreshDevices(DeviceList.SelectedIndex, nextKey, preserveTransientUi: false);
            ShowStatus(message);
        }
        finally
        {
            isRenamingDevice = false;
            ConfirmRenameDeviceButton.IsEnabled = true;
        }
    }

    private async Task<(bool Ok, string Message, string SelectUniqueKey)> ApplyDeviceRenameAsync(
        DeviceListItem item,
        string newName)
    {
        if (item.IsLocalDevice && item.LocalProfile is null)
        {
            return await ApplyLocalDeviceNameAsync(newName, item.UniqueKey);
        }

        if (item.IsCloudSynced && item.SyncedDevice is { } synced)
        {
            var aliases = settings.DeviceAliases is not null
                ? new Dictionary<string, string>(settings.DeviceAliases, StringComparer.OrdinalIgnoreCase)
                : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            aliases[synced.DeviceKey] = newName;
            SaveSettings(settings with { DeviceAliases = aliases });

            var serverSynced = false;
            string? serverError = null;
            if (IsAccountLoggedIn() &&
                settings.Account is { } acc &&
                SecretProtector.TryUnprotect(acc.ProtectedSessionToken, out var token) &&
                !string.IsNullOrEmpty(token))
            {
                try
                {
                    var updated = await syncApiClient.RenameDeviceAsync(
                        acc.ServerUrl,
                        token,
                        synced.Id,
                        newName,
                        acc.ServerCertThumbprint);
                    syncedDevices = syncedDevices
                        .Select(d => string.Equals(d.Id, updated.Id, StringComparison.OrdinalIgnoreCase) ? updated : d)
                        .ToArray();
                    serverSynced = true;
                }
                catch (Exception ex) when (ex is SyncApiException or SyncCertificateMismatchException or HttpRequestException or TaskCanceledException or IOException)
                {
                    serverError = ex.Message;
                }
            }

            return serverSynced || serverError is null
                ? (true, $"已将设备重命名为「{newName}」。", item.UniqueKey)
                : (true, $"已在本地重命名为「{newName}」，但同步服务器更新失败（{serverError}）。", item.UniqueKey);
        }

        var localIdx = FindLocalDeviceIndex(item);
        if (localIdx < 0 || localIdx >= settings.Devices.Count)
            return (false, "未找到该本地设备记录。", item.UniqueKey);

        var devices = settings.Devices.ToList();
        var old = devices[localIdx];
        var updatedProfile = DeviceProfile.Create(newName, old.HostAddress, old.Stream, old.StreamAppName);
        devices[localIdx] = updatedProfile;
        SaveSettings(settings with { Devices = devices });

        return (true, $"已将设备重命名为「{newName}」。", DeviceListItem.FromProfile(updatedProfile, item.IsControlledByLocal, item.IsLocalDevice).UniqueKey);
    }

    private async Task<(bool Ok, string Message, string SelectUniqueKey)> ApplyLocalDeviceNameAsync(
        string newName,
        string? preferredUniqueKey = null)
    {
        var localKey = EnsureLocalDeviceKeyPersisted();
        var aliases = settings.DeviceAliases is not null
            ? new Dictionary<string, string>(settings.DeviceAliases, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        aliases[localKey] = newName;

        SaveSettings(settings with
        {
            LocalDeviceName = newName,
            DeviceAliases = aliases,
        });

        var localSynced = syncedDevices.FirstOrDefault(d =>
            string.Equals(d.DeviceKey, localKey, StringComparison.OrdinalIgnoreCase));
        if (localSynced is not null &&
            IsAccountLoggedIn() &&
            settings.Account is { } acc &&
            SecretProtector.TryUnprotect(acc.ProtectedSessionToken, out var token) &&
            !string.IsNullOrEmpty(token))
        {
            try
            {
                var updated = await syncApiClient.RenameDeviceAsync(
                    acc.ServerUrl,
                    token,
                    localSynced.Id,
                    newName,
                    acc.ServerCertThumbprint);
                syncedDevices = syncedDevices
                    .Select(d => string.Equals(d.Id, updated.Id, StringComparison.OrdinalIgnoreCase) ? updated : d)
                    .ToArray();
            }
            catch
            {
                // Keep local rename even if cloud server is temporarily unreachable.
            }
        }

        await PushConfigToDaemonIfRunningAsync();
        ReconcileLocalPeerHost();
        RefreshLocalIdentityBadges();

        var resolvedKey = preferredUniqueKey ?? (localSynced is not null ? $"cloud:{localSynced.Id}" : "local-self");
        return (true, $"已将本机显示名称更新为「{newName}」。", resolvedKey);
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

        WindowBackdropHelper.ApplyMainWindowBackdrop(this, dark, SidebarBorder);
    }
}
