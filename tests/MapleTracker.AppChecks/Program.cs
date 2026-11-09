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
}
