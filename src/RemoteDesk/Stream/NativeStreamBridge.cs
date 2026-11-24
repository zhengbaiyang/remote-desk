using System.IO;
using System.Runtime.InteropServices;

namespace RemoteDesk.Streaming;

internal enum ControlInputMode
{
    OfficeFreePointer,
    GameRelativeLock,
}

[StructLayout(LayoutKind.Sequential)]
internal struct RdStreamStats
{
    public double ReceivedFps;
    public double RenderedFps;
    public double DecodeTimeMs;
    public uint RttMs;
    public uint RttVarianceMs;
    public uint BitrateKbps;
    public uint DroppedFrames;
    public int VideoWidth;
    public int VideoHeight;
    public int ActiveCodec;
    public int HwAccelerated;

    public string CodecDisplayName => ActiveCodec switch
    {
        StreamViewportMapper.NativePrefCodecAv1 => "AV1",
        StreamViewportMapper.NativePrefCodecHevc => "HEVC",
        _ => "H.264",
    };
}

internal static class NativeStreamBridge
{
    public const string DllName = "RemoteDesk.StreamCore.dll";

    public const byte BtnActionPress = 0x07;
    public const byte BtnActionRelease = 0x08;
    public const int BtnLeft = 0x01;
    public const int BtnMiddle = 0x02;
    public const int BtnRight = 0x03;
    public const int BtnX1 = 0x04;
    public const int BtnX2 = 0x05;

    public const sbyte KeyDown = 0x03;
    public const sbyte KeyUp = 0x04;

    public const sbyte ModShift = 0x01;
    public const sbyte ModCtrl = 0x02;
    public const sbyte ModAlt = 0x04;
    public const sbyte ModMeta = 0x08;
    public const sbyte ModExtended = 0x10;

    public const int HotkeyCtrlAltDel = 1;
    public const int HotkeyWin = 2;
    public const int HotkeyWinD = 3;
    public const int HotkeyAltTab = 4;
    public const int HotkeyCtrlShiftEsc = 5;
    public const int HotkeyWinL = 6;

    [StructLayout(LayoutKind.Sequential)]
    internal struct RdStreamConfig
    {
        public IntPtr Hwnd;
        public int ViewportWidth;
        public int ViewportHeight;

        public IntPtr Host;
        public IntPtr AppVersion;
        public IntPtr GfeVersion;
        public IntPtr RtspSessionUrl;

