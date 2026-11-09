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
    public Dictionary<string, long> Items { get; } = new(StringComparer.OrdinalIgnoreCase);
    public GameWindow? Target { get; set; }
    public string Status { get; private set; } = "Choose your game window in Setup to begin.";
    public string DiagnosticsText { get; private set; } = "No frames read yet.";
    public bool Running { get; private set; }
    public bool Demo { get; private set; }
    public event Action? Changed;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly Stopwatch active = new();
    private readonly LineTracker chatLines = new();
    private readonly LineTracker lootLines = new();
    private readonly SessionTracker session;
    private OcrReader? ocr;
    private bool busy;
    private int generation;
    private HudReading? pendingHud;
    private long lastConfirmedAbsolute;
    private bool hasHud;
    private int demoStep;
    private StreamWriter? journal;
    private string? journalWarning;
    public SessionSnapshot Snapshot => session.Snapshot(active.Elapsed);

    public TrackerController(TrackerSettings settings)
    {
        Settings = settings;
        Table = ExperienceTable.Load(Path.Combine(SettingsStore.DataDirectory, "exp-table.json"));
        session = new(Table);
        timer.Tick += async (_, _) => await Tick();
        timer.Start();
    }
    public void Start()
    {
        if (Target == null) { Status = "Select a game window in Setup first."; Changed?.Invoke(); return; }
        if (Demo) Reset();
        Demo = false; Running = true;
        timer.Interval = TimeSpan.FromMilliseconds(Settings.PollMilliseconds);
        Status = "Waiting for game focus…";
        Changed?.Invoke();
    }
    public void Pause()
    {
        Running = false; active.Stop(); generation++; Status = "Paused · session preserved"; Changed?.Invoke();
    }
    public void Reset()
    {
        generation++; Running = false; Demo = false; active.Reset(); session.Reset();
        chatLines.Reset(); lootLines.Reset(); pendingHud = null; hasHud = false;
        Entries.Clear(); Items.Clear(); journal?.Dispose(); journal = null; journalWarning = null;
        Status = "New session · ready"; DiagnosticsText = "No frames read yet."; Changed?.Invoke();
    }
    public void StartDemo()
    {
        Reset(); Demo = true; Running = true; demoStep = 0;
        timer.Interval = TimeSpan.FromMilliseconds(1000);
        session.Observe(new(27, 50548), TimeSpan.Zero);
        active.Start(); Status = "DEMO · simulated messages and gains"; Changed?.Invoke();
    }

    private async Task Tick()
    {
        if (busy) return;
        if (!Running) { Changed?.Invoke(); return; }
        if (Demo)
        {
            active.Start(); demoStep++;
            var absolute = 50548L + demoStep * 75;
            var level = 27;
            while (Table.Required(level) is long needed && absolute >= needed && level < 100) { absolute -= needed; level++; }
            session.Observe(new(level, absolute), active.Elapsed);
            if (demoStep % 2 == 1)
            {
                string[] messages = ["LeafTrader: B> Dagger Attack Scroll — offer", "MushroomShop: S> Steel Ore x20", "Randgris: Anyone heading to Kerning?", "Trader: WTB potions / WTS ores"];
                AddChat(messages[(demoStep / 2) % messages.Length]);
                AddLoot("Charm of the Undead x1 earned. (Etc)");
                AddLoot("You have gained mesos (+125)");
            }
            Changed?.Invoke(); return;
        }
        var target = Target;
        if (target == null || WindowCapture.IsIconic(target.Handle) || WindowCapture.GetForegroundWindow() != target.Handle)
        {
            active.Stop(); Status = "Waiting · focus the game to capture (session clock stopped)"; Changed?.Invoke(); return;
        }
        if (WindowCapture.RegionsCovered(target.Handle, Settings))
        {
            active.Stop(); Status = "Waiting · move other windows away from the game's text regions"; Changed?.Invoke(); return;
        }
        active.Start(); busy = true;
        var version = generation;
        try
        {
            var frame = await Task.Run(() =>
            {
                using var bitmap = WindowCapture.Capture(target.Handle);
                ocr ??= new(Table);
                return ocr.Read(bitmap, Settings);
            });
            if (version != generation || !Running) return;
            if (WindowCapture.GetForegroundWindow() != target.Handle || WindowCapture.RegionsCovered(target.Handle, Settings)) { active.Stop(); return; }
            foreach (var line in chatLines.Update(frame.Chat)) AddChat(line);
            foreach (var line in lootLines.Update(frame.Loot)) AddLoot(line);
            AcceptHud(frame.Hud);
            DiagnosticsText = $"LEVEL  {frame.LevelText}\nEXP  {frame.ExpText}\n\nLOOT\n{string.Join('\n', frame.Loot)}\n\nCHAT\n{string.Join('\n', frame.Chat)}";
            Status = (frame.Hud == null ? "Reading · EXP unreadable; check regions in Setup" : "Live · " + target.Title) + (journalWarning == null ? "" : " · " + journalWarning);
        }
        catch (Exception ex)
        {
            Running = false; active.Stop(); Status = "Capture stopped · " + ex.Message;
        }
        finally { busy = false; Changed?.Invoke(); }
    }

    private void AcceptHud(HudReading? reading)
    {
        if (reading == null) { pendingHud = null; return; }
        var absolute = Table.Absolute(reading.Level, reading.Experience);
        // Confirm baseline, losses, level changes and large jumps using two successive plausible observations.
        var snapshot = Snapshot;
        var unusual = !hasHud || reading.Level != snapshot.Level || absolute < lastConfirmedAbsolute ||
            absolute - lastConfirmedAbsolute > (Table.Required(reading.Level) ?? long.MaxValue) / 10;
        if (unusual && (pendingHud == null || pendingHud.Level != reading.Level ||
            Math.Abs(absolute - Table.Absolute(pendingHud.Level, pendingHud.Experience)) > (Table.Required(reading.Level) ?? long.MaxValue) / 20))
        { pendingHud = reading; return; }
        pendingHud = null;
        if (session.Observe(reading, active.Elapsed)) { lastConfirmedAbsolute = absolute; hasHud = true; }
    }

    private void AddChat(string text) => AddEntry(new(DateTimeOffset.Now, text, new TradeClassifier(Settings).Classify(text)));
    private void AddLoot(string text)
    {
        if (TextParsing.Mesos(text) is long amount) session.AddMesos(amount);
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
        File.WriteAllText(file, JsonSerializer.Serialize(new { Demo, Snapshot = snapshot, Items, Entries }, new JsonSerializerOptions { WriteIndented = true }));
    }
    public async Task WaitForIdle() { while (busy) await Task.Delay(30); }
    public async Task Shutdown()
    {
        Pause(); timer.Stop(); await WaitForIdle(); ocr?.Dispose(); journal?.Dispose();
    }
}
