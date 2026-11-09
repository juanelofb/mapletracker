using System;
using System.Collections.Generic;
using System.Drawing;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using MapleTracker.App;
using MapleTracker.App.Services;
using MapleTracker.Core;

namespace MapleTracker.AppChecks;

internal static class Program
{
    private static int passed;

    [STAThread]
    private static int Main()
    {
        var app = new App.App();
        app.InitializeComponent();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        try
        {
            CheckSnapshotDialog(save: true, failCapture: false, snapshots: 2);
            CheckSnapshotDialog(save: false, failCapture: false, snapshots: 1);
            CheckSnapshotDialog(save: true, failCapture: true, snapshots: 1);
            CheckAlertEditor(save: true);
            CheckAlertEditor(save: false);
            CheckAlertDelivery();
            CheckMainWindowClosing();
            CheckScreenshotOcr();
            Console.WriteLine($"\n{passed} WPF regression checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }
        finally { app.Shutdown(); }
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
        passed++;
        Console.WriteLine("PASS: " + message);
    }

    private static void CheckSnapshotDialog(bool save, bool failCapture, int snapshots)
    {
        var settings = new TrackerSettings();
        var controller = new TrackerController(settings);
        var owner = new Window { Title = "MapleTracker test owner", Width = 260, Height = 120, Opacity = .9 };
        var stats = new Window { Title = "MapleTracker test statistics", Width = 240, Height = 120, Opacity = .7, Topmost = true };
        owner.Show(); stats.Show();
        var target = new GameWindow(new WindowInteropHelper(owner).Handle, "Test capture source");
        bool returned = false;
        Exception? failure = null;
        SetupWindow? dialog = null;
        dialog = new SetupWindow(controller, async handle =>
        {
            // Force a dispatcher turn, like the real asynchronous game-focus delay.
            await Task.Delay(30);
            Check(!returned && dialog!.IsVisible, "snapshot keeps setup in its modal loop");
            Check(owner.IsVisible && stats.IsVisible, "snapshot does not hide owner or statistics");
            Check(dialog!.Opacity == 0 && owner.Opacity == 0 && stats.Opacity == 0, "tracker pixels suppressed during capture");
            Check(!dialog.Topmost && !owner.Topmost && !stats.Topmost, "tracker windows leave the topmost layer during capture");
            if (failCapture) throw new InvalidOperationException("Simulated capture failure");
            return new Bitmap(64, 64);
        }) { Owner = owner, Opacity = .8 };
        var picker = (ComboBox)dialog.FindName("WindowPicker");
        picker.ItemsSource = new[] { target }; picker.SelectedItem = target;
        ((TextBox)dialog.FindName("BuyingTerms")).Text = "WTB, purchase";
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                for (int i = 0; i < snapshots; i++)
                {
                    await dialog.TakeSnapshotAsync();
                    Check(!returned && dialog.IsVisible, "snapshot completion does not return ShowDialog early");
                    Check(dialog.Opacity == .8 && owner.Opacity == .9 && stats.Opacity == .7, "original opacity restored after snapshot");
                    Check(stats.Topmost && !owner.Topmost, "original topmost preferences restored after snapshot");
                }
                if (failCapture)
                    Check(((TextBlock)dialog.FindName("SetupStatus")).Text == "Simulated capture failure", "capture failure stays in setup without crashing");
                if (save)
                    ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                else dialog.Close();
            }
            catch (Exception error) { failure = error; dialog.Close(); }
        };
        try
        {
            var result = dialog.ShowDialog();
            returned = true;
            if (failure != null) throw new InvalidOperationException("Setup flow failed", failure);
            Check(result == save, save ? "Save returns true after snapshot without DialogResult exception" : "cancel returns false after snapshot");
            Check(controller.Target == (save ? target : null), "target applied only on Save");
            Check(settings.BuyingTerms[0] == (save ? "WTB" : "Buying"), "edited settings applied only on Save");
            if (save)
            {
                var store = new SettingsStore(); store.Save(settings);
                var loaded = store.Load();
                Check(loaded.WindowTitle == target.Title && loaded.BuyingTerms[1] == "purchase", "saved setup survives a settings round trip");
            }
        }
        finally
        {
            dialog.Close(); stats.Close(); owner.Close();
            controller.Shutdown().GetAwaiter().GetResult();
        }
    }

