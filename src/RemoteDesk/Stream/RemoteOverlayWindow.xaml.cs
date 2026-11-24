using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RemoteDesk.UI;

namespace RemoteDesk.Streaming;

public partial class RemoteOverlayWindow : Window
{
    private readonly DispatcherTimer _barAutoHideTimer = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromMilliseconds(2600) };
    private bool _isFullscreen;
    private bool _isExternalPopupOpen;
    private bool _isGameMode;
    private int _barAnimGen;
    private int _toastAnimGen;

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
            HideToastAnimated();
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
        CollapseFullscreenBar(immediate: true);
    }

    public void SetTelemetryHudVisible(bool visible)
    {
        TelemetryHudCard.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        FsHudBtn.Tag = visible ? "Selected" : null;
    }

    internal void SetInputMode(ControlInputMode mode)
    {
        _isGameMode = mode == ControlInputMode.GameRelativeLock;
        FsOfficeModeBtn.Tag = _isGameMode ? null : "Selected";
        FsGameModeBtn.Tag = _isGameMode ? "Selected" : null;
        UpdateFsModeIndicator(animate: IsLoaded && FullscreenExpandedBar.Visibility == Visibility.Visible);
    }

    private void FsModeTrackGridSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateFsModeIndicator(animate: false);

    private void UpdateFsModeIndicator(bool animate)
    {
        if (FsModeTrackGrid is null || FsModeIndicatorTransform is null)
            return;

        var width = FsModeTrackGrid.ActualWidth / 2.0;
        if (width <= 0)
            return;

        MotionBehaviors.AnimateHorizontalIndicator(FsModeIndicatorTransform, _isGameMode ? width : 0.0, animate);
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

        var gen = ++_toastAnimGen;
        ToastText.Text = message;
        ToastBanner.BeginAnimation(OpacityProperty, null);
        ToastSlide.BeginAnimation(TranslateTransform.YProperty, null);
        ToastBanner.Opacity = 0;
        ToastSlide.Y = 8;
        ToastBanner.Visibility = Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        ToastBanner.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        ToastSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });

        _toastTimer.Stop();
        _toastTimer.Start();
        _ = gen;
    }

    private void HideToastAnimated()
    {
        var gen = ++_toastAnimGen;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var fade = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (gen == _toastAnimGen)
                ToastBanner.Visibility = Visibility.Collapsed;
        };
        ToastBanner.BeginAnimation(OpacityProperty, fade);
        ToastSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(6.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
    }

    private void ExpandFullscreenBar()
    {
        _barAutoHideTimer.Stop();
        var gen = ++_barAnimGen;

        FullscreenPullTab.BeginAnimation(OpacityProperty, null);
        FullscreenPullTab.Visibility = Visibility.Collapsed;

        FullscreenExpandedBar.BeginAnimation(OpacityProperty, null);
        ExpandedBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        ExpandedBarScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        ExpandedBarSlide.BeginAnimation(TranslateTransform.YProperty, null);

        FullscreenExpandedBar.Opacity = 0;
        ExpandedBarScale.ScaleX = 0.94;
        ExpandedBarScale.ScaleY = 0.94;
        ExpandedBarSlide.Y = -6;
        FullscreenExpandedBar.Visibility = Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(190);
        FullscreenExpandedBar.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, dur) { EasingFunction = ease });
        ExpandedBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, dur) { EasingFunction = ease });
        ExpandedBarScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, dur) { EasingFunction = ease });
        ExpandedBarSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, dur) { EasingFunction = ease });

        Dispatcher.BeginInvoke(() =>
        {
            if (gen == _barAnimGen)
                UpdateFsModeIndicator(animate: false);
        }, DispatcherPriority.Loaded);
    }

    private void CollapseFullscreenBar(bool immediate = false)
    {
        var gen = ++_barAnimGen;
        if (immediate || FullscreenExpandedBar.Visibility != Visibility.Visible)
        {
            FullscreenExpandedBar.BeginAnimation(OpacityProperty, null);
            FullscreenExpandedBar.Visibility = Visibility.Collapsed;
            FullscreenPullTab.BeginAnimation(OpacityProperty, null);
            FullscreenPullTab.Opacity = 1.0;
            FullscreenPullTab.Visibility = _isFullscreen ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        var dur = TimeSpan.FromMilliseconds(140);
        var fadeOut = new DoubleAnimation(0.0, dur) { EasingFunction = ease };
        fadeOut.Completed += (_, _) =>
        {
            if (gen != _barAnimGen)
                return;

            FullscreenExpandedBar.Visibility = Visibility.Collapsed;
            if (_isFullscreen)
            {
                FullscreenPullTab.Opacity = 0.0;
                PullTabSlide.Y = -4;
                FullscreenPullTab.Visibility = Visibility.Visible;
                FullscreenPullTab.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, dur) { EasingFunction = ease });
                PullTabSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0.0, dur) { EasingFunction = ease });
            }
        };

        FullscreenExpandedBar.BeginAnimation(OpacityProperty, fadeOut);
        ExpandedBarScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.96, dur) { EasingFunction = ease });
        ExpandedBarScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.96, dur) { EasingFunction = ease });
        ExpandedBarSlide.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6.0, dur) { EasingFunction = ease });
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
