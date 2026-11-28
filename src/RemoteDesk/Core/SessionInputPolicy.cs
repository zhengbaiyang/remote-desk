namespace RemoteDesk;

internal enum SessionShortcutAction
{
    None,
    ReleasePointerLock,
    ToggleInputMode,
    ToggleFullscreen,
    DisconnectSession,
    ToggleHelpOverlay,
}

internal static class SessionInputPolicy
{
    public const int VkTab = 0x09;
    public const int VkShift = 0x10;
    public const int VkControl = 0x11;
    public const int VkMenu = 0x12;
    public const int VkPause = 0x13;
    public const int VkEscape = 0x1B;
    public const int VkLWin = 0x5B;
    public const int VkRWin = 0x5C;
    public const int VkApps = 0x5D;
    public const int VkF11 = 0x7A;
    public const int VkLShift = 0xA0;
    public const int VkRShift = 0xA1;
    public const int VkLControl = 0xA2;
    public const int VkRControl = 0xA3;
    public const int VkLMenu = 0xA4;
    public const int VkRMenu = 0xA5;
    public const int VkProcessKey = 0xE5;
    public const int MoonlightKeyPrefix = 0x8000;

    public const ushort RawKeyBreakFlag = 0x01;
    public const ushort RawKeyE0Flag = 0x02;
    public const ushort RawKeyE1Flag = 0x04;
    public const ushort RawKeyboardOverrunCode = 0xFF;

    public const byte ModMaskShift = 0x01;
    public const byte ModMaskCtrl = 0x02;
    public const byte ModMaskAlt = 0x04;
    public const byte ModMaskMeta = 0x08;
    public const byte ModMaskExtended = 0x10;

    public static short NormalizeVirtualKey(int vkCode, uint scanCode, bool isExtended)
    {
        if (vkCode == VkShift)
            return (short)(scanCode == 0x36 ? VkRShift : VkLShift);
        if (vkCode == VkControl)
            return (short)(isExtended ? VkRControl : VkLControl);
        if (vkCode == VkMenu)
            return (short)(isExtended ? VkRMenu : VkLMenu);
        return (short)(vkCode & 0xFF);
    }

    public static bool TryNormalizeRawKeyboardEvent(
        ushort vKey,
        ushort makeCode,
        ushort flags,
        out short normalizedVk,
        out bool isDown,
        out bool isExtended)
    {
        if (vKey == 0 || vKey == RawKeyboardOverrunCode || makeCode == RawKeyboardOverrunCode)
        {
            normalizedVk = 0;
            isDown = false;
            isExtended = false;
            return false;
        }

        isDown = (flags & RawKeyBreakFlag) == 0;
        var isE0 = (flags & RawKeyE0Flag) != 0;
        var isE1 = (flags & RawKeyE1Flag) != 0;

        if (vKey == VkPause || (isE1 && makeCode == 0x1D))
        {
            normalizedVk = VkPause;
            isExtended = false;
            return true;
        }

        normalizedVk = NormalizeVirtualKey(vKey, makeCode, isE0);
        isExtended = vKey != VkShift && isE0;
        return true;
    }

    public static bool ShouldIgnoreKeyInOfficeMode(int vkCode, bool isPointerLocked) =>
        !isPointerLocked && vkCode is VkLWin or VkRWin;

    public static uint BuildMapVirtualKeyScanCode(uint scanCode, bool isExtended)
    {
        if (scanCode == 0)
            return 0;

        return isExtended ? (0xE000u | (scanCode & 0xFFu)) : (scanCode & 0xFFu);
    }

    public static short ToMoonlightKeyCode(int vkCode) =>
        unchecked((short)(MoonlightKeyPrefix | (vkCode & 0xFF)));

    public static bool IsLocalOsEscapeKey(int vkCode, bool altDown) =>
        altDown && vkCode is VkTab or VkEscape;

