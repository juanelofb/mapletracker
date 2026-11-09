namespace MapleTracker.App.Services;

// Hide the pixels without hiding the windows: Window.Hide() ends WPF's modal loop.
internal sealed class SnapshotWindowScope : IDisposable
{
    private readonly (Window Window, double Opacity, bool Topmost)[] windows;

    public SnapshotWindowScope(IEnumerable<Window> candidates)
    {
        windows = candidates.Where(window => window.IsVisible)
            .Select(window => (window, window.Opacity, window.Topmost)).ToArray();
        foreach (var (window, _, _) in windows)
        {
            window.Opacity = 0;
            window.Topmost = false;
        }
    }

    public void Dispose()
    {
        foreach (var (window, opacity, topmost) in windows)
        {
            window.Topmost = topmost;
            window.Opacity = opacity;
        }
    }
}
