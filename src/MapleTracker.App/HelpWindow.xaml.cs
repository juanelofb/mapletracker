using MapleTracker.App.Services;

namespace MapleTracker.App;

public partial class HelpWindow : Window
{
    public HelpWindow(TrackerSettings settings)
    {
        InitializeComponent(); Topmost = settings.AlwaysOnTop;
        WindowAppearance.SetSurfaceOpacity(this, settings.ChatOpacity);
        ShowText(RuntimeAssets.ReadGuide());
    }
    private void GuideClick(object sender, RoutedEventArgs e) => ShowText(RuntimeAssets.ReadGuide());
    private void NoticesClick(object sender, RoutedEventArgs e) => ShowText(RuntimeAssets.ReadNotices());
    private void ShowText(string text)
    {
        Document.Children.Clear();
        foreach (var original in text.Replace("\r", "").Split('\n'))
        {
            var line = original;
            bool title = line.StartsWith("# "), heading = line.StartsWith("## "), code = line.StartsWith("    ");
            if (title) line = line[2..]; else if (heading) line = line[3..]; else if (code) line = line[4..];
            if (line.StartsWith("- ")) line = "• " + line[2..];
            Document.Children.Add(new TextBlock
            {
                Text = line, TextWrapping = TextWrapping.Wrap,
                FontSize = title ? 24 : heading ? 18 : 13,
                FontWeight = title || heading ? FontWeights.SemiBold : FontWeights.Normal,
                FontFamily = new FontFamily(code ? "Consolas" : "Segoe UI"),
                Margin = new Thickness(0, heading ? 15 : 0, 0, line.Length == 0 ? 8 : 5)
            });
        }
    }
}
