using System.ComponentModel;
using System.Windows.Data;
using MapleTracker.App.Services;
using Microsoft.Win32;

namespace MapleTracker.App;

public partial class MainWindow : Window
{
    private readonly TrackerController controller;
    private readonly SettingsStore store;
    private readonly List<ListCollectionView> views = [];
    private StatsWindow? stats;
    private TradeAlertWindow? tradePopup;
    private bool shuttingDown;
    private bool allowClose;
    private long itemCount = -1;
    public MainWindow(TrackerController controller, SettingsStore store)
    {
        this.controller = controller; this.store = store; InitializeComponent();
        var s = controller.Settings;
        WindowAppearance.Restore(this, s.ChatLeft, s.ChatTop, s.ChatWidth, s.ChatHeight);
        Topmost = s.AlwaysOnTop; TopCheck.IsChecked = Topmost; Opacity = s.Opacity;
        AddView(AllList, e => e.Kind != MessageKind.Item);
        AddView(BuyingList, e => e.Kind.HasFlag(MessageKind.Buying));
        AddView(SellingList, e => e.Kind.HasFlag(MessageKind.Selling));
        AddView(ItemsList, e => e.Kind == MessageKind.Item);
        controller.Changed += Refresh;
        controller.AlertRaised += ShowAlert;
        controller.Entries.CollectionChanged += (_, _) =>
        {
            if (FollowCheck.IsChecked == true) Dispatcher.BeginInvoke(() =>
            {
                foreach (var list in new[] { AllList, BuyingList, SellingList, ItemsList }) if (list.Items.Count > 0) list.ScrollIntoView(list.Items[^1]);
            });
        };
        Loaded += (_, _) => { WindowAppearance.DarkTitleBar(this); ShowStats(); };
        Closing += OnClosing; Refresh();
    }
    private void AddView(ListBox list, Predicate<FeedEntry> predicate)
    {
        var view = new ListCollectionView(controller.Entries) { Filter = o => o is FeedEntry e && predicate(e) && e.Text.Contains(Search.Text, StringComparison.OrdinalIgnoreCase) };
        views.Add(view); list.ItemsSource = view;
    }
    private void Refresh()
    {
        StatusText.Text = controller.Status; StartButton.IsEnabled = !controller.Running; PauseButton.IsEnabled = controller.Running;
        EmptyState.Visibility = controller.Entries.Any(e => e.Kind != MessageKind.Item) ? Visibility.Collapsed : Visibility.Visible;
        var count = controller.Items.Values.Sum();
        if (itemCount != count)
        {
            itemCount = count; LootTotals.ItemsSource = controller.Items.OrderByDescending(x => x.Value).ThenBy(x => x.Key).ToArray();
            LootHeading.Text = $"{count:N0} items picked up · {controller.Items.Count:N0} types";
        }
        stats?.Refresh();
        AlertsButton.Content = controller.Alerts.Count > 0 ? $"Item alerts ({controller.Alerts.Count})" : "Item alerts";
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
            Topmost = controller.Settings.AlwaysOnTop; TopCheck.IsChecked = Topmost; Opacity = controller.Settings.Opacity;
            if (stats != null) { stats.Topmost = Topmost; stats.Opacity = Opacity; }
            Save();
        }
    }
    private void StatsClick(object sender, RoutedEventArgs e) => ShowStats();
    private void AlertsClick(object sender, RoutedEventArgs e)
    {
        var dialog = new AlertsWindow(controller) { Owner = this };
        dialog.PreviewRequested += ShowAlert;
        if (dialog.ShowDialog() == true)
        {
            Save();
            if (!controller.Settings.AlertsEnabled) tradePopup?.Close();
        }
        dialog.PreviewRequested -= ShowAlert;
    }
    private void ShowAlert(TradeAlert alert)
    {
        if (shuttingDown) return;
        if (tradePopup == null)
        {
            tradePopup = new TradeAlertWindow { Owner = this };
            tradePopup.Closed += (_, _) => tradePopup = null;
        }
        tradePopup.Present(alert, this);
    }
    private void ShowStats()
    {
        if (stats == null || !stats.IsLoaded) { stats = new StatsWindow(controller); stats.Closed += (_, _) => stats = null; stats.Show(); }
        else { stats.Show(); stats.Activate(); }
    }
    private void DemoClick(object sender, RoutedEventArgs e) { controller.StartDemo(); ShowStats(); }
    private void ResetClick(object sender, RoutedEventArgs e) => controller.Reset();
    private void TopChanged(object sender, RoutedEventArgs e) { Topmost = TopCheck.IsChecked == true; controller.Settings.AlwaysOnTop = Topmost; if (stats != null) stats.Topmost = Topmost; Save(); }
    private void ExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog { Filter = "JSON session (*.json)|*.json", FileName = $"mapletracker-{DateTime.Now:yyyyMMdd-HHmm}.json" };
        if (dialog.ShowDialog(this) != true) return;
        try { controller.Export(dialog.FileName); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { MessageBox.Show(this, ex.Message, "Export failed"); }
    }
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
        stats?.RememberBounds(); Save(); await controller.Shutdown(); tradePopup?.Close(); stats?.Close(); allowClose = true;
        // Shutdown can complete synchronously. Let this Closing event return before closing again.
        _ = Dispatcher.BeginInvoke(new Action(Close));
    }
}
