using MapleTracker.App.Services;

namespace MapleTracker.App;

public partial class StatsWindow : Window
{
    private readonly TrackerController controller;
    private readonly Action? requestSetup;
    public StatsWindow(TrackerController controller, Action? requestSetup = null)
    {
        this.controller = controller; this.requestSetup = requestSetup; InitializeComponent(); var s = controller.Settings;
        WindowAppearance.Restore(this, s.StatsLeft, s.StatsTop, s.StatsWidth, s.StatsHeight);
        Topmost = s.AlwaysOnTop; ApplyAppearance();
        Transparency.Initialize(controller, Save, statistics: true);
        controller.AppearanceChanged += ApplyAppearance;
        Closing += (_, _) => { RememberBounds(); Save(); };
        Closed += (_, _) => controller.AppearanceChanged -= ApplyAppearance;
        Refresh();
    }
    private void ApplyAppearance() => WindowAppearance.SetSurfaceOpacity(this, controller.Settings.StatisticsOpacity);
    private void Save()
    {
        try { new SettingsStore().Save(controller.Settings); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { MessageBox.Show(this, error.Message, "Could not save preferences"); }
    }
    private void StartClick(object sender, RoutedEventArgs e)
    {
        if (controller.Target == null && requestSetup != null) { requestSetup(); return; }
        controller.StartStatistics(); Refresh();
    }
    private void StopClick(object sender, RoutedEventArgs e) { controller.StopStatistics(); Refresh(); }
    private void ResetClick(object sender, RoutedEventArgs e) { controller.ResetStatistics(); Refresh(); }
    public void RememberBounds() { if (WindowState != WindowState.Normal) return; var s = controller.Settings; s.StatsLeft = Left; s.StatsTop = Top; s.StatsWidth = Width; s.StatsHeight = Height; }
    public void Refresh()
    {
        MapTitle.Text = controller.MapTitle; MapTitle.ToolTip = controller.MapInfo;
        var s = controller.Snapshot; Mode.Text = !controller.StatisticsRunning ? "STOPPED" : controller.Running ? "RUNNING" : "CAPTURE PAUSED";
        StatsStartButton.IsEnabled = !controller.StatisticsRunning || !controller.Running;
        StatsStopButton.IsEnabled = controller.StatisticsRunning;
        if (s.Level != null)
        {
            LevelText.Text = s.Level == 100 ? "Level 100 · table complete" : $"Level {s.Level} → {s.Level + 1}";
            ExperienceText.Text = s.Required != null ? $"{s.Experience:N0} / {s.Required:N0} EXP" : $"{s.Experience:N0} EXP";
            ExperienceProgress.Value = s.Required is > 0 ? 100.0 * s.Experience!.Value / s.Required.Value : 100;
            RemainingText.Text = s.Required != null ? $"{s.Required - s.Experience:N0} left · {ExperienceProgress.Value:F2}% · {controller.HudStatus}" : "No next-level requirement in the supplied table";
        }
        else { LevelText.Text = "Waiting for EXP"; ExperienceText.Text = "Select EXP in Setup"; ExperienceProgress.Value = 0; RemainingText.Text = ""; }
        EtaText.Text = s.Eta == null ? "Gathering pace…" : FormatDuration(s.Eta.Value); if (s.Level == 100) EtaText.Text = "Table complete";
        XpHour.Text = Compact(s.ExperiencePerMinute * 60); XpMinute.Text = $"{s.ExperiencePerMinute:N0} / min";
        MesoHour.Text = Compact(s.MesosPerMinute * 60); MesoMinute.Text = $"{s.MesosPerMinute:N0} / min";
        TotalsText.Text = $"EXP gained    {s.GainedExperience:N0}\nMeso looted   {s.Mesos:N0}\nSession avg   {s.ExperiencePerMinute:N0} EXP / min";
        ActiveText.Text = "Active capture    " + FormatDuration(s.ActiveTime);
    }
    private static string Compact(double v) => Math.Abs(v) >= 1e9 ? $"{v / 1e9:0.##}B" : Math.Abs(v) >= 1e6 ? $"{v / 1e6:0.##}M" : Math.Abs(v) >= 1e3 ? $"{v / 1e3:0.#}K" : $"{v:N0}";
    private static string FormatDuration(TimeSpan value) => value.TotalDays >= 1 ? $"{(int)value.TotalDays}d {value.Hours}h {value.Minutes}m" : value.TotalHours >= 1 ? $"{(int)value.TotalHours}h {value.Minutes}m" : $"{value.Minutes}m {value.Seconds}s";
}
