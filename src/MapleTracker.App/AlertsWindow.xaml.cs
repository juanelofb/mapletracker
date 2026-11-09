using MapleTracker.App.Services;

namespace MapleTracker.App;

public partial class AlertsWindow : Window
{
    private readonly TrackerSettings settings;
    public event Action<TradeAlert>? PreviewRequested;

    public AlertsWindow(TrackerController controller)
    {
        settings = controller.Settings;
        InitializeComponent();
        EnabledCheck.IsChecked = settings.AlertsEnabled;
        BuyingList.Text = string.Join(Environment.NewLine, settings.BuyingItems);
        SellingList.Text = string.Join(Environment.NewLine, settings.SellingItems);
        HistoryList.ItemsSource = controller.Alerts;
        Loaded += (_, _) => WindowAppearance.DarkTitleBar(this);
    }

    private static string[] ReadItems(string text) => text.Split(['\r', '\n'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private void SaveClick(object sender, RoutedEventArgs e)
    {
        var buying = ReadItems(BuyingList.Text);
        var selling = ReadItems(SellingList.Text);
        if (buying.Length > 200 || selling.Length > 200 || buying.Concat(selling).Any(item => item.Length > 300))
        {
            SaveStatus.Text = "Use up to 200 items per list, and up to 300 characters per line.";
            return;
        }
        settings.BuyingItems = buying;
        settings.SellingItems = selling;
        settings.AlertsEnabled = EnabledCheck.IsChecked == true;
        DialogResult = true;
    }

    private void TestClick(object sender, RoutedEventArgs e)
    {
        var sell = ReadItems(SellingList.Text).FirstOrDefault();
        var buy = ReadItems(BuyingList.Text).FirstOrDefault();
        var item = (sell ?? buy ?? "Steel Ore").Split('|')[0].Trim();
        var intent = sell != null || buy == null ? MessageKind.Buying : MessageKind.Selling;
        PreviewRequested?.Invoke(new(DateTimeOffset.Now, "ExampleTrader", item, intent,
            $"ExampleTrader: {(intent == MessageKind.Buying ? "B>" : "S>")} {item} · popup preview"));
    }
}