        public int Width;
        public int Height;
        public int Fps;
        public int BitrateKbps;
        public int ServerCodecModeSupport;
        public int PreferredCodec;
        public int HardwareDecode;
        public int StreamingRemotely;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteInputAesKey;

        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 16)]
        public byte[] RemoteInputAesIv;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct RdStreamCallbacks
    {
        public IntPtr OnStage;
        public IntPtr OnConnected;
        public IntPtr OnDisconnected;
        public IntPtr OnConnectionStatus;
        public IntPtr OnStats;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void StageCallback(int stage, IntPtr stageName);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ConnectedCallback();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void DisconnectedCallback(int errorCode, IntPtr message);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void ConnectionStatusCallback(int isPoor);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    internal delegate void StatsCallback(IntPtr statsPtr);

    private static readonly Lazy<bool> Availability = new(ProbeNativeDll);

    public static bool IsAvailable() => Availability.Value;

    public static uint QuerySupportedCodecs()
    {
        if (!IsAvailable())
            return StreamViewportMapper.CodecMaskH264;

        try
        {
            return rd_stream_query_supported_codecs();
        }
        catch
        {
            return StreamViewportMapper.CodecMaskH264;
        }
    }

    private static bool ProbeNativeDll()
    {
        try
        {
            var candidate = Path.Combine(AppContext.BaseDirectory, DllName);
            if (!File.Exists(candidate))
                return false;

            if (NativeLibrary.TryLoad(candidate, out var handle))
            {
                NativeLibrary.Free(handle);
                return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern uint rd_stream_query_supported_codecs();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rd_stream_start(ref RdStreamConfig config, ref RdStreamCallbacks callbacks);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rd_stream_stop();

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rd_stream_resize(int width, int height);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern void rd_stream_set_audio_muted(int muted);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rd_input_mouse_position(short x, short y, short refWidth, short refHeight);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rd_input_mouse_move(short dx, short dy);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rd_input_mouse_button(byte action, int button);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rd_input_scroll_v(short highResDelta);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rd_input_scroll_h(short highResDelta);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rd_input_key_event(short vkCode, sbyte keyAction, sbyte modifiers);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rd_input_utf8_text(byte[] text, uint length);

    [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
    internal static extern int rd_input_send_hotkey(int hotkeyId);
}

internal sealed class NativeStreamSession : IDisposable
{
    private readonly object _stateLock = new();
    private bool _started;
    private bool _disposed;

    // Pinned delegate references to prevent GC collection while native threads invoke them.
    private NativeStreamBridge.StageCallback? _stageCb;
    private NativeStreamBridge.ConnectedCallback? _connectedCb;
    private NativeStreamBridge.DisconnectedCallback? _disconnectedCb;
    private NativeStreamBridge.ConnectionStatusCallback? _statusCb;
    private NativeStreamBridge.StatsCallback? _statsCb;

    public event Action<int, string>? StageChanged;
    public event Action? Connected;
    public event Action<int, string>? Disconnected;
    public event Action<bool>? ConnectionQualityChanged;
    public event Action<RdStreamStats>? StatsUpdated;

    public void Start(
        IntPtr hwnd,
        int viewportWidth,
        int viewportHeight,
        string host,
        GameStreamServerInfo serverInfo,
        GameStreamLaunchResult launch,
        StreamOptions options,
        int effectiveCodecNativeId,
        bool isRemoteNetwork)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_stateLock)
        {
            if (_started)
                throw new InvalidOperationException("串流会话已经处于启动状态。");
            _started = true;
        }

        _stageCb = (stage, namePtr) =>
        {
            var stageName = Marshal.PtrToStringUTF8(namePtr) ?? $"Stage {stage}";
            StageChanged?.Invoke(stage, stageName);
        };
        _connectedCb = () => Connected?.Invoke();
        _disconnectedCb = (errorCode, msgPtr) =>
        {
            var msg = Marshal.PtrToStringUTF8(msgPtr) ?? "连接已断开";
            Disconnected?.Invoke(errorCode, msg);
        };
        _statusCb = isPoor => ConnectionQualityChanged?.Invoke(isPoor != 0);
        _statsCb = statsPtr =>
        {
            if (statsPtr == IntPtr.Zero)
                return;
            var stats = Marshal.PtrToStructure<RdStreamStats>(statsPtr);
            StatsUpdated?.Invoke(stats);
        };

        var callbacks = new NativeStreamBridge.RdStreamCallbacks
        {
            OnStage = Marshal.GetFunctionPointerForDelegate(_stageCb),
            OnConnected = Marshal.GetFunctionPointerForDelegate(_connectedCb),
            OnDisconnected = Marshal.GetFunctionPointerForDelegate(_disconnectedCb),
            OnConnectionStatus = Marshal.GetFunctionPointerForDelegate(_statusCb),
            OnStats = Marshal.GetFunctionPointerForDelegate(_statsCb),
        };

        var hostPtr = Marshal.StringToCoTaskMemUTF8(host);
        var appVerPtr = Marshal.StringToCoTaskMemUTF8(serverInfo.AppVersion);
        var gfeVerPtr = Marshal.StringToCoTaskMemUTF8(serverInfo.GfeVersion);
        var rtspPtr = Marshal.StringToCoTaskMemUTF8(launch.SessionUrl);

        try
        {
            var key = new byte[16];
            var iv = new byte[16];
            Buffer.BlockCopy(launch.RemoteInputAesKey, 0, key, 0, Math.Min(16, launch.RemoteInputAesKey.Length));
            Buffer.BlockCopy(launch.RemoteInputAesIv, 0, iv, 0, Math.Min(16, launch.RemoteInputAesIv.Length));

            var config = new NativeStreamBridge.RdStreamConfig
            {
                Hwnd = hwnd,
                ViewportWidth = Math.Max(1, viewportWidth),
                ViewportHeight = Math.Max(1, viewportHeight),
                Host = hostPtr,
                AppVersion = appVerPtr,
                GfeVersion = gfeVerPtr,
                RtspSessionUrl = rtspPtr,
                Width = options.Width,
                Height = options.Height,
                Fps = options.Fps,
                BitrateKbps = options.BitrateMbps * 1000,
                ServerCodecModeSupport = serverInfo.ServerCodecModeSupport,
                PreferredCodec = effectiveCodecNativeId,
                HardwareDecode = options.HardwareDecoding ? 1 : 0,
                StreamingRemotely = isRemoteNetwork ? 1 : 0,
                RemoteInputAesKey = key,
                RemoteInputAesIv = iv,
            };

            var rc = NativeStreamBridge.rd_stream_start(ref config, ref callbacks);
            if (rc != 0)
            {
                lock (_stateLock)
                    _started = false;
                throw new InvalidOperationException($"启动原生串流核心失败（错误码 {rc}）。");
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(hostPtr);
            Marshal.FreeCoTaskMem(appVerPtr);
            Marshal.FreeCoTaskMem(gfeVerPtr);
            Marshal.FreeCoTaskMem(rtspPtr);
        }
    }

    public void Resize(int pixelWidth, int pixelHeight)
    {
        if (_disposed || !NativeStreamBridge.IsAvailable())
            return;
        if (pixelWidth > 0 && pixelHeight > 0)
            NativeStreamBridge.rd_stream_resize(pixelWidth, pixelHeight);
    }

    public void SetAudioMuted(bool muted)
    {
        if (_disposed || !NativeStreamBridge.IsAvailable())
            return;
        NativeStreamBridge.rd_stream_set_audio_muted(muted ? 1 : 0);
    }

    public void SendMousePosition(short x, short y, short refWidth, short refHeight)
    {
        if (!_disposed && _started)
            NativeStreamBridge.rd_input_mouse_position(x, y, refWidth, refHeight);
    }

    public void SendMouseMove(short dx, short dy)
    {
        if (!_disposed && _started)
            NativeStreamBridge.rd_input_mouse_move(dx, dy);
    }

    public void SendMouseButton(byte action, int button)
    {
        if (!_disposed && _started)
            NativeStreamBridge.rd_input_mouse_button(action, button);
    }

    public void SendScrollV(short highResDelta)
    {
        if (!_disposed && _started)
            NativeStreamBridge.rd_input_scroll_v(highResDelta);
    }

    public void SendScrollH(short highResDelta)
    {
        if (!_disposed && _started)
            NativeStreamBridge.rd_input_scroll_h(highResDelta);
    }

    public void SendKeyEvent(short vkCode, sbyte keyAction, sbyte modifiers)
    {
        if (!_disposed && _started)
            NativeStreamBridge.rd_input_key_event(vkCode, keyAction, modifiers);
    }

    public void SendUtf8Text(string text)
    {
        if (_disposed || !_started || string.IsNullOrEmpty(text))
            return;
        var bytes = System.Text.Encoding.UTF8.GetBytes(text);
        NativeStreamBridge.rd_input_utf8_text(bytes, (uint)bytes.Length);
    }

    public void SendHotkey(int hotkeyId)
    {
        if (!_disposed && _started)
            NativeStreamBridge.rd_input_send_hotkey(hotkeyId);
    }

    /// <summary>
    /// Stops native streaming threads. Never call synchronously from inside a native callback.
    /// </summary>
    public void Stop()
    {
        lock (_stateLock)
        {
            if (!_started)
                return;
            _started = false;
        }

        if (NativeStreamBridge.IsAvailable())
            NativeStreamBridge.rd_stream_stop();
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        Stop();
        _disposed = true;
    }
}
