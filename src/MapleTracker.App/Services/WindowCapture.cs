using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using PixelFormat = System.Drawing.Imaging.PixelFormat;

namespace MapleTracker.App.Services;

public sealed record GameWindow(nint Handle, string Title)
{
    public override string ToString() => Title;
}

public static class WindowCapture
{
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { public int X, Y; }
    private delegate bool EnumProc(nint hwnd, nint parameter);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] public static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hwnd, StringBuilder text, int count);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint process);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(nint hwnd, ref Point point);
    [DllImport("user32.dll")] public static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(nint hwnd);

    public static IReadOnlyList<GameWindow> List()
    {
        var list = new List<GameWindow>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == Environment.ProcessId) return true;
            var title = new StringBuilder(512);
            GetWindowText(h, title, title.Capacity);
            if (title.Length > 0 && GetClientRect(h, out var r) && r.Right > 200 && r.Bottom > 150)
                list.Add(new(h, title.ToString()));
            return true;
        }, 0);
        return list.OrderBy(w => w.Title).ToArray();
    }

    // Captures the visible client surface. Never reads game memory or injects into it.
    public static Bitmap Capture(nint hwnd)
    {
        if (IsIconic(hwnd) || !GetClientRect(hwnd, out var rect) || rect.Right <= 0 || rect.Bottom <= 0)
            throw new InvalidOperationException("Game window is minimized or unavailable. Select it again in Setup.");
        var origin = new Point();
        if (!ClientToScreen(hwnd, ref origin)) throw new InvalidOperationException("Cannot locate the game window.");
        var bitmap = new Bitmap(rect.Right, rect.Bottom, PixelFormat.Format24bppRgb);
        try
        {
            using var graphics = Graphics.FromImage(bitmap);
            graphics.CopyFromScreen(origin.X, origin.Y, 0, 0, bitmap.Size, CopyPixelOperation.SourceCopy);
            return bitmap;
        }
        catch { bitmap.Dispose(); throw; }
    }

    public static Bitmap Crop(Bitmap image, CaptureRegion region)
    {
        if (!region.IsValid) throw new InvalidOperationException("Invalid capture region. Recalibrate in Setup.");
        var x = Math.Clamp((int)Math.Round(region.X * image.Width), 0, image.Width - 1);
        var y = Math.Clamp((int)Math.Round(region.Y * image.Height), 0, image.Height - 1);
        var w = Math.Clamp((int)Math.Round(region.Width * image.Width), 1, image.Width - x);
        var h = Math.Clamp((int)Math.Round(region.Height * image.Height), 1, image.Height - y);
        return image.Clone(new Rectangle(x, y, w, h), PixelFormat.Format24bppRgb);
    }

    public static bool RegionsCovered(nint hwnd, TrackerSettings settings)
    {
        if (!GetClientRect(hwnd, out var client)) return true;
        var origin = new Point(); if (!ClientToScreen(hwnd, ref origin)) return true;
        var regions = new[] { settings.Chat, settings.Loot, settings.Level, settings.Experience }
            .Select(r => new Rectangle(origin.X + (int)(r.X * client.Right), origin.Y + (int)(r.Y * client.Bottom),
                Math.Max(1, (int)(r.Width * client.Right)), Math.Max(1, (int)(r.Height * client.Bottom)))).ToArray();
        bool covered = false;
        EnumWindows((other, _) =>
        {
            if (other == hwnd) return false;
            if (!IsWindowVisible(other) || IsIconic(other) || !GetWindowRect(other, out var bounds)) return true;
            var rectangle = Rectangle.FromLTRB(bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
            if (regions.Any(r => r.IntersectsWith(rectangle))) { covered = true; return false; }
            return true;
        }, 0);
        return covered;
    }
}
