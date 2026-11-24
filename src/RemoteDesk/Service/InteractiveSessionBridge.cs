using System.ComponentModel;
using System.IO;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace RemoteDesk;

internal sealed record SessionBridgeRequest(
    string Command,
    string? PayloadJson = null);

internal sealed record SessionBridgeResponse(
    bool Ok,
    string? PayloadJson = null,
    string? Error = null);

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class DisplayModeController
{
    private const int EnumCurrentSettings = -1;
    private const uint DmPelsWidth = 0x00080000;
    private const uint DmPelsHeight = 0x00100000;
    private const uint DmDisplayFrequency = 0x00400000;
    private const int DispChangeSuccessful = 0;

    private readonly object _lock = new();
    private DEVMODEW? _originalDevMode;
    private DisplayModeInfo? _originalInfo;
    private bool _overrideActive;

    public bool IsOverrideActive
    {
        get { lock (_lock) return _overrideActive; }
    }

    public AgentDisplayStateDto GetState()
    {
        lock (_lock)
        {
            var currentDm = QueryCurrentMode();
            var current = ToModeInfo(currentDm);
            var supported = EnumerateSupportedModes();
            return new AgentDisplayStateDto(current, supported, _overrideActive, _originalInfo);
        }
    }

    public AgentDisplayApplyResultDto ApplyMode(AgentDisplayApplyRequestDto req)
    {
        lock (_lock)
        {
            try
            {
                var currentDm = QueryCurrentMode();
                var currentInfo = ToModeInfo(currentDm);
                var supported = EnumerateSupportedModes();
                var selected = DisplayResolutionPlanner.SelectBestMode(
                    req.Width,
                    req.Height,
                    req.RefreshRate,
                    currentInfo,
                    supported);

                if (selected == currentInfo)
                {
                    return new AgentDisplayApplyResultDto(
                        Ok: true,
                        Changed: false,
                        Applied: currentInfo,
                        Original: _originalInfo,
                        Message: "当前显示器分辨率已处于最佳匹配状态。");
                }

                if (!_overrideActive)
                {
                    _originalDevMode = currentDm;
                    _originalInfo = currentInfo;
                }

                var nextDm = _originalDevMode ?? currentDm;
                nextDm.dmSize = (ushort)Marshal.SizeOf<DEVMODEW>();
                nextDm.dmPelsWidth = (uint)selected.Width;
                nextDm.dmPelsHeight = (uint)selected.Height;
                nextDm.dmDisplayFrequency = (uint)selected.RefreshRate;
                nextDm.dmFields = DmPelsWidth | DmPelsHeight | DmDisplayFrequency;

                var rc = ChangeDisplaySettingsExW(null, ref nextDm, IntPtr.Zero, 0, IntPtr.Zero);
                if (rc != DispChangeSuccessful)
                {
                    if (!_overrideActive)
                    {
                        _originalDevMode = null;
                        _originalInfo = null;
                    }
                    return new AgentDisplayApplyResultDto(
                        Ok: false,
                        Changed: false,
                        Applied: currentInfo,
                        Original: _originalInfo,
                        Error: $"DISP_CHANGE_{rc}",
                        Message: $"切换远程显示器分辨率失败（Win32 状态码 {rc}）。");
                }

                _overrideActive = true;
                return new AgentDisplayApplyResultDto(
                    Ok: true,
                    Changed: true,
                    Applied: selected,
                    Original: _originalInfo,
                    Message: $"已自动适配远程分辨率至 {selected.Width}×{selected.Height} @ {selected.RefreshRate}Hz");
            }
            catch (Exception ex)
            {
                return new AgentDisplayApplyResultDto(
                    Ok: false,
                    Changed: false,
                    Error: "DISPLAY_EXCEPTION",
                    Message: $"修改远程显示器设置失败：{ex.Message}");
            }
        }
    }

    public AgentDisplayApplyResultDto RestoreMode()
    {
        lock (_lock)
        {
            if (!_overrideActive || _originalDevMode is not { } origDm)
            {
                return new AgentDisplayApplyResultDto(
                    Ok: true,
                    Changed: false,
                    Message: "显示器未处于临时适配状态，无需恢复。");
            }

            try
            {
                origDm.dmSize = (ushort)Marshal.SizeOf<DEVMODEW>();
                var rc = ChangeDisplaySettingsExW(null, ref origDm, IntPtr.Zero, 0, IntPtr.Zero);
                var origInfo = _originalInfo;
                if (rc == DispChangeSuccessful)
                {
                    _overrideActive = false;
                    _originalDevMode = null;
                    _originalInfo = null;
                    return new AgentDisplayApplyResultDto(
                        Ok: true,
                        Changed: true,
                        Applied: origInfo,
                        Original: origInfo,
                        Message: origInfo is not null
                            ? $"已恢复原分辨率 {origInfo.Width}×{origInfo.Height} @ {origInfo.RefreshRate}Hz"
                            : "已恢复原显示器设置。");
                }

                return new AgentDisplayApplyResultDto(
                    Ok: false,
                    Changed: false,
                    Error: $"DISP_CHANGE_{rc}",
                    Message: $"恢复原分辨率失败（Win32 状态码 {rc}）。");
            }
            catch (Exception ex)
            {
                return new AgentDisplayApplyResultDto(
                    Ok: false,
                    Changed: false,
                    Error: "DISPLAY_RESTORE_FAILED",
                    Message: $"恢复原分辨率异常：{ex.Message}");
            }
        }
    }

    private static DEVMODEW QueryCurrentMode()
    {
        var dm = CreateDevMode();
        if (!EnumDisplaySettingsExW(null, EnumCurrentSettings, ref dm, 0))
            throw new InvalidOperationException("无法读取当前主显示器模式。");
        return dm;
    }

    private static List<DisplayModeInfo> EnumerateSupportedModes()
    {
        var seen = new HashSet<(int W, int H, int Hz)>();
        var list = new List<DisplayModeInfo>();
        var dm = CreateDevMode();
        for (var i = 0; EnumDisplaySettingsExW(null, i, ref dm, 0); i++)
        {
            var w = (int)dm.dmPelsWidth;
            var h = (int)dm.dmPelsHeight;
            var hz = (int)dm.dmDisplayFrequency;
            if (w >= 640 && h >= 480 && hz >= 24 && seen.Add((w, h, hz)))
                list.Add(new DisplayModeInfo(w, h, hz));
            dm = CreateDevMode();
        }
        return list;
    }

    private static DisplayModeInfo ToModeInfo(in DEVMODEW dm) =>
        new((int)dm.dmPelsWidth, (int)dm.dmPelsHeight, (int)dm.dmDisplayFrequency);

    private static DEVMODEW CreateDevMode() =>
        new() { dmSize = (ushort)Marshal.SizeOf<DEVMODEW>() };

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DEVMODEW
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmDeviceName;
        public ushort dmSpecVersion;
        public ushort dmDriverVersion;
        public ushort dmSize;
        public ushort dmDriverExtra;
        public uint dmFields;
        public int dmPositionX;
        public int dmPositionY;
        public uint dmDisplayOrientation;
        public uint dmDisplayFixedOutput;
        public short dmColor;
        public short dmDuplex;
        public short dmYResolution;
        public short dmTTOption;
        public short dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string dmFormName;
        public ushort dmLogPixels;
        public uint dmBitsPerPel;
        public uint dmPelsWidth;
        public uint dmPelsHeight;
        public uint dmDisplayFlags;
        public uint dmDisplayFrequency;
        public uint dmICMMethod;
        public uint dmICMIntent;
        public uint dmMediaType;
        public uint dmDitherType;
        public uint dmReserved1;
        public uint dmReserved2;
        public uint dmPanningWidth;
        public uint dmPanningHeight;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplaySettingsExW(string? lpszDeviceName, int iModeNum, ref DEVMODEW lpDevMode, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ChangeDisplaySettingsExW(string? lpszDeviceName, ref DEVMODEW lpDevMode, IntPtr hwnd, uint dwflags, IntPtr lParam);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class PrivacyCurtainController : IDisposable
{
    private const int GwlExStyle = -20;
    private const int WsExTransparent = 0x00000020;
    private const int WsExLayered = 0x00080000;
    private const int WsExToolWindow = 0x00000080;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExTopMost = 0x00000008;
    private const uint LwaAlpha = 0x00000002;
    private const uint WdaExcludeFromCapture = 0x00000011;

    private const int WhKeyboardLl = 13;
    private const uint WmKeyDown = 0x0100;
    private const uint WmSysKeyDown = 0x0104;
    private const uint VkControl = 0x11;
    private const uint VkMenu = 0x12;
    private const uint VkEnd = 0x23;

    private delegate IntPtr HookProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);

    private readonly Dispatcher _dispatcher;
    private Window? _curtainWindow;
    private IntPtr _hookHandle;
    private HookProcDelegate? _hookProc;
    private bool _disposed;

    public PrivacyCurtainController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public bool IsEnabled { get; private set; }

    public AgentPrivacyResultDto SetEnabled(bool enabled)
    {
        if (_disposed)
            return new AgentPrivacyResultDto(false, false, "DISPOSED", "会话桥接已释放。");

        return _dispatcher.Invoke(() => enabled ? EnableOnUiThread() : DisableOnUiThread());
    }

    public void SyncBounds()
    {
        if (_disposed || !IsEnabled)
            return;

        _dispatcher.BeginInvoke(() =>
        {
            if (_curtainWindow is null)
                return;
            _curtainWindow.Left = SystemParameters.VirtualScreenLeft;
            _curtainWindow.Top = SystemParameters.VirtualScreenTop;
            _curtainWindow.Width = SystemParameters.VirtualScreenWidth;
            _curtainWindow.Height = SystemParameters.VirtualScreenHeight;
        });
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            if (_dispatcher.CheckAccess())
                DisableOnUiThread();
            else
                _dispatcher.Invoke(DisableOnUiThread);
        }
        catch
        {
        }
    }

    private AgentPrivacyResultDto EnableOnUiThread()
    {
        if (_curtainWindow is not null && IsEnabled)
        {
            SyncVirtualBounds(_curtainWindow);
            return new AgentPrivacyResultDto(true, true, Message: "防窥黑屏已保持开启（本机物理显示器已熄黑，仅远控画面可见）。");
        }

        var win = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            ShowActivated = false,
            Focusable = false,
            Topmost = true,
            Background = Brushes.Black,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = SystemParameters.VirtualScreenLeft,
            Top = SystemParameters.VirtualScreenTop,
            Width = SystemParameters.VirtualScreenWidth,
            Height = SystemParameters.VirtualScreenHeight,
        };

        var affinityApplied = false;
        win.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(win).Handle;
            var exStyle = GetWindowLong(hwnd, GwlExStyle);
            exStyle |= WsExTransparent | WsExLayered | WsExToolWindow | WsExNoActivate | WsExTopMost;
            SetWindowLong(hwnd, GwlExStyle, exStyle);
            SetLayeredWindowAttributes(hwnd, 0, 255, LwaAlpha);
            affinityApplied = SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture);
        };

        win.Show();

        if (!affinityApplied)
        {
            try { win.Close(); } catch { }
            IsEnabled = false;
            return new AgentPrivacyResultDto(
                false,
                false,
                "WDA_UNSUPPORTED",
                "当前 Windows 版本不支持采集排除（需要 Windows 10 2004 或 Windows 11），已取消黑屏以避免阻断远控画面。");
        }

        _curtainWindow = win;
        IsEnabled = true;
        InstallEscapeHook();
        return new AgentPrivacyResultDto(
            true,
            true,
            Message: "已开启被控端防窥黑屏（按 Ctrl+Alt+End 可在被控端本地紧急解除）。");
    }

    private AgentPrivacyResultDto DisableOnUiThread()
    {
        UninstallEscapeHook();
        if (_curtainWindow is not null)
        {
            try { _curtainWindow.Close(); } catch { }
            _curtainWindow = null;
        }
        IsEnabled = false;
        return new AgentPrivacyResultDto(true, false, Message: "已关闭被控端防窥黑屏。");
    }

    private static void SyncVirtualBounds(Window win)
    {
        win.Left = SystemParameters.VirtualScreenLeft;
        win.Top = SystemParameters.VirtualScreenTop;
        win.Width = SystemParameters.VirtualScreenWidth;
        win.Height = SystemParameters.VirtualScreenHeight;
    }

    private void InstallEscapeHook()
    {
        if (_hookHandle != IntPtr.Zero)
            return;

        _hookProc = EscapeHookProc;
        _hookHandle = SetWindowsHookEx(WhKeyboardLl, _hookProc, GetModuleHandle(null), 0);
    }

    private void UninstallEscapeHook()
    {
        if (_hookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_hookHandle);
            _hookHandle = IntPtr.Zero;
        }
        _hookProc = null;
    }

    private IntPtr EscapeHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && IsEnabled)
        {
            var msg = (uint)wParam.ToInt64();
            if (msg is WmKeyDown or WmSysKeyDown)
            {
                var vk = (uint)Marshal.ReadInt32(lParam);
                if (vk == VkEnd &&
                    (GetAsyncKeyState((int)VkControl) & 0x8000) != 0 &&
                    (GetAsyncKeyState((int)VkMenu) & 0x8000) != 0)
                {
                    _dispatcher.BeginInvoke(() => DisableOnUiThread());
                }
            }
        }

        return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint dwAffinity);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProcDelegate lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class InteractiveClipboardController
{
    public const int MaxTextLength = 1_000_000;
    public const int MaxImagePngBytes = 8 * 1024 * 1024;

    private readonly Dispatcher _dispatcher;
    private long _sequence;
    private string? _lastSha256;

    public InteractiveClipboardController(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
    }

    public AgentClipboardDto ReadClipboard() =>
        _dispatcher.Invoke(ReadOnUiThread);

    public AgentClipboardSetResponseDto WriteClipboard(AgentClipboardSetRequestDto req) =>
        _dispatcher.Invoke(() => WriteOnUiThread(req));

    public static string ComputePayloadSha256(string kind, byte[] payloadBytes)
    {
        var prefix = Encoding.ASCII.GetBytes(kind + ":");
        var combined = new byte[prefix.Length + payloadBytes.Length];
        Buffer.BlockCopy(prefix, 0, combined, 0, prefix.Length);
        Buffer.BlockCopy(payloadBytes, 0, combined, prefix.Length, payloadBytes.Length);
        return Convert.ToHexString(SHA256.HashData(combined)).ToLowerInvariant();
    }

    private AgentClipboardDto ReadOnUiThread()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Clipboard.ContainsText())
                {
                    var text = Clipboard.GetText();
                    if (!string.IsNullOrEmpty(text) && text.Length <= MaxTextLength)
                    {
                        var sha = ComputePayloadSha256("text", Encoding.UTF8.GetBytes(text));
                        if (!string.Equals(sha, _lastSha256, StringComparison.Ordinal))
                        {
                            _lastSha256 = sha;
                            _sequence++;
                        }
                        return new AgentClipboardDto(_sequence, "text", text, null, sha);
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
                        if (ms.Length <= MaxImagePngBytes)
                        {
                            var png = ms.ToArray();
                            var sha = ComputePayloadSha256("image", png);
                            if (!string.Equals(sha, _lastSha256, StringComparison.Ordinal))
                            {
                                _lastSha256 = sha;
                                _sequence++;
                            }
                            return new AgentClipboardDto(_sequence, "image", null, Convert.ToBase64String(png), sha);
                        }
                    }
                }

                return new AgentClipboardDto(_sequence, "none", null, null, _lastSha256);
            }
            catch (ExternalException)
            {
                Thread.Sleep(25);
            }
        }

        return new AgentClipboardDto(_sequence, "none", null, null, _lastSha256);
    }

    private AgentClipboardSetResponseDto WriteOnUiThread(AgentClipboardSetRequestDto req)
    {
        if (req.Kind == "text")
        {
            var text = req.Text ?? "";
            if (text.Length > MaxTextLength)
                return new AgentClipboardSetResponseDto(false, Error: "CLIPBOARD_TOO_LARGE", Message: "剪贴板文本超出 1 MB 上限。");

            var sha = ComputePayloadSha256("text", Encoding.UTF8.GetBytes(text));
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetText(text);
                    _lastSha256 = sha;
                    _sequence++;
                    return new AgentClipboardSetResponseDto(true, sha, Message: "剪贴板文本已同步。");
                }
                catch (ExternalException)
                {
                    Thread.Sleep(25);
                }
            }
            return new AgentClipboardSetResponseDto(false, Error: "CLIPBOARD_BUSY", Message: "剪贴板正被占用。");
        }

        if (req.Kind == "image")
        {
            if (string.IsNullOrWhiteSpace(req.ImageBase64))
                return new AgentClipboardSetResponseDto(false, Error: "EMPTY_IMAGE", Message: "图片数据为空。");

            byte[] pngBytes;
            try
            {
                pngBytes = Convert.FromBase64String(req.ImageBase64);
            }
            catch (FormatException)
            {
                return new AgentClipboardSetResponseDto(false, Error: "INVALID_IMAGE_BASE64", Message: "剪贴板图片编码无效。");
            }

            if (pngBytes.Length == 0 || pngBytes.Length > MaxImagePngBytes)
                return new AgentClipboardSetResponseDto(false, Error: "CLIPBOARD_TOO_LARGE", Message: "剪贴板图片超出 8 MB 上限。");

            BitmapImage bitmap;
            try
            {
                using var ms = new MemoryStream(pngBytes);
                bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.StreamSource = ms;
                bitmap.EndInit();
                bitmap.Freeze();
            }
            catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or FileFormatException)
            {
                return new AgentClipboardSetResponseDto(false, Error: "INVALID_PNG", Message: $"无法解析剪贴板图片：{ex.Message}");
            }

            var sha = ComputePayloadSha256("image", pngBytes);
            for (var attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    Clipboard.SetImage(bitmap);
                    _lastSha256 = sha;
                    _sequence++;
                    return new AgentClipboardSetResponseDto(true, sha, Message: "剪贴板图片已同步。");
                }
                catch (ExternalException)
                {
                    Thread.Sleep(25);
                }
            }
            return new AgentClipboardSetResponseDto(false, Error: "CLIPBOARD_BUSY", Message: "剪贴板正被占用。");
        }

        return new AgentClipboardSetResponseDto(false, Error: "UNSUPPORTED_KIND", Message: "不支持的剪贴板类型。");
    }
}

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
internal sealed class SessionBridgeServer : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly Dispatcher _dispatcher;
    private readonly DisplayModeController _display = new();
    private readonly PrivacyCurtainController _privacy;
    private readonly InteractiveClipboardController _clipboard;

    private CancellationTokenSource? _cts;
    private Task? _loopTask;
    private Mutex? _instanceMutex;
    private bool _disposed;

    public SessionBridgeServer(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _privacy = new PrivacyCurtainController(_dispatcher);
        _clipboard = new InteractiveClipboardController(_dispatcher);
    }

    public bool IsListening { get; private set; }

    public bool Start()
    {
        if (_disposed || _loopTask is not null)
            return IsListening;

        var mutex = new Mutex(initiallyOwned: true, @"Local\RemoteDeskSessionBridgeLock", out var createdNew);
        if (!createdNew)
        {
            mutex.Dispose();
            IsListening = false;
            return false;
        }

        _instanceMutex = mutex;
        _cts = new CancellationTokenSource();
        IsListening = true;
        _loopTask = Task.Run(() => RunAcceptLoopAsync(_cts.Token), _cts.Token);
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        IsListening = false;

        try { _cts?.Cancel(); } catch { }
        _privacy.Dispose();
        try { _display.RestoreMode(); } catch { }

        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); } catch { }
            _instanceMutex.Dispose();
            _instanceMutex = null;
        }

        _cts?.Dispose();
        _cts = null;
    }

    public static int RunStandaloneBridgeApp()
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        using var server = new SessionBridgeServer(app.Dispatcher);
        if (!server.Start())
            return 0;

        app.SessionEnding += (_, _) =>
        {
            server.Dispose();
            app.Shutdown();
        };

        return app.Run();
    }

    private async Task RunAcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                var security = CreatePipeSecurity();
                pipe = NamedPipeServerStreamAcl.Create(
                    AgentIpcConstants.SessionBridgePipeName,
                    PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous,
                    inBufferSize: 12 * 1024 * 1024,
                    outBufferSize: 12 * 1024 * 1024,
                    security);

                await pipe.WaitForConnectionAsync(ct).ConfigureAwait(false);
                var connected = pipe;
                pipe = null;
                _ = Task.Run(() => HandleClientAsync(connected, ct), ct);
            }
            catch (OperationCanceledException)
            {
                pipe?.Dispose();
                break;
            }
            catch
            {
                pipe?.Dispose();
                try { await Task.Delay(300, ct).ConfigureAwait(false); } catch { break; }
            }
        }
    }

    private async Task HandleClientAsync(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using (pipe)
        {
            try
            {
                using var reader = new StreamReader(pipe, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, leaveOpen: true);
                using var writer = new StreamWriter(pipe, new UTF8Encoding(false), leaveOpen: true) { AutoFlush = true };

                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                    return;

                var req = JsonSerializer.Deserialize<SessionBridgeRequest>(line, JsonOptions);
                var resp = req is null
                    ? new SessionBridgeResponse(false, Error: "请求为空。")
                    : ExecuteRequest(req);

                await writer.WriteLineAsync(JsonSerializer.Serialize(resp, JsonOptions)).ConfigureAwait(false);
            }
            catch
            {
            }
        }
    }

    private SessionBridgeResponse ExecuteRequest(SessionBridgeRequest req)
    {
        switch (req.Command)
        {
            case "clipboard_get":
            {
                var dto = _clipboard.ReadClipboard();
                return new SessionBridgeResponse(true, JsonSerializer.Serialize(dto, JsonOptions));
            }
            case "clipboard_set":
            {
                var payload = string.IsNullOrWhiteSpace(req.PayloadJson)
                    ? null
                    : JsonSerializer.Deserialize<AgentClipboardSetRequestDto>(req.PayloadJson, JsonOptions);
                if (payload is null)
                    return new SessionBridgeResponse(false, Error: "缺少剪贴板数据。");

                var res = _clipboard.WriteClipboard(payload);
                return new SessionBridgeResponse(res.Ok, JsonSerializer.Serialize(res, JsonOptions), res.Message);
            }
            case "display_get":
            {
                var state = _display.GetState();
                return new SessionBridgeResponse(true, JsonSerializer.Serialize(state, JsonOptions));
            }
            case "display_apply":
            {
                var payload = string.IsNullOrWhiteSpace(req.PayloadJson)
                    ? null
                    : JsonSerializer.Deserialize<AgentDisplayApplyRequestDto>(req.PayloadJson, JsonOptions);
                if (payload is null)
                    return new SessionBridgeResponse(false, Error: "缺少显示器参数。");

                var res = _display.ApplyMode(payload);
                if (res.Changed && _privacy.IsEnabled)
                    _privacy.SyncBounds();
                return new SessionBridgeResponse(res.Ok, JsonSerializer.Serialize(res, JsonOptions), res.Message);
            }
            case "display_restore":
            {
                var res = _display.RestoreMode();
                if (res.Changed && _privacy.IsEnabled)
                    _privacy.SyncBounds();
                return new SessionBridgeResponse(res.Ok, JsonSerializer.Serialize(res, JsonOptions), res.Message);
            }
            case "privacy_set":
            {
                var payload = string.IsNullOrWhiteSpace(req.PayloadJson)
                    ? null
                    : JsonSerializer.Deserialize<AgentPrivacyRequestDto>(req.PayloadJson, JsonOptions);
                if (payload is null)
                    return new SessionBridgeResponse(false, Error: "缺少防窥开关参数。");

                var res = _privacy.SetEnabled(payload.Enabled);
                return new SessionBridgeResponse(res.Ok, JsonSerializer.Serialize(res, JsonOptions), res.Message);
            }
            case "power_lock":
            {
                var ok = LockWorkStation();
                var res = new AgentPowerResultDto(ok, "lock", ok ? null : "LOCK_FAILED", ok ? "已锁定远程主机屏幕。" : "调用系统锁屏失败。");
                return new SessionBridgeResponse(ok, JsonSerializer.Serialize(res, JsonOptions), res.Message);
            }
            case "get_upload_dir":
            {
                var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "RemoteDesk");
                return new SessionBridgeResponse(true, JsonSerializer.Serialize(downloads, JsonOptions));
            }
            default:
                return new SessionBridgeResponse(false, Error: $"未知会话桥接命令：{req.Command}");
        }
    }

    private static PipeSecurity CreatePipeSecurity()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null),
            PipeAccessRights.FullControl,
            AccessControlType.Allow));
        security.AddAccessRule(new PipeAccessRule(
            new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null),
            PipeAccessRights.ReadWrite,
            AccessControlType.Allow));
        return security;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LockWorkStation();
}
