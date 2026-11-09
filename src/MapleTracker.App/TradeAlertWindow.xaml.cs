using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace MapleTracker.App;

public partial class TradeAlertWindow : Window
{
    private readonly DispatcherTimer dismiss = new() { Interval = TimeSpan.FromSeconds(12) };
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint window, int index, nint value);

    public TradeAlertWindow()
    {
        InitializeComponent();
        dismiss.Tick += (_, _) => Close();
        Closed += (_, _) => dismiss.Stop();
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
        Left = Math.Clamp(anchor.Left + Math.Max(0, anchor.ActualWidth - Width), SystemParameters.VirtualScreenLeft,
            SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - Width);
        Top = Math.Clamp(anchor.Top + 64, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - MaxHeight);
        if (!IsVisible) Show();
        dismiss.Stop(); dismiss.Start();
    }

    private void DismissClick(object sender, RoutedEventArgs e) => Close();
}
