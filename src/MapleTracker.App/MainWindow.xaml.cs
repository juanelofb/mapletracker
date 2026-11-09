using System.ComponentModel;
using System.Windows.Data;
using MapleTracker.App.Services;

namespace MapleTracker.App;

public partial class MainWindow : Window
{
    private readonly TrackerController controller;
    private readonly SettingsStore store;
    private readonly List<ListCollectionView> views = [];
    private StatsWindow? stats;
    private readonly TradeNotificationPresenter notifications;
    private bool shuttingDown;
    private bool allowClose;
    private long itemCount = -1;
    public MainWindow(TrackerController controller, SettingsStore store)
    {
        this.controller = controller; this.store = store; InitializeComponent();
        notifications = new(this);
        var s = controller.Settings;
        WindowAppearance.Restore(this, s.ChatLeft, s.ChatTop, s.ChatWidth, s.ChatHeight);
        Topmost = s.AlwaysOnTop; TopCheck.IsChecked = Topmost; PopupToggle.IsChecked = s.AlertsEnabled;
        ApplyAppearance(); Transparency.Initialize(controller, Save);
        AddView(AllList, e => e.Kind != MessageKind.Item);
        AddView(BuyingList, e => e.Kind.HasFlag(MessageKind.Buying));
        AddView(SellingList, e => e.Kind.HasFlag(MessageKind.Selling));
        AddView(ItemsList, e => e.Kind == MessageKind.Item);
        controller.Changed += Refresh;
        controller.AlertRaised += ShowAlert;
        controller.AppearanceChanged += ApplyAppearance;
        controller.Entries.CollectionChanged += (_, _) =>
        {
            if (FollowCheck.IsChecked == true) Dispatcher.BeginInvoke(() =>
            {
                foreach (var list in new[] { AllList, BuyingList, SellingList, ItemsList }) if (list.Items.Count > 0) list.ScrollIntoView(list.Items[^1]);
            });
        };
        Loaded += (_, _) => { WindowAppearance.DarkTitleBar(this); ShowStats(); };
        Closing += OnClosing; Refresh();
        Closed += (_, _) => { controller.Changed -= Refresh; controller.AlertRaised -= ShowAlert; controller.AppearanceChanged -= ApplyAppearance; };
    }
    private void AddView(ListBox list, Predicate<FeedEntry> predicate)
    {
        var view = new ListCollectionView(controller.Entries) { Filter = o => o is FeedEntry e && predicate(e) && e.Text.Contains(Search.Text, StringComparison.OrdinalIgnoreCase) };
        views.Add(view); list.ItemsSource = view;
    }
    private void Refresh()
    {
        StatusText.Text = controller.Status; StartButton.IsEnabled = !controller.Running; PauseButton.IsEnabled = controller.Running;
        TypingNotice.Visibility = controller.Settings.ChatHistoryOnly ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = controller.Entries.Any(e => e.Kind != MessageKind.Item) ? Visibility.Collapsed : Visibility.Visible;
        var count = controller.Items.Values.Sum();
        if (itemCount != count)
        {
            itemCount = count; LootTotals.ItemsSource = controller.Items.OrderByDescending(x => x.Value).ThenBy(x => x.Key).ToArray();
            LootHeading.Text = $"{count:N0} items picked up · {controller.Items.Count:N0} types";
        }
        stats?.Refresh();
        AlertsButton.Content = controller.Alerts.Count > 0 ? $"Item alerts ({controller.Alerts.Count})" : "Item alerts";
        PopupToggle.IsChecked = controller.Settings.AlertsEnabled;
        if (!controller.Settings.AlertsEnabled) notifications.Clear();
    }
    private void ApplyAppearance() => WindowAppearance.SetSurfaceOpacity(this, controller.Settings.ChatOpacity);
    private void PopupChanged(object sender, RoutedEventArgs e)
    {
        controller.SetPopupsEnabled(PopupToggle.IsChecked == true); Save();
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) { foreach (var view in views) view.Refresh(); }
    private void StartClick(object sender, RoutedEventArgs e) { if (controller.Target == null) { SetupClick(sender, e); return; } controller.Start(); }
    private void PauseClick(object sender, RoutedEventArgs e) => controller.Pause();
    private async void SetupClick(object sender, RoutedEventArgs e)
    {
        controller.Pause(); await controller.WaitForIdle();
        var dialog = new SetupWindow(controller) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Topmost = controller.Settings.AlwaysOnTop; TopCheck.IsChecked = Topmost;
            if (stats != null) stats.Topmost = Topmost;
            Save();
        }
    }
    private void StatsClick(object sender, RoutedEventArgs e) => ShowStats();
    private void AlertsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AlertsWindow(controller) { Owner = this };
        if (dialog.ShowDialog() == true)
        {
            Save();
            controller.SetPopupsEnabled(controller.Settings.AlertsEnabled);
        }
    }
    private void ShowAlert(TradeAlert alert)
    {
        if (shuttingDown) return;
        try { notifications.Show(alert); }
        catch (Exception error)
        {
            StatusText.Text = "Popup could not be displayed · " + error.Message;
            try { File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "notification-error.log"), error.ToString()); }
            catch (Exception fileError) when (fileError is IOException or UnauthorizedAccessException) { }
        }
    }
    private void ShowStats()
    {
        if (stats == null || !stats.IsLoaded) { stats = new StatsWindow(controller, () => SetupClick(this, new RoutedEventArgs())); stats.Closed += (_, _) => stats = null; stats.Show(); }
        else { stats.Show(); stats.Activate(); }
    }
    private void HelpClick(object sender, RoutedEventArgs e) => new HelpWindow(controller.Settings) { Owner = this }.Show();
    private void TopChanged(object sender, RoutedEventArgs e) { Topmost = TopCheck.IsChecked == true; controller.Settings.AlwaysOnTop = Topmost; if (stats != null) stats.Topmost = Topmost; Save(); }
    private void Save()
    {
        try { store.Save(controller.Settings); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { MessageBox.Show(this, e.Message, "Could not save settings"); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (allowClose) return;
        e.Cancel = true; if (shuttingDown) return; shuttingDown = true;
        var s = controller.Settings;
        if (WindowState == WindowState.Normal) { s.ChatLeft = Left; s.ChatTop = Top; s.ChatWidth = Width; s.ChatHeight = Height; }
        stats?.RememberBounds(); Save(); await controller.Shutdown(); notifications.Dispose(); stats?.Close(); allowClose = true;
        // Shutdown can complete synchronously. Let this Closing event return before closing again.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
