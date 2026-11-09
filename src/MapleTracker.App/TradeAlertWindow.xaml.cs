using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace MapleTracker.App;

public partial class TradeAlertWindow : Window
{
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] internal static extern bool GetWindowRect(nint window, out NativeRect bounds);

    public TradeAlertWindow()
    {
        InitializeComponent();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var handle = new WindowInteropHelper(this).Handle;
        const int extendedStyle = -20;
        const long noActivate = 0x08000000;
        SetWindowLongPtr(handle, extendedStyle, (nint)((long)GetWindowLongPtr(handle, extendedStyle) | noActivate));
    }

    public void Present(TradeAlert alert, Window anchor)
    {
        ListText.Text = alert.ListLabel;
        SummaryText.Text = alert.Summary;
        SourceText.Text = alert.Message;
        if (!IsVisible) Show();
        UpdateLayout();
        var handle = new WindowInteropHelper(this).Handle;
        var monitor = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(anchor).Handle).WorkingArea;
        GetWindowRect(handle, out var bounds);
        int width = Math.Min(bounds.Right - bounds.Left, monitor.Width);
        int height = Math.Min(bounds.Bottom - bounds.Top, monitor.Height);
        // Use physical monitor coordinates and explicitly reassert topmost without activation.
        if (!SetWindowPos(handle, new nint(-1), Math.Max(monitor.Left, monitor.Right - width - 20),
            Math.Max(monitor.Top, monitor.Bottom - height - 20), width, height, 0x0010 | 0x0040))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public void SetPendingCount(int count)
    {
        PendingText.Text = count > 0 ? $"{count} more waiting · dismiss to show the next match" : "Stays visible until you dismiss it";
    }

    private void DismissClick(object sender, RoutedEventArgs e) => Close();
}
