using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Shapes;
using Path = System.IO.Path;

namespace RemoteDesk;

internal sealed record DeviceListItem(
    DeviceProfile Profile,
    string Name,
    string Id,
    string TargetSubtitle,
    string ModeBadgeText,
    bool IsStreamMode)
{
    public static DeviceListItem FromProfile(DeviceProfile profile)
    {
        var isStream = profile.PreferredMode == ConnectionMode.Stream;
        var subtitle = isStream
            ? (profile.HostAddress ?? (profile.Id is not null ? $"ID {profile.Id}" : "未配置地址"))
            : (profile.Id is not null ? $"ID {profile.Id}" : (profile.HostAddress ?? "未配置地址"));

        return new DeviceListItem(
            Profile: profile,
            Name: profile.Name,
            Id: profile.Id ?? profile.HostAddress ?? "",
            TargetSubtitle: subtitle,
            ModeBadgeText: isStream ? "高刷" : "办公",
            IsStreamMode: isStream);
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

    private AppSettings settings = new();
    private DeviceProfile? editingDevice;
    private AppPage currentPage = AppPage.Remote;
    private bool isSyncingThemeToggle;
    private bool isSyncingStreamUi;
    private bool isDraggingBitrate;
    private bool isSyncingHostToggle;

    private DeviceProfile? SelectedDevice => (DeviceList.SelectedItem as DeviceListItem)?.Profile;

    public MainWindow()
    {
        InitializeComponent();
        InitializeEngineHub();

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
                _ = RefreshEasyTierStatusAsync();
        };
        Closed += (_, _) =>
        {
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
            prefillHostAddress: virtualIpv4,
            prefillMode: ConnectionMode.Stream);
        ShowEditorMessage($"已从 EasyTier 节点预填主机名与虚拟 IP（{virtualIpv4}），确认后点击「保存电脑」即可发起高刷串流。");
    }

    private void EditDevice(object sender, RoutedEventArgs e) => OpenEditor(SelectedDevice);

    private void OpenEditor(
        DeviceProfile? device,
        string? prefillName = null,
        string? prefillHostAddress = null,
        ConnectionMode? prefillMode = null)
    {
        editingDevice = device;
        RemoveButton.Visibility = device is null ? Visibility.Collapsed : Visibility.Visible;
        EditorTitle.Text = device is null ? "添加电脑" : "编辑电脑";
        NameInput.Text = device?.Name ?? prefillName ?? "";
        HostAddressInput.Text = device?.HostAddress ?? prefillHostAddress ?? "";
        IdInput.Text = device?.Id ?? "";
        StreamAppNameInput.Text = device?.StreamAppName ?? DeviceProfile.DefaultStreamAppName;
        SelectComboByTag(EditorModeCombo, (device?.PreferredMode ?? prefillMode ?? ConnectionMode.Stream).ToString());
        ShowEditorMessage("校园网/局域网地址或 EasyTier 虚拟 IP 用于 Moonlight 高刷串流，RustDesk ID 用于办公模式。");
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
            var index = DeviceList.SelectedIndex;
            if (editingDevice is null || index < 0 || !ReferenceEquals(editingDevice, SelectedDevice))
                return;

            var devices = settings.Devices.ToList();
            devices.RemoveAt(index);
            SaveSettings(settings with { Devices = devices });
            RefreshDevices(Math.Min(index, devices.Count - 1));
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
            var selectedModeTag = (EditorModeCombo.SelectedItem as ComboBoxItem)?.Tag as string;
            var preferredMode = string.Equals(selectedModeTag, nameof(ConnectionMode.Office), StringComparison.Ordinal)
                ? ConnectionMode.Office
                : ConnectionMode.Stream;

            var streamOptions = editingDevice?.Stream ?? settings.DefaultStream ?? StreamOptions.FromPreset(StreamPresetKind.High2K);
            var appName = string.IsNullOrWhiteSpace(StreamAppNameInput.Text) ? null : StreamAppNameInput.Text;

            var device = DeviceProfile.Create(
                NameInput.Text,
                IdInput.Text,
                HostAddressInput.Text,
                preferredMode,
                streamOptions,
                appName);

            var devices = settings.Devices.ToList();
            if (device.Id is not null && devices.Any(existing =>
                !ReferenceEquals(existing, editingDevice) &&
                string.Equals(existing.Id, device.Id, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(existing.StreamAppName, device.StreamAppName, StringComparison.OrdinalIgnoreCase)))
            {
                ShowEditorMessage("相同的 RustDesk 设备 ID 已存在于列表中。", warning: true);
                IdInput.Focus();
                return;
            }

            var index = devices.Count;
            if (editingDevice is null)
            {
                devices.Add(device);
            }
            else
            {
                index = DeviceList.SelectedIndex;
                devices[index] = device;
            }

            SaveSettings(settings with { Devices = devices });
            RefreshDevices(index);
            ShowStatus($"已保存设备「{device.Name}」。");
            FocusDeviceAction();
        }
        catch (ArgumentException error)
        {
            ShowEditorMessage(error.Message, warning: true);
            if (error.ParamName == "name")
                NameInput.Focus();
            else if (error.ParamName == "hostAddress")
                HostAddressInput.Focus();
            else if (error.ParamName == "streamAppName")
                StreamAppNameInput.Focus();
            else
                IdInput.Focus();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowEditorMessage("无法保存设置，请检查本机用户目录的写入权限。", warning: true);
        }
    }

    private void SelectStreamMode(object sender, RoutedEventArgs e) => SwitchSelectedDeviceMode(ConnectionMode.Stream);

    private void SelectOfficeMode(object sender, RoutedEventArgs e) => SwitchSelectedDeviceMode(ConnectionMode.Office);

    private void SwitchSelectedDeviceMode(ConnectionMode mode)
    {
        if (SelectedDevice is not { } device || device.PreferredMode == mode)
            return;

        var updated = DeviceProfile.Create(
            device.Name,
            device.Id,
            device.HostAddress,
            mode,
            device.Stream,
            device.StreamAppName);

        PersistUpdatedSelectedDevice(updated);
    }

    private void SelectPresetUltra120(object sender, RoutedEventArgs e) => ApplyStreamPreset(StreamPresetKind.Ultra120);

    private void SelectPresetHigh2K(object sender, RoutedEventArgs e) => ApplyStreamPreset(StreamPresetKind.High2K);

    private void SelectPresetSaver(object sender, RoutedEventArgs e) => ApplyStreamPreset(StreamPresetKind.Saver);

    private void SelectPresetCustom(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device)
            return;

        var custom = device.Stream with { Preset = StreamPresetKind.Custom };
        UpdateSelectedDeviceStream(custom, persist: true);
    }

    private void ApplyStreamPreset(StreamPresetKind preset)
    {
        if (SelectedDevice is null)
            return;

        var options = StreamOptions.FromPreset(preset);
        UpdateSelectedDeviceStream(options, persist: true);
    }

    private void StreamParamChanged(object sender, SelectionChangedEventArgs e)
    {
        if (isSyncingStreamUi || SelectedDevice is null)
            return;

        CommitCustomStreamFromControls(persist: true);
    }

    private void BitrateSliderDragStarted(object sender, DragStartedEventArgs e) => isDraggingBitrate = true;

    private void BitrateSliderDragCompleted(object sender, DragCompletedEventArgs e)
    {
        isDraggingBitrate = false;
        if (isSyncingStreamUi || SelectedDevice is null)
            return;

        CommitCustomStreamFromControls(persist: true);
    }

    private void BitrateSliderValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (BitrateValueText is null)
            return;

        var mbps = (int)Math.Round(e.NewValue);
        BitrateValueText.Text = $"{mbps} Mbps";

        if (isSyncingStreamUi || SelectedDevice is null)
            return;

        CommitCustomStreamFromControls(persist: !isDraggingBitrate);
    }

    private void HardwareDecodeChanged(object sender, RoutedEventArgs e)
    {
        if (HardwareDecodeToggle is null)
            return;

        HardwareDecodeToggle.Content = HardwareDecodeToggle.IsChecked == true ? "已开启" : "已关闭";
        if (isSyncingStreamUi || SelectedDevice is null)
            return;

        CommitCustomStreamFromControls(persist: true);
    }

    private void CommitCustomStreamFromControls(bool persist)
    {
        if (SelectedDevice is not { } device)
            return;

        var (width, height) = ParseSelectedResolution(device.Stream.Width, device.Stream.Height);
        var fps = ParseSelectedFps(device.Stream.Fps);
        var codec = ParseSelectedCodec(device.Stream.Codec);
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
        if (SelectedDevice is not { } device)
            return;

        var updated = DeviceProfile.Create(
            device.Name,
            device.Id,
            device.HostAddress,
            device.PreferredMode,
            options,
            device.StreamAppName);

        if (persist)
        {
            PersistUpdatedSelectedDevice(updated);
        }
        else
        {
            // Live UI preview while dragging slider without spamming disk writes.
            SyncStreamControls(options, syncSliderValue: false);
        }
    }

    private void PersistUpdatedSelectedDevice(DeviceProfile updated)
    {
        var index = DeviceList.SelectedIndex;
        if (index < 0 || index >= settings.Devices.Count)
            return;

        try
        {
            var devices = settings.Devices.ToList();
            devices[index] = updated;
            SaveSettings(settings with { Devices = devices, DefaultStream = updated.Stream });
            RefreshDevices(index);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            ShowStatus("无法保存设备参数，请检查本机用户目录写入权限。", warning: true);
        }
    }

    private void ConnectDevice(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device)
            return;

        if (device.PreferredMode == ConnectionMode.Stream)
        {
            if (!device.SupportsStream)
            {
                ShowStatus("该设备未配置校园网/局域网地址，请点击「编辑」补充主机地址后再发起高刷连接。", warning: true);
                return;
            }

            var moonlightExe = ResolveMoonlightExecutable();
            if (moonlightExe is null)
            {
                NavigateTo(AppPage.Engines);
                ShowStatus("尚未安装 Moonlight 高刷控制端，请点击「一键安装」或「导入本地包」。", warning: true);
                return;
            }

            try
            {
                var startInfo = MoonlightClient.CreateStreamConnection(moonlightExe, device);
                using var process = Process.Start(startInfo);
                ShowStatus($"已启动 Moonlight 高刷串流 -> {device.HostAddress}（{device.Stream.Width}x{device.Stream.Height} @ {device.Stream.Fps}fps / {device.Stream.BitrateMbps}Mbps）。");
            }
            catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or ArgumentException)
            {
                ShowStatus($"启动 Moonlight 失败：{error.Message}", warning: true);
            }
            return;
        }

        // Office mode (RustDesk)
        var rustDeskExe = ResolveRustDeskExecutable();
        if (rustDeskExe is null)
        {
            NavigateTo(AppPage.Engines);
            ShowStatus("尚未安装 RustDesk 办公引擎，请在引擎中心一键安装或指定路径。", warning: true);
            return;
        }

        try
        {
            var startInfo = RustDeskClient.CreateConnection(rustDeskExe, device);
            using var process = Process.Start(startInfo);
            var target = device.Id ?? device.HostAddress;
            ShowStatus($"已向 RustDesk 发起办公连接 -> {target}，请在会话窗口查看结果。");
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or ArgumentException)
        {
            ShowStatus($"启动 RustDesk 失败：{error.Message}", warning: true);
        }
    }

    private void PairDevice(object sender, RoutedEventArgs e)
    {
        if (SelectedDevice is not { } device)
            return;

        if (string.IsNullOrEmpty(device.HostAddress))
        {
            ShowStatus("配对需要目标主机的校园网/局域网地址，请先点击「编辑」填写地址。", warning: true);
            return;
        }

        var moonlightExe = ResolveMoonlightExecutable();
        if (moonlightExe is null)
        {
            NavigateTo(AppPage.Engines);
            ShowStatus("尚未安装 Moonlight，无法发起配对。请先在引擎中心安装 Moonlight。", warning: true);
            return;
        }

        try
        {
            var startInfo = MoonlightClient.CreatePairConnection(moonlightExe, device.HostAddress);
            using var process = Process.Start(startInfo);
            ShowStatus($"已向 {device.HostAddress} 发起 Moonlight 配对请求，请在目标电脑的 Sunshine 网页控制台输入 PIN 码。");
        }
        catch (Exception error) when (error is IOException or Win32Exception or InvalidOperationException or ArgumentException)
        {
            ShowStatus($"启动配对失败：{error.Message}", warning: true);
        }
    }

    private void RefreshDevices(int index)
    {
        var items = settings.Devices.Select(DeviceListItem.FromProfile).ToList();
        DeviceList.ItemsSource = items;
        DeviceList.SelectedIndex = items.Count == 0 ? -1 : Math.Clamp(index, 0, items.Count - 1);
        DeviceCount.Text = $"{items.Count} 台";
        DeviceListEmpty.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (DeviceList.SelectedItem is not null)
            DeviceList.ScrollIntoView(DeviceList.SelectedItem);
        ShowDevice();
    }

    private void DeviceSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedDevice is not null && currentPage != AppPage.Remote)
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
        var device = SelectedDevice;
        editingDevice = null;
        SetDeviceListEnabled(true);

        EmptyView.Visibility = device is null ? Visibility.Visible : Visibility.Collapsed;
        DeviceView.Visibility = device is null ? Visibility.Collapsed : Visibility.Visible;
        EditorView.Visibility = Visibility.Collapsed;
        RemovalView.Visibility = Visibility.Collapsed;

        if (device is null)
            return;

        var isStream = device.PreferredMode == ConnectionMode.Stream;

        DeviceNameText.Text = device.Name;
        DeviceActiveModeText.Text = isStream ? "高刷串流" : "办公模式";
        DeviceActiveModeBadge.SetResourceReference(Border.BackgroundProperty, isStream ? "AccentSoft" : "PurpleSoft");
        DeviceActiveModeText.SetResourceReference(TextBlock.ForegroundProperty, isStream ? "Accent" : "PurpleAccent");

        DeviceHostText.Text = device.HostAddress is not null ? $"直连 {device.HostAddress}" : "未配置直连地址";
        DeviceIdText.Text = device.Id is not null ? $"ID {device.Id}" : "未配置 RustDesk ID";
        DeviceAppText.Text = $"应用 {device.StreamAppName}";
        DeviceAppPill.Visibility = isStream ? Visibility.Visible : Visibility.Collapsed;

        ModeStreamButton.Tag = isStream ? "Selected" : null;
        ModeOfficeButton.Tag = isStream ? null : "Selected";
        ModeHintText.Text = isStream
            ? "Moonlight 低延迟硬件串流，适合高帧率操控"
            : "RustDesk 通用桌面协议，适合跨网办公与文件传输";

        OfficeModeNotice.Visibility = isStream ? Visibility.Collapsed : Visibility.Visible;
        StreamConfigBody.IsEnabled = isStream;
        StreamConfigBody.Opacity = isStream ? 1.0 : 0.48;

        SyncStreamControls(device.Stream, syncSliderValue: true);

        ConnectButtonText.Text = isStream ? "发起高刷连接" : "发起办公连接";
        ConnectButtonIcon.Text = isStream ? "\uE945" : "\uE821";
        PairButton.Visibility = isStream ? Visibility.Visible : Visibility.Collapsed;
        ActionTargetSummary.Text = isStream
            ? (device.HostAddress is not null ? $"目标 {device.HostAddress} · {device.StreamAppName}" : "缺少目标主机地址")
            : $"目标 {device.Id ?? device.HostAddress}";

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
        if (SelectedDevice is not { } device)
            return;

        if (device.PreferredMode == ConnectionMode.Stream)
        {
            var mlPath = ResolveMoonlightExecutable();
            if (!device.SupportsStream)
            {
                ActionEngineDot.SetResourceReference(Shape.FillProperty, "Warning");
                ActionEngineHint.Text = "高刷模式需要配置目标电脑的校园网/局域网地址，请点击右上角「编辑」。";
                ActionEngineLink.Visibility = Visibility.Collapsed;
            }
            else if (mlPath is null)
            {
                ActionEngineDot.SetResourceReference(Shape.FillProperty, "Warning");
                ActionEngineHint.Text = "未检测到 Moonlight 控制端，请前往引擎中心一键安装或导入便携包。";
                ActionEngineLink.Visibility = Visibility.Visible;
            }
            else
            {
                ActionEngineDot.SetResourceReference(Shape.FillProperty, "Ready");
                ActionEngineHint.Text = $"Moonlight 控制端已就绪（{Path.GetFileName(mlPath)}）";
                ActionEngineLink.Visibility = Visibility.Collapsed;
            }
        }
        else
        {
            var rdPath = ResolveRustDeskExecutable();
            if (rdPath is null)
            {
                ActionEngineDot.SetResourceReference(Shape.FillProperty, "Warning");
                ActionEngineHint.Text = "未检测到 RustDesk 引擎，请前往引擎中心一键安装或指定程序路径。";
                ActionEngineLink.Visibility = Visibility.Visible;
            }
            else
            {
                ActionEngineDot.SetResourceReference(Shape.FillProperty, "Ready");
                ActionEngineHint.Text = $"RustDesk 办公引擎已就绪（{Path.GetFileName(rdPath)}）";
                ActionEngineLink.Visibility = Visibility.Collapsed;
            }
        }
    }

    private void FocusDeviceAction()
    {
        if (currentPage != AppPage.Remote)
            return;
        (SelectedDevice is null ? AddDeviceButton : ConnectButton).Focus();
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
