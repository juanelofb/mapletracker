using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using MapleTracker.App.Services;
using Microsoft.Win32;
using Point = System.Windows.Point;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace MapleTracker.App;

public partial class SetupWindow : Window
{
    private readonly TrackerController controller;
    private readonly TrackerSettings draft;
    private readonly Func<nint, Task<Bitmap>> captureSnapshot;
    private Bitmap? reference;
    private Point? dragStart;
    private CaptureRegion? dragging;
    private bool working;
    public SetupWindow(TrackerController controller) : this(controller, CaptureGameSnapshotAsync) { }

    internal SetupWindow(TrackerController controller, Func<nint, Task<Bitmap>> captureSnapshot)
    {
        this.controller = controller;
        this.captureSnapshot = captureSnapshot;
        draft = JsonSerializer.Deserialize<TrackerSettings>(JsonSerializer.Serialize(controller.Settings))!;
        InitializeComponent();
        HistoryOnly.IsChecked = draft.ChatHistoryOnly;
        OmitBadges.IsChecked = draft.IgnoreChannelBadges;
        QualityPicker.ItemsSource = Enum.GetValues<RecognitionQuality>();
        QualityPicker.SelectedItem = draft.RecognitionQuality;
        ResourceUsage.Text = $"Last captured frame: {controller.LastOcrMilliseconds:N0} ms · Current app memory: {controller.WorkingSetMegabytes:N0} MB. Test recognition measures the selected preset.";
        BuyingTerms.Text = string.Join(", ", draft.BuyingTerms); SellingTerms.Text = string.Join(", ", draft.SellingTerms);
        RefreshWindows(); UpdateRegionInfo(); Loaded += (_, _) => WindowAppearance.DarkTitleBar(this);
        Closed += (_, _) => reference?.Dispose();
        Closing += (_, e) => { if (working) e.Cancel = true; };
    }
    private void RefreshWindows()
    {
        var windows = WindowCapture.List(); WindowPicker.ItemsSource = windows;
        WindowPicker.SelectedItem = windows.FirstOrDefault(w => w.Handle == controller.Target?.Handle)
            ?? windows.FirstOrDefault(w => w.Title == draft.WindowTitle);
    }
    private void RefreshClick(object sender, RoutedEventArgs e) => RefreshWindows();
    private void QualityChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (QualityDescription == null) return;
        QualityDescription.Text = (QualityPicker.SelectedItem is RecognitionQuality quality ? quality : RecognitionQuality.Balanced) switch
        {
            RecognitionQuality.Low => "Low: 1 contrast view per chat row/part. Least OCR work; difficult text may be missed or misread.",
            RecognitionQuality.High => "High: 4 contrast views per chat row/part, with candidate comparison. More CPU work; difficult text and prices may still be wrong.",
            _ => "Balanced: 2 contrast views per chat row/part. A compromise between CPU work and difficult text recognition."
        };
        QualityDescription.Text += " All presets share one OCR model. Extra passes run sequentially; RAM and processing time vary with the selected region and PC.";
    }
    private async void SnapshotClick(object sender, RoutedEventArgs e) => await TakeSnapshotAsync();

    internal async Task TakeSnapshotAsync()
    {
        if (working) return;
        if (WindowPicker.SelectedItem is not GameWindow target) { SetupStatus.Text = "Choose the game window first."; return; }
        working = true;
        try
        {
            using var suppressed = new SnapshotWindowScope(System.Windows.Application.Current.Windows.Cast<Window>());
            SetReference(await captureSnapshot(target.Handle));
            SetupStatus.Text = "Snapshot ready. Choose each region and drag a box around its text.";
        }
        catch (Exception ex) { SetupStatus.Text = ex.Message; }
        finally
        {
            working = false;
            Activate();
        }
    }
    private static async Task<Bitmap> CaptureGameSnapshotAsync(nint handle)
    {
        WindowCapture.SetForegroundWindow(handle);
        await Task.Delay(650);
        if (WindowCapture.GetForegroundWindow() != handle) throw new InvalidOperationException("Could not focus the game. Focus it manually, then try again.");
        return WindowCapture.Capture(handle);
    }
    private void LoadClick(object sender, RoutedEventArgs e)
    {
        if (working) return;
        var dialog = new OpenFileDialog { Filter = "Screenshots|*.png;*.jpg;*.jpeg;*.bmp" };
        if (dialog.ShowDialog(this) != true) return;
        try { SetReference(new Bitmap(dialog.FileName)); SetupStatus.Text = "Image loaded. Regions must match the game's client area, without window borders."; }
        catch (Exception ex) { SetupStatus.Text = "Cannot load image: " + ex.Message; }
    }
    private void SetReference(Bitmap image)
    {
        reference?.Dispose(); reference = image;
        using var stream = new MemoryStream(); image.Save(stream, ImageFormat.Png); stream.Position = 0;
        var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.StreamSource = stream; source.EndInit(); source.Freeze();
        ReferenceImage.Source = source; PreviewHint.Visibility = Visibility.Collapsed; DrawRegions();
    }
    private CaptureRegion CurrentRegion => LootRadio?.IsChecked == true ? draft.Loot : LevelRadio?.IsChecked == true ? draft.Level : ExpRadio?.IsChecked == true ? draft.Experience : draft.Chat;
    private void SetCurrent(CaptureRegion region)
    {
        if (LootRadio.IsChecked == true) draft.Loot = region;
        else if (LevelRadio.IsChecked == true) draft.Level = region;
        else if (ExpRadio.IsChecked == true) draft.Experience = region;
        else draft.Chat = region;
    }
    private System.Windows.Rect ImageBounds()
    {
        if (reference == null) return System.Windows.Rect.Empty;
        var scale = Math.Min(Preview.ActualWidth / reference.Width, Preview.ActualHeight / reference.Height);
        var w = reference.Width * scale; var h = reference.Height * scale;
        return new((Preview.ActualWidth - w) / 2, (Preview.ActualHeight - h) / 2, w, h);
    }
    private Point? Normalize(Point p, bool clamp = false)
    {
        var r = ImageBounds(); if (r.IsEmpty || r.Width <= 0 || r.Height <= 0 || !clamp && !r.Contains(p)) return null;
        return new(Math.Clamp((p.X - r.X) / r.Width, 0, 1), Math.Clamp((p.Y - r.Y) / r.Height, 0, 1));
    }
    private void SelectionStart(object sender, MouseButtonEventArgs e)
    {
        dragStart = Normalize(e.GetPosition(SelectionCanvas)); if (dragStart != null) SelectionCanvas.CaptureMouse();
    }
    private void SelectionMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (dragStart is not Point start || Normalize(e.GetPosition(SelectionCanvas), true) is not Point end) return;
        dragging = new(Math.Min(start.X, end.X), Math.Min(start.Y, end.Y), Math.Abs(end.X - start.X), Math.Abs(end.Y - start.Y)); DrawRegions();
    }
    private void SelectionEnd(object sender, MouseButtonEventArgs e)
    {
        if (dragging is { IsValid: true }) SetCurrent(dragging);
        dragStart = null; dragging = null; SelectionCanvas.ReleaseMouseCapture(); DrawRegions(); UpdateRegionInfo();
    }
    private void RegionChanged(object sender, RoutedEventArgs e) { if (SelectionCanvas == null) return; DrawRegions(); UpdateRegionInfo(); }
    private void UpdateRegionInfo() { if (RegionInfo != null) RegionInfo.Text = "Selected box\n" + CurrentRegion; }
    private void PreviewSizeChanged(object sender, SizeChangedEventArgs e) => DrawRegions();
    private void DrawRegions()
    {
        if (SelectionCanvas == null) return;
        SelectionCanvas.Children.Clear(); var r = ImageBounds(); if (r.IsEmpty) return;
        void Draw(CaptureRegion region, string color, bool selected)
        {
            var rectangle = new Rectangle { Width = region.Width * r.Width, Height = region.Height * r.Height, Stroke = (SolidColorBrush)new BrushConverter().ConvertFromString(color)!, StrokeThickness = selected ? 3 : 1, IsHitTestVisible = false };
            Canvas.SetLeft(rectangle, r.X + region.X * r.Width); Canvas.SetTop(rectangle, r.Y + region.Y * r.Height); SelectionCanvas.Children.Add(rectangle);
        }
        Draw(draft.Chat, "#75C6E2", ReferenceEquals(CurrentRegion, draft.Chat)); Draw(draft.Loot, "#E1BC70", ReferenceEquals(CurrentRegion, draft.Loot));
        Draw(draft.Level, "#F07961", ReferenceEquals(CurrentRegion, draft.Level)); Draw(draft.Experience, "#C7D66A", ReferenceEquals(CurrentRegion, draft.Experience));
        if (dragging != null) Draw(dragging, "#FFFFFF", true);
    }
    private async void TestClick(object sender, RoutedEventArgs e)
    {
        if (reference == null) { SetupStatus.Text = "Load an image or take a snapshot first."; return; }
        if (working) return; working = true; SetupStatus.Text = "Testing local OCR…";
        try
        {
            using var image = (Bitmap)reference.Clone();
            draft.ChatHistoryOnly = HistoryOnly.IsChecked == true;
            draft.IgnoreChannelBadges = OmitBadges.IsChecked == true;
            draft.RecognitionQuality = QualityPicker.SelectedItem is RecognitionQuality quality ? quality : RecognitionQuality.Balanced;
            var result = await Task.Run(() => { using var reader = new OcrReader(controller.Table); return reader.Read(image, draft); });
            ResourceUsage.Text = $"Test: {result.OcrMilliseconds:N0} ms / frame · {result.ChatPasses} chat passes · Current app memory: {controller.WorkingSetMegabytes:N0} MB. One frame, not a fixed resource guarantee.";
            var text = $"LEVEL: {result.LevelText}\nEXP: {result.ExpText}\nValidated HUD: {(result.Hud == null ? "not readable" : $"Level {result.Hud.Level}, EXP {result.Hud.Experience:N0}")}\n\nCHAT\n{string.Join('\n', result.Chat)}\n\nLOOT\n{string.Join('\n', result.Loot)}";
            var results = new Window { Owner = this, Style = (Style)FindResource("TrackerWindow"), Title = "MapleTracker · Recognition test", Width = 680, Height = 480, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = new TextBox { Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(20) } };
            results.ShowDialog(); SetupStatus.Text = result.Hud == null ? "HUD unreadable. Tighten the level and EXP boxes, then test again." : "HUD recognized. Review chat and loot before saving.";
        }
        catch (Exception ex) { SetupStatus.Text = ex.Message; }
        finally { working = false; }
    }
    private void SaveClick(object sender, RoutedEventArgs e)
    {
        if (working) return;
        if (WindowPicker.SelectedItem is not GameWindow target) { SetupStatus.Text = "Choose a game window before saving."; return; }
        var s = controller.Settings;
        bool changedSource = controller.Target?.Handle != target.Handle;
        bool changedRegions = s.Chat != draft.Chat || s.Loot != draft.Loot || s.Level != draft.Level || s.Experience != draft.Experience || s.ChatHistoryOnly != (HistoryOnly.IsChecked == true);
        if (changedSource || changedRegions) controller.Reset();
        s.Chat = draft.Chat; s.Loot = draft.Loot; s.Level = draft.Level; s.Experience = draft.Experience;
        s.ChatHistoryOnly = HistoryOnly.IsChecked == true;
        s.IgnoreChannelBadges = OmitBadges.IsChecked == true;
        s.RecognitionQuality = QualityPicker.SelectedItem is RecognitionQuality quality ? quality : RecognitionQuality.Balanced;
        s.BuyingTerms = BuyingTerms.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        s.SellingTerms = SellingTerms.Text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        s.WindowTitle = target.Title; controller.Target = target;
        DialogResult = true;
    }
}
