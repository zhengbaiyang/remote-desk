using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace RemoteDesk.Streaming;

internal sealed class StreamVideoHost : HwndHost
{
    private const string WindowClassName = "RemoteDeskNativeStreamVideoHost";
    private static readonly object ClassLock = new();
    private static ushort _atom;
    private static WndProcDelegate? _staticClassWndProc;

    private delegate IntPtr WndProcDelegate(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
    private delegate IntPtr HookProcDelegate(int nCode, IntPtr wParam, IntPtr lParam);

    private IntPtr _hwnd;
    private IntPtr _topLevelHwnd;
    private IntPtr _keyboardHook;
    private HookProcDelegate? _hookProc;
    private bool _rawMouseRegistered;
    private bool _hookCtrlDown;
    private bool _hookAltDown;
    private bool _isPointerLocked;
    private bool _suppressNextLButtonUp;

    private int _hostPixelW = 1280;
    private int _hostPixelH = 720;
    private int _streamW = 1920;
    private int _streamH = 1080;
    private ControlInputMode _inputMode = ControlInputMode.OfficeFreePointer;

    public IntPtr ChildHwnd => _hwnd;
    public NativeStreamSession? Session { get; set; }
    public ControlInputMode InputMode => _inputMode;
    public bool IsPointerLocked => _isPointerLocked;

    public event Action<ControlInputMode, bool, string?>? InputModeChanged;
    public event Action? FullscreenToggleRequested;
    public event Action? DisconnectRequested;
    public event Action? HelpToggleRequested;
    public event Action<string[]>? FilesDropped;

    public void UpdateStreamDimensions(int streamWidth, int streamHeight)
    {
        if (streamWidth > 0) _streamW = streamWidth;
        if (streamHeight > 0) _streamH = streamHeight;
    }

    public void SetInputMode(ControlInputMode mode, string? toastReason = null, bool lockImmediately = false)
    {
        if (mode == ControlInputMode.OfficeFreePointer)
        {
            var changed = _inputMode != mode || _isPointerLocked;
            _inputMode = mode;
            ExitGameLock();
            if (changed || !string.IsNullOrWhiteSpace(toastReason))
                InputModeChanged?.Invoke(_inputMode, false, toastReason);
            return;
        }

        var shouldLockNow = SessionInputPolicy.ShouldAutoLockOnModeSwitch(
            fromToolbarButton: !lockImmediately,
            IsCursorInsideVideoClient());

        if (_inputMode == mode && _isPointerLocked == shouldLockNow)
            return;

        _inputMode = mode;
        if (shouldLockNow)
            EnterGameLock();
        else
            ExitGameLock();

        InputModeChanged?.Invoke(_inputMode, _isPointerLocked, toastReason);
    }

    public void ReleaseCaptureForDeactivation()
    {
        ClipCursor(IntPtr.Zero);
        if (_isPointerLocked)
        {
            SetInputMode(ControlInputMode.OfficeFreePointer, "窗口失去焦点，已自动释放鼠标锁定");
            return;
        }

        SendReleaseAllModifiers();
    }

    public (int PixelWidth, int PixelHeight) GetCurrentPixelSize()
    {
        if (_hwnd != IntPtr.Zero && GetClientRect(_hwnd, out var rc))
        {
            var w = Math.Max(1, rc.Right - rc.Left);
            var h = Math.Max(1, rc.Bottom - rc.Top);
            return (w, h);
        }
        return (_hostPixelW, _hostPixelH);
    }

    protected override HandleRef BuildWindowCore(HandleRef hwndParent)
    {
        var hInstance = GetModuleHandle(null);
        EnsureClassRegistered(hInstance);

        var dpi = VisualTreeHelper.GetDpi(this);
        _hostPixelW = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        _hostPixelH = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));

        _hwnd = CreateWindowEx(
            0,
            WindowClassName,
            string.Empty,
            WS_CHILD | WS_VISIBLE | WS_CLIPSIBLINGS,
            0,
            0,
            _hostPixelW,
            _hostPixelH,
            hwndParent.Handle,
            IntPtr.Zero,
            hInstance,
            IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
            throw new InvalidOperationException($"创建原生视频渲染宿主窗口失败（Win32 错误 {Marshal.GetLastWin32Error()}）。");

        DragAcceptFiles(_hwnd, true);
        _topLevelHwnd = GetAncestor(_hwnd, GA_ROOT);
        return new HandleRef(this, _hwnd);
    }

