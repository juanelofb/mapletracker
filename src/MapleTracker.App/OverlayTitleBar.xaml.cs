using System.Windows.Input;

namespace MapleTracker.App;

public partial class OverlayTitleBar : UserControl
{
    public OverlayTitleBar() => InitializeComponent();
    private void DragTitle(object sender, MouseButtonEventArgs e)
    {
        if (e.Handled || e.ChangedButton != MouseButton.Left) return;
        var window = Window.GetWindow(this);
        if (e.ClickCount == 2) window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        else window.DragMove();
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => Window.GetWindow(this).WindowState = WindowState.Minimized;
    private void CloseClick(object sender, RoutedEventArgs e) => Window.GetWindow(this).Close();
}
