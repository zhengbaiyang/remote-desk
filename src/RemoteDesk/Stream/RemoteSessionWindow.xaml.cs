using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;

namespace RemoteDesk.Streaming;

internal sealed record AgentPeerSessionContext(
    int AgentPort,
    string AgentAuthKey);

internal sealed record RemoteSessionLockContext(
    string LocalDeviceKey,
    string LocalDeviceName,
    string? TargetDeviceKey,
    string? SyncServerUrl,
    string? SyncSessionToken,
    string? SyncServerCertThumbprint,
    Action<IReadOnlyList<SyncedDeviceInfo>>? OnSyncedDevicesUpdated);

public partial class RemoteSessionWindow : Window
{
    private const int WmClipboardUpdate = 0x031D;

    private readonly DeviceProfile _device;
    private readonly string _targetHost;
    private readonly string _linkHint;
    private readonly Func<StreamOptions, Task>? _onStreamOptionsChanged;
    private readonly AgentPeerSessionContext? _peerContext;
    private readonly RemoteSessionLockContext? _lockContext;
    private readonly Action<bool, bool>? _onSessionPrefsChanged;
    private readonly string _sessionId = Guid.NewGuid().ToString("N");
    private readonly SemaphoreSlim _uploadLock = new(1, 1);
    private readonly DispatcherTimer _clipboardPollTimer = new() { Interval = TimeSpan.FromMilliseconds(1200) };
    private readonly DispatcherTimer _sessionHeartbeatTimer = new() { Interval = TimeSpan.FromSeconds(15) };
    private readonly DispatcherTimer _powerConfirmResetTimer = new() { Interval = TimeSpan.FromSeconds(4) };

    private StreamOptions _currentOptions;
    private StreamPresetKind _pendingPreset;
    private GameStreamIdentity? _identity;
    private GameStreamClient? _gsClient;
    private AgentPeerClient? _peerClient;
    private SyncApiClient? _syncApiClient;
    private NativeStreamSession? _activeSession;
    private RemoteOverlayWindow? _overlay;
    private HwndSource? _hwndSource;
    private CancellationTokenSource? _connectCts;
    private int _connectionGeneration;

    private bool _isFullscreen;
    private WindowState _savedWindowState = WindowState.Normal;
    private Rect _savedWindowRect;
    private bool _isMuted;
    private bool _isHudVisible;
    private bool _isClosing;
    private bool _hasConnectedOnce;
    private bool _suppressPopupUiSync;
    private int _activeMenuCount;

    private bool _autoMatchResolution;
    private bool _clipboardSyncEnabled;
    private bool _privacyEnabled;
    private bool _displayOverrideApplied;
    private bool _suppressLocalClipboardEvent;
    private bool _clipboardPollInFlight;
    private string? _lastClipboardSha256;
    private string? _pendingPowerConfirmAction;
    private bool _syncLockAcquired;
    private bool _peerLockAcquired;
    private bool _sessionHeartbeatInFlight;

    public RemoteSessionWindow()
        : this(DeviceProfile.Create("远程主机", "127.0.0.1"), "127.0.0.1", "直连", null)
    {
    }

    internal RemoteSessionWindow(
        DeviceProfile device,
        string targetHost,
        string? linkTypeHint,
        Func<StreamOptions, Task>? onStreamOptionsChanged = null,
        AgentPeerSessionContext? peerContext = null,
        bool autoMatchResolution = true,
        bool autoSyncClipboard = true,
        Action<bool, bool>? onSessionPrefsChanged = null,
        RemoteSessionLockContext? lockContext = null)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (string.IsNullOrWhiteSpace(targetHost))
            throw new ArgumentException("缺少目标主机地址。", nameof(targetHost));

        InitializeComponent();

        _device = device;
        _targetHost = targetHost.Trim();
        _linkHint = string.IsNullOrWhiteSpace(linkTypeHint) ? "直连" : linkTypeHint.Trim();
        _onStreamOptionsChanged = onStreamOptionsChanged;
        _peerContext = peerContext;
        _lockContext = lockContext;
        _autoMatchResolution = autoMatchResolution;
        _clipboardSyncEnabled = autoSyncClipboard;
        _onSessionPrefsChanged = onSessionPrefsChanged;

        if (_peerContext is not null)
            _peerClient = new AgentPeerClient();
        if (_lockContext is { SyncServerUrl.Length: > 0, SyncSessionToken.Length: > 0, TargetDeviceKey.Length: > 0 })
            _syncApiClient = new SyncApiClient();

        _currentOptions = StreamOptions.Normalize(device.Stream);
        _pendingPreset = _currentOptions.Preset;

        Title = $"RemoteDesk · {device.Name} ({_targetHost})";
        ToolbarDeviceName.Text = device.Name;
        ToolbarLinkBadge.Text = _linkHint;
        UpdateQualitySummaryButton();
        SyncQualityPopupControls(_currentOptions);
        UpdateCodecCapabilityHint();
        ApplyPeerCapabilityUi();

        VideoHost.InputModeChanged += OnVideoHostInputModeChanged;
        VideoHost.FullscreenToggleRequested += () => ToggleFullscreen();
        VideoHost.DisconnectRequested += () => Close();
        VideoHost.HelpToggleRequested += ToggleHelpPopup;
        VideoHost.FilesDropped += paths => _ = UploadFilesAsync(paths);

        _clipboardPollTimer.Tick += async (_, _) => await PollRemoteClipboardAsync();
        _sessionHeartbeatTimer.Tick += async (_, _) => await HeartbeatSessionLocksAsync();
        _powerConfirmResetTimer.Tick += (_, _) => ResetPowerConfirmButtons();

