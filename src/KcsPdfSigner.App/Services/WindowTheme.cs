using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace KcsPdfSigner.App.Services;

/// <summary>Asks Windows for a black title bar with white text and a white border.</summary>
public static class WindowTheme
{
    private const int DwmUseImmersiveDarkModeOld = 19;
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmBorderColor = 34;
    private const int DwmCaptionColor = 35;
    private const int DwmTextColor = 36;

    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    /// <summary>Call once; applies as soon as the window has a handle.</summary>
    public static void Apply(Window window)
    {
        if (new WindowInteropHelper(window).Handle != IntPtr.Zero)
        {
            ApplyNow(window);
            return;
        }

        window.SourceInitialized += (_, _) => ApplyNow(window);
    }

    private static void ApplyNow(Window window)
    {
        try
        {
            var handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            var on = 1;
            if (DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref on, sizeof(int)) != 0)
            {
                DwmSetWindowAttribute(handle, DwmUseImmersiveDarkModeOld, ref on, sizeof(int));
            }

            // COLORREF is 0x00BBGGRR. These three only exist on Windows 11; older builds ignore them.
            var black = 0x00000000;
            var white = 0x00FFFFFF;
            DwmSetWindowAttribute(handle, DwmCaptionColor, ref black, sizeof(int));
            DwmSetWindowAttribute(handle, DwmTextColor, ref white, sizeof(int));
            DwmSetWindowAttribute(handle, DwmBorderColor, ref white, sizeof(int));
        }
        catch
        {
            // Cosmetic only.
        }
    }
}
