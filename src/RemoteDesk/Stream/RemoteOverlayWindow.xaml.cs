using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace RemoteDesk.Streaming;

public partial class RemoteOverlayWindow : Window
{
    private readonly DispatcherTimer _barAutoHideTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(2600) };
    private bool _isFullscreen;
    private bool _isExternalPopupOpen;

    public RemoteOverlayWindow()
    {
        InitializeComponent();

        _barAutoHideTimer.Tick += (_, _) =>
        {
            _barAutoHideTimer.Stop();
            if (_isFullscreen && !_isExternalPopupOpen && !FullscreenExpandedBar.IsMouseOver)
                CollapseFullscreenBar();
        };

        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            ToastBanner.Visibility = Visibility.Collapsed;
        };
    }

    internal event Action<ControlInputMode>? InputModeRequested;
    internal event Action<FrameworkElement>? QualityMenuRequested;
    internal event Action<FrameworkElement>? HotkeysMenuRequested;
    internal event Action? FileUploadRequested;
    internal event Action? ClipboardSyncToggleRequested;
    internal event Action? PrivacyToggleRequested;
    internal event Action<string[]>? FilesDropped;
    internal event Action? MuteToggleRequested;
    internal event Action? TelemetryHudToggleRequested;
    internal event Action? ExitFullscreenRequested;
    internal event Action? DisconnectRequested;
    internal event Action? RetryRequested;

    public bool IsExternalPopupOpen
    {
        get => _isExternalPopupOpen;
        set
        {
            _isExternalPopupOpen = value;
            if (!value && _isFullscreen && FullscreenExpandedBar.Visibility == Visibility.Visible && !FullscreenExpandedBar.IsMouseOver)
                _barAutoHideTimer.Start();
            else if (value)
                _barAutoHideTimer.Stop();
        }
    }

    public void SetDeviceInfo(string deviceName, string linkHint)
    {
        FsDeviceNameText.Text = deviceName;
        HudLinkText.Text = linkHint;
    }

    public void SetFullscreenState(bool isFullscreen)
    {
        _isFullscreen = isFullscreen;
        _barAutoHideTimer.Stop();
        FullscreenBarRoot.Visibility = isFullscreen ? Visibility.Visible : Visibility.Collapsed;
        CollapseFullscreenBar();
    }

    public void SetTelemetryHudVisible(bool visible)
    {
        TelemetryHudCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FsHudBtn.Tag = visible ? "Selected" : null;
    }

    internal void SetInputMode(ControlInputMode mode)
    {
        var isGame = mode == ControlInputMode.GameRelativeLock;
        FsOfficeModeBtn.Tag = isGame ? null : "Selected";
        FsGameModeBtn.Tag = isGame ? "Selected" : null;
    }

    public void SetMuted(bool muted)
    {
        FsMuteBtn.Content = muted ? "已静音" : "声音";
        FsMuteBtn.Tag = muted ? "MutedActive" : null;
    }

    public void SetPeerToolsAvailable(bool available)
    {
        FsFilesBtn.IsEnabled = available;
        FsClipboardBtn.IsEnabled = available;
        FsPrivacyBtn.IsEnabled = available;
    }

    public void SetClipboardSyncEnabled(bool enabled)
    {
        FsClipboardBtn.Content = enabled ? "剪贴板" : "剪贴板关";
        FsClipboardBtn.Tag = enabled ? "Selected" : null;
    }

    public void SetPrivacyEnabled(bool enabled)
    {
        FsPrivacyBtn.Content = enabled ? "防窥中" : "防窥";
        FsPrivacyBtn.Tag = enabled ? "MutedActive" : null;
    }

    internal void UpdateStats(RdStreamStats stats, string linkHint, bool isPoorConnection)
    {
        var dotBrush = (Brush)FindResource(isPoorConnection ? "WarningBrush" : "ReadyBrush");
        PullTabDot.Fill = dotBrush;
        FsStatusDot.Fill = dotBrush;

        PullTabHintText.Text = stats.RttMs > 0
            ? $"{stats.RttMs}ms · {stats.RenderedFps:0}fps"
            : "展开控制栏";

        HudLinkText.Text = linkHint;
        HudLinkText.Foreground = dotBrush;
        HudRttText.Text = stats.RttMs > 0
            ? $" · RTT {stats.RttMs.ToString(CultureInfo.InvariantCulture)} ms"
            : " · RTT --";
        var accel = stats.HwAccelerated != 0 ? "硬件解码" : "软件解码";
        HudCodecText.Text = $" · {stats.CodecDisplayName} ({accel})";

        var mbps = stats.BitrateKbps > 0 ? stats.BitrateKbps / 1000.0 : 0.0;
        var resText = stats.VideoWidth > 0 && stats.VideoHeight > 0
            ? $"{stats.VideoWidth}×{stats.VideoHeight} · "
            : "";
        HudMetricsText.Text = string.Create(
            CultureInfo.InvariantCulture,
            $"{resText}渲染 {stats.RenderedFps:0}/{stats.ReceivedFps:0} fps · 解码 {stats.DecodeTimeMs:0.0} ms · 丢帧 {stats.DroppedFrames} · {mbps:0.0} Mbps");
    }

    public void ShowConnecting(string title, string detail)
    {
        StatusTitleText.Text = title;
        StatusDetailText.Text = detail;
        StatusActionRow.Visibility = Visibility.Collapsed;
        StatusCard.Visibility = Visibility.Visible;
        var dotBrush = (Brush)FindResource("WarningBrush");
        PullTabDot.Fill = dotBrush;
        FsStatusDot.Fill = dotBrush;
    }

    public void ShowError(string title, string detail)
    {
        StatusTitleText.Text = title;
        StatusDetailText.Text = detail;
        StatusActionRow.Visibility = Visibility.Visible;
        StatusCard.Visibility = Visibility.Visible;
        var dotBrush = (Brush)FindResource("DangerBrush");
        PullTabDot.Fill = dotBrush;
        FsStatusDot.Fill = dotBrush;
    }

    public void HideStatusCard()
    {
        StatusCard.Visibility = Visibility.Collapsed;
        var dotBrush = (Brush)FindResource("ReadyBrush");
        PullTabDot.Fill = dotBrush;
        FsStatusDot.Fill = dotBrush;
    }

    public void ShowToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        ToastText.Text = message;
        ToastBanner.Visibility = Visibility.Visible;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private void ExpandFullscreenBar()
    {
        _barAutoHideTimer.Stop();
        FullscreenPullTab.Visibility = Visibility.Collapsed;
        FullscreenExpandedBar.Visibility = Visibility.Visible;
    }

    private void CollapseFullscreenBar()
    {
        FullscreenExpandedBar.Visibility = Visibility.Collapsed;
        FullscreenPullTab.Visibility = _isFullscreen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PullTabMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isFullscreen)
            ExpandFullscreenBar();
    }

    private void ExpandedBarMouseEnter(object sender, System.Windows.Input.MouseEventArgs e) => _barAutoHideTimer.Stop();

    private void ExpandedBarMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_isFullscreen && !_isExternalPopupOpen)
        {
            _barAutoHideTimer.Stop();
            _barAutoHideTimer.Start();
        }
    }

    private void OfficeModeClicked(object sender, RoutedEventArgs e) => InputModeRequested?.Invoke(ControlInputMode.OfficeFreePointer);
    private void GameModeClicked(object sender, RoutedEventArgs e) => InputModeRequested?.Invoke(ControlInputMode.GameRelativeLock);
    private void QualityClicked(object sender, RoutedEventArgs e) => QualityMenuRequested?.Invoke(FsQualityBtn);
    private void HotkeysClicked(object sender, RoutedEventArgs e) => HotkeysMenuRequested?.Invoke(FsHotkeysBtn);
    private void FileUploadClicked(object sender, RoutedEventArgs e) => FileUploadRequested?.Invoke();
    private void ClipboardSyncClicked(object sender, RoutedEventArgs e) => ClipboardSyncToggleRequested?.Invoke();
    private void PrivacyClicked(object sender, RoutedEventArgs e) => PrivacyToggleRequested?.Invoke();
    private void MuteClicked(object sender, RoutedEventArgs e) => MuteToggleRequested?.Invoke();
    private void ToggleHudClicked(object sender, RoutedEventArgs e) => TelemetryHudToggleRequested?.Invoke();
    private void ExitFullscreenClicked(object sender, RoutedEventArgs e) => ExitFullscreenRequested?.Invoke();
    private void DisconnectClicked(object sender, RoutedEventArgs e) => DisconnectRequested?.Invoke();
    private void RetryClicked(object sender, RoutedEventArgs e) => RetryRequested?.Invoke();

    private void OverlayDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void OverlayDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            e.Handled = true;
            FilesDropped?.Invoke(files);
        }
    }
}