    public static SessionShortcutAction ResolveShortcut(
        int vkCode,
        bool isKeyDown,
        bool ctrlDown,
        bool altDown,
        bool shiftDown,
        bool isPointerLocked)
    {
        if (!isKeyDown)
            return SessionShortcutAction.None;

        if (vkCode == 'Q' && ctrlDown && altDown && shiftDown)
            return SessionShortcutAction.DisconnectSession;

        if (vkCode == 'G' && ctrlDown && altDown && !shiftDown)
            return SessionShortcutAction.ToggleInputMode;

        if (vkCode == 'F' && ctrlDown && altDown && !shiftDown)
            return SessionShortcutAction.ToggleFullscreen;

        if (vkCode == 'H' && ctrlDown && altDown && !shiftDown)
            return SessionShortcutAction.ToggleHelpOverlay;

        if (vkCode == VkF11 && !ctrlDown && !altDown && !shiftDown)
            return SessionShortcutAction.ToggleFullscreen;

        if (isPointerLocked &&
            ctrlDown &&
            altDown &&
            !shiftDown &&
            vkCode is VkControl or VkLControl or VkRControl or VkMenu or VkLMenu or VkRMenu)
        {
            return SessionShortcutAction.ReleasePointerLock;
        }

        return SessionShortcutAction.None;
    }

    public static bool ShouldForwardSystemKeyToRemote(int vkCode, bool altDown, bool isPointerLocked)
    {
        _ = altDown;
        if (!isPointerLocked)
            return false;

        // Never swallow Alt+Tab or Alt+Esc so local OS window-switching remains an escape hatch.
        return vkCode is VkLWin or VkRWin or VkApps;
    }

    public static bool ShouldAutoLockOnModeSwitch(bool fromToolbarButton, bool cursorInsideVideoViewport) =>
        !fromToolbarButton && cursorInsideVideoViewport;
}

internal struct SessionModifierState
{
    private const byte BitLShift = 1 << 0;
    private const byte BitRShift = 1 << 1;
    private const byte BitLCtrl = 1 << 2;
    private const byte BitRCtrl = 1 << 3;
    private const byte BitLAlt = 1 << 4;
    private const byte BitRAlt = 1 << 5;
    private const byte BitLWin = 1 << 6;
    private const byte BitRWin = 1 << 7;

    private byte _bits;

    public bool IsShiftDown => (_bits & (BitLShift | BitRShift)) != 0;
    public bool IsCtrlDown => (_bits & (BitLCtrl | BitRCtrl)) != 0;
    public bool IsAltDown => (_bits & (BitLAlt | BitRAlt)) != 0;
    public bool IsMetaDown => (_bits & (BitLWin | BitRWin)) != 0;

    public void Update(int normalizedVk, bool isDown)
    {
        var mask = normalizedVk switch
        {
            SessionInputPolicy.VkLShift => BitLShift,
            SessionInputPolicy.VkRShift => BitRShift,
            SessionInputPolicy.VkLControl => BitLCtrl,
            SessionInputPolicy.VkRControl => BitRCtrl,
            SessionInputPolicy.VkLMenu => BitLAlt,
            SessionInputPolicy.VkRMenu => BitRAlt,
            SessionInputPolicy.VkLWin => BitLWin,
            SessionInputPolicy.VkRWin => BitRWin,
            _ => (byte)0,
        };

        if (mask == 0)
            return;

        if (isDown)
            _bits |= mask;
        else
            _bits &= (byte)~mask;
    }

    public void Reset() => _bits = 0;

    public sbyte ToMoonlightModifiers(bool isExtended)
    {
        byte mods = 0;
        if (IsShiftDown) mods |= SessionInputPolicy.ModMaskShift;
        if (IsCtrlDown) mods |= SessionInputPolicy.ModMaskCtrl;
        if (IsAltDown) mods |= SessionInputPolicy.ModMaskAlt;
        if (IsMetaDown) mods |= SessionInputPolicy.ModMaskMeta;
        if (isExtended) mods |= SessionInputPolicy.ModMaskExtended;
        return unchecked((sbyte)mods);
    }
}
