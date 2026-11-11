using System;
using System.Collections.Generic;
using System.Linq;
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
        AppContext.SetData("MapleTracker.DataDirectory", System.IO.Path.Combine(AppContext.BaseDirectory, "data"));
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
            CheckStatisticsControls();
            CheckNotificationQueue();
            CheckModernShell();
            CheckScreenshotOcr();
            CheckLootPasses();
            CheckGameplayFrames();
            CheckBadgeScreenshot();
            CheckRepeatFixtures();
            CheckBundledAssets();
            CheckPreviewProfile();
            CheckWorldCatalogIntegration();
            CatalogRefreshChecks.Run(Check);
            CheckMesoGlyphReplay();
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
        ((CheckBox)dialog.FindName("HistoryOnly")).IsChecked = true;
        ((CheckBox)dialog.FindName("OmitBadges")).IsChecked = false;
        ((CheckBox)dialog.FindName("ExperimentalFont")).IsChecked = true;
        ((ComboBox)dialog.FindName("QualityPicker")).SelectedItem = RecognitionQuality.High;
        ((RadioButton)dialog.FindName("MapRadio")).IsChecked = true;
        var manualMap = new CaptureRegion(.04, .04, .16, .05);
        dialog.SetCurrent(manualMap);
        dialog.Loaded += async (_, _) =>
        {
            try
            {
                var previewPath = Environment.GetEnvironmentVariable("MAPLETRACKER_SETUP_PREVIEW");
                if (save && !failCapture && !string.IsNullOrEmpty(previewPath))
                {
                    dialog.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(dialog);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = System.IO.File.Create(previewPath); encoder.Save(output);
                }
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
            Check(settings.ChatHistoryOnly == save, "history-only capture preference applied only on Save");
            Check(settings.AutoMapRegion != save && settings.Map == (save ? manualMap : new TrackerSettings().Map), "manual map calibration applies only on Save");
            Check(settings.UseExperimentalChatFont == (save && RuntimeAssets.HasExperimentalChatModel), "experimental reader applies only on Save when its model is bundled");
            Check(settings.IgnoreChannelBadges != save && settings.RecognitionQuality == (save ? RecognitionQuality.High : RecognitionQuality.Balanced), "badge and quality preferences apply only on Save");
            if (save)
            {
                var store = new SettingsStore(); store.Save(settings);
                var loaded = store.Load();
                Check(loaded.WindowTitle == target.Title && loaded.BuyingTerms[1] == "purchase", "saved setup survives a settings round trip");
                Check(loaded.ChatHistoryOnly, "history-only capture preference survives restart");
                Check(!loaded.AutoMapRegion && loaded.Map == manualMap, "manual map calibration survives restart");
                Check(!loaded.IgnoreChannelBadges && loaded.RecognitionQuality == RecognitionQuality.High, "quality and badge preferences survive restart");
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
                var preview = Environment.GetEnvironmentVariable("MAPLETRACKER_ALERTS_PREVIEW");
                if (save && !string.IsNullOrEmpty(preview))
                {
                    dialog.UpdateLayout();
                    var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)dialog.ActualWidth, (int)dialog.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                    bitmap.Render(dialog);
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                    using var output = System.IO.File.Create(preview); encoder.Save(output);
                }
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
            (Environment.GetEnvironmentVariable("MAPLETRACKER_LATEST_SCREENSHOT"), 54754L),
            (Environment.GetEnvironmentVariable("MAPLETRACKER_FADING_SCREENSHOT"), 55187L)
        };
        var table = ExperienceTable.Load(RuntimeAssets.ExperienceTablePath);
        using var reader = new OcrReader(table);
        foreach (var (path, expected) in cases)
        {
            if (string.IsNullOrEmpty(path)) continue;
            using var image = new Bitmap(path);
            var frame = reader.Read(image, new TrackerSettings());
            Check(frame.Hud == new HudReading(27, expected), $"screenshot OCR validates level 27 and EXP {expected:N0}");
            if (expected == 50548)
                Check(frame.Chat.Any(line => line.Contains("dagger", StringComparison.OrdinalIgnoreCase)), "compact white-bar OCR reads the original received chat message: " + string.Join(" / ", frame.Chat));
            if (expected == 55187)
                Check(frame.LootLines.Any(line => TextParsing.Mesos(line.Text) == 47), "contrast fallback recovers the visible +47 meso notification");
        }
    }

    private static void CheckLootPasses()
    {
        var lines = OcrReader.SelectLootLines(
            [new("You have gained mesos (+47)", .6, .2, 61)],
            [new("You have gained mesos (+9)", .61, .2, 58), new("Leaf x1 earned.", .2, .2, 75)]);
        Check(lines.Length == 2 && TextParsing.Mesos(lines[1].Text) == 47, "alternate OCR passes cannot add a second amount for the same notification row");
        var feed = new LootFeedTracker();
        Check(feed.Update(lines, TimeSpan.Zero).Count == 0, "two OCR passes do not satisfy two-frame loot confirmation");
        Check(feed.Update(lines, TimeSpan.FromSeconds(.5)).Count == 2, "separate readable frames confirm each pickup exactly once");
        Check(feed.Update(lines, TimeSpan.FromSeconds(1)).Count == 0, "stable loot does not repeat after contrast fallback");
    }

    private static void CheckGameplayFrames()
    {
        var directory = Environment.GetEnvironmentVariable("MAPLETRACKER_GAMEPLAY_FIXTURES");
        if (string.IsNullOrEmpty(directory)) return;
        var settings = System.Text.Json.JsonSerializer.Deserialize<TrackerSettings>(System.IO.File.ReadAllText(System.IO.Path.Combine(directory, "tight-settings.json")))!;
        settings.ChatHistoryOnly = true;
        var table = ExperienceTable.Load(RuntimeAssets.ExperienceTablePath);
        using var reader = new OcrReader(table);
        foreach (var (suffix, exp) in new[] { ("-2", 32255L), ("-4", 32263L), ("-5", 32263L) })
        {
            using var image = new Bitmap(System.IO.Path.Combine(directory, $"ocr-game-client{suffix}.png"));
            var frame = reader.Read(image, settings);
            Check(frame.Hud == new HudReading(28, exp), "gameplay frame preserves validated HUD " + suffix);
            Check(frame.Chat.Length > 0, "history row produces chat on gameplay frame " + suffix);
            if (suffix == "-2")
                Check(frame.Chat.Any(line => new TradeClassifier(settings).Classify(line).HasFlag(MessageKind.Selling) && line.Contains("Earring", StringComparison.OrdinalIgnoreCase)), "yellow market banner retains selling intent and the linked item");
            if (suffix == "-4")
                Check(frame.Chat.Any(line => line.Contains("sit", StringComparison.OrdinalIgnoreCase)), "white history chat remains readable over scenery");
            if (suffix == "-5")
            {
                var matcher = new TradeAlertMatcher(new TrackerSettings { SellingItems = ["Fairy wings"] });
                var alerts = frame.Chat.SelectMany(line => matcher.Match(line, DateTimeOffset.Now)).ToArray();
                Check(alerts.Length == 1 && alerts[0].Player == "Gibs", "pink banner OCR reaches the item alert matcher with the sender and buying intent");
            }
        }
    }

    private static void CheckBadgeScreenshot()
    {
        var path = Environment.GetEnvironmentVariable("MAPLETRACKER_BADGE_SCREENSHOT");
        if (string.IsNullOrEmpty(path)) return;
        using var image = new Bitmap(path);
        var badges = ChannelBadges.Find(image, Console.WriteLine);
        Console.WriteLine("Badge bounds: " + string.Join("; ", badges.Select(r => r.ToString())));
        Check(badges.Length == 6, "all six channel/ear rectangles are detected across cyan, pink and yellow rows");
        var table = ExperienceTable.Load(RuntimeAssets.ExperienceTablePath);
        using var reader = new OcrReader(table);
        foreach (var quality in Enum.GetValues<RecognitionQuality>())
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            var result = reader.ReadChat(image, new TrackerSettings { Chat = new(0, 0, 1, 1), ChatHistoryOnly = true, RecognitionQuality = quality }, null);
            Console.WriteLine($"PROFILE {quality}: {clock.Elapsed.TotalMilliseconds:N0} ms, {Environment.WorkingSet / (1024 * 1024)} MB app working set. " + string.Join(" / ", result));
            Check(result.All(line => !System.Text.RegularExpressions.Regex.IsMatch(line, @"\bCH\s*\d{1,3}\b")), "channel badge text is omitted with " + quality);
        }
    }

    private static void CheckRepeatFixtures()
    {
        var folder = Environment.GetEnvironmentVariable("MAPLETRACKER_REPEAT_FIXTURES");
        if (string.IsNullOrEmpty(folder)) return;
        var table = ExperienceTable.Load(RuntimeAssets.ExperienceTablePath);
        using var reader = new OcrReader(table);
        // Frozen frame 1 is unreadable in the previous release; frame 2's accepted
        // value is 33,199 (the screenshot's 33,189 differs by 10 within HUD tolerance).
        foreach (var (name, exp) in new[] { ("game-1", 32800L), ("game-2", 33199L), ("game-final", 33701L) })
        foreach (var quality in Enum.GetValues<RecognitionQuality>())
        {
            using var image = new Bitmap(System.IO.Path.Combine(folder, name + "-client.png"));
            var settings = new TrackerSettings { Chat = new(.21, .886, .4, .025), ChatHistoryOnly = true, RecognitionQuality = quality };
            var frame = reader.Read(image, settings);
            Console.WriteLine($"FRAME {name} {quality}: {frame.OcrMilliseconds:N0} ms, {frame.ChatPasses} chat passes: " + string.Join(" / ", frame.Chat));
            Check(frame.Hud == new HudReading(28, exp) || name == "game-1" && frame.Hud == null, "recognition preset preserves validated HUD or rejects unreadable digits on " + name + " " + quality);
            if (name == "game-1" && quality == RecognitionQuality.High)
                Check(frame.Chat.Any(line => line.Contains("490") && line.Contains("kumbi", StringComparison.OrdinalIgnoreCase)), "high preset retains the observed Kumbi price after omitting its badge");
            if (name == "game-final" && quality == RecognitionQuality.High)
                Check(frame.Chat.Any(line => new TradeClassifier(settings).Classify(line).HasFlag(MessageKind.Selling)), "high preset recognizes selling intent on the previously repeating Scaat banner");
        }
    }

    private static void CheckBundledAssets()
    {
        var tablePath = RuntimeAssets.ExperienceTablePath;
        System.IO.File.WriteAllText(tablePath, "corrupted cache fixture");
        Check(ExperienceTable.Load(RuntimeAssets.ExperienceTablePath).Absolute(100, 0) == 18194778397L, "bundled EXP table repairs an altered cache without external data files");
        var guide = RuntimeAssets.ReadGuide();
        Check(guide.Contains("Buying terms") && guide.Contains("I WANT TO BUY") && guide.Contains("Items") && guide.Contains("cumulative session"), "bundled guide covers custom tags, trade watchlists, pickups and cumulative statistics");
        var help = new HelpWindow(new TrackerSettings()); help.Show(); help.Close();
        Check(RuntimeAssets.ReadNotices().Contains("Apache License"), "third-party notices are available from embedded assets");
        if (RuntimeAssets.HasExperimentalChatModel)
        {
            RuntimeAssets.PrepareNativeOcr();
            var modelDirectory=RuntimeAssets.ExperimentalChatDataDirectory;
            Check(System.IO.File.Exists(System.IO.Path.Combine(modelDirectory,"maple_chat_int.traineddata")), "preview model extracts from embedded assets");
            using var engine=new Tesseract.TesseractEngine(modelDirectory,"maple_chat_int",Tesseract.EngineMode.LstmOnly);
            if (RuntimeAssets.HasExperimentalFallbackModel)
            {
                var fallbackDirectory=RuntimeAssets.ExperimentalFallbackDataDirectory;
                Check(System.IO.File.Exists(System.IO.Path.Combine(fallbackDirectory,"maple_chat_int.traineddata")), "companion font extracts to a separate fallback cache");
                using var fallback=new Tesseract.TesseractEngine(fallbackDirectory,"maple_chat_int",Tesseract.EngineMode.LstmOnly);
            }
            Check(RuntimeAssets.ReadNotices().Contains("tessdata_best"), "preview includes its source-model license attribution");
            var fixture=Environment.GetEnvironmentVariable("MAPLETRACKER_PREVIEW_SMOKE_SCREENSHOT");
            if(!string.IsNullOrEmpty(fixture) && System.IO.File.Exists(fixture))
            {
                using var image=new Bitmap(fixture);
                var table=ExperienceTable.Load(RuntimeAssets.ExperienceTablePath);
                using var regular=new OcrReader(table);
                using var preview=new OcrReader(table);
                var baseline=regular.Read(image,new TrackerSettings());
                var candidate=preview.Read(image,new TrackerSettings { UseExperimentalChatFont=true });
                Check(baseline.Hud!=null && baseline.Hud==candidate.Hud && baseline.Loot.SequenceEqual(candidate.Loot), "experimental chat leaves validated full-frame HUD and loot recognition unchanged");
                Check(candidate.ChatPasses>baseline.ChatPasses, "embedded preview model runs the extra guarded chat pass");
            }
        }
    }

    private static void CheckPreviewProfile()
    {
        var root=System.IO.Path.Combine(SettingsStore.DataDirectory,"profile-check-"+Guid.NewGuid().ToString("N"));
        var source=System.IO.Path.Combine(root,"stable");
        var destination=System.IO.Path.Combine(root,"preview");
        System.IO.Directory.CreateDirectory(source);
        var original=new TrackerSettings { ChatWidth=913, BuyingItems=["Steel Ore"], UseExperimentalChatFont=true };
        var originalJson=System.Text.Json.JsonSerializer.Serialize(original);
        System.IO.File.WriteAllText(System.IO.Path.Combine(source,"settings.json"),originalJson);
        PreviewProfile.Prepare(source,destination);
        var path=System.IO.Path.Combine(destination,"settings.json");
        var copied=System.Text.Json.JsonSerializer.Deserialize<TrackerSettings>(System.IO.File.ReadAllText(path))!;
        Check(copied.ChatWidth==913 && copied.BuyingItems.SequenceEqual(original.BuyingItems), "preview imports calibration and watchlist preferences");
        Check(!copied.UseExperimentalChatFont, "new preview profile starts with the experimental reader off");
        Check(System.IO.File.ReadAllText(System.IO.Path.Combine(source,"settings.json"))==originalJson, "preview profile import preserves stable settings byte for byte");
        copied.UseExperimentalChatFont=true;
        System.IO.File.WriteAllText(path,System.Text.Json.JsonSerializer.Serialize(copied));
        PreviewProfile.Prepare(source,destination);
        Check(System.Text.Json.JsonSerializer.Deserialize<TrackerSettings>(System.IO.File.ReadAllText(path))!.UseExperimentalChatFont, "existing preview settings survive subsequent launches");
        var rejected=false;
        try { PreviewProfile.Prepare(source,source); } catch(ArgumentException) { rejected=true; }
        Check(rejected, "preview profile refuses the stable directory as its destination");
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

    private static void CheckStatisticsControls()
    {
        var controller = new TrackerController(new TrackerSettings { BuyingItems = ["Steel Ore"] });
        try
        {
            controller.AcceptHud(new(27, 50000)); controller.AcceptHud(new(27, 50000));
            controller.AcceptHud(new(27, 51000)); controller.AddLoot("You received 100 mesos.");
            Check(controller.Snapshot.GainedExperience == 1000 && controller.Snapshot.Mesos == 100, "statistics accumulate before Stop");
            controller.StopStatistics();
            controller.AcceptHud(new(27, 55000)); controller.AddLoot("You received 500 mesos.");
            controller.AddChat("Player: S> Steel Ore");
            Check(controller.Snapshot.GainedExperience == 1000 && controller.Snapshot.Mesos == 100, "Stop freezes EXP and meso totals");
            Check(controller.Alerts.Count == 1, "trade alerts continue while statistics are stopped");
            controller.StartStatistics();
            controller.AcceptHud(new(27, 55000)); controller.AcceptHud(new(27, 55000));
            Check(controller.Snapshot.GainedExperience == 1000, "Start rebases EXP after the stopped interval");
            controller.AcceptHud(new(27, 55500)); controller.AddLoot("You received 50 mesos.");
            Check(controller.Snapshot.GainedExperience == 1500 && controller.Snapshot.Mesos == 150, "Start resumes both counters");
            controller.AddLoot("You received 999 mesos.", countStatistics: false);
            Check(controller.Snapshot.Mesos == 150, "obsolete in-flight frame cannot add mesos after a control change");
            controller.ResetStatistics();
            Check(controller.Snapshot.GainedExperience == 0 && controller.Snapshot.Mesos == 0 && controller.Snapshot.ActiveTime == TimeSpan.Zero, "Reset clears statistics and elapsed time");
            Check(controller.Alerts.Count == 1 && controller.Entries.Count > 0 && controller.StatisticsRunning, "statistics Reset preserves chat, alerts and running state");
            controller.StopStatistics(); controller.ResetStatistics();
            Check(!controller.StatisticsRunning, "Reset does not start a stopped statistics tracker");
            controller.SetPopupsEnabled(false);
            int raised = 0; controller.AlertRaised += _ => raised++;
            controller.AddChat("AnotherPlayer: S> Steel Ore");
            Check(raised == 0 && controller.Alerts.Count == 2, "popup switch suppresses popups but preserves history");
        }
        finally { controller.Shutdown().GetAwaiter().GetResult(); }
    }

    private static void CheckNotificationQueue()
    {
        var anchor = new Window { Title = "Notification anchor test", Width = 300, Height = 150 };
        var foreground = new Window { Title = "Foreground game substitute", Width = 350, Height = 180, Topmost = true };
        anchor.Show(); anchor.WindowState = WindowState.Minimized;
        foreground.Show(); foreground.Activate();
        var before = WindowCapture.GetForegroundWindow();
        using var presenter = new TradeNotificationPresenter(anchor);
        try
        {
            presenter.Show(new(DateTimeOffset.Now, "Alice", "Steel Ore", MessageKind.Selling, "Alice: S> Steel Ore"));
            var first = presenter.Current!;
            Check(first.IsVisible && first.Owner == null, "popup remains independent of a minimized chat window");
            Check(WindowCapture.GetForegroundWindow() == before, "background notification preserves game focus");
            var screen = System.Windows.Forms.Screen.FromHandle(new WindowInteropHelper(anchor).Handle).WorkingArea;
            TradeAlertWindow.GetWindowRect(new WindowInteropHelper(first).Handle, out var rectangle);
            Check(rectangle.Left >= screen.Left && rectangle.Top >= screen.Top && rectangle.Right <= screen.Right && rectangle.Bottom <= screen.Bottom,
                "popup is positioned inside the visible monitor work area");
            presenter.Show(new(DateTimeOffset.Now, "Bob", "Potions", MessageKind.Buying, "Bob: B> Potions"));
            Check(ReferenceEquals(first, presenter.Current) && presenter.PendingCount == 1, "second match queues without replacing the visible popup");
            first.Close();
            Check(presenter.Current != null && ((TextBlock)presenter.Current.FindName("SummaryText")).Text == "Bob wants to buy Potions",
                "dismissing a match presents the next queued alert");
            presenter.Clear();
            Check(presenter.Current == null && presenter.PendingCount == 0, "turning off popups clears displayed and queued notifications");
        }
        finally { presenter.Clear(); foreground.Close(); anchor.Close(); }
    }

    private static void CheckModernShell()
    {
        var controller = new TrackerController(new TrackerSettings());
        var window = new MainWindow(controller, new SettingsStore());
        window.Show();
        try
        {
            Check(window.AllowsTransparency && window.WindowStyle == WindowStyle.None, "modern shell supports a translucent custom frame");
            var transparency = (TransparencyControl)window.FindName("Transparency");
            ((Slider)transparency.FindName("TransparencySlider")).Value = 35;
            Check(Math.Abs(controller.Settings.ChatOpacity - .65) < .001 && window.Opacity == 1,
                "bottom slider changes surface transparency without fading text");
            Check(((System.Windows.Media.SolidColorBrush)window.Background).Color.A is >= 164 and <= 166, "surface brush applies the requested alpha");
            var popupSwitch = (CheckBox)window.FindName("PopupToggle");
            popupSwitch.IsChecked = false;
            popupSwitch.RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(!controller.Settings.AlertsEnabled, "chat popup switch updates the saved preference");
            var stats = new StatsWindow(controller);
            stats.Show();
            Check(Math.Abs(controller.Settings.StatisticsOpacity - .9) < .001, "chat transparency does not change statistics transparency");
            var statsTransparency = (TransparencyControl)stats.FindName("Transparency");
            ((Slider)statsTransparency.FindName("TransparencySlider")).Value = 20;
            Check(Math.Abs(controller.Settings.StatisticsOpacity - .8) < .001 && Math.Abs(controller.Settings.ChatOpacity - .65) < .001,
                "statistics transparency changes independently of chat");
            var settingsStore = new SettingsStore(); settingsStore.Save(controller.Settings);
            var restoredSettings = settingsStore.Load();
            Check(Math.Abs(restoredSettings.ChatOpacity - .65) < .001 && Math.Abs(restoredSettings.StatisticsOpacity - .8) < .001,
                "independent transparency settings survive restart");
            ((Button)stats.FindName("StatsStopButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(!controller.StatisticsRunning, "statistics Stop button is wired");
            ((Button)stats.FindName("StatsStartButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(controller.StatisticsRunning, "statistics Start button is wired");
            ((Button)stats.FindName("StatsResetButton")).RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(controller.Snapshot.Mesos == 0, "statistics Reset button is wired");
            stats.Close();
        }
        finally
        {
            window.Close();
            System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
    }

    private static void CheckWorldCatalogIntegration()
    {
        Check(RuntimeAssets.WorldCatalog.Items.Count == 2259 && RuntimeAssets.WorldCatalog.Maps.Count == 336, "offline catalog is embedded in the app");
        Check(RuntimeAssets.ReadNotices().Contains("OSMS Data Explorer") && RuntimeAssets.ReadNotices().Contains("creativecommons.org/licenses/by/4.0"), "OSMS attribution and license travel with the EXE");
        var store = new SettingsStore();
        store.Save(new TrackerSettings { Map = null!, AutoMapRegion = false, BuyingItems = ["Steel Ore"], MapItemHints = new() { [10003010] = [4000006] } });
        var migrated = store.Load();
        Check(migrated.AutoMapRegion && migrated.Map.IsValid && migrated.BuyingItems.SequenceEqual(new[] { "Steel Ore" }), "invalid map calibration recovers without discarding existing watchlists");
        Check(migrated.MapItemHints[10003010].SequenceEqual(new[] { 4000006 }), "observed map hints survive a settings round trip");
        var controller = new TrackerController(new TrackerSettings());
        var stats = new StatsWindow(controller);
        try
        {
            var map = RuntimeAssets.WorldCatalog.MatchMap(["Victoria Road", "Kerning City Construction Site"])!;
            controller.AcceptMap(map); controller.AcceptMap(map);
            controller.AddLoot("Squishy Liquid x19 earned. (Etc)");
            controller.AddLoot("Squishy Liguid x5 earned. (Etc)");
            Check(controller.Items.Count == 1 && controller.Items["Squishy Liquid"] == 24, "controller merges misspelled loot totals without changing quantity");
            Check(controller.Entries.Last() is { Text: "Squishy Liquid x5 earned. (Etc)", RawText: "Squishy Liguid x5 earned. (Etc)", ItemId: 4000006, MapId: 10003010 }, "corrected feed preserves original OCR and pickup map");
            stats.Show(); stats.Refresh();
            Check(((TextBlock)stats.FindName("MapTitle")).Text == map.Name, "statistics displays the confirmed canonical map title");
            Check(controller.MapInfo.Contains("Monsters:") && controller.MapInfo.Contains("Session rates continue"), "map tooltip describes local monsters and cumulative session rates");
            Check(stats.FindName("Diagnostics") == null, "statistics no longer exposes Recognition details");
            var preview = Environment.GetEnvironmentVariable("MAPLETRACKER_MAP_STATS_PREVIEW");
            if (!string.IsNullOrEmpty(preview))
            {
                stats.UpdateLayout();
                var bitmap = new System.Windows.Media.Imaging.RenderTargetBitmap((int)stats.ActualWidth, (int)stats.ActualHeight, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                bitmap.Render(stats);
                var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder(); encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bitmap));
                using var output = System.IO.File.Create(preview); encoder.Save(output);
            }
        }
        finally { stats.Close(); controller.Shutdown().GetAwaiter().GetResult(); }
        using var reader = new OcrReader(ExperienceTable.Load(RuntimeAssets.ExperienceTablePath));
        foreach (var (variable, expected) in new[] {
            ("MAPLETRACKER_CONSTRUCTION_MAP", "Kerning City Construction Site"),
            ("MAPLETRACKER_ORIGINAL_SCREENSHOT", "Ant Tunnel Park"),
            ("MAPLETRACKER_ANT_MAP", "Ant Tunnel III") })
        {
            var path = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(path)) continue;
            using var image = new Bitmap(path);
            var frame = reader.Read(image, new TrackerSettings());
            Check(frame.Map?.Name == expected, "real minimap title resolves: " + expected + " (" + string.Join(" / ", frame.MapText) + ")");
            var manual = new TrackerSettings { AutoMapRegion = false };
            var region = OcrReader.FindMapTitle(image, manual.Map)!.Value;
            manual.Map = new(region.X / (double)image.Width, region.Y / (double)image.Height, region.Width / (double)image.Width, region.Height / (double)image.Height);
            Check(reader.Read(image, manual).Map?.Name == expected, "manual map calibration reads the same title: " + expected);
        }
        using var blank = new Bitmap(1920, 1080);
        Check(reader.Read(blank, new TrackerSettings()).Map == null, "unreadable minimap does not reuse a cached map");
    }

    private static void CheckMesoGlyphReplay()
    {
        var store = new SettingsStore();
        store.Save(new TrackerSettings { Loot = new(.85, .75, .15, .06) });
        Check(store.Load().Loot == new TrackerSettings().Loot, "legacy default loot box expands to include the party notification stack");
        var custom = new CaptureRegion(.82, .70, .17, .12);
        store.Save(new TrackerSettings { Loot = custom });
        Check(store.Load().Loot == custom, "custom loot calibration survives the default-region upgrade");
        var folder = Environment.GetEnvironmentVariable("MAPLETRACKER_MESO_FIXTURES");
        if (string.IsNullOrEmpty(folder)) return;
        var controller = new TrackerController(new TrackerSettings());
        using var reader = new OcrReader(controller.Table);
        try
        {
            RecognitionFrame Read(int index)
            {
                using var image = new Bitmap(System.IO.Path.Combine(folder, $"game-{index:D4}.png"));
                return reader.Read(image, controller.Settings);
            }
            long[] Mesos(RecognitionFrame frame) => frame.Loot.Select(TextParsing.Mesos).OfType<long>().ToArray();
            Check(Mesos(Read(0)).SequenceEqual(new long[] { 7, 15, 7 }), "bright-map screenshot reads all three meso rows, including those above the old crop");
            Check(Mesos(Read(18)).SequenceEqual(new long[] { 4, 7, 5 }), "meso glyph reader separates amounts in a different notification stack");
            Check(Mesos(Read(3)).Length == 0 && Mesos(Read(20)).Length == 0, "party EXP and empty scenery do not become meso pickups");
            var feed = new LootFeedTracker();
            var first = Read(16); var next = Read(17);
            Check(feed.Update(first.LootLines, TimeSpan.Zero).Count == 0, "one frame does not confirm a visual meso reading");
            var added = feed.Update(next.LootLines, TimeSpan.FromSeconds(.65));
            foreach (var line in added) controller.AddLoot(line);
            Check(controller.Snapshot.Mesos == 12, "same visual pickup on two frames reaches Statistics exactly once");
            Check(feed.Update(next.LootLines.Where(l => TextParsing.Mesos(l.Text) == 12), TimeSpan.FromSeconds(1.3)).Count == 0, "the same visible meso notification is not counted again");
        }
        finally { controller.Shutdown().GetAwaiter().GetResult(); }
    }
}