        PreviewKeyDown += OnWindowPreviewKeyDown;
        Loaded += OnWindowLoaded;
        LocationChanged += (_, _) => SyncOverlayBounds();
        SizeChanged += (_, _) => SyncOverlayBounds();
        StateChanged += OnWindowStateChanged;
        Deactivated += (_, _) => VideoHost.ReleaseCaptureForDeactivation();
        Closing += OnWindowClosing;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        RemoteDesk.UI.WindowBackdropHelper.ApplyDarkChromeOnly(this, dark: true);
        if (_hwndSource is null && PresentationSource.FromVisual(this) is HwndSource src)
        {
            _hwndSource = src;
            _hwndSource.AddHook(WindowWndProcHook);
            AddClipboardFormatListener(src.Handle);
        }
    }

    private void ApplyPeerCapabilityUi()
    {
        var hasPeer = _peerContext is not null;
        UploadFilesBtn.IsEnabled = hasPeer;
        ClipboardSyncBtn.IsEnabled = hasPeer;
        PrivacyBtn.IsEnabled = hasPeer;
        PowerLockBtn.IsEnabled = hasPeer;
        PowerRestartBtn.IsEnabled = hasPeer;
        PowerShutdownBtn.IsEnabled = hasPeer;
        PopupAutoMatchResCheck.IsEnabled = hasPeer;
        PopupRestoreResBtn.IsEnabled = hasPeer;

        if (!hasPeer)
        {
            const string tip = "需要同账号云同步且对方启用「后台守护服务」";
            UploadFilesBtn.ToolTip = tip;
            ClipboardSyncBtn.ToolTip = tip;
            PrivacyBtn.ToolTip = tip;
        }

        UpdateClipboardUi();
        UpdatePrivacyUi();
    }

    private async void OnWindowLoaded(object sender, RoutedEventArgs e)
    {
        EnsureOverlayWindow();
        SyncOverlayBounds();
        if (_peerContext is not null && _clipboardSyncEnabled)
            _clipboardPollTimer.Start();
        await StartOrReconnectAsync(cancelExistingRemoteSession: false);
    }

    private void EnsureOverlayWindow()
    {
        if (_overlay is not null)
            return;

        _overlay = new RemoteOverlayWindow { Owner = this };
        _overlay.SetDeviceInfo(_device.Name, _linkHint);
        _overlay.SetTelemetryHudVisible(_isHudVisible);
        _overlay.SetPointerLockState(VideoHost.InputMode, VideoHost.IsPointerLocked);
        _overlay.SetMuted(_isMuted);
        _overlay.SetPeerToolsAvailable(_peerContext is not null);
        _overlay.SetClipboardSyncEnabled(_clipboardSyncEnabled);
        _overlay.SetPrivacyEnabled(_privacyEnabled);
        _overlay.PreviewKeyDown += OnWindowPreviewKeyDown;

        _overlay.InputModeRequested += mode => VideoHost.SetInputMode(
            mode,
            mode == ControlInputMode.GameRelativeLock
                ? "游戏锁定已就绪：点击画面锁鼠，按 Ctrl+Alt 随时释放"
                : "已切换回办公自由指针模式",
            lockImmediately: false);
        _overlay.QualityMenuRequested += target => OpenQualityPopup(target);
        _overlay.HotkeysMenuRequested += target => OpenHotkeysPopup(target);
        _overlay.HelpMenuRequested += target => OpenHelpPopup(target);
        _overlay.FileUploadRequested += () => OpenUploadDialogAndSend();
        _overlay.ClipboardSyncToggleRequested += ToggleClipboardSync;
        _overlay.PrivacyToggleRequested += () => _ = TogglePrivacyAsync();
        _overlay.FilesDropped += paths => _ = UploadFilesAsync(paths);
        _overlay.MuteToggleRequested += ToggleMute;
        _overlay.TelemetryHudToggleRequested += ToggleHud;
        _overlay.ExitFullscreenRequested += () =>
        {
            if (_isFullscreen)
                ToggleFullscreen();
        };
        _overlay.DisconnectRequested += () => Close();
        _overlay.RetryRequested += async () => await StartOrReconnectAsync(cancelExistingRemoteSession: false);

        _overlay.Show();
    }

    private void SyncOverlayBounds()
    {
        if (_overlay is null || !IsLoaded)
            return;

        if (WindowState == WindowState.Minimized)
        {
            if (_overlay.IsVisible)
                _overlay.Hide();
            return;
        }

        if (!_overlay.IsVisible && !_isClosing)
            _overlay.Show();

        if (VideoContainer.ActualWidth <= 0 || VideoContainer.ActualHeight <= 0)
            return;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget is null)
            return;

        var topLeftPx = VideoContainer.PointToScreen(new Point(0, 0));
        var bottomRightPx = VideoContainer.PointToScreen(new Point(VideoContainer.ActualWidth, VideoContainer.ActualHeight));
        var transform = source.CompositionTarget.TransformFromDevice;
        var topLeftDip = transform.Transform(topLeftPx);
        var bottomRightDip = transform.Transform(bottomRightPx);

        _overlay.Left = topLeftDip.X;
        _overlay.Top = topLeftDip.Y;
        _overlay.Width = Math.Max(1, bottomRightDip.X - topLeftDip.X);
        _overlay.Height = Math.Max(1, bottomRightDip.Y - topLeftDip.Y);
    }

    private async Task StartOrReconnectAsync(bool cancelExistingRemoteSession)
    {
        var gen = Interlocked.Increment(ref _connectionGeneration);

        CancellationTokenSource? oldCts;
        NativeStreamSession? oldSession;
        lock (this)
        {
            oldCts = _connectCts;
            _connectCts = new CancellationTokenSource();
            oldSession = _activeSession;
            _activeSession = null;
            VideoHost.Session = null;
        }

        try { oldCts?.Cancel(); } catch { }
        oldCts?.Dispose();

        var ct = _connectCts.Token;

        SetConnectingStatus("正在初始化原生串流引擎…", $"目标主机 {_targetHost} · {_currentOptions.Width}×{_currentOptions.Height} @ {_currentOptions.Fps}fps");

        if (oldSession is not null)
        {
            await Task.Run(() =>
            {
                try { oldSession.Dispose(); } catch { }
            });
        }

        try
        {
            _identity ??= GameStreamIdentity.LoadOrCreate();
            _gsClient ??= new GameStreamClient(_identity);

            if (!cancelExistingRemoteSession)
            {
                SetConnectingStatus("正在锁定远控独占会话…", $"核验 {_device.Name} 当前未被其他设备远控");
                var (syncOk, syncErr) = await AcquireSyncLockAsync(ct);
                if (!syncOk)
                {
                    SetErrorStatus("该设备当前不可被远控", syncErr ?? "目标设备正处于远控会话中。");
                    return;
                }

                var (peerOk, peerErr) = await AcquirePeerLockAsync(ct);
                if (!peerOk)
                {
                    await ReleaseSyncLockAsync();
                    SetErrorStatus("该设备当前不可被远控", peerErr ?? "目标设备当前正处于远控会话中。");
                    return;
                }

                _sessionHeartbeatTimer.Start();
            }

            if (ct.IsCancellationRequested || _isClosing || gen != Volatile.Read(ref _connectionGeneration))
                return;

            if (cancelExistingRemoteSession)
                await _gsClient.CancelSessionAsync(_targetHost, ct);

            if (ct.IsCancellationRequested || _isClosing || gen != Volatile.Read(ref _connectionGeneration))
                return;

            // Optional automatic resolution adaptation before Sunshine launches stream
            if (_peerContext is not null && _peerClient is not null && _autoMatchResolution)
            {
                SetConnectingStatus("正在匹配远程显示器分辨率…", $"{_currentOptions.Width}×{_currentOptions.Height} @ {_currentOptions.Fps}Hz");
                var dispRes = await _peerClient.ApplyDisplayModeAsync(
                    _targetHost,
                    _peerContext.AgentPort,
                    _peerContext.AgentAuthKey,
                    _currentOptions.Width,
                    _currentOptions.Height,
                    _currentOptions.Fps,
                    _sessionId,
                    ct);
                if (dispRes.Ok && dispRes.Changed)
                {
                    _displayOverrideApplied = true;
                    if (!string.IsNullOrWhiteSpace(dispRes.Message))
                        _overlay?.ShowToast(dispRes.Message);
                    await Task.Delay(350, ct);
                }
            }

            if (ct.IsCancellationRequested || _isClosing || gen != Volatile.Read(ref _connectionGeneration))
                return;

            SetConnectingStatus($"正在探测 {_device.Name}…", $"查询主机信息 ({_targetHost}:47984/47989)");
            var serverInfo = await _gsClient.GetServerInfoAsync(_targetHost, ct);

            if (ct.IsCancellationRequested || _isClosing || gen != Volatile.Read(ref _connectionGeneration))
                return;

            if (_peerContext is null && !_syncLockAcquired && !cancelExistingRemoteSession && serverInfo.CurrentGameId != 0)
            {
                SetErrorStatus("目标设备正被远控", "目标主机当前已存在活跃远控会话，暂不可被其他设备远控。");
                return;
            }

            if (serverInfo.PairStatus != 1)
            {
                if (_peerContext is not null && _peerClient is not null)
                {
                    SetConnectingStatus($"正在自动完成 {_device.Name} 安全配对…", "同账号零 PIN 静默授权中");
                    var autoPair = new AutoPairOrchestrator(_peerClient);
                    var localHostName = HostServiceManager.GetLocalNetworkInfo().HostName;
                    var pairRes = await autoPair.NativeAutoPairAsync(
                        _gsClient,
                        _targetHost,
                        _peerContext.AgentPort,
                        _peerContext.AgentAuthKey,
                        localHostName,
                        securityPin: null,
                        ct);

                    if (ct.IsCancellationRequested || _isClosing || gen != Volatile.Read(ref _connectionGeneration))
                        return;

                    if (pairRes.Status is AutoPairStatus.NeedSecurityPin or AutoPairStatus.InvalidSecurityPin)
                    {
                        SetErrorStatus(
                            "需要输入目标电脑独立安全密码",
                            "请返回设备列表重新点击「一键高刷连接」，在密码框输入独立安全密码完成首次验证。");
                        return;
                    }

                    if (!pairRes.IsSuccess)
                    {
                        SetErrorStatus("自动配对失败", pairRes.Message);
                        return;
                    }

                    serverInfo = await _gsClient.GetServerInfoAsync(_targetHost, ct);
                }
                else
                {
                    SetErrorStatus(
                        "目标主机尚未配对",
                        "请返回设备列表选中该设备并点击「配对设备 (Pair)」。");
                    return;
                }
            }

            SetConnectingStatus("正在获取远程桌面会话…", "读取 Sunshine 应用列表");
            var apps = await _gsClient.GetAppListAsync(_targetHost, ct);
            var targetApp =
                apps.FirstOrDefault(a => string.Equals(a.Title, _device.StreamAppName, StringComparison.OrdinalIgnoreCase)) ??
                apps.FirstOrDefault(a => string.Equals(a.Title, "Desktop", StringComparison.OrdinalIgnoreCase)) ??
                apps.FirstOrDefault();

            if (targetApp is null)
            {
                SetErrorStatus("未找到可串流的应用", "请检查目标电脑 Sunshine 配置，确保包含「Desktop」应用。");
                return;
            }

            var shouldResume = serverInfo.CurrentGameId == targetApp.Id;
            SetConnectingStatus(
                shouldResume ? $"正在恢复 {targetApp.Title}" : $"正在启动 {targetApp.Title}",
                $"{_currentOptions.Width}×{_currentOptions.Height} @ {_currentOptions.Fps}fps · {_currentOptions.BitrateMbps} Mbps");

            var launchResult = await _gsClient.LaunchOrResumeAsync(
                _targetHost,
                targetApp.Id,
                _currentOptions.Width,
                _currentOptions.Height,
                _currentOptions.Fps,
                shouldResume,
                ct);

            if (ct.IsCancellationRequested || _isClosing || gen != Volatile.Read(ref _connectionGeneration))
                return;

            var hwnd = VideoHost.ChildHwnd;
            if (hwnd == IntPtr.Zero)
            {
                await System.Windows.Threading.Dispatcher.Yield();
                hwnd = VideoHost.ChildHwnd;
            }
            if (hwnd == IntPtr.Zero)
                throw new InvalidOperationException("视频渲染窗口句柄尚未就绪。");

            var supportedMask = NativeStreamBridge.QuerySupportedCodecs();
            var (nativeCodecId, _, fellBack) = StreamViewportMapper.ResolveEffectiveCodec(_currentOptions.Codec, supportedMask);
            if (fellBack)
                _overlay?.ShowToast("本机硬件不支持所选编码，已自动回退至 H.264");

            var session = new NativeStreamSession();
            AttachSessionHandlers(session, gen);

            lock (this)
            {
                if (_isClosing || gen != Volatile.Read(ref _connectionGeneration))
                {
                    session.Dispose();
                    return;
                }
                _activeSession = session;
            }

            VideoHost.UpdateStreamDimensions(_currentOptions.Width, _currentOptions.Height);
            VideoHost.Session = session;

            var (vpW, vpH) = VideoHost.GetCurrentPixelSize();
            var isRemoteNet = !_linkHint.Contains("局域网", StringComparison.Ordinal);

            SetConnectingStatus("正在建立加密音视频通道…", "握手 RTSP / ENet 媒体传输流");
            session.Start(
                hwnd,
                vpW,
                vpH,
                _targetHost,
                serverInfo,
                launchResult,
                _currentOptions,
                nativeCodecId,
                isRemoteNet);

            session.SetAudioMuted(_isMuted);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            if (!_isClosing && gen == Volatile.Read(ref _connectionGeneration))
                SetErrorStatus("远控连接失败", ex.Message);
        }
    }

    private void AttachSessionHandlers(NativeStreamSession session, int generation)
    {
        session.StageChanged += (stage, name) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing || generation != Volatile.Read(ref _connectionGeneration))
                    return;
                _overlay?.ShowConnecting($"正在连接 {_device.Name}", $"握手阶段 {stage}：{name}");
            });
        };

        session.Connected += () =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing || generation != Volatile.Read(ref _connectionGeneration))
                    return;
                _hasConnectedOnce = true;
                ToolbarStatusDot.Fill = (Brush)FindResource("ReadyBrush");
                MiniStatsText.Text = "已连接 · 等待首帧…";
                _overlay?.HideStatusCard();
                _overlay?.ShowToast(
                    VideoHost.InputMode == ControlInputMode.GameRelativeLock
                        ? "已进入游戏锁定模式 · 按 Ctrl+Alt 释放光标"
                        : "已连接 · 办公自由模式（光标可自由移出）· F11 全屏 · Ctrl+Alt+H 查看操作提示",
                    durationMs: 4500);
            });
        };

        session.Disconnected += (errorCode, message) =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing || generation != Volatile.Read(ref _connectionGeneration))
                    return;
                _sessionHeartbeatTimer.Stop();
                _ = ReleasePeerLockAsync();
                _ = ReleaseSyncLockAsync();
                VideoHost.SetInputMode(ControlInputMode.OfficeFreePointer, null);
                VideoHost.ReleaseCaptureForDeactivation();
                ToolbarStatusDot.Fill = (Brush)FindResource("DangerBrush");
                MiniStatsText.Text = "已断开";
                var title = errorCode == 0 ? "远控会话已结束" : $"远控连接已断开 ({errorCode})";
                _overlay?.ShowError(title, message);
            });
        };

        session.ConnectionQualityChanged += isPoor =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing || generation != Volatile.Read(ref _connectionGeneration))
                    return;
                ToolbarStatusDot.Fill = (Brush)FindResource(isPoor ? "WarningBrush" : "ReadyBrush");
                if (isPoor)
                    _overlay?.ShowToast("当前网络波动较大，建议在「画质/帧率」中适当降低码率");
            });
        };

        session.StatsUpdated += stats =>
        {
            Dispatcher.BeginInvoke(() =>
            {
                if (_isClosing || generation != Volatile.Read(ref _connectionGeneration))
                    return;

                var isPoor = stats.RttMs > 120 || stats.DroppedFrames > 20;
                ToolbarStatusDot.Fill = (Brush)FindResource(isPoor ? "WarningBrush" : "ReadyBrush");
                var mbps = stats.BitrateKbps > 0 ? stats.BitrateKbps / 1000.0 : _currentOptions.BitrateMbps;
                MiniStatsText.Text = string.Create(
                    CultureInfo.InvariantCulture,
                    $"{(stats.RttMs > 0 ? $"{stats.RttMs}ms" : "--")} · {stats.RenderedFps:0}fps · {stats.DecodeTimeMs:0.0}ms · {mbps:0.0}M · {stats.CodecDisplayName}");
                _overlay?.UpdateStats(stats, _linkHint, isPoor);
            });
        };
    }

    private IntPtr WindowWndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate &&
            !_suppressLocalClipboardEvent &&
            _clipboardSyncEnabled &&
            _peerContext is not null &&
            !_isClosing &&
            _hasConnectedOnce)
        {
            _ = PushLocalClipboardAsync();
        }
        return IntPtr.Zero;
    }

    private async Task PushLocalClipboardAsync()
    {
        if (_peerContext is null || _peerClient is null || _isClosing)
            return;

        AgentClipboardSetRequestDto? payload = null;
        string? sha = null;
        try
        {
            if (Clipboard.ContainsText())
            {
                var text = Clipboard.GetText();
                if (!string.IsNullOrEmpty(text) && text.Length <= InteractiveClipboardController.MaxTextLength)
                {
                    sha = InteractiveClipboardController.ComputePayloadSha256("text", Encoding.UTF8.GetBytes(text));
                    if (!string.Equals(sha, _lastClipboardSha256, StringComparison.Ordinal))
                        payload = new AgentClipboardSetRequestDto("text", text, null);
                }
            }
            else if (Clipboard.ContainsImage())
            {
                var image = Clipboard.GetImage();
                if (image is not null)
                {
                    var encoder = new PngBitmapEncoder();
                    encoder.Frames.Add(BitmapFrame.Create(image));
                    using var ms = new MemoryStream();
                    encoder.Save(ms);
                    if (ms.Length <= InteractiveClipboardController.MaxImagePngBytes)
                    {
                        var png = ms.ToArray();
                        sha = InteractiveClipboardController.ComputePayloadSha256("image", png);
                        if (!string.Equals(sha, _lastClipboardSha256, StringComparison.Ordinal))
                            payload = new AgentClipboardSetRequestDto("image", null, Convert.ToBase64String(png));
                    }
                }
            }
        }
        catch
        {
            return;
        }

        if (payload is null || sha is null)
            return;

        _lastClipboardSha256 = sha;
        await _peerClient.SetClipboardAsync(_targetHost, _peerContext.AgentPort, _peerContext.AgentAuthKey, payload);
    }

    private async Task PollRemoteClipboardAsync()
    {
        if (_clipboardPollInFlight ||
            !_clipboardSyncEnabled ||
            _peerContext is null ||
            _peerClient is null ||
            _isClosing ||
            !_hasConnectedOnce)
        {
            return;
        }

        _clipboardPollInFlight = true;
        try
        {
            var (ok, clip, _) = await _peerClient.GetClipboardAsync(_targetHost, _peerContext.AgentPort, _peerContext.AgentAuthKey);
            if (!ok || clip is null || _isClosing || !_clipboardSyncEnabled)
                return;

            if (clip.Kind == "none" || string.IsNullOrEmpty(clip.Sha256))
                return;

            if (string.Equals(clip.Sha256, _lastClipboardSha256, StringComparison.Ordinal))
                return;

            _lastClipboardSha256 = clip.Sha256;
            _suppressLocalClipboardEvent = true;
            try
            {
                if (clip.Kind == "text" && clip.Text is not null)
                {
                    Clipboard.SetText(clip.Text);
                }
                else if (clip.Kind == "image" && !string.IsNullOrEmpty(clip.ImageBase64))
                {
                    var bytes = Convert.FromBase64String(clip.ImageBase64);
                    using var ms = new MemoryStream(bytes);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.StreamSource = ms;
                    bmp.EndInit();
                    bmp.Freeze();
                    Clipboard.SetImage(bmp);
                }
            }
            catch
            {
            }
            finally
            {
                await Dispatcher.Yield();
                _suppressLocalClipboardEvent = false;
            }
        }
        finally
        {
            _clipboardPollInFlight = false;
        }
    }

    private void OpenUploadDialogAndSend()
    {
        if (_peerContext is null)
        {
            _overlay?.ShowToast("需使用云同步设备且对方开启守护服务才支持文件快传");
            return;
        }

        VideoHost.ReleaseCaptureForDeactivation();
        var dialog = new OpenFileDialog
        {
            Title = $"发送文件到 {_device.Name}",
            Multiselect = true,
            CheckFileExists = true,
        };
        if (dialog.ShowDialog(this) == true && dialog.FileNames.Length > 0)
            _ = UploadFilesAsync(dialog.FileNames);
    }

    private async Task UploadFilesAsync(string[] rawPaths)
    {
        if (_peerContext is null || _peerClient is null)
        {
            _overlay?.ShowToast("需同账号云同步设备才支持拖拽/按钮文件快传");
            return;
        }

        var files = rawPaths.Where(File.Exists).ToArray();
        if (files.Length == 0)
        {
            _overlay?.ShowToast("仅支持直接发送文件（暂不支持直接拖拽整个文件夹）");
            return;
        }

        await _uploadLock.WaitAsync();
        try
        {
            var succeeded = 0;
            string? lastSaved = null;
            for (var i = 0; i < files.Length; i++)
            {
                if (_isClosing)
                    break;

                var file = files[i];
                var fileName = Path.GetFileName(file);
                var lastPct = -10;
                var progress = new Progress<double>(pct =>
                {
                    var rounded = (int)Math.Floor(pct / 10.0) * 10;
                    if (rounded >= lastPct + 10 || rounded >= 100)
                    {
                        lastPct = rounded;
                        Dispatcher.BeginInvoke(() => _overlay?.ShowToast($"正在发送 ({i + 1}/{files.Length}) {fileName} · {pct:0}%"));
                    }
                });

                var res = await _peerClient.UploadFileAsync(
                    _targetHost,
                    _peerContext.AgentPort,
                    _peerContext.AgentAuthKey,
                    file,
                    progress);

                if (res.Ok)
                {
                    succeeded++;
                    lastSaved = res.SavedPath ?? fileName;
                }
                else
                {
                    _overlay?.ShowToast($"文件 {fileName} 发送失败：{res.Message}");
                }
            }

            if (succeeded > 0)
            {
                _overlay?.ShowToast(succeeded == 1
                    ? $"文件已发送至远程：{lastSaved}"
                    : $"已成功发送 {succeeded}/{files.Length} 个文件至远程 下载\\RemoteDesk");
            }
        }
        finally
        {
            _uploadLock.Release();
        }
    }

    private void ToggleClipboardSync()
    {
        if (_peerContext is null)
            return;

        _clipboardSyncEnabled = !_clipboardSyncEnabled;
        if (_clipboardSyncEnabled)
            _clipboardPollTimer.Start();
        else
            _clipboardPollTimer.Stop();

        UpdateClipboardUi();
        _onSessionPrefsChanged?.Invoke(_autoMatchResolution, _clipboardSyncEnabled);
        _overlay?.ShowToast(_clipboardSyncEnabled ? "已开启双向剪贴板同步（文本与截图）" : "已暂停双向剪贴板同步");
    }

    private void UpdateClipboardUi()
    {
        ClipboardSyncBtn.Content = _clipboardSyncEnabled ? "剪贴板" : "剪贴板关";
        ClipboardSyncBtn.Tag = _clipboardSyncEnabled && _peerContext is not null ? "Selected" : null;
        _overlay?.SetClipboardSyncEnabled(_clipboardSyncEnabled);
    }

    private async Task TogglePrivacyAsync()
    {
        if (_peerContext is null || _peerClient is null)
            return;

        var targetState = !_privacyEnabled;
        var res = await _peerClient.SetPrivacyScreenAsync(_targetHost, _peerContext.AgentPort, _peerContext.AgentAuthKey, targetState);
        if (res.Ok)
        {
            _privacyEnabled = res.Enabled;
            UpdatePrivacyUi();
            _overlay?.ShowToast(res.Message ?? (_privacyEnabled ? "已开启防窥黑屏" : "已关闭防窥黑屏"));
        }
        else
        {
            _overlay?.ShowToast(res.Message ?? "防窥黑屏切换失败");
        }
    }

    private void UpdatePrivacyUi()
    {
        PrivacyBtn.Content = _privacyEnabled ? "防窥中" : "防窥";
        PrivacyBtn.Tag = _privacyEnabled ? "MutedActive" : null;
        _overlay?.SetPrivacyEnabled(_privacyEnabled);
    }

    private void SetConnectingStatus(string title, string detail)
    {
        ToolbarStatusDot.Fill = (Brush)FindResource("WarningBrush");
        MiniStatsText.Text = "连接中…";
        _overlay?.ShowConnecting(title, detail);
    }

    private void SetErrorStatus(string title, string detail)
    {
        ToolbarStatusDot.Fill = (Brush)FindResource("DangerBrush");
        MiniStatsText.Text = "连接异常";
        _overlay?.ShowError(title, detail);
    }

    private void OnVideoHostInputModeChanged(ControlInputMode mode, bool isPointerLocked, string? toastReason)
    {
        var isGame = mode == ControlInputMode.GameRelativeLock;
        OfficeModeBtn.Tag = isGame ? null : "Selected";
        GameModeBtn.Tag = isGame ? "Selected" : null;
        UpdateSessionModeIndicator(isGame, animate: IsLoaded);
        _overlay?.SetPointerLockState(mode, isPointerLocked);

        if (!string.IsNullOrWhiteSpace(toastReason))
            _overlay?.ShowToast(toastReason);
    }

    private void SessionModeTrackGridSizeChanged(object sender, SizeChangedEventArgs e) =>
        UpdateSessionModeIndicator(VideoHost.InputMode == ControlInputMode.GameRelativeLock, animate: false);

    private void UpdateSessionModeIndicator(bool isGame, bool animate)
    {
        if (SessionModeTrackGrid is null || SessionModeIndicatorTransform is null)
            return;

        var width = SessionModeTrackGrid.ActualWidth / 2.0;
        if (width <= 0)
            return;

        RemoteDesk.UI.MotionBehaviors.AnimateHorizontalIndicator(
            SessionModeIndicatorTransform,
            isGame ? width : 0.0,
            animate);
    }

    private void ToggleFullscreen()
    {
        if (!_isFullscreen)
        {
            _savedWindowState = WindowState == WindowState.Minimized ? WindowState.Normal : WindowState;
            _savedWindowRect = RestoreBounds;

            VideoHost.ReleaseCaptureForDeactivation();
            WindowedToolbar.Visibility = Visibility.Collapsed;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowState = WindowState.Normal;
            WindowState = WindowState.Maximized;
            _isFullscreen = true;

            _overlay?.SetFullscreenState(true);
            SyncOverlayBounds();
            _overlay?.ShowToast("已进入无边框全屏 · 鼠标移至顶部唤出控制栏 · F11 退出");
        }
        else
        {
            VideoHost.ReleaseCaptureForDeactivation();
            _isFullscreen = false;
            WindowState = WindowState.Normal;
            ResizeMode = ResizeMode.CanResize;
            WindowStyle = WindowStyle.SingleBorderWindow;

            if (_savedWindowRect.Width > 200 && _savedWindowRect.Height > 200)
            {
                Left = _savedWindowRect.Left;
                Top = _savedWindowRect.Top;
                Width = _savedWindowRect.Width;
                Height = _savedWindowRect.Height;
            }
            if (_savedWindowState == WindowState.Maximized)
                WindowState = WindowState.Maximized;

            WindowedToolbar.Visibility = Visibility.Visible;
            _overlay?.SetFullscreenState(false);
            SyncOverlayBounds();
        }
    }

    private void ToggleMute()
    {
        _isMuted = !_isMuted;
        _activeSession?.SetAudioMuted(_isMuted);
        MuteBtn.Content = _isMuted ? "已静音" : "声音";
        MuteBtn.Tag = _isMuted ? "MutedActive" : null;
        _overlay?.SetMuted(_isMuted);
        _overlay?.ShowToast(_isMuted ? "已静音远程声音" : "已恢复远程声音");
    }

    private void ToggleHud()
    {
        _isHudVisible = !_isHudVisible;
        _overlay?.SetTelemetryHudVisible(_isHudVisible);
        if (_hasConnectedOnce)
            _overlay?.ShowToast(_isHudVisible ? "已开启悬浮性能仪表盘" : "已隐藏悬浮性能仪表盘");
    }

    private void OpenQualityPopup(FrameworkElement placementTarget)
    {
        VideoHost.ReleaseCaptureForDeactivation();
        HotkeysPopup.IsOpen = false;
        HelpPopup.IsOpen = false;
        SyncQualityPopupControls(_currentOptions);
        UpdateCodecCapabilityHint();
        QualityPopup.PlacementTarget = placementTarget;
        QualityPopup.IsOpen = true;
    }

    private void OpenHotkeysPopup(FrameworkElement placementTarget)
    {
        VideoHost.ReleaseCaptureForDeactivation();
        QualityPopup.IsOpen = false;
        HelpPopup.IsOpen = false;
        ResetPowerConfirmButtons();
        HotkeysPopup.PlacementTarget = placementTarget;
        HotkeysPopup.IsOpen = true;
    }

    private void OpenHelpPopup(FrameworkElement placementTarget)
    {
        VideoHost.ReleaseCaptureForDeactivation();
        QualityPopup.IsOpen = false;
        HotkeysPopup.IsOpen = false;
        HelpPopup.PlacementTarget = placementTarget;
        HelpPopup.IsOpen = true;
    }

    private void ToggleHelpPopup()
    {
        if (HelpPopup.IsOpen)
        {
            HelpPopup.IsOpen = false;
            return;
        }

        var anchor = _isFullscreen && _overlay is not null
            ? _overlay.FullscreenMenuAnchor
            : (FrameworkElement)HelpBtn;
        OpenHelpPopup(anchor);
    }

    private void OnWindowPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (VideoHost.TryHandleWindowShortcut(key, Keyboard.Modifiers))
            e.Handled = true;
    }

    private void UpdateQualitySummaryButton() =>
        QualityBtn.Content = $"画质 · {_currentOptions.Height}p {_currentOptions.Fps}";

    private void UpdateCodecCapabilityHint()
    {
        var mask = NativeStreamBridge.QuerySupportedCodecs();
        var supported = new List<string> { "H.264" };
        if ((mask & StreamViewportMapper.CodecMaskHevc) != 0) supported.Add("HEVC");
        if ((mask & StreamViewportMapper.CodecMaskAv1) != 0) supported.Add("AV1");
        CodecCapabilityHint.Text = $"本机 D3D11 硬件解码支持：{string.Join(" / ", supported)}";
    }

    private void SyncQualityPopupControls(StreamOptions options)
    {
        _suppressPopupUiSync = true;
        try
        {
            _pendingPreset = options.Preset;
            SelectComboItemByTag(PopupResolutionCombo, $"{options.Width}x{options.Height}");
            SelectComboItemByTag(PopupFpsCombo, options.Fps.ToString(CultureInfo.InvariantCulture));
            SelectComboItemByTag(PopupCodecCombo, options.Codec.ToString());
            PopupBitrateSlider.Value = Math.Clamp(options.BitrateMbps, (int)PopupBitrateSlider.Minimum, (int)PopupBitrateSlider.Maximum);
            PopupBitrateValueText.Text = $"{(int)PopupBitrateSlider.Value} Mbps";
            PopupHwDecodeCheck.IsChecked = options.HardwareDecoding;
            PopupAutoMatchResCheck.IsChecked = _autoMatchResolution;

            Preset1080_120Btn.Tag = options is { Width: 1920, Height: 1080, Fps: 120 } ? "Selected" : null;
            Preset2K60Btn.Tag = options is { Width: 2560, Height: 1440, Fps: 60 } ? "Selected" : null;
            Preset4K60Btn.Tag = options is { Width: 3840, Height: 2160, Fps: 60 } ? "Selected" : null;
            PresetSaverBtn.Tag = options.Preset == StreamPresetKind.Saver ? "Selected" : null;
        }
        finally
        {
            _suppressPopupUiSync = false;
        }
    }

    private static void SelectComboItemByTag(ComboBox combo, string tag)
    {
        foreach (var obj in combo.Items)
        {
            if (obj is ComboBoxItem item && string.Equals(item.Tag as string, tag, StringComparison.OrdinalIgnoreCase))
            {
                combo.SelectedItem = item;
                return;
            }
        }
    }

    private void OfficeModeClicked(object sender, RoutedEventArgs e) =>
        VideoHost.SetInputMode(ControlInputMode.OfficeFreePointer, "已切换到办公自由指针模式", lockImmediately: false);

    private void GameModeClicked(object sender, RoutedEventArgs e) =>
        VideoHost.SetInputMode(ControlInputMode.GameRelativeLock, "游戏锁定已就绪：点击画面锁鼠，按 Ctrl+Alt 随时释放", lockImmediately: false);

    private void ToggleHudClicked(object sender, RoutedEventArgs e) => ToggleHud();
    private void QualityBtnClicked(object sender, RoutedEventArgs e) => OpenQualityPopup(QualityBtn);
    private void UploadFilesBtnClicked(object sender, RoutedEventArgs e) => OpenUploadDialogAndSend();
    private void ClipboardSyncBtnClicked(object sender, RoutedEventArgs e) => ToggleClipboardSync();
    private async void PrivacyBtnClicked(object sender, RoutedEventArgs e) => await TogglePrivacyAsync();
    private void HotkeysBtnClicked(object sender, RoutedEventArgs e) => OpenHotkeysPopup(HotkeysBtn);
    private void HelpBtnClicked(object sender, RoutedEventArgs e) => ToggleHelpPopup();
    private void MuteBtnClicked(object sender, RoutedEventArgs e) => ToggleMute();
    private void FullscreenBtnClicked(object sender, RoutedEventArgs e) => ToggleFullscreen();
    private void DisconnectBtnClicked(object sender, RoutedEventArgs e) => Close();

    private void MenuPopupOpened(object? sender, EventArgs e)
    {
        _activeMenuCount++;
        if (_overlay is not null)
            _overlay.IsExternalPopupOpen = true;
    }

    private void MenuPopupClosed(object? sender, EventArgs e)
    {
        _activeMenuCount = Math.Max(0, _activeMenuCount - 1);
        if (_overlay is not null && _activeMenuCount == 0)
            _overlay.IsExternalPopupOpen = false;
        if (ReferenceEquals(sender, HotkeysPopup))
            ResetPowerConfirmButtons();
    }

    private void AutoMatchResCheckChanged(object sender, RoutedEventArgs e)
    {
        if (_suppressPopupUiSync)
            return;
        _autoMatchResolution = PopupAutoMatchResCheck.IsChecked ?? true;
        _onSessionPrefsChanged?.Invoke(_autoMatchResolution, _clipboardSyncEnabled);
    }

    private async void RestoreResolutionClicked(object sender, RoutedEventArgs e)
    {
        if (_peerContext is null || _peerClient is null)
            return;

        QualityPopup.IsOpen = false;
        var res = await _peerClient.RestoreDisplayModeAsync(_targetHost, _peerContext.AgentPort, _peerContext.AgentAuthKey);
        if (res.Ok)
            _displayOverrideApplied = false;
        _overlay?.ShowToast(res.Message ?? "已发送恢复原分辨率请求");
    }

    private void PresetQuickClicked(object sender, RoutedEventArgs e)
    {
        if (ReferenceEquals(sender, Preset1080_120Btn))
            SyncQualityPopupControls(StreamOptions.FromPreset(StreamPresetKind.Ultra120));
        else if (ReferenceEquals(sender, Preset2K60Btn))
            SyncQualityPopupControls(StreamOptions.FromPreset(StreamPresetKind.High2K));
        else if (ReferenceEquals(sender, Preset4K60Btn))
            SyncQualityPopupControls(StreamOptions.Create(StreamPresetKind.Custom, 3840, 2160, 60, 65, VideoCodecPreference.H265, true));
        else if (ReferenceEquals(sender, PresetSaverBtn))
            SyncQualityPopupControls(StreamOptions.FromPreset(StreamPresetKind.Saver));
    }

    private void PopupBitrateSliderChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressPopupUiSync || PopupBitrateValueText is null)
            return;
        PopupBitrateValueText.Text = $"{(int)Math.Round(e.NewValue)} Mbps";
    }

    private async void ApplyQualityClicked(object sender, RoutedEventArgs e)
    {
        var (w, h) = (_currentOptions.Width, _currentOptions.Height);
        var resTag = (PopupResolutionCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        if (!string.IsNullOrWhiteSpace(resTag))
        {
            var parts = resTag.Split('x');
            if (parts.Length == 2 &&
                int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var pw) &&
                int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ph))
            {
                w = pw;
                h = ph;
            }
        }

        var fpsTag = (PopupFpsCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var fps = int.TryParse(fpsTag, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedFps)
            ? parsedFps
            : _currentOptions.Fps;

        var codecTag = (PopupCodecCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        var codec = Enum.TryParse<VideoCodecPreference>(codecTag, out var parsedCodec)
            ? parsedCodec
            : _currentOptions.Codec;

        var bitrate = Math.Clamp((int)Math.Round(PopupBitrateSlider.Value), StreamOptions.MinBitrateMbps, StreamOptions.MaxBitrateMbps);
        var hwDecode = PopupHwDecodeCheck.IsChecked ?? true;

        var nextOptions = StreamOptions.Create(_pendingPreset, w, h, fps, bitrate, codec, hwDecode);
        _currentOptions = nextOptions;
        UpdateQualitySummaryButton();
        QualityPopup.IsOpen = false;

        if (_onStreamOptionsChanged is not null)
        {
            try { await _onStreamOptionsChanged(nextOptions); } catch { }
        }

        _overlay?.ShowToast($"正在以 {w}×{h} @ {fps}fps / {bitrate}Mbps 平滑重连…");
        await StartOrReconnectAsync(cancelExistingRemoteSession: true);
    }

    private void SendHotkeyBtnClicked(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && int.TryParse(btn.Tag as string, NumberStyles.Integer, CultureInfo.InvariantCulture, out var hotkeyId))
        {
            _activeSession?.SendHotkey(hotkeyId);
            HotkeysPopup.IsOpen = false;
            _overlay?.ShowToast($"已向远程主机发送 {btn.Content}");
        }
    }

    private async void PowerLockClicked(object sender, RoutedEventArgs e)
    {
        if (_peerContext is null || _peerClient is null)
            return;

        HotkeysPopup.IsOpen = false;
        var res = await _peerClient.ExecutePowerActionAsync(_targetHost, _peerContext.AgentPort, _peerContext.AgentAuthKey, "lock");
        _overlay?.ShowToast(res.Message ?? "已发送远程锁屏指令");
    }

    private async void PowerRestartClicked(object sender, RoutedEventArgs e) =>
        await HandleConfirmedPowerActionAsync("restart", PowerRestartBtn, "重启远程电脑", "再次点击确认重启");

    private async void PowerShutdownClicked(object sender, RoutedEventArgs e) =>
        await HandleConfirmedPowerActionAsync("shutdown", PowerShutdownBtn, "关闭远程电脑", "再次点击确认关机");

    private async Task HandleConfirmedPowerActionAsync(string action, Button button, string defaultLabel, string confirmLabel)
    {
        if (_peerContext is null || _peerClient is null)
            return;

        if (!string.Equals(_pendingPowerConfirmAction, action, StringComparison.Ordinal))
        {
            ResetPowerConfirmButtons();
            _pendingPowerConfirmAction = action;
            button.Content = confirmLabel;
            _powerConfirmResetTimer.Start();
            return;
        }

        ResetPowerConfirmButtons();
        HotkeysPopup.IsOpen = false;
        var res = await _peerClient.ExecutePowerActionAsync(_targetHost, _peerContext.AgentPort, _peerContext.AgentAuthKey, action);
        _overlay?.ShowToast(res.Message ?? $"已发送 {defaultLabel} 指令");
    }

    private void ResetPowerConfirmButtons()
    {
        _powerConfirmResetTimer.Stop();
        _pendingPowerConfirmAction = null;
        if (PowerRestartBtn is not null) PowerRestartBtn.Content = "重启远程电脑";
        if (PowerShutdownBtn is not null) PowerShutdownBtn.Content = "关闭远程电脑";
    }

    private void WindowDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }
    }

    private void WindowDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            e.Handled = true;
            _ = UploadFilesAsync(files);
        }
    }

    private void OnWindowStateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
            VideoHost.ReleaseCaptureForDeactivation();
        SyncOverlayBounds();
    }

    private void OnWindowClosing(object? sender, CancelEventArgs e)
    {
        _isClosing = true;
        Interlocked.Increment(ref _connectionGeneration);
        VideoHost.SetInputMode(ControlInputMode.OfficeFreePointer, null);
        VideoHost.ReleaseCaptureForDeactivation();

        _clipboardPollTimer.Stop();
        _sessionHeartbeatTimer.Stop();
        _powerConfirmResetTimer.Stop();

        if (_hwndSource is not null)
        {
            try { RemoveClipboardFormatListener(_hwndSource.Handle); } catch { }
            _hwndSource.RemoveHook(WindowWndProcHook);
            _hwndSource = null;
        }

        try { _connectCts?.Cancel(); } catch { }
        _connectCts?.Dispose();
        _connectCts = null;

        var session = _activeSession;
        _activeSession = null;
        VideoHost.Session = null;

        if (session is not null)
        {
            try { session.Dispose(); } catch { }
        }

        var gsClient = _gsClient;
        if (gsClient is not null)
            _ = Task.Run(() => gsClient.CancelSessionAsync(_targetHost, CancellationToken.None));

        var peerClient = _peerClient;
        var peerCtx = _peerContext;
        var restoreDisplay = _displayOverrideApplied;
        var disablePrivacy = _privacyEnabled;
        var releasePeerLock = _peerLockAcquired;
        _peerLockAcquired = false;
        _peerClient = null;

        if (peerClient is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    if (peerCtx is not null && disablePrivacy)
                        await peerClient.SetPrivacyScreenAsync(_targetHost, peerCtx.AgentPort, peerCtx.AgentAuthKey, false).ConfigureAwait(false);
                    if (peerCtx is not null && restoreDisplay)
                        await peerClient.RestoreDisplayModeAsync(_targetHost, peerCtx.AgentPort, peerCtx.AgentAuthKey).ConfigureAwait(false);
                    if (peerCtx is not null && releasePeerLock)
                        await peerClient.ReleaseSessionAsync(_targetHost, peerCtx.AgentPort, peerCtx.AgentAuthKey, _sessionId).ConfigureAwait(false);
                }
                catch
                {
                }
                finally
                {
                    peerClient.Dispose();
                }
            });
        }

        var syncClient = _syncApiClient;
        var lockCtx = _lockContext;
        var releaseSyncLock = _syncLockAcquired;
        _syncLockAcquired = false;
        _syncApiClient = null;
        if (syncClient is not null && lockCtx is not null && releaseSyncLock &&
            !string.IsNullOrEmpty(lockCtx.SyncServerUrl) &&
            !string.IsNullOrEmpty(lockCtx.SyncSessionToken))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var rel = await syncClient.ReleaseRemoteSessionAsync(
                        lockCtx.SyncServerUrl,
                        lockCtx.SyncSessionToken,
                        _sessionId,
                        lockCtx.LocalDeviceKey,
                        lockCtx.SyncServerCertThumbprint).ConfigureAwait(false);
                    if (rel.Devices is not null && lockCtx.OnSyncedDevicesUpdated is not null)
                    {
                        _ = Application.Current?.Dispatcher.BeginInvoke(() => lockCtx.OnSyncedDevicesUpdated(rel.Devices));
                    }
                }
                catch
                {
                }
            });
        }

        if (_overlay is not null)
        {
            try { _overlay.Close(); } catch { }
            _overlay = null;
        }

        _identity?.Dispose();
        _identity = null;
    }

    private async Task<(bool Ok, string? Error)> AcquireSyncLockAsync(CancellationToken ct)
    {
        if (_syncApiClient is null ||
            _lockContext is null ||
            string.IsNullOrEmpty(_lockContext.SyncServerUrl) ||
            string.IsNullOrEmpty(_lockContext.SyncSessionToken) ||
            string.IsNullOrEmpty(_lockContext.TargetDeviceKey))
        {
            return (true, null);
        }

        try
        {
            var res = await _syncApiClient.AcquireRemoteSessionAsync(
                _lockContext.SyncServerUrl,
                _lockContext.SyncSessionToken,
                _sessionId,
                _lockContext.LocalDeviceKey,
                _lockContext.TargetDeviceKey,
                _lockContext.SyncServerCertThumbprint,
                ct);

            if (!res.Ok)
                return (false, res.Message ?? "目标设备当前处于远控占用状态。");

            if (!res.UnsupportedByServer)
                _syncLockAcquired = true;

            if (res.Devices is not null)
                _lockContext.OnSyncedDevicesUpdated?.Invoke(res.Devices);

            return (true, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // If sync server is temporarily unreachable, fall through to direct peer claim.
            return (true, null);
        }
    }

    private async Task<(bool Ok, string? Error)> AcquirePeerLockAsync(CancellationToken ct)
    {
        if (_peerClient is null || _peerContext is null)
            return (true, null);

        var localKey = _lockContext?.LocalDeviceKey ?? Environment.MachineName;
        var localName = _lockContext?.LocalDeviceName ?? Environment.MachineName;
        var res = await _peerClient.ClaimSessionAsync(
            _targetHost,
            _peerContext.AgentPort,
            _peerContext.AgentAuthKey,
            _sessionId,
            localKey,
            localName,
            ct);

        if (!res.Ok)
        {
            if (string.Equals(res.Error, "NETWORK_ERROR", StringComparison.Ordinal))
                return (true, null);
            return (false, res.Message ?? "目标设备当前处于远控占用状态。");
        }

        if (!res.Unsupported)
            _peerLockAcquired = true;

        return (true, null);
    }

    private async Task HeartbeatSessionLocksAsync()
    {
        if (_sessionHeartbeatInFlight || _isClosing)
            return;

        _sessionHeartbeatInFlight = true;
        try
        {
            if (_syncLockAcquired &&
                _syncApiClient is not null &&
                _lockContext is { SyncServerUrl.Length: > 0, SyncSessionToken.Length: > 0, TargetDeviceKey.Length: > 0 })
            {
                try
                {
                    var hb = await _syncApiClient.HeartbeatRemoteSessionAsync(
                        _lockContext.SyncServerUrl,
                        _lockContext.SyncSessionToken,
                        _sessionId,
                        _lockContext.LocalDeviceKey,
                        _lockContext.TargetDeviceKey,
                        _lockContext.SyncServerCertThumbprint);
                    if (hb.Devices is not null && !_isClosing)
                        _lockContext.OnSyncedDevicesUpdated?.Invoke(hb.Devices);
                }
                catch
                {
                }
            }

            if (_peerLockAcquired && _peerClient is not null && _peerContext is not null)
            {
                try
                {
                    var localKey = _lockContext?.LocalDeviceKey ?? Environment.MachineName;
                    var localName = _lockContext?.LocalDeviceName ?? Environment.MachineName;
                    await _peerClient.ClaimSessionAsync(
                        _targetHost,
                        _peerContext.AgentPort,
                        _peerContext.AgentAuthKey,
                        _sessionId,
                        localKey,
                        localName);
                }
                catch
                {
                }
            }
        }
        finally
        {
            _sessionHeartbeatInFlight = false;
        }
    }

    private async Task ReleasePeerLockAsync()
    {
        if (!_peerLockAcquired || _peerClient is null || _peerContext is null)
            return;

        _peerLockAcquired = false;
        await _peerClient.ReleaseSessionAsync(_targetHost, _peerContext.AgentPort, _peerContext.AgentAuthKey, _sessionId);
    }

    private async Task ReleaseSyncLockAsync()
    {
        if (!_syncLockAcquired ||
            _syncApiClient is null ||
            _lockContext is null ||
            string.IsNullOrEmpty(_lockContext.SyncServerUrl) ||
            string.IsNullOrEmpty(_lockContext.SyncSessionToken))
        {
            return;
        }

        _syncLockAcquired = false;
        try
        {
            var rel = await _syncApiClient.ReleaseRemoteSessionAsync(
                _lockContext.SyncServerUrl,
                _lockContext.SyncSessionToken,
                _sessionId,
                _lockContext.LocalDeviceKey,
                _lockContext.SyncServerCertThumbprint);
            if (rel.Devices is not null)
                _lockContext.OnSyncedDevicesUpdated?.Invoke(rel.Devices);
        }
        catch
        {
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
