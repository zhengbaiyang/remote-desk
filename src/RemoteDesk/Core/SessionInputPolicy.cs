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
