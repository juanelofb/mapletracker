using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;

namespace MapleTracker.App.Services;

public sealed class TrackerController
{
    public TrackerSettings Settings { get; }
    public ExperienceTable Table { get; }
    public ObservableCollection<FeedEntry> Entries { get; } = [];
    public ObservableCollection<TradeAlert> Alerts { get; } = [];
    public event Action<TradeAlert>? AlertRaised;
    public Dictionary<string, long> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    public GameWindow? Target { get; set; }
    public string Status { get; private set; } = "Choose your game window in Setup to begin.";
    public string DiagnosticsText { get; private set; } = "No frames read yet.";
    public bool Running { get; private set; }
    public bool StatisticsRunning { get; private set; } = true;
    public event Action? AppearanceChanged;
    public event Action? Changed;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Stopwatch active = new();
    private readonly Stopwatch notificationClock = Stopwatch.StartNew();
    private readonly LineTracker chatLines = new();
    private readonly LootFeedTracker lootLines = new();
    private readonly SessionTracker session;
    private readonly TradeAlertMatcher alertMatcher;
    private OcrReader? ocr;
    private bool busy;
    private int generation;
    private HudReading? pendingHud;
    private long lastConfirmedAbsolute;
    private bool hasHud;
    private int statisticsGeneration;
    private bool rebaseStatistics;
    private StreamWriter? journal;
    private string? journalWarning;
    private DateTimeOffset? lastHudAt;
    public string HudStatus => !StatisticsRunning ? "Statistics stopped · totals preserved" : lastHudAt == null ? "Waiting for a confirmed EXP reading" :
        $"EXP confirmed {Math.Max(0, (int)(DateTimeOffset.UtcNow - lastHudAt.Value).TotalSeconds)}s ago";
    public SessionSnapshot Snapshot => session.Snapshot(active.Elapsed);

    public TrackerController(TrackerSettings settings)
    {
        Settings = settings;
        Table = ExperienceTable.Load(Path.Combine(SettingsStore.DataDirectory, "exp-table.json"));
        session = new(Table);
        alertMatcher = new(settings);
        timer.Tick += async (_, _) => await Tick();
        timer.Start();
    }
    public void Start()
    {
        if (Target == null) { Status = "Select a game window in Setup first."; Changed?.Invoke(); return; }
        Running = true;
        timer.Interval = TimeSpan.FromMilliseconds(Settings.PollMilliseconds);
        Status = "Waiting for game focus…";
        Changed?.Invoke();
    }
    public void Pause()
    {
        Running = false; SuspendStatisticsClock(); generation++; Status = "Capture paused · session preserved"; Changed?.Invoke();
    }
    public void Reset()
    {
        generation++; statisticsGeneration++; Running = false; active.Reset(); session.Reset(); rebaseStatistics = false;
        chatLines.Reset(); lootLines.Reset(); pendingHud = null; hasHud = false;
        Entries.Clear(); Items.Clear(); Alerts.Clear(); alertMatcher.Reset(); journal?.Dispose(); journal = null; journalWarning = null;
        lastHudAt = null;
        Status = "New session · ready"; DiagnosticsText = "No frames read yet."; Changed?.Invoke();
    }
    public void StartStatistics()
    {
        if (!StatisticsRunning)
        {
            StatisticsRunning = true; statisticsGeneration++; rebaseStatistics = hasHud; pendingHud = null;
        }
        if (!Running && Target != null) Start();
        Changed?.Invoke();
    }
    public void StopStatistics()
    {
        StatisticsRunning = false; statisticsGeneration++; SuspendStatisticsClock(); Changed?.Invoke();
    }
    public void ResetStatistics()
    {
        statisticsGeneration++; active.Reset(); session.Reset(); pendingHud = null; hasHud = false;
        rebaseStatistics = false; lastHudAt = null; Changed?.Invoke();
    }
    private void SuspendStatisticsClock()
    {
        if (active.IsRunning) { active.Stop(); rebaseStatistics = hasHud; pendingHud = null; }
    }
    public void SetOpacity(double opacity)
    {
        Settings.Opacity = Math.Clamp(opacity, .4, 1);
        AppearanceChanged?.Invoke();
    }
    public void SetPopupsEnabled(bool enabled)
    {
        Settings.AlertsEnabled = enabled; Changed?.Invoke();
    }