    protected override void DestroyWindowCore(HandleRef hwnd)
    {
        ExitGameLock();
        if (hwnd.Handle != IntPtr.Zero)
            DestroyWindow(hwnd.Handle);
        _hwnd = IntPtr.Zero;
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);
        SyncHostSize();
    }

    protected override bool TabIntoCore(System.Windows.Input.TraversalRequest request)
    {
        if (_hwnd != IntPtr.Zero)
            SetFocus(_hwnd);
        return true;
    }

    protected override bool TranslateAcceleratorCore(ref MSG msg, System.Windows.Input.ModifierKeys modifiers)
    {
        if (_hwnd == IntPtr.Zero || GetFocus() != _hwnd)
            return base.TranslateAcceleratorCore(ref msg, modifiers);

        var m = (uint)msg.message;
        if (m is WM_KEYDOWN or WM_SYSKEYDOWN or WM_KEYUP or WM_SYSKEYUP)
        {
            if (HandleKeyMessage(m, msg.wParam, msg.lParam))
                return true;
        }

        return base.TranslateAcceleratorCore(ref msg, modifiers);
    }

    protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch ((uint)msg)
        {
            case WM_SIZE:
                _hostPixelW = Math.Max(1, (int)(lParam.ToInt64() & 0xFFFF));
                _hostPixelH = Math.Max(1, (int)((lParam.ToInt64() >> 16) & 0xFFFF));
                Session?.Resize(_hostPixelW, _hostPixelH);
                if (_isPointerLocked)
                    ApplyCursorClip();
                handled = true;
                return IntPtr.Zero;

            case WM_SETCURSOR:
                if (_isPointerLocked && (lParam.ToInt64() & 0xFFFF) == HTCLIENT)
                {
                    SetCursor(IntPtr.Zero);
                    handled = true;
                    return (IntPtr)1;
                }
                break;

            case WM_KILLFOCUS:
                if (_isPointerLocked)
                    Dispatcher.BeginInvoke(() => ReleaseCaptureForDeactivation());
                else
                    SendReleaseAllModifiers();
                break;

            case WM_MOUSEMOVE:
                if ((_inputMode == ControlInputMode.OfficeFreePointer || !_isPointerLocked) && Session is not null)
                {
                    var x = (short)(lParam.ToInt64() & 0xFFFF);
                    var y = (short)((lParam.ToInt64() >> 16) & 0xFFFF);
                    var (sx, sy) = StreamViewportMapper.MapClientToStream(x, y, _hostPixelW, _hostPixelH, _streamW, _streamH);
                    Session.SendMousePosition(sx, sy, (short)Math.Max(1, _streamW), (short)Math.Max(1, _streamH));
                }
                handled = true;
                return IntPtr.Zero;

            case WM_INPUT:
                if (_isPointerLocked && Session is not null)
                    ProcessRawMouseInput(lParam);
                handled = true;
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
                SetFocus(hwnd);
                if (_inputMode == ControlInputMode.GameRelativeLock && !_isPointerLocked)
                {
                    EnterGameLock();
                    _suppressNextLButtonUp = true;
                    InputModeChanged?.Invoke(_inputMode, true, "已锁定鼠标 · 按 Ctrl+Alt 释放 · Ctrl+Alt+Shift+Q 断开");
                    handled = true;
                    return IntPtr.Zero;
                }
                Session?.SendMouseButton(NativeStreamBridge.BtnActionPress, NativeStreamBridge.BtnLeft);
                handled = true;
                return IntPtr.Zero;

            case WM_LBUTTONUP:
                if (_suppressNextLButtonUp)
                {
                    _suppressNextLButtonUp = false;
                    handled = true;
                    return IntPtr.Zero;
                }
                Session?.SendMouseButton(NativeStreamBridge.BtnActionRelease, NativeStreamBridge.BtnLeft);
                handled = true;
                return IntPtr.Zero;

            case WM_RBUTTONDOWN:
                SetFocus(hwnd);
                Session?.SendMouseButton(NativeStreamBridge.BtnActionPress, NativeStreamBridge.BtnRight);
                handled = true;
                return IntPtr.Zero;

            case WM_RBUTTONUP:
                Session?.SendMouseButton(NativeStreamBridge.BtnActionRelease, NativeStreamBridge.BtnRight);
                handled = true;
                return IntPtr.Zero;

            case WM_MBUTTONDOWN:
                SetFocus(hwnd);
                Session?.SendMouseButton(NativeStreamBridge.BtnActionPress, NativeStreamBridge.BtnMiddle);
                handled = true;
                return IntPtr.Zero;

            case WM_MBUTTONUP:
                Session?.SendMouseButton(NativeStreamBridge.BtnActionRelease, NativeStreamBridge.BtnMiddle);
                handled = true;
                return IntPtr.Zero;

            case WM_XBUTTONDOWN:
            case WM_XBUTTONUP:
                if ((uint)msg == WM_XBUTTONDOWN)
                    SetFocus(hwnd);
                var xBtn = ((wParam.ToInt64() >> 16) & 0xFFFF) == 1 ? NativeStreamBridge.BtnX1 : NativeStreamBridge.BtnX2;
                var xAction = (uint)msg == WM_XBUTTONDOWN ? NativeStreamBridge.BtnActionPress : NativeStreamBridge.BtnActionRelease;
                Session?.SendMouseButton(xAction, xBtn);
                handled = true;
                return (IntPtr)1;

            case WM_MOUSEWHEEL:
                var wheelDelta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                Session?.SendScrollV(wheelDelta);
                handled = true;
                return IntPtr.Zero;

            case WM_MOUSEHWHEEL:
                var hWheelDelta = (short)((wParam.ToInt64() >> 16) & 0xFFFF);
                Session?.SendScrollH(hWheelDelta);
                handled = true;
                return IntPtr.Zero;

            case WM_DROPFILES:
                ProcessDroppedFiles(wParam);
                handled = true;
                return IntPtr.Zero;

            case WM_KEYDOWN:
            case WM_SYSKEYDOWN:
            case WM_KEYUP:
            case WM_SYSKEYUP:
                if (HandleKeyMessage((uint)msg, wParam, lParam))
                {
                    handled = true;
                    return IntPtr.Zero;
                }
                break;
        }

        return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
    }

    private void ProcessDroppedFiles(IntPtr hDrop)
    {
        var files = new List<string>();
        try
        {
            var count = DragQueryFileW(hDrop, uint.MaxValue, null, 0);
            for (uint i = 0; i < count; i++)
            {
                var len = DragQueryFileW(hDrop, i, null, 0);
                if (len == 0 || len > 4096)
                    continue;
                var sb = new StringBuilder((int)len + 1);
                if (DragQueryFileW(hDrop, i, sb, (uint)sb.Capacity) > 0)
                {
                    var path = sb.ToString();
                    if (File.Exists(path))
                        files.Add(path);
                }
            }
        }
        finally
        {
            DragFinish(hDrop);
        }

        if (files.Count > 0)
        {
            var snapshot = files.ToArray();
            Dispatcher.BeginInvoke(() => FilesDropped?.Invoke(snapshot));
        }
    }

    private void SyncHostSize()
    {
        if (_hwnd == IntPtr.Zero)
            return;
        var dpi = VisualTreeHelper.GetDpi(this);
        var pw = Math.Max(1, (int)Math.Round(ActualWidth * dpi.DpiScaleX));
        var ph = Math.Max(1, (int)Math.Round(ActualHeight * dpi.DpiScaleY));
        _hostPixelW = pw;
        _hostPixelH = ph;
        Session?.Resize(pw, ph);
        if (_isPointerLocked)
            ApplyCursorClip();
    }

    public bool TryHandleWindowShortcut(System.Windows.Input.Key key, System.Windows.Input.ModifierKeys modifiers)
    {
        var vk = System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0)
            return false;

        var ctrlDown = (modifiers & System.Windows.Input.ModifierKeys.Control) != 0;
        var altDown = (modifiers & System.Windows.Input.ModifierKeys.Alt) != 0;
        var shiftDown = (modifiers & System.Windows.Input.ModifierKeys.Shift) != 0;

        var action = SessionInputPolicy.ResolveShortcut(vk, isKeyDown: true, ctrlDown, altDown, shiftDown, _isPointerLocked);
        if (action == SessionShortcutAction.None)
            return false;

        ExecuteShortcutAction(action, synchronous: true);
        return true;
    }

    private bool HandleKeyMessage(uint msg, IntPtr wParam, IntPtr lParam)
    {
        var vk = (short)(wParam.ToInt64() & 0xFFFF);
        var isDown = msg is WM_KEYDOWN or WM_SYSKEYDOWN;
        var isExtended = ((lParam.ToInt64() >> 24) & 1) != 0;

        var ctrlDown = (GetKeyState(VK_CONTROL) & 0x8000) != 0;
        var altDown = (GetKeyState(VK_MENU) & 0x8000) != 0;
        var shiftDown = (GetKeyState(VK_SHIFT) & 0x8000) != 0;

        var action = SessionInputPolicy.ResolveShortcut(vk, isDown, ctrlDown, altDown, shiftDown, _isPointerLocked);
        if (action != SessionShortcutAction.None)
        {
            if (action == SessionShortcutAction.ReleasePointerLock)
                ClipCursor(IntPtr.Zero);
            Dispatcher.BeginInvoke(() => ExecuteShortcutAction(action, synchronous: true));
            return true;
        }

        if (!isDown && vk == VK_F11 && !ctrlDown && !altDown && !shiftDown)
            return true;

        SendKeyToRemote(vk, isDown, isExtended);
        return true;
    }

    private void ExecuteShortcutAction(SessionShortcutAction action, bool synchronous)
    {
        switch (action)
        {
            case SessionShortcutAction.ReleasePointerLock:
                ClipCursor(IntPtr.Zero);
                if (synchronous)
                    SetInputMode(ControlInputMode.OfficeFreePointer, "已按 Ctrl+Alt 释放鼠标，可自由操作界面");
                else
                    Dispatcher.BeginInvoke(() => SetInputMode(ControlInputMode.OfficeFreePointer, "已按 Ctrl+Alt 释放鼠标，可自由操作界面"));
                break;

            case SessionShortcutAction.ToggleInputMode:
                void ToggleMode()
                {
                    var next = _inputMode == ControlInputMode.OfficeFreePointer
                        ? ControlInputMode.GameRelativeLock
                        : ControlInputMode.OfficeFreePointer;
                    var willLock = next == ControlInputMode.GameRelativeLock && IsCursorInsideVideoClient();
                    var reason = next == ControlInputMode.GameRelativeLock
                        ? (willLock
                            ? "已进入游戏锁定模式 · 按 Ctrl+Alt 释放光标"
                            : "游戏锁定已就绪：点击画面锁鼠，按 Ctrl+Alt 随时释放")
                        : "已切换回办公自由指针模式";
                    SetInputMode(next, reason, lockImmediately: true);
                }

                if (synchronous) ToggleMode();
                else Dispatcher.BeginInvoke(ToggleMode);
                break;

            case SessionShortcutAction.ToggleFullscreen:
                if (synchronous) FullscreenToggleRequested?.Invoke();
                else Dispatcher.BeginInvoke(() => FullscreenToggleRequested?.Invoke());
                break;

            case SessionShortcutAction.DisconnectSession:
                void Disconnect()
                {
                    SetInputMode(ControlInputMode.OfficeFreePointer, null);
                    DisconnectRequested?.Invoke();
                }

                if (synchronous) Disconnect();
                else Dispatcher.BeginInvoke(Disconnect);
                break;

            case SessionShortcutAction.ToggleHelpOverlay:
                void ToggleHelp()
                {
                    SetInputMode(ControlInputMode.OfficeFreePointer, null);
                    HelpToggleRequested?.Invoke();
                }

                if (synchronous) ToggleHelp();
                else Dispatcher.BeginInvoke(ToggleHelp);
                break;
        }
    }

    private void SendReleaseAllModifiers()
    {
        if (Session is null)
            return;

        Session.SendKeyEvent(VK_CONTROL, NativeStreamBridge.KeyUp, 0);
        Session.SendKeyEvent(VK_MENU, NativeStreamBridge.KeyUp, 0);
        Session.SendKeyEvent(VK_SHIFT, NativeStreamBridge.KeyUp, 0);
        Session.SendKeyEvent(VK_LWIN, NativeStreamBridge.KeyUp, NativeStreamBridge.ModExtended);
    }

    private void SendKeyToRemote(short vk, bool isDown, bool isExtended)
    {
        if (Session is null)
            return;

        sbyte mods = 0;
        if ((GetKeyState(VK_SHIFT) & 0x8000) != 0) mods |= NativeStreamBridge.ModShift;
        if ((GetKeyState(VK_CONTROL) & 0x8000) != 0) mods |= NativeStreamBridge.ModCtrl;
        if ((GetKeyState(VK_MENU) & 0x8000) != 0) mods |= NativeStreamBridge.ModAlt;
        if (((GetKeyState(VK_LWIN) & 0x8000) != 0) || ((GetKeyState(VK_RWIN) & 0x8000) != 0))
            mods |= NativeStreamBridge.ModMeta;
        if (isExtended) mods |= NativeStreamBridge.ModExtended;

        Session.SendKeyEvent(vk, isDown ? NativeStreamBridge.KeyDown : NativeStreamBridge.KeyUp, mods);
    }

    private void ProcessRawMouseInput(IntPtr hRawInput)
    {
        uint size = 0;
        var headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        if (GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize) != 0 || size == 0)
            return;

        if (size < headerSize + Marshal.SizeOf<RAWMOUSE>())
            return;

        var buf = Marshal.AllocHGlobal((int)size);
        try
        {
            if (GetRawInputData(hRawInput, RID_INPUT, buf, ref size, headerSize) == uint.MaxValue)
                return;

            var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buf);
            if (header.dwType != RIM_TYPEMOUSE)
                return;

            var mouse = Marshal.PtrToStructure<RAWMOUSE>(IntPtr.Add(buf, (int)headerSize));
            if ((mouse.usFlags & MOUSE_MOVE_ABSOLUTE) == 0 && (mouse.lLastX != 0 || mouse.lLastY != 0))
            {
                var dx = (short)Math.Clamp(mouse.lLastX, short.MinValue, short.MaxValue);
                var dy = (short)Math.Clamp(mouse.lLastY, short.MinValue, short.MaxValue);
                Session?.SendMouseMove(dx, dy);
                RecenterCursorInHost();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    private bool IsCursorInsideVideoClient()
    {
        if (_hwnd == IntPtr.Zero || !GetCursorPos(out var pt) || !GetClientRect(_hwnd, out var rc))
            return false;

        var topLeft = new POINT { X = rc.Left, Y = rc.Top };
        var bottomRight = new POINT { X = rc.Right, Y = rc.Bottom };
        if (!ClientToScreen(_hwnd, ref topLeft) || !ClientToScreen(_hwnd, ref bottomRight))
            return false;

        return pt.X >= topLeft.X && pt.X < bottomRight.X && pt.Y >= topLeft.Y && pt.Y < bottomRight.Y;
    }

    private void RecenterCursorInHost()
    {
        if (!_isPointerLocked || _hwnd == IntPtr.Zero || !GetClientRect(_hwnd, out var rc))
            return;

        var center = new POINT
        {
            X = (rc.Left + rc.Right) / 2,
            Y = (rc.Top + rc.Bottom) / 2,
        };
        if (ClientToScreen(_hwnd, ref center))
            SetCursorPos(center.X, center.Y);
    }

    private void EnterGameLock()
    {
        if (_hwnd == IntPtr.Zero)
            return;

        _isPointerLocked = true;
        _topLevelHwnd = GetAncestor(_hwnd, GA_ROOT);
        SetFocus(_hwnd);
        ApplyCursorClip();
        RecenterCursorInHost();
        SetCursor(IntPtr.Zero);

        if (!_rawMouseRegistered)
        {
            var rid = new RAWINPUTDEVICE[]
            {
                new()
                {
                    usUsagePage = 0x01,
                    usUsage = 0x02,
                    dwFlags = 0,
                    hwndTarget = _hwnd,
                },
            };
            _rawMouseRegistered = RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
        }

        if (_keyboardHook == IntPtr.Zero)
        {
            _hookCtrlDown = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            _hookAltDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            _hookProc = LowLevelKeyboardHookProc;
            _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _hookProc, GetModuleHandle(null), 0);
        }
    }

    private void ExitGameLock()
    {
        var wasLocked = _isPointerLocked;
        _isPointerLocked = false;
        _suppressNextLButtonUp = false;
        ClipCursor(IntPtr.Zero);
        if (_hwnd != IntPtr.Zero)
            SetCursor(LoadCursor(IntPtr.Zero, IDC_ARROW));

        if (_rawMouseRegistered)
        {
            var rid = new RAWINPUTDEVICE[]
            {
                new()
                {
                    usUsagePage = 0x01,
                    usUsage = 0x02,
                    dwFlags = RIDEV_REMOVE,
                    hwndTarget = IntPtr.Zero,
                },
            };
            RegisterRawInputDevices(rid, (uint)rid.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            _rawMouseRegistered = false;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }
        _hookProc = null;
        _hookCtrlDown = false;
        _hookAltDown = false;

        if (wasLocked)
            SendReleaseAllModifiers();
    }

    private void ApplyCursorClip()
    {
        if (_hwnd == IntPtr.Zero || !GetClientRect(_hwnd, out var rc))
            return;

        var topLeft = new POINT { X = rc.Left, Y = rc.Top };
        var bottomRight = new POINT { X = rc.Right, Y = rc.Bottom };
        if (ClientToScreen(_hwnd, ref topLeft) && ClientToScreen(_hwnd, ref bottomRight))
        {
            var screenRect = new RECT
            {
                Left = topLeft.X,
                Top = topLeft.Y,
                Right = Math.Max(topLeft.X + 1, bottomRight.X),
                Bottom = Math.Max(topLeft.Y + 1, bottomRight.Y),
            };
            ClipCursor(ref screenRect);
        }
    }

    private IntPtr LowLevelKeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _isPointerLocked)
        {
            var msg = (uint)wParam.ToInt64();
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            var vk = (short)kb.vkCode;
            var isUp = msg is WM_KEYUP or WM_SYSKEYUP;
            var isExtended = (kb.flags & LLKHF_EXTENDED) != 0;

            if (vk is VK_CONTROL or VK_LCONTROL or VK_RCONTROL)
                _hookCtrlDown = !isUp;
            if (vk is VK_MENU or VK_LMENU or VK_RMENU)
                _hookAltDown = !isUp;

            if (_topLevelHwnd != IntPtr.Zero && GetForegroundWindow() != _topLevelHwnd)
            {
                ClipCursor(IntPtr.Zero);
                Dispatcher.BeginInvoke(() => ReleaseCaptureForDeactivation());
                return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            var ctrlDown = _hookCtrlDown || (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;
            var altDown = _hookAltDown || (GetAsyncKeyState(VK_MENU) & 0x8000) != 0 || (kb.flags & LLKHF_ALTDOWN) != 0;
            var shiftDown = (GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

            var action = SessionInputPolicy.ResolveShortcut(vk, !isUp, ctrlDown, altDown, shiftDown, isPointerLocked: true);
            if (action != SessionShortcutAction.None)
            {
                ClipCursor(IntPtr.Zero);
                ExecuteShortcutAction(action, synchronous: false);
                return (IntPtr)1;
            }

            if (SessionInputPolicy.ShouldForwardSystemKeyToRemote(vk, altDown, _isPointerLocked))
            {
                SendKeyToRemote(vk, !isUp, isExtended);
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static void EnsureClassRegistered(IntPtr hInstance)
    {
        lock (ClassLock)
        {
            if (_atom != 0)
                return;

            _staticClassWndProc = DefWindowProc;
            var wc = new WNDCLASSEX
            {
                cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
                style = CS_OWNDC,
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_staticClassWndProc),
                cbClsExtra = 0,
                cbWndExtra = 0,
                hInstance = hInstance,
                hIcon = IntPtr.Zero,
                hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
                hbrBackground = GetStockObject(BLACK_BRUSH),
                lpszMenuName = null,
                lpszClassName = WindowClassName,
                hIconSm = IntPtr.Zero,
            };

            _atom = RegisterClassEx(ref wc);
        }
    }

    #region Win32 Interop

    private const int WS_CHILD = 0x40000000;
    private const int WS_VISIBLE = 0x10000000;
    private const int WS_CLIPSIBLINGS = 0x04000000;
    private const uint CS_OWNDC = 0x0020;
    private const int BLACK_BRUSH = 4;
    private const int IDC_ARROW = 32512;
    private const int HTCLIENT = 1;
    private const uint GA_ROOT = 2;

    private const uint WM_SIZE = 0x0005;
    private const uint WM_KILLFOCUS = 0x0008;
    private const uint WM_SETCURSOR = 0x0020;
    private const uint WM_INPUT = 0x00FF;
    private const uint WM_KEYDOWN = 0x0100;
    private const uint WM_KEYUP = 0x0101;
    private const uint WM_SYSKEYDOWN = 0x0104;
    private const uint WM_SYSKEYUP = 0x0105;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_LBUTTONDOWN = 0x0201;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_RBUTTONDOWN = 0x0204;
    private const uint WM_RBUTTONUP = 0x0205;
    private const uint WM_MBUTTONDOWN = 0x0207;
    private const uint WM_MBUTTONUP = 0x0208;
    private const uint WM_MOUSEWHEEL = 0x020A;
    private const uint WM_XBUTTONDOWN = 0x020B;
    private const uint WM_XBUTTONUP = 0x020C;
    private const uint WM_MOUSEHWHEEL = 0x020E;
    private const uint WM_DROPFILES = 0x0233;

    private const int WH_KEYBOARD_LL = 13;
    private const uint LLKHF_EXTENDED = 0x01;
    private const uint LLKHF_ALTDOWN = 0x20;
    private const uint RID_INPUT = 0x10000003;
    private const uint RIDEV_REMOVE = 0x00000001;
    private const uint RIM_TYPEMOUSE = 0;
    private const ushort MOUSE_MOVE_ABSOLUTE = 0x01;

    private const int VK_TAB = 0x09;
    private const int VK_SHIFT = 0x10;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_ESCAPE = 0x1B;
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_APPS = 0x5D;
    private const int VK_F11 = 0x7A;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_LMENU = 0xA4;
    private const int VK_RMENU = 0xA5;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct RAWMOUSE
    {
        [FieldOffset(0)] public ushort usFlags;
        [FieldOffset(4)] public ushort usButtonFlags;
        [FieldOffset(6)] public ushort usButtonData;
        [FieldOffset(8)] public uint ulRawButtons;
        [FieldOffset(12)] public int lLastX;
        [FieldOffset(16)] public int lLastY;
        [FieldOffset(20)] public uint ulExtraInformation;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        string lpClassName,
        string lpWindowName,
        int dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetFocus();

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(ref RECT lpRect);

    [DllImport("user32.dll")]
    private static extern bool ClipCursor(IntPtr lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int X, int Y);

    [DllImport("user32.dll")]
    private static extern IntPtr SetCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadCursor(IntPtr hInstance, int lpCursorName);

    [DllImport("user32.dll")]
    private static extern short GetKeyState(int nVirtKey);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] pRawInputDevices, uint uiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    private static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand, IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProcDelegate lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")]
    private static extern IntPtr GetStockObject(int fnObject);

    [DllImport("shell32.dll")]
    private static extern void DragAcceptFiles(IntPtr hWnd, [MarshalAs(UnmanagedType.Bool)] bool fAccept);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFileW(IntPtr hDrop, uint iFile, StringBuilder? lpszFile, uint cch);

    [DllImport("shell32.dll")]
    private static extern void DragFinish(IntPtr hDrop);

    #endregion
}
