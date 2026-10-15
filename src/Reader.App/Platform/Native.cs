using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Reader.App.Platform;

internal static class Native
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("shell32.dll")]
    public static extern void SHChangeNotify(int eventId, int flags, IntPtr item1, IntPtr item2);

    public const int SHCNE_ASSOCCHANGED = 0x08000000;
    public const int WM_MOUSEHWHEEL = 0x020E;

    /// <summary>A dark title bar for the Dark and Black themes (Windows 10 20H1+ and 11).</summary>
    public static void SetDarkTitleBar(Window window, bool dark)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var value = dark ? 1 : 0;
        // 20 = DWMWA_USE_IMMERSIVE_DARK_MODE; 19 on Windows 10 builds before 20H1.
        if (DwmSetWindowAttribute(hwnd, 20, ref value, sizeof(int)) != 0)
        {
            DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
        }
    }

    /// <summary>The title bar's colour (Windows 11), so it matches the reading theme.</summary>
    public static void SetCaptionColor(Window window, uint rgb)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        // COLORREF is 0x00BBGGRR. 35 = DWMWA_CAPTION_COLOR; ignored before Windows 11.
        var colorref = (int)(((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF));
        DwmSetWindowAttribute(hwnd, 35, ref colorref, sizeof(int));
    }
}