    private static void CheckAlertEditor(bool save)
    {
        var settings = new TrackerSettings { BuyingItems = ["Steel Ore"], SellingItems = ["Potions"] };
        var controller = new TrackerController(settings);
        var owner = new Window { Title = "MapleTracker alert editor test", Width = 300, Height = 150 };
        owner.Show();
        var dialog = new AlertsWindow(controller) { Owner = owner };
        Exception? failure = null;
        dialog.Loaded += (_, _) =>
        {
            try
            {
                Check(((TextBox)dialog.FindName("BuyingList")).Text == "Steel Ore", "alert editor loads buying list");
                Check(((TextBox)dialog.FindName("SellingList")).Text == "Potions", "alert editor loads selling list");
                ((TextBox)dialog.FindName("BuyingList")).Text = "  Gloves Attack Scroll | Glove att\r\nSteel Ore\r\nsteel ore  ";
                ((TextBox)dialog.FindName("SellingList")).Text = "Dagger Attack Scroll\r\nPotions";
                ((CheckBox)dialog.FindName("EnabledCheck")).IsChecked = false;
                if (save)
                    ((Button)dialog.FindName("SaveButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                else dialog.Close();
            }
            catch (Exception error) { failure = error; dialog.Close(); }
        };
        try
        {
            Check(dialog.ShowDialog() == save, "watchlist dialog returns the correct result");
            if (failure != null) throw failure;
            if (save)
            {
                Check(settings.BuyingItems.Length == 2 && settings.BuyingItems[0] == "Gloves Attack Scroll | Glove att", "watchlist save trims and deduplicates entries");
                Check(settings.SellingItems.Length == 2 && !settings.AlertsEnabled, "selling list and enable toggle applied");
                var store = new SettingsStore(); store.Save(settings);
                var loaded = store.Load();
                Check(loaded.BuyingItems.Length == 2 && loaded.SellingItems[0] == "Dagger Attack Scroll" && !loaded.AlertsEnabled,
                    "both watchlists and enable toggle survive restart");
            }
            else
                Check(settings.BuyingItems[0] == "Steel Ore" && settings.SellingItems[0] == "Potions" && settings.AlertsEnabled,
                    "cancelling alert edits preserves settings");
        }
        finally { dialog.Close(); owner.Close(); controller.Shutdown().GetAwaiter().GetResult(); }
    }

    private static void CheckAlertDelivery()
    {
        var controller = new TrackerController(new TrackerSettings { BuyingItems = ["Steel Ore"], SellingItems = ["Potions"] });
        var owner = new Window { Title = "MapleTracker notification test", Width = 420, Height = 200 };
        owner.Show(); owner.Activate();
        var before = WindowCapture.GetForegroundWindow();
        var popup = new TradeAlertWindow { Owner = owner };
        int notifications = 0;
        controller.AlertRaised += alert => { notifications++; popup.Present(alert, owner); };
        try
        {
            controller.AddChat("Alice: S > Steel Ore x20");
            Check(notifications == 1 && controller.Alerts.Count == 1 && popup.IsVisible, "captured message produces popup and history entry");
            Check(((TextBlock)popup.FindName("SummaryText")).Text == "Alice is selling Steel Ore", "popup shows player and matched item");
            Check(!popup.ShowActivated && WindowCapture.GetForegroundWindow() == before, "popup does not take foreground focus");
            controller.AddChat("Alice: S> Steel Ore new price");
            Check(notifications == 1 && controller.Alerts.Count == 1, "controller suppresses repeat popups during cooldown");
            controller.AddChat("Bob: B> Potions");
            Check(notifications == 2 && controller.Alerts[0].Summary == "Bob wants to buy Potions", "selling watchlist triggers buyer notification");
            controller.AddChat("Other: WTB Steel Ore");
            Check(notifications == 2, "opposite direction does not notify");
            controller.Reset();
            Check(controller.Alerts.Count == 0, "new session clears alert history");
        }
        finally { popup.Close(); owner.Close(); controller.Shutdown().GetAwaiter().GetResult(); }
    }

    private static void CheckScreenshotOcr()
    {
        // Optional local fixtures; never copy user screenshots into the repository.
        var cases = new[]
        {
            (Environment.GetEnvironmentVariable("MAPLETRACKER_ORIGINAL_SCREENSHOT"), 50548L),
            (Environment.GetEnvironmentVariable("MAPLETRACKER_LATEST_SCREENSHOT"), 54754L)
        };
        var table = ExperienceTable.Load(System.IO.Path.Combine(SettingsStore.DataDirectory, "exp-table.json"));
        using var reader = new OcrReader(table);
        foreach (var (path, expected) in cases)
        {
            if (string.IsNullOrEmpty(path)) continue;
            using var image = new Bitmap(path);
            var frame = reader.Read(image, new TrackerSettings());
            Check(frame.Hud == new HudReading(27, expected), $"screenshot OCR validates level 27 and EXP {expected:N0}");
        }
    }

    private static void CheckMainWindowClosing()
    {
        var controller = new TrackerController(new TrackerSettings());
        var window = new MainWindow(controller, new SettingsStore());
        bool closed = false;
        window.Closed += (_, _) => closed = true;
        window.Show();
        window.Close();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        Check(closed, "closing an idle chat window completes without reentrant Close");
    }
}
