using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MapleTracker.App;

public static class WindowAppearance
{
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    public static void DarkTitleBar(Window window) { int value = 1; DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref value, sizeof(int)); }
    public static void SetSurfaceOpacity(Window window, double opacity)
    {
        window.Opacity = 1;
        window.Background = new SolidColorBrush(System.Windows.Media.Color.FromArgb((byte)(Math.Clamp(opacity, .4, 1) * 255), 17, 24, 37));
    }
    public static void Restore(Window w, double left, double top, double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || !double.IsFinite(left) || !double.IsFinite(top)) return;
        w.Width = Math.Clamp(width, w.MinWidth, Math.Max(w.MinWidth, SystemParameters.VirtualScreenWidth));
        w.Height = Math.Clamp(height, w.MinHeight, Math.Max(w.MinHeight, SystemParameters.VirtualScreenHeight));
        w.Left = Math.Clamp(left, SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 100);
        w.Top = Math.Clamp(top, SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 100);
    }
}