    private async Task Tick()
    {
        if (busy) { Changed?.Invoke(); return; }
        if (!Running) { Changed?.Invoke(); return; }
        var target = Target;
        if (target == null || WindowCapture.IsIconic(target.Handle) || WindowCapture.GetForegroundWindow() != target.Handle)
        {
            SuspendStatisticsClock(); Status = "Waiting · focus the game to capture"; Changed?.Invoke(); return;
        }
        if (WindowCapture.RegionsCovered(target.Handle, Settings))
        {
            SuspendStatisticsClock(); Status = "Waiting · move other windows away from the game's text regions"; Changed?.Invoke(); return;
        }
        if (StatisticsRunning) active.Start();
        busy = true;
        var version = generation;
        var statsVersion = statisticsGeneration;
        try
        {
            var frame = await Task.Run(() =>
            {
                using var bitmap = WindowCapture.Capture(target.Handle);
                ocr ??= new(Table);
                return ocr.Read(bitmap, Settings);
            });
            if (version != generation || !Running) return;
            if (WindowCapture.GetForegroundWindow() != target.Handle || WindowCapture.RegionsCovered(target.Handle, Settings)) { SuspendStatisticsClock(); return; }
            foreach (var line in chatLines.Update(frame.Chat)) AddChat(line);
            foreach (var line in lootLines.Update(frame.LootLines, notificationClock.Elapsed)) AddLoot(line, statsVersion == statisticsGeneration);
            if (statsVersion == statisticsGeneration) AcceptHud(frame.Hud);
            DiagnosticsText = $"{HudStatus}\nLEVEL  {frame.LevelText}\nEXP attempts\n{string.Join('\n', frame.HudCandidates.Select(c => c.Text))}\n\nLOOT\n{string.Join('\n', frame.Loot)}\n\nCHAT\n{string.Join('\n', frame.Chat)}";
            WriteRecognitionDiagnostics(frame);
            Status = (frame.Hud == null ? "Reading · EXP unreadable; check regions in Setup" : "Live · " + target.Title) + (journalWarning == null ? "" : " · " + journalWarning);
        }
        catch (Exception ex)
        {
            Running = false; SuspendStatisticsClock(); Status = "Capture stopped · " + ex.Message;
        }
        finally { busy = false; Changed?.Invoke(); }
    }

    internal void AcceptHud(HudReading? reading)
    {
        if (!StatisticsRunning) return;
        if (reading == null) { pendingHud = null; return; }
        var absolute = Table.Absolute(reading.Level, reading.Experience);
        // Confirm baseline, losses, level changes and large jumps using two successive plausible observations.
        var snapshot = Snapshot;
        var unusual = rebaseStatistics || !hasHud || reading.Level != snapshot.Level || absolute < lastConfirmedAbsolute ||
            absolute - lastConfirmedAbsolute > (Table.Required(reading.Level) ?? long.MaxValue) / 10;
        if (unusual && (pendingHud == null || pendingHud.Level != reading.Level ||
            Math.Abs(absolute - Table.Absolute(pendingHud.Level, pendingHud.Experience)) > (Table.Required(reading.Level) ?? long.MaxValue) / 20))
        { pendingHud = reading; return; }
        pendingHud = null;
        if (rebaseStatistics) { session.ResumeAt(reading); rebaseStatistics = false; }
        if (session.Observe(reading, active.Elapsed)) { lastConfirmedAbsolute = absolute; hasHud = true; lastHudAt = DateTimeOffset.UtcNow; }
    }

    private DateTimeOffset nextDiagnosticsWrite;
    private void WriteRecognitionDiagnostics(RecognitionFrame frame)
    {
        if (DateTimeOffset.UtcNow < nextDiagnosticsWrite) return;
        nextDiagnosticsWrite = DateTimeOffset.UtcNow.AddSeconds(2);
        try
        {
            Directory.CreateDirectory(SettingsStore.DataDirectory);
            File.WriteAllText(Path.Combine(SettingsStore.DataDirectory, "recognition-last.json"), JsonSerializer.Serialize(new
            {
                Time = DateTimeOffset.Now, LastConfirmedHud = lastHudAt, Frame = frame, Snapshot
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    internal void AddChat(string text)
    {
        var now = DateTimeOffset.Now;
        AddEntry(new(now, text, new TradeClassifier(Settings).Classify(text)));
        foreach (var alert in alertMatcher.Match(text, now))
        {
            Alerts.Insert(0, alert);
            if (Alerts.Count > 200) Alerts.RemoveAt(Alerts.Count - 1);
            if (Settings.AlertsEnabled) AlertRaised?.Invoke(alert);
        }
    }
    internal void AddLoot(string text, bool countStatistics = true)
    {
        if (StatisticsRunning && countStatistics && TextParsing.Mesos(text) is long amount) session.AddMesos(amount);
        if (TextParsing.Loot(text) is not { } item) return;
        Items[item.Name] = Items.GetValueOrDefault(item.Name) + item.Quantity;
        AddEntry(new(DateTimeOffset.Now, text, MessageKind.Item, item.Name, item.Quantity));
    }
    private void AddEntry(FeedEntry entry)
    {
        Entries.Add(entry);
        if (Entries.Count > 5000) Entries.RemoveAt(0);
        try
        {
            if (journal == null)
            {
                var folder = Path.Combine(SettingsStore.DataDirectory, "sessions"); Directory.CreateDirectory(folder);
                journal = new StreamWriter(Path.Combine(folder, $"{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.jsonl")) { AutoFlush = true };
            }
            journal.WriteLine(JsonSerializer.Serialize(entry));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { journalWarning = "session log unavailable"; }
    }
    public void Export(string file)
    {
        var snapshot = Snapshot;
        File.WriteAllText(file, JsonSerializer.Serialize(new { StatisticsRunning, Snapshot = snapshot, Items, Entries, Alerts }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public async Task WaitForIdle() { while (busy) await Task.Delay(30); }
    public async Task Shutdown()
    {
        Pause(); timer.Stop(); await WaitForIdle(); ocr?.Dispose(); journal?.Dispose();
    }
}
