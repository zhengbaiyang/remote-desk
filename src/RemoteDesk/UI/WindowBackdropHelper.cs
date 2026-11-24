using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace RemoteDesk.UI;

internal static class WindowBackdropHelper
{
    private const int DwmwaUseImmersiveDarkMode = 20;
    private const int DwmwaWindowCornerPreference = 33;
    private const int DwmwaSystemBackdropType = 38;

    private const int DwmwcpRound = 2;
    private const int DwmsbtMainWindow = 2;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd,
        int dwAttribute,
        ref int pvAttribute,
        int cbAttribute);

    public static void ApplyMainWindowBackdrop(Window window, bool dark, Border? sidebarBorder)
    {
        if (window is null)
            return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            int darkFlag = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkFlag, sizeof(int));

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int round = DwmwcpRound;
                _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
            }

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22621))
            {
                int mica = DwmsbtMainWindow;
                if (DwmSetWindowAttribute(hwnd, DwmwaSystemBackdropType, ref mica, sizeof(int)) == 0)
                {
                    var chrome = WindowChrome.GetWindowChrome(window);
                    if (chrome is not null)
                    {
                        chrome.GlassFrameThickness = new Thickness(-1);
                        chrome.CornerRadius = new CornerRadius(0);
                    }

                    if (HwndSource.FromHwnd(hwnd)?.CompositionTarget is { } target)
                        target.BackgroundColor = Colors.Transparent;

                    window.Background = Brushes.Transparent;
                    sidebarBorder?.SetResourceReference(Border.BackgroundProperty, "RailMica");
                    return;
                }
            }
        }
        catch
        {
            // Fall back silently to opaque Oink surfaces if DWM backdrop is unavailable.
        }

        window.SetResourceReference(Control.BackgroundProperty, "WindowSurface");
        sidebarBorder?.SetResourceReference(Border.BackgroundProperty, "Rail");
    }

    public static void ApplyDarkChromeOnly(Window window, bool dark = true)
    {
        if (window is null)
            return;

        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero)
            return;

        try
        {
            int darkFlag = dark ? 1 : 0;
            _ = DwmSetWindowAttribute(hwnd, DwmwaUseImmersiveDarkMode, ref darkFlag, sizeof(int));

            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000))
            {
                int round = DwmwcpRound;
                _ = DwmSetWindowAttribute(hwnd, DwmwaWindowCornerPreference, ref round, sizeof(int));
            }
        }
        catch
        {
        }
    }
}
